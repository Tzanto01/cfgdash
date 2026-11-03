import { useEffect, useId, useRef, useState, type ReactNode } from 'react';
import { Button } from '../components/ui/button';
import { Switch } from '../components/ui/switch';
import { api, ApiError, isAbort } from '../lib/api';
import type { AssistantStatus, CfgdashPatch, CfgdashSettings, CfgdashValues } from '../lib/models';
import { cn } from '../lib/utils';
import { ModelPicker, pickerProviders } from '../shell/ModelPicker';
import { focusPendingHeading, LoadingPanel } from '../shell/Shell';
import { useAnnounce } from '../state/announcer';
import { useSnapshotStore } from '../state/snapshot';
import { setTheme, useTheme } from '../state/theme';

const LOCAL_ONLY = "cfgdash settings are only available from this machine's browser (localhost).";
const FIELD = 'touch-target h-8 w-full rounded-md border border-border-strong bg-raised px-2 text-sm placeholder:text-muted';
const INVALID: Record<string, string> = {
  'bad-type': 'Wrong type',
  'bad-value': 'Not an allowed value',
  'unknown-key': 'Not a cfgdash setting',
  'bad-request': 'The request was not valid'
};

type Field = 'startAtLogin' | 'theme' | 'defaultDir' | 'tailnet' | 'fleet' | 'model' | 'budget' | 'budgetEnabled' | 'applyEnabled';

/** The inline message for a failed PUT, in words. */
function putFailure(error: unknown, field: Field): string {
  if (error instanceof ApiError) {
    if (error.status === 500 && error.code === 'autostart-failed') {
      return `Couldn't change start at login${error.reason ? `: ${error.reason}` : ''}. Nothing was saved.`;
    }
    if (error.status === 409 && error.code === 'config-unreadable') {
      return "config.json can't be read, so nothing was saved. Fix or remove the file first.";
    }
    if (error.status === 400 && error.code === 'invalid-setting') {
      if (field === 'defaultDir' && error.reason === 'bad-value') {
        return 'Not an existing absolute folder.';
      }
      return `${(error.reason && INVALID[error.reason]) ?? error.reason ?? 'Not valid'}.`;
    }
    if (error.status === 403) {
      return "cfgdash refused the change (it only accepts changes from this machine's browser).";
    }
    if (error.status === 404) {
      return LOCAL_ONLY;
    }
    if (error.code === 'write-failed') {
      return `Couldn't write config.json${error.reason ? `: ${error.reason}` : ''}.`;
    }
  }
  return "Couldn't save the change.";
}

const onOff = (on: boolean) => (on ? 'On' : 'Off');

interface RowProps {
  label: string;
  description: ReactNode;
  error?: string;
  children(ids: { labelId: string; descId: string; errorId?: string }): ReactNode;
}

function Row({ label, description, error, children }: RowProps) {
  const id = useId();
  const ids = { labelId: `${id}-label`, descId: `${id}-desc`, errorId: error ? `${id}-error` : undefined };
  return (
    <div className="grid gap-x-6 gap-y-2 border-b border-border px-4 py-3 last:border-b-0 md:grid-cols-[minmax(0,1fr)_minmax(0,320px)]">
      <div className="min-w-0">
        <span id={ids.labelId} className="font-medium">
          {label}
        </span>
        <div id={ids.descId} className="mt-1 text-muted">
          {description}
        </div>
      </div>
      <div className="min-w-0 space-y-1.5">
        {children(ids)}
        {error && (
          <p id={ids.errorId} role="alert" className="text-xs font-medium text-danger">
            {error}
          </p>
        )}
      </div>
    </div>
  );
}

function Card({ title, children }: { title: string; children: ReactNode }) {
  const id = useId();
  return (
    <section aria-labelledby={id} className="card rounded-lg border border-border bg-surface">
      <h2 id={id} className="flex h-10 items-center border-b border-border px-4 font-semibold">
        {title}
      </h2>
      {children}
    </section>
  );
}

interface TextSettingProps {
  ids: { labelId: string; descId: string; errorId?: string };
  value: string;
  type?: 'text' | 'number';
  placeholder?: string;
  busy: boolean;
  onSave(text: string): void;
}

