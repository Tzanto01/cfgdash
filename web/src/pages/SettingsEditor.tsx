import { Search } from 'lucide-react';
import { useEffect, useId, useRef, useState, type KeyboardEvent } from 'react';
import { flushSync } from 'react-dom';
import { Button } from '../components/ui/button';
import { api, ApiError, isAbort } from '../lib/api';
import type { AuditEntry, EditScope, Proposal, SettingChange, SettingRow as Row, SettingsCategory, SettingsResponse } from '../lib/models';
import { useUrl } from '../lib/url';
import { cn } from '../lib/utils';
import { focusPendingHeading, LoadingPanel } from '../shell/Shell';
import { useAnnounce } from '../state/announcer';
import { useSnapshotStore } from '../state/snapshot';
import { ChangeLog } from './ChangeLog';
import { DiffCard } from './DiffCard';
import { EditToggle } from './EditToggle';
import { SCOPE_LABEL, SettingRow } from './SettingRow';

const SCOPES: EditScope[] = ['user', 'project', 'local', 'machine'];
const KEY_STEP: Record<string, number> = { ArrowRight: 1, ArrowDown: 1, ArrowLeft: -1, ArrowUp: -1 };
const LOCAL_ONLY = "Editing is only available from this machine's browser (localhost).";
const FORBIDDEN = "cfgdash refused the change (it only accepts edits from this machine's browser).";
const INVALID: Record<string, string> = {
  'bad-type': 'Wrong type',
  'bad-option': 'Not one of the allowed options',
  'out-of-range': 'Out of range',
  'not-editable': 'Not editable',
  'bad-scope': 'Not settable in this scope',
  'scope-unavailable': 'This scope is not available for the selected folder',
  'unsafe-target': 'The settings file is a link outside its folder: not editable',
  'parse-error': 'The settings file is not valid JSON; fix it by hand first',
  'not-an-object': 'A parent key is not an object in the file',
  duplicate: 'The same setting is changed twice',
  'unknown-key': 'Not in the settings catalog',
  'bad-request': 'The request was not valid',
  'no-change': 'Nothing would change'
};
const STALE_UNDO = 'These files changed since that change; undo is no longer safe.';
const FLOATING = 'sticky z-20 bottom-[calc(56px+env(safe-area-inset-bottom)+8px)] md:bottom-[calc(env(safe-area-inset-bottom)+16px)]';

interface Banner {
  tone: 'ok' | 'danger';
  title: string;
  detail?: string;
  retry?: string;
}

interface Loaded {
  dir?: string;
  data?: SettingsResponse;
  error?: unknown;
}

const stageKey = (scope: EditScope, key: string) => `${scope}\n${key}`;
const plural = (n: number, word: string) => `${n} ${n === 1 ? word : `${word}s`}`;

function applyFailure(error: unknown): Banner {
  const again = 'Review again';
  if (error instanceof ApiError) {
    if (error.status === 409) {
      return { tone: 'danger', title: 'The files changed since the review. Review the changes again.', retry: again };
    }
    if (error.status === 400 && error.code === 'confirm-mismatch') {
      return { tone: 'danger', title: 'The review no longer matches. Review the changes again.', retry: again };
    }
    if (error.status === 404) {
      return { tone: 'danger', title: 'The review expired. Review the changes again.', retry: again };
    }
    if (error.status === 403) {
      return { tone: 'danger', title: FORBIDDEN };
    }
    if (error.code === 'write-failed') {
      const written = error.body?.['written'];
      const auditId = error.body?.['auditId'];
      const some = Array.isArray(written) ? written.length > 0 : Boolean(written);
      return {
        tone: 'danger',
        title: 'Writing failed',
        detail: some ? `Some files were written; the audit entry ${typeof auditId === 'string' ? auditId : ''} records it.` : 'Nothing was written.',
        retry: again
      };
    }
  }
  return { tone: 'danger', title: "Couldn't apply the changes.", retry: again };
}

