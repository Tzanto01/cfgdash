import { X } from 'lucide-react';
import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { Button } from '../components/ui/button';
import { api, ApiError, isAbort } from '../lib/api';
import type { AssistantEffort, AssistantProposal, AssistantStatus } from '../lib/models';
import { Markdown } from '../lib/markdown';
import { cn } from '../lib/utils';
import { DiffCard } from '../pages/DiffCard';
import { SCOPE_LABEL } from '../pages/SettingRow';
import { useAnnounce } from '../state/announcer';
import { MAX_CONTEXT, useAssistant } from '../state/assistant';
import { useSnapshotStore } from '../state/snapshot';
import { LoadingPanel } from './Shell';

const EFFORTS: AssistantEffort[] = ['low', 'medium', 'high', 'xhigh', 'max'];
const BANNER = 'Proposed by the assistant from your config files';
const MODEL_OUTPUT = 'Assistant · model output derived from your files; review before pasting';
const FIELD = 'touch-target h-8 w-full rounded-md border border-border-strong bg-raised px-2 text-sm';

type Entry =
  | { kind: 'user'; text: string }
  | { kind: 'assistant'; text: string }
  | { kind: 'tool'; name: string; status: string }
  | { kind: 'error'; text: string }
  | { kind: 'note'; text: string }
  | { kind: 'proposal'; proposal: AssistantProposal; result?: { ok: boolean; text: string }; busy?: boolean };

interface Session {
  id: string;
  dir: string;
  model: string;
  effort: string;
}

/** The inline message for a failed assistant request, in words. */
function requestFailure(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 400 && error.code === 'bad-context') {
      return 'Some context items are not available to the assistant. Remove them and send again.';
    }
    if (error.status === 400) {
      return 'The assistant refused the request as not valid.';
    }
    if (error.status === 409 && error.code === 'budget-spent') {
      return "This session's budget is spent. End the session to start a new one.";
    }
    if (error.status === 409) {
      return 'The assistant is still answering. Wait for it to finish.';
    }
    if (error.status === 429) {
      return 'Too many assistant sessions are running (at most 2). End another one first.';
    }
    if (error.status === 503) {
      return `The assistant isn't available${error.reason ? `: ${error.reason}` : ''}.`;
    }
    if (error.status === 404) {
      return 'The session ended. Send again to start a new one.';
    }
    if (error.status === 403) {
      return "cfgdash refused the request (the assistant only works from this machine's browser).";
    }
  }
  return "Couldn't reach the assistant.";
}

function applyFailure(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 409) {
      return 'The files changed since this proposal. Ask the assistant again.';
    }
    if (error.status === 400 && error.code === 'confirm-mismatch') {
      return 'This proposal no longer matches. Ask the assistant again.';
    }
    if (error.status === 404) {
      return 'This proposal expired. Ask the assistant again.';
    }
    if (error.status === 403) {
      return "cfgdash refused the change (it only accepts edits from this machine's browser).";
    }
  }
  return "Couldn't apply the proposal.";
}

function parse(event: Event): Record<string, unknown> {
  try {
    const data: unknown = JSON.parse((event as MessageEvent<string>).data);
    return data && typeof data === 'object' ? (data as Record<string, unknown>) : {};
  } catch {
    return {};
  }
}

const str = (value: unknown) => (typeof value === 'string' ? value : '');