/** A text or number input with Save; the draft resets when the saved value changes (keyed by the caller). */
function TextSetting({ ids, value, type = 'text', placeholder, busy, onSave }: TextSettingProps) {
  const [draft, setDraft] = useState(value);
  return (
    <form
      className="flex gap-2"
      onSubmit={(event) => {
        event.preventDefault();
        if (draft.trim() !== value) {
          onSave(draft.trim());
        }
      }}
    >
      <input
        type={type}
        inputMode={type === 'number' ? 'decimal' : undefined}
        step={type === 'number' ? '0.1' : undefined}
        aria-labelledby={ids.labelId}
        aria-describedby={ids.errorId ? `${ids.descId} ${ids.errorId}` : ids.descId}
        aria-invalid={ids.errorId ? true : undefined}
        autoComplete="off"
        spellCheck={false}
        placeholder={placeholder}
        value={draft}
        onChange={(event) => setDraft(event.target.value)}
        className={cn(FIELD, 'mono-value min-w-0 flex-1')}
      />
      <Button type="submit" disabled={busy || draft.trim() === value} aria-busy={busy}>
        Save
      </Button>
    </form>
  );
}

/** cfgdash's own settings (cfgdash-config-v1), opened from the header gear: every change is a PUT of the changed keys. */
export default function CfgdashSettingsPage() {
  const store = useSnapshotStore();
  const announce = useAnnounce();
  const theme = useTheme();
  const [loaded, setLoaded] = useState<{ data?: CfgdashSettings; error?: unknown } | null>(null);
  const [reloads, setReloads] = useState(0);
  const [busy, setBusy] = useState<Field | null>(null);
  const [errors, setErrors] = useState<Partial<Record<Field, string>>>({});
  const [assistantStatus, setAssistantStatus] = useState<AssistantStatus>();
  const [pick, setPick] = useState<CfgdashValues['assistant'] | null>(null);
  const queuedPick = useRef<CfgdashValues['assistant'] | null>(null);
  const picking = useRef(false);

  useEffect(() => {
    focusPendingHeading();
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    api.cfgdashSettings(controller.signal).then(
      (data) => setLoaded({ data }),
      (error: unknown) => {
        if (!isAbort(error) && !controller.signal.aborted) {
          setLoaded({ error });
        }
      }
    );
    return () => controller.abort();
  }, [reloads]);

  useEffect(() => {
    const controller = new AbortController();
    api.assistantStatus(controller.signal).then(setAssistantStatus, () => undefined);
    return () => controller.abort();
  }, []);

  const save = async (field: Field, patch: CfgdashPatch): Promise<CfgdashSettings | undefined> => {
    if (busy) {
      return undefined;
    }
    setBusy(field);
    setErrors((prev) => ({ ...prev, [field]: undefined }));
    try {
      const data = await api.putCfgdashSettings(patch);
      setLoaded({ data });
      if (patch.theme) {
        setTheme(patch.theme);
      }
      announce('Saved');
      return data;
    } catch (error) {
      setErrors((prev) => ({ ...prev, [field]: putFailure(error, field) }));
      return undefined;
    } finally {
      setBusy(null);
    }
  };

  const data = loaded?.data;

  const savePicks = async (saved: CfgdashValues['assistant']) => {
    picking.current = true;
    let base = saved;
    while (queuedPick.current) {
      const next = queuedPick.current;
      queuedPick.current = null;
      const patch: NonNullable<CfgdashPatch['assistant']> = {};
      if (next.provider !== base.provider) {
        patch.provider = next.provider;
      }
      if (next.model !== base.model) {
        patch.model = next.model;
      }
      if (next.effort !== base.effort) {
        patch.effort = next.effort;
      }
      if (!patch.provider && !patch.model && !patch.effort) {
        continue;
      }
      const result = await save('model', { assistant: patch });
      if (!result) {
        queuedPick.current = null;
        break;
      }
      base = result.values.assistant;
    }
    picking.current = false;
    setPick(null);
  };

  const body = () => {
    if (!loaded) {
      return <LoadingPanel text="Reading cfgdash settings..." />;
    }
    if (!data) {
      return (
        <div className="space-y-3">
          <p>{loaded.error instanceof ApiError && loaded.error.status === 404 ? LOCAL_ONLY : "Couldn't load cfgdash settings."}</p>
          <Button
            onClick={() => {
              setLoaded(null);
              setReloads((n) => n + 1);
            }}
          >
            Try again
          </Button>
        </div>
      );
    }
    const { values, startAtLogin } = data;
    const actualOn = startAtLogin.actual === 'on';
    const mismatch = startAtLogin.actual !== 'unsupported' && startAtLogin.configured !== actualOn;
    const targetDir = store.snapshot?.context.targetDir;
    return (
      <div className="space-y-4">
        {data.configError && (
          <div className="tint-warn rounded-lg border border-warn px-4 py-3">
            <p className="font-medium">config.json can't be read: {data.configError}</p>
            <p className="mt-1">Showing the defaults. Changes can't be saved until the file is fixed or removed.</p>
          </div>
        )}

        <Card title="Start">
          <Row label="Start at login" description="Start cfgdash in the background when you log in to this machine." error={errors.startAtLogin}>
            {(ids) => (
              <>
                <div className="flex items-center gap-2">
                  <Switch
                    on={values.startAtLogin}
                    aria-labelledby={ids.labelId}
                    aria-describedby={ids.descId}
                    aria-busy={busy === 'startAtLogin'}
                    disabled={busy !== null}
                    onClick={() => void save('startAtLogin', { startAtLogin: !values.startAtLogin })}
                  />
                  <span className="text-sm text-muted">{onOff(values.startAtLogin)}</span>
                </div>
                <p className="text-xs text-muted">
                  Configured: {onOff(startAtLogin.configured)} · Actual:{' '}
                  {startAtLogin.actual === 'unsupported' ? 'not supported' : onOff(actualOn)}
                </p>
                {startAtLogin.actual === 'unsupported' && startAtLogin.detail && <p className="text-xs text-muted">{startAtLogin.detail}</p>}
                {mismatch && (
                  <p className="text-xs font-medium text-warn">
                    Mismatch: the login entry on this machine doesn't match config.json. Switch it off and on again to fix it.
                  </p>
                )}
              </>
            )}
          </Row>
          <Row
            label="Default folder"
            description="The folder cfgdash opens when it starts without --dir. Empty means the folder it was started in (your home folder at login)."
            error={errors.defaultDir}
          >
            {(ids) => (
              <>
                <TextSetting
                  key={values.defaultDir ?? ''}
                  ids={ids}
                  value={values.defaultDir ?? ''}
                  placeholder="Not set"
                  busy={busy === 'defaultDir'}
                  onSave={(text) => void save('defaultDir', { defaultDir: text === '' ? null : text })}
                />
                <div className="flex flex-wrap gap-1">
                  {targetDir && targetDir !== values.defaultDir && (
                    <Button size="sm" variant="ghost" disabled={busy !== null} onClick={() => void save('defaultDir', { defaultDir: targetDir })}>
                      Use the current folder
                    </Button>
                  )}
                  {values.defaultDir !== null && (
                    <Button size="sm" variant="ghost" disabled={busy !== null} onClick={() => void save('defaultDir', { defaultDir: null })}>
                      Clear
                    </Button>
                  )}
                </div>
              </>
            )}
          </Row>
          <Row
            label="Publish on the tailnet"
            description="Publish the dashboard on your tailnet at start, like --tailnet. Takes effect the next time cfgdash starts."
            error={errors.tailnet}
          >
            {(ids) => (
              <div className="flex items-center gap-2">
                <Switch
                  on={values.tailnet}
                  aria-labelledby={ids.labelId}
                  aria-describedby={ids.descId}
                  aria-busy={busy === 'tailnet'}
                  disabled={busy !== null}
                  onClick={() => void save('tailnet', { tailnet: !values.tailnet })}
                />
                <span className="text-sm text-muted">{onOff(values.tailnet)}</span>
              </div>
            )}
          </Row>
        </Card>

        <Card title="Appearance">
          <Row label="Theme" description="System follows your operating system." error={errors.theme}>
            {(ids) => (
              <select
                aria-labelledby={ids.labelId}
                aria-describedby={ids.descId}
                disabled={busy !== null}
                value={theme}
                onChange={(event) => void save('theme', { theme: event.target.value as CfgdashSettings['values']['theme'] })}
                className={FIELD}
              >
                <option value="system">System</option>
                <option value="light">Light</option>
                <option value="dark">Dark</option>
              </select>
            )}
          </Row>
        </Card>

        <Card title="Fleet">
          <Row
            label="Answer fleet peers"
            description="Let your other machines on the tailnet read this machine's snapshot, findings and health. Needs the tailnet publish."
            error={errors.fleet}
          >
            {(ids) => (
              <div className="flex items-center gap-2">
                <Switch
                  on={values.fleet.enabled}
                  aria-labelledby={ids.labelId}
                  aria-describedby={ids.descId}
                  aria-busy={busy === 'fleet'}
                  disabled={busy !== null}
                  onClick={() => void save('fleet', { fleet: { enabled: !values.fleet.enabled } })}
                />
                <span className="text-sm text-muted">{onOff(values.fleet.enabled)}</span>
              </div>
            )}
          </Row>
        </Card>

        <Card title="Assistant">
          <Row label="Model and effort" description="Default model and reasoning effort for the config assistant." error={errors.model}>
            {(ids) => (
              <ModelPicker
                labelledBy={ids.labelId}
                describedBy={ids.descId}
                disabled={busy !== null}
                providers={pickerProviders(assistantStatus)}
                value={pick ?? values.assistant}
                defaultEffort="medium"
                onChange={(next) => {
                  const chosen = { ...values.assistant, provider: next.provider, model: next.model, effort: next.effort };
                  setPick(chosen);
                  queuedPick.current = chosen;
                  if (!picking.current) {
                    void savePicks(values.assistant);
                  }
                }}
              />
            )}
          </Row>
          <Row
            label="Limit cost per session"
            description="Stop an assistant session once it has spent the budget below. The 10-minute turn limit and the other limits stay either way."
            error={errors.budgetEnabled}
          >
            {(ids) => (
              <div className="flex items-center gap-2">
                <Switch
                  on={values.assistant.budgetEnabled}
                  aria-labelledby={ids.labelId}
                  aria-describedby={ids.descId}
                  aria-busy={busy === 'budgetEnabled'}
                  disabled={busy !== null}
                  onClick={() => void save('budgetEnabled', { assistant: { budgetEnabled: !values.assistant.budgetEnabled } })}
                />
                <span className="text-sm text-muted">{onOff(values.assistant.budgetEnabled)}</span>
              </div>
            )}
          </Row>
          {values.assistant.budgetEnabled && (
            <Row label="Budget per session (USD)" description="The most one assistant session may spend, from 0.1 to 50." error={errors.budget}>
              {(ids) => (
                <TextSetting
                  key={values.assistant.maxBudgetUsd}
                  ids={ids}
                  type="number"
                  value={String(values.assistant.maxBudgetUsd)}
                  busy={busy === 'budget'}
                  onSave={(text) => void save('budget', { assistant: { maxBudgetUsd: Number(text) } })}
                />
              )}
            </Row>
          )}
          <Row
            label="Let the assistant apply settings changes"
            description="The assistant applies settings changes itself instead of proposing them. Each one is audited and can be undone in the change log; file edits always wait for your confirm."
            error={errors.applyEnabled}
          >
            {(ids) => (
              <>
                <div className="flex items-center gap-2">
                  <Switch
                    on={values.assistant.applyEnabled}
                    aria-labelledby={ids.labelId}
                    aria-describedby={`${ids.descId} ${ids.labelId}-warn`}
                    aria-busy={busy === 'applyEnabled'}
                    disabled={busy !== null}
                    onClick={() => void save('applyEnabled', { assistant: { applyEnabled: !values.assistant.applyEnabled } })}
                  />
                  <span className="text-sm text-muted">{onOff(values.assistant.applyEnabled)}</span>
                </div>
                <p id={`${ids.labelId}-warn`} className="text-xs font-medium text-warn">
                  Changes take effect without asking you first.
                </p>
              </>
            )}
          </Row>
        </Card>

        <Card title="Files">
          <dl className="px-4 py-3 text-sm">
            <dt className="text-xs font-medium text-muted">Settings file</dt>
            <dd className="mono-value mb-2 wrap-anywhere">{data.paths.config}</dd>
            <dt className="text-xs font-medium text-muted">Overrides file (edited by hand)</dt>
            <dd className="mono-value wrap-anywhere">{data.paths.overrides}</dd>
          </dl>
        </Card>
      </div>
    );
  };

  return (
    <>
      <header className="mb-4">
        <h1 tabIndex={-1} className="text-2xl leading-[30px] font-semibold">
          cfgdash settings
        </h1>
        <p className="mt-1 text-muted">cfgdash's own settings, saved in its config.json. Each change is saved right away.</p>
      </header>
      {body()}
    </>
  );
}