/** Settings editor (edits-v1): category nav with search, scope switch, staged changes, dry-run review and apply. */
export default function SettingsEditor() {
  const { dir } = useUrl();
  const store = useSnapshotStore();
  const announce = useAnnounce();
  const baseId = useId();
  const targetDir = store.snapshot?.context.targetDir;
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [reloads, setReloads] = useState(0);
  const [query, setQuery] = useState('');
  const [categoryId, setCategoryId] = useState<string | null>(null);
  const [scope, setScope] = useState<EditScope>('user');
  const [staged, setStaged] = useState<Record<string, SettingChange>>({});
  const [stagedDir, setStagedDir] = useState(dir);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [proposal, setProposal] = useState<Proposal | null>(null);
  const [busy, setBusy] = useState<'review' | 'apply' | null>(null);
  const [banner, setBanner] = useState<Banner | null>(null);
  const [audit, setAudit] = useState<{ entries?: AuditEntry[]; failed?: boolean } | null>(null);
  const [undoOf, setUndoOf] = useState<string | null>(null);
  const [undoBusy, setUndoBusy] = useState<string | null>(null);
  const [undoError, setUndoError] = useState<{ auditId: string; text: string } | null>(null);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const reviewRef = useRef<HTMLHeadingElement>(null);
  const bannerRef = useRef<HTMLDivElement>(null);
  const pending = useRef(false);
  const latest = useRef({ dir, staged });
  latest.current = { dir, staged };

  if (stagedDir !== dir) {
    setStagedDir(dir);
    setStaged({});
    setFieldErrors({});
    setProposal(null);
    setUndoOf(null);
    setBanner(null);
  }

  useEffect(() => {
    focusPendingHeading();
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    api.settings(dir, controller.signal).then(
      (data) => {
        if (!controller.signal.aborted) {
          setLoaded({ dir, data });
        }
      },
      (error: unknown) => {
        if (!isAbort(error) && !controller.signal.aborted) {
          setLoaded({ dir, error });
        }
      }
    );
    return () => controller.abort();
  }, [dir, reloads]);

  useEffect(() => {
    const controller = new AbortController();
    api.audit(controller.signal).then(
      (response) => setAudit({ entries: response.entries }),
      (error: unknown) => {
        if (!isAbort(error) && !controller.signal.aborted) {
          setAudit({ failed: true });
        }
      }
    );
    return () => controller.abort();
  }, [reloads]);

  const current = loaded && loaded.dir === dir ? loaded : null;
  const data = current?.data;
  const categories = data?.categories ?? [];
  const needle = query.trim().toLowerCase();
  const matches = (row: Row) => [row.label, row.key, row.description].some((text) => text.toLowerCase().includes(needle));
  const activeCategory = categories.find((c) => c.id === categoryId) ?? categories[0];
  const shown: SettingsCategory[] = needle
    ? categories.map((c) => ({ ...c, settings: c.settings.filter(matches) })).filter((c) => c.settings.length > 0)
    : activeCategory
      ? [activeCategory]
      : [];
  const scopes = SCOPES.filter((s) => shown.some((c) => c.settings.some((row) => row.scopes.includes(s))));
  const activeScope: EditScope | undefined = scopes.includes(scope) ? scope : scopes[0];
  const rowScope = activeScope ?? 'user';
  const scopeFile = data?.files.find((f) => f.scope === activeScope);
  const changes = Object.values(staged);
  const matchCount = needle ? shown.reduce((n, c) => n + c.settings.length, 0) : 0;

  useEffect(() => {
    if (needle) {
      announce(matchCount === 1 ? '1 setting matches' : `${matchCount} settings match`);
    }
  }, [announce, needle, matchCount]);

  const showBanner = (next: Banner) => {
    flushSync(() => setBanner(next));
    bannerRef.current?.focus();
  };

  const clearError = (key: string) =>
    setFieldErrors((prev) => {
      if (!(key in prev)) {
        return prev;
      }
      const next = { ...prev };
      delete next[key];
      return next;
    });

  const stage = (row: Row, change: SettingChange) => {
    const id = stageKey(change.scope, change.key);
    const before = row.values[change.scope] ?? null;
    const same = 'unset' in change ? before === null : JSON.stringify(change.value) === JSON.stringify(before);
    setStaged((prev) => {
      const next = { ...prev };
      if (same) {
        delete next[id];
      } else {
        next[id] = change;
      }
      return next;
    });
    clearError(row.key);
  };

  const unstage = (key: string, s: EditScope) => {
    setStaged((prev) => {
      const next = { ...prev };
      delete next[stageKey(s, key)];
      return next;
    });
    clearError(key);
  };

  const labelOf = (key: string) => categories.flatMap((c) => c.settings).find((row) => row.key === key)?.label ?? key;

  const review = async () => {
    if (pending.current || !changes.length) {
      return;
    }
    if (!targetDir) {
      showBanner({ tone: 'danger', title: 'The folder is still loading. Try again in a moment.', retry: 'Try again' });
      return;
    }
    pending.current = true;
    setBusy('review');
    const asked = latest.current;
    const outdated = () => latest.current.dir !== asked.dir || latest.current.staged !== asked.staged;
    try {
      const next = await api.propose(targetDir, changes);
      if (outdated()) {
        return;
      }
      flushSync(() => {
        setProposal(next);
        setBanner(null);
        setFieldErrors({});
      });
      reviewRef.current?.focus();
    } catch (error) {
      if (outdated()) {
        return;
      }
      if (error instanceof ApiError && error.status === 400 && error.code === 'invalid-change') {
        const raw = error.body?.['key'];
        const key = typeof raw === 'string' ? raw : null;
        const words = (error.reason && INVALID[error.reason]) ?? error.reason ?? 'Not valid';
        setFieldErrors(key ? { [key]: words } : {});
        showBanner({ tone: 'danger', title: "Some changes can't be reviewed.", detail: key ? `${labelOf(key)}: ${words}.` : `${words}.` });
      } else if (error instanceof ApiError && error.status === 404) {
        showBanner({ tone: 'danger', title: LOCAL_ONLY });
      } else if (error instanceof ApiError && error.status === 403) {
        showBanner({ tone: 'danger', title: FORBIDDEN });
      } else {
        showBanner({ tone: 'danger', title: "Couldn't prepare the review.", retry: 'Try again' });
      }
    } finally {
      pending.current = false;
      setBusy(null);
    }
  };

  const undo = async (auditId: string) => {
    if (pending.current) {
      return;
    }
    pending.current = true;
    setUndoBusy(auditId);
    setUndoError(null);
    try {
      const next = await api.revert(auditId);
      flushSync(() => {
        setProposal(next);
        setUndoOf(auditId);
        setBanner(null);
      });
      reviewRef.current?.focus();
    } catch (error) {
      const text =
        error instanceof ApiError && error.status === 409
          ? STALE_UNDO
          : error instanceof ApiError && error.status === 404
            ? "This change can't be undone: its backup is gone."
            : error instanceof ApiError && error.status === 403
              ? FORBIDDEN
              : "Couldn't prepare the undo.";
      setUndoError({ auditId, text });
    } finally {
      pending.current = false;
      setUndoBusy(null);
    }
  };

  const apply = async () => {
    if (pending.current || !proposal) {
      return;
    }
    pending.current = true;
    setBusy('apply');
    const undoing = undoOf;
    const askedDir = latest.current.dir;
    try {
      const result = await api.apply(proposal.id, proposal.confirm);
      const text = `${undoing ? 'Undone' : 'Applied'}. Audit entry ${result.auditId}.`;
      flushSync(() => {
        setProposal(null);
        setUndoOf(null);
        if (!undoing && latest.current.dir === askedDir) {
          setStaged({});
          setFieldErrors({});
        }
        setBanner({ tone: 'ok', title: text });
      });
      bannerRef.current?.focus();
      announce(text);
      setReloads((n) => n + 1);
      store.refresh();
    } catch (error) {
      flushSync(() => {
        setProposal(null);
        setUndoOf(null);
        setBanner(undoing ? { ...applyFailure(error), retry: undefined } : applyFailure(error));
      });
      bannerRef.current?.focus();
      if (error instanceof ApiError && (error.code === 'write-failed' || error.status === 409)) {
        setReloads((n) => n + 1);
        store.refresh();
      }
    } finally {
      pending.current = false;
      setBusy(null);
    }
  };

  const backToEditing = () => {
    flushSync(() => {
      setProposal(null);
      setUndoOf(null);
    });
    headingRef.current?.focus();
  };

  const discard = () => {
    flushSync(() => {
      setStaged({});
      setFieldErrors({});
    });
    headingRef.current?.focus();
    announce('Staged changes discarded');
  };

  const retryLoad = () => {
    setLoaded(null);
    setReloads((n) => n + 1);
  };

  const moveScope = (event: KeyboardEvent<HTMLDivElement>) => {
    if (!activeScope) {
      return;
    }
    const step = KEY_STEP[event.key];
    const index = scopes.indexOf(activeScope);
    const next =
      event.key === 'Home' ? 0 : event.key === 'End' ? scopes.length - 1 : step === undefined ? -1 : (index + step + scopes.length) % scopes.length;
    if (next < 0) {
      return;
    }
    event.preventDefault();
    setScope(scopes[next]);
    (event.currentTarget.children.item(next) as HTMLElement | null)?.focus();
  };

  const folder = targetDir ?? dir;

  const body = () => {
    if (!current) {
      return <LoadingPanel text="Reading settings..." />;
    }
    if (!data) {
      return (
        <div className="space-y-3">
          <p>{current.error instanceof ApiError && current.error.status === 404 ? LOCAL_ONLY : "Couldn't load the settings."}</p>
          <Button onClick={retryLoad}>Try again</Button>
        </div>
      );
    }
    if (proposal) {
      const count = changes.length;
      return (
        <section aria-labelledby={`${baseId}-review`}>
          <h2 ref={reviewRef} id={`${baseId}-review`} tabIndex={-1} className="text-lg font-semibold">
            {undoOf ? `Review undo of ${undoOf}` : 'Review changes'}
          </h2>
          <p className="mb-4 text-muted">Dry run: nothing is written until you apply.</p>
          <div className="space-y-4">
            {proposal.files.map((file) => (
              <DiffCard
                key={`${file.scope}\n${file.path}`}
                file={file}
                scopeLabel={SCOPE_LABEL[file.scope] ?? file.scope}
                gitTracked={data.files.some((f) => f.path === file.path && f.gitTracked)}
              />
            ))}
          </div>
          <div className={cn(FLOATING, 'mt-4 flex h-16 items-center justify-end gap-2 rounded-lg border border-border bg-raised px-4 shadow-overlay')}>
            <Button onClick={backToEditing} disabled={busy === 'apply'}>
              Back to editing
            </Button>
            <Button variant="primary" aria-busy={busy === 'apply'} onClick={() => void apply()}>
              {busy === 'apply' ? 'Applying...' : undoOf ? 'Apply undo' : `Apply ${plural(count, 'change')}`}
            </Button>
          </div>
        </section>
      );
    }
    return (
      <div className="md:grid md:grid-cols-[200px_minmax(0,1fr)] md:gap-6">
        <div className="mb-4 space-y-3 md:sticky md:top-4 md:mb-0 md:self-start">
          <div className="relative">
            <Search className="pointer-events-none absolute top-1/2 left-2 z-10 size-4 -translate-y-1/2 text-muted" strokeWidth={1.75} aria-hidden="true" />
            <input
              type="search"
              aria-label="Search settings"
              placeholder="Search settings"
              autoComplete="off"
              spellCheck={false}
              value={query}
              onChange={(event) => setQuery(event.target.value)}
              className="touch-target h-8 w-full rounded-md border border-border-strong bg-raised pr-2 pl-8 text-sm placeholder:text-muted"
            />
          </div>
          <label className="block md:hidden">
            <span className="text-xs font-medium text-muted">Category</span>
            <select
              value={activeCategory?.id ?? ''}
              onChange={(event) => {
                setCategoryId(event.target.value);
                setQuery('');
              }}
              className="touch-target mt-1 h-8 w-full rounded-md border border-border-strong bg-raised px-2 text-sm"
            >
              {categories.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.label}
                </option>
              ))}
            </select>
          </label>
          <nav aria-label="Setting categories" className="hidden md:block">
            <ul className="space-y-0.5">
              {categories.map((c) => {
                const active = !needle && c.id === activeCategory?.id;
                return (
                  <li key={c.id}>
                    <button
                      type="button"
                      aria-current={active ? 'true' : undefined}
                      onClick={() => {
                        setCategoryId(c.id);
                        setQuery('');
                      }}
                      className={cn(
                        'touch-target flex h-9 w-full items-center rounded-lg px-3 text-left transition-colors duration-100',
                        active ? 'tint-accent font-semibold text-accent' : 'hover:bg-hover'
                      )}
                    >
                      {c.label}
                    </button>
                  </li>
                );
              })}
            </ul>
          </nav>
        </div>

        <div className="min-w-0 space-y-4">
          {banner && (
            <div
              ref={bannerRef}
              tabIndex={-1}
              role={banner.tone === 'ok' ? 'status' : 'alert'}
              className={cn('rounded-lg border px-4 py-3', banner.tone === 'ok' ? 'tint-ok border-ok' : 'tint-danger border-danger')}
            >
              <p className="font-medium">{banner.title}</p>
              {banner.detail && <p className="mt-1">{banner.detail}</p>}
              {banner.retry && changes.length > 0 && (
                <Button size="sm" className="mt-2" aria-busy={busy === 'review'} onClick={() => void review()}>
                  {banner.retry}
                </Button>
              )}
            </div>
          )}

          {activeScope && (
            <div>
              <div role="radiogroup" aria-label="Scope" onKeyDown={moveScope} className="inline-flex flex-wrap rounded-md border border-border-strong bg-surface p-0.5">
                {scopes.map((s) => {
                  const checked = s === activeScope;
                  return (
                    <button
                      key={s}
                      type="button"
                      role="radio"
                      aria-checked={checked}
                      tabIndex={checked ? 0 : -1}
                      onClick={() => setScope(s)}
                      className={cn(
                        'touch-target h-7 rounded-[5px] px-3 text-sm transition-colors duration-100',
                        checked ? 'bg-accent font-medium text-accent-fg' : 'hover:bg-hover'
                      )}
                    >
                      {SCOPE_LABEL[s]}
                    </button>
                  );
                })}
              </div>
              {scopeFile && (
                <div className="mt-2 text-xs text-muted">
                  <p>
                    Writes to <span className="mono-value wrap-anywhere text-text">{scopeFile.path}</span>
                    {!scopeFile.exists && ' (not created yet)'}
                  </p>
                  {scopeFile.gitTracked && <p className="text-warn">Tracked by git: applying leaves the repo with uncommitted changes.</p>}
                </div>
              )}
            </div>
          )}

          {shown.length === 0 ? (
            <p className="text-muted">{needle ? 'No settings match the search.' : 'No settings.'}</p>
          ) : (
            shown.map((category, index) => (
              <section key={category.id} aria-labelledby={`${baseId}-cat-${index}`} className="card rounded-lg border border-border bg-surface">
                <h2 id={`${baseId}-cat-${index}`} className="flex h-10 items-center border-b border-border px-4 font-semibold">
                  {category.label}
                </h2>
                {category.settings.length ? (
                  category.settings.map((row) => (
                    <SettingRow
                      key={row.key}
                      row={row}
                      scope={rowScope}
                      staged={staged[stageKey(rowScope, row.key)]}
                      error={fieldErrors[row.key]}
                      onStage={(change) => stage(row, change)}
                      onUnstage={() => unstage(row.key, rowScope)}
                    />
                  ))
                ) : (
                  <p className="px-4 py-3 text-muted">No settings in this category.</p>
                )}
              </section>
            ))
          )}

          <ChangeLog
            entries={audit?.entries ?? null}
            failed={audit?.failed === true}
            busyId={undoBusy}
            error={undoError}
            onUndo={(auditId) => void undo(auditId)}
            onRetry={() => {
              setAudit(null);
              setReloads((n) => n + 1);
            }}
          />

          {changes.length > 0 && (
            <div className={cn(FLOATING, 'flex flex-wrap items-center gap-2 rounded-lg border border-border bg-raised px-4 py-3 shadow-overlay')}>
              <p className="mr-auto font-medium">{plural(changes.length, 'change')} staged</p>
              <Button onClick={discard}>Discard</Button>
              <Button variant="primary" aria-busy={busy === 'review'} onClick={() => void review()}>
                {busy === 'review' ? 'Preparing review...' : 'Review changes'}
              </Button>
            </div>
          )}
        </div>
      </div>
    );
  };

  return (
    <>
      <header className="mb-4">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
          <h1 ref={headingRef} tabIndex={-1} className="text-2xl leading-[30px] font-semibold">
            Edit settings
          </h1>
          <span className="ml-auto">
            <EditToggle />
          </span>
        </div>
        {folder && (
          <p className="mt-1 text-muted">
            Folder <span className="mono-value wrap-anywhere">{folder}</span>
          </p>
        )}
      </header>
      {body()}
    </>
  );
}