/** The config assistant side panel (assistant-v1): model and effort, context chips, streamed answers and proposals. */
export default function AssistantPanel() {
  const assistant = useAssistant();
  const store = useSnapshotStore();
  const announce = useAnnounce();
  const dir = store.snapshot?.context.targetDir;
  const [status, setStatus] = useState<{ data?: AssistantStatus; failed?: boolean } | null>(null);
  const [model, setModel] = useState('');
  const [effort, setEffort] = useState<AssistantEffort>('medium');
  const [session, setSession] = useState<Session | null>(null);
  const [entries, setEntries] = useState<Entry[]>([]);
  const [running, setRunning] = useState(false);
  const [cost, setCost] = useState<number | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [text, setText] = useState('');
  const [tracked, setTracked] = useState<string[]>([]);
  const headingRef = useRef<HTMLHeadingElement>(null);
  const events = useRef<EventSource | null>(null);
  const live = useRef<Session | null>(null);
  const pending = useRef(false);
  const mounted = useRef(true);
  const effortTouched = useRef(false);
  live.current = session;

  useEffect(() => {
    mounted.current = true;
    headingRef.current?.focus();
    const controller = new AbortController();
    api.assistantStatus(controller.signal).then(
      (data) => {
        setStatus({ data });
        if (data.available) {
          api.settings(dir, controller.signal).then(
            (settings) => setTracked(settings.files.filter((f) => f.gitTracked).map((f) => f.path)),
            () => undefined
          );
        }
      },
      (error: unknown) => !isAbort(error) && setStatus({ failed: true })
    );
    api.cfgdashSettings(controller.signal).then(
      (settings) => {
        if (!settings.configError) {
          setModel((m) => m || settings.values.assistant.model);
          if (!effortTouched.current) {
            setEffort(settings.values.assistant.effort);
          }
        }
      },
      () => undefined
    );
    const leave = () => {
      if (live.current) {
        api.assistantEnd(live.current.id, true).catch(() => undefined);
      }
    };
    window.addEventListener('pagehide', leave);
    return () => {
      mounted.current = false;
      window.removeEventListener('pagehide', leave);
      controller.abort();
      events.current?.close();
      if (live.current) {
        api.assistantEnd(live.current.id).catch(() => undefined);
      }
    };
  }, []);

  const push = (entry: Entry) => setEntries((prev) => [...prev, entry]);

  const listen = (id: string) => {
    events.current?.close();
    const source = new EventSource(api.assistantEventsUrl(id));
    events.current = source;
    source.addEventListener('delta', (event) => {
      const chunk = str(parse(event)['text']);
      setEntries((prev) => {
        const last = prev[prev.length - 1];
        return last?.kind === 'assistant' ? [...prev.slice(0, -1), { kind: 'assistant', text: last.text + chunk }] : [...prev, { kind: 'assistant', text: chunk }];
      });
    });
    source.addEventListener('tool', (event) => {
      const data = parse(event);
      const name = str(data['name']);
      const state = str(data['status']);
      setEntries((prev) => {
        const index = prev.findLastIndex((e) => e.kind === 'tool' && e.name === name && e.status === 'started');
        return index >= 0 && state !== 'started'
          ? prev.map((e, i) => (i === index ? { kind: 'tool', name, status: state } : e))
          : [...prev, { kind: 'tool', name, status: state }];
      });
    });
    source.addEventListener('proposal', (event) => {
      const data = parse(event);
      if (typeof data['proposalId'] === 'string' && typeof data['confirm'] === 'string' && Array.isArray(data['files'])) {
        push({ kind: 'proposal', proposal: data as unknown as AssistantProposal });
        announce('The assistant proposed a change');
      }
    });
    source.addEventListener('cost', (event) => {
      const value = parse(event)['costUsd'];
      if (typeof value === 'number') {
        setCost(value);
      }
    });
    source.addEventListener('done', () => {
      setRunning(false);
      announce('The assistant finished');
    });
    source.addEventListener('error', (event) => {
      if (event instanceof MessageEvent) {
        const data = parse(event);
        push({ kind: 'error', text: str(data['message']) || str(data['error']) || 'The assistant stopped.' });
        setRunning(false);
      } else if (source.readyState === EventSource.CLOSED) {
        push({ kind: 'error', text: 'Lost the connection to the assistant. Send again to start a new session.' });
        setRunning(false);
        setSession(null);
      }
    });
  };

  const end = async () => {
    const current = live.current;
    events.current?.close();
    events.current = null;
    setSession(null);
    setRunning(false);
    setCost(null);
    if (current) {
      await api.assistantEnd(current.id).catch(() => undefined);
      push({ kind: 'note', text: 'Session ended.' });
    }
  };

  const send = async (check: boolean) => {
    const message = text.trim();
    if (pending.current || running || !dir || (!check && !message)) {
      return;
    }
    if (!chosenModel) {
      setNotice('No model is available. Pick one in cfgdash settings.');
      return;
    }
    pending.current = true;
    setNotice(null);
    const entry: Entry = { kind: 'user', text: check ? 'Full check' : message };
    try {
      let current = live.current;
      if (current && (current.dir !== dir || current.model !== chosenModel || current.effort !== effort)) {
        await end();
        current = null;
      }
      if (!current) {
        const { id } = await api.assistantStart(dir, chosenModel, effort);
        if (!mounted.current) {
          api.assistantEnd(id).catch(() => undefined);
          return;
        }
        current = { id, dir, model: chosenModel, effort };
        setSession(current);
        live.current = current;
        listen(id);
        push({ kind: 'note', text: `New session: ${chosenModel}, effort ${effort}.` });
      }
      const refs = assistant.items.map((i) => i.ref);
      setRunning(true);
      push(entry);
      await (check ? api.assistantCheck(current.id) : api.assistantMessage(current.id, message, refs));
      if (!check) {
        setText((t) => (t.trim() === message ? '' : t));
        assistant.drop(refs);
      }
    } catch (error) {
      setRunning(false);
      setEntries((prev) => prev.filter((e) => e !== entry));
      setNotice(requestFailure(error));
      if (error instanceof ApiError && error.status === 404) {
        events.current?.close();
        setSession(null);
      }
    } finally {
      pending.current = false;
    }
  };

  const apply = async (index: number, proposal: AssistantProposal) => {
    const update = (patch: Partial<Extract<Entry, { kind: 'proposal' }>>) =>
      setEntries((prev) => prev.map((e, i) => (i === index && e.kind === 'proposal' ? { ...e, ...patch } : e)));
    update({ busy: true });
    try {
      const result = await api.apply(proposal.proposalId, proposal.confirm);
      const done = `Applied. Audit entry ${result.auditId}.`;
      update({ busy: false, result: { ok: true, text: done } });
      announce(done);
      store.refresh();
    } catch (error) {
      update({ busy: false, result: { ok: false, text: applyFailure(error) } });
    }
  };

  const onKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    if (event.key === 'Escape') {
      event.stopPropagation();
      assistant.setOpen(false);
      document.getElementById('assistant-toggle')?.focus();
    }
  };

  const data = status?.data;
  const models = data?.models ?? [];
  const chosenModel = model || models[0] || '';
  const modelOptions = chosenModel && !models.includes(chosenModel) ? [chosenModel, ...models] : models;

  return (
    <aside
      aria-labelledby="assistant-title"
      onKeyDown={onKeyDown}
      className="fixed inset-0 z-40 flex flex-col bg-surface md:sticky md:top-0 md:z-auto md:h-screen md:w-[400px] md:shrink-0 md:border-l md:border-border"
    >
      <div className="flex h-[52px] shrink-0 items-center gap-2 border-b border-border px-4">
        <h2 id="assistant-title" ref={headingRef} tabIndex={-1} className="font-semibold">
          Assistant
        </h2>
        {cost !== null && <span className="text-xs text-muted tabular">Cost ${cost.toFixed(4)}</span>}
        <span className="flex-1" />
        <Button
          variant="ghost"
          size="icon"
          aria-label="Close assistant"
          onClick={() => {
            assistant.setOpen(false);
            document.getElementById('assistant-toggle')?.focus();
          }}
        >
          <X strokeWidth={1.75} aria-hidden="true" />
        </Button>
      </div>

      {!status ? (
        <LoadingPanel text="Checking the assistant..." className="px-4" />
      ) : !data ? (
        <p className="p-4">Couldn't check whether the assistant is available.</p>
      ) : !data.available ? (
        <div className="space-y-2 p-4">
          <p className="font-medium">The assistant isn't available on this machine.</p>
          <p>{data.reason ?? 'No reason given.'}</p>
        </div>
      ) : (
        <>
          <div className="min-h-0 flex-1 space-y-3 overflow-y-auto p-4" aria-live="off">
            {entries.length === 0 && (
              <p className="text-muted">
                Ask about this folder's harness, or run a full check. Add rows, files and findings to the context with their "Add to context" buttons.
              </p>
            )}
            {entries.map((entry, index) => {
              switch (entry.kind) {
                case 'user':
                  return (
                    <p key={index} className="ml-8 rounded-lg bg-hover px-3 py-2 whitespace-pre-wrap">
                      {entry.text}
                    </p>
                  );
                case 'assistant':
                  return (
                    <div key={index} className="border-l-2 border-border-strong pl-3">
                      <p className="mb-1 text-xs text-muted">{MODEL_OUTPUT}</p>
                      <Markdown text={entry.text} />
                    </div>
                  );
                case 'tool':
                  return (
                    <p key={index} className="font-mono text-xs text-muted">
                      {entry.name}: {entry.status}
                    </p>
                  );
                case 'error':
                  return (
                    <p key={index} className="text-sm text-danger">
                      {entry.text}
                    </p>
                  );
                case 'note':
                  return (
                    <p key={index} className="text-xs text-muted">
                      {entry.text}
                    </p>
                  );
                default:
                  return (
                    <section key={index} aria-label="Assistant proposal" className="space-y-2 rounded-lg border border-accent p-2">
                      <p className="tint-accent rounded-md px-2 py-1 text-sm font-medium text-accent">{BANNER}</p>
                      {entry.proposal.rationale && <p className="whitespace-pre-wrap">{entry.proposal.rationale}</p>}
                      {entry.proposal.files.map((file) => (
                        <DiffCard key={`${file.scope}\n${file.path}`} file={file} scopeLabel={SCOPE_LABEL[file.scope] ?? file.scope} gitTracked={tracked.includes(file.path)} />
                      ))}
                      {entry.result ? (
                        <p role={entry.result.ok ? 'status' : 'alert'} className={cn('text-sm font-medium', entry.result.ok ? 'text-ok' : 'text-danger')}>
                          {entry.result.text}
                        </p>
                      ) : (
                        <Button variant="primary" aria-busy={entry.busy} disabled={entry.busy} onClick={() => void apply(index, entry.proposal)}>
                          {entry.busy ? 'Applying...' : 'Apply this proposal'}
                        </Button>
                      )}
                    </section>
                  );
              }
            })}
            {running && <LoadingPanel text="The assistant is working..." className="py-2" />}
          </div>

          <div className="shrink-0 space-y-2 border-t border-border p-4 pb-[calc(16px+env(safe-area-inset-bottom))]">
            {notice && (
              <p role="alert" className="text-sm font-medium text-danger">
                {notice}
              </p>
            )}
            <div className="grid grid-cols-2 gap-2">
              <label className="text-xs font-medium text-muted">
                Model
                <select value={chosenModel} onChange={(event) => setModel(event.target.value)} className={cn(FIELD, 'mt-1 text-text')}>
                  {modelOptions.map((m) => (
                    <option key={m} value={m}>
                      {m}
                    </option>
                  ))}
                </select>
              </label>
              <label className="text-xs font-medium text-muted">
                Effort
                <select value={effort} onChange={(event) => {
                    effortTouched.current = true;
                    setEffort(event.target.value as AssistantEffort);
                  }} className={cn(FIELD, 'mt-1 text-text')}>
                  {EFFORTS.map((e) => (
                    <option key={e} value={e}>
                      {e}
                    </option>
                  ))}
                </select>
              </label>
            </div>
            {assistant.items.length > 0 && (
              <div>
                <p className="text-xs text-muted tabular">
                  Context {assistant.items.length} of {MAX_CONTEXT}
                </p>
                <ul aria-label="Context" className="mt-1 flex flex-wrap gap-1">
                  {assistant.items.map((item, index) => (
                    <li key={JSON.stringify(item.ref)} className="inline-flex max-w-full items-center gap-1 rounded-full border border-border-strong bg-raised py-0.5 pr-1 pl-2 text-xs">
                      <span className="truncate">{item.label}</span>
                      <button
                        type="button"
                        aria-label={`Remove ${item.label} from context`}
                        onClick={() => assistant.remove(index)}
                        className="touch-target rounded-full p-0.5 hover:bg-hover"
                      >
                        <X className="size-3" strokeWidth={1.75} aria-hidden="true" />
                      </button>
                    </li>
                  ))}
                </ul>
              </div>
            )}
            <label className="block text-xs font-medium text-muted">
              Message
              <textarea
                value={text}
                onChange={(event) => setText(event.target.value)}
                onKeyDown={(event) => {
                  if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
                    event.preventDefault();
                    void send(false);
                  }
                }}
                rows={3}
                maxLength={16000}
                className="mt-1 w-full rounded-md border border-border-strong bg-raised px-2 py-1.5 text-sm text-text"
              />
            </label>
            <div className="flex flex-wrap gap-2">
              <Button variant="primary" disabled={running || !text.trim()} aria-busy={running} onClick={() => void send(false)}>
                Send
              </Button>
              <Button disabled={running} onClick={() => void send(true)}>
                Full check
              </Button>
              {session && (
                <Button variant="ghost" className="ml-auto" onClick={() => void end()}>
                  End session
                </Button>
              )}
            </div>
          </div>
        </>
      )}
    </aside>
  );
}
