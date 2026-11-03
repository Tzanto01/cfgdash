import { useEffect, useRef, useState } from 'react';
import { Button } from '../components/ui/button';
import { api, ApiError } from '../lib/api';
import type { FileContent, Proposal } from '../lib/models';
import { cn } from '../lib/utils';
import { DiffCard } from '../pages/DiffCard';
import { useAnnounce } from '../state/announcer';
import { useSnapshotStore } from '../state/snapshot';
import { NOT_EDITABLE } from './fileEdit';

const MARKER = '[redacted]';
const LINE = 'font-mono text-[12.5px] leading-[18px]';


const INVALID: Record<string, string> = {
  'redacted-value': `The text contains the ${MARKER} marker. Put the real value back or remove the line.`,
  'no-change': 'Nothing changed.',
  'parse-error': 'This is not valid JSON. A settings file must stay one JSON object.',
  'too-large': 'The text is larger than 256 KB.',
  'not-text': "The text contains characters cfgdash can't save (a NUL or broken Unicode)."
};

const STALE = 'This file changed since you opened it. Reload it and make your edit again.';

function failure(error: unknown, undoing: boolean): { text: string; stale?: boolean } {
  if (error instanceof ApiError) {
    if (error.status === 400 && error.code === 'invalid-change') {
      return { text: (error.reason && (INVALID[error.reason] ?? NOT_EDITABLE[error.reason])) || `The change was refused (${error.reason ?? 'not valid'}).` };
    }
    if (error.status === 409) {
      return undoing ? { text: 'This file changed since that change; undo is no longer safe.' } : { text: STALE, stale: true };
    }
    if (error.status === 400 && error.code === 'confirm-mismatch') {
      return { text: 'This review no longer matches. Review the change again.' };
    }
    if (error.status === 404) {
      return { text: undoing ? "This change can't be undone: its backup is gone." : 'This review expired. Review the change again.' };
    }
    if (error.status === 403) {
      return { text: "cfgdash refused the change (it only accepts edits from this machine's browser)." };
    }
  }
  return { text: "Couldn't save the change." };
}

type Phase = 'edit' | 'review' | 'applied' | 'undo-review' | 'undone';

interface FileEditorProps {
  file: FileContent & { edit: NonNullable<FileContent['edit']> };
  dir: string;
  /** Leaves Edit mode; `changed` when the file on disk changed. */
  onDone(changed: boolean): void;
  /** Reports whether there is an edit not applied yet. */
  onDirty(dirty: boolean): void;
}

/**
 * The file viewer's Edit mode (edits-v2): a plain textarea with line numbers, Review changes (dry-run diff), Apply
 * (the proposal's confirm) and Undo through the audit revert. It only ever holds and sends the text the server served.
 */
export default function FileEditor({ file, dir, onDone, onDirty }: FileEditorProps) {
  const store = useSnapshotStore();
  const announce = useAnnounce();
  const eol = file.content.includes('\r\n') ? '\r\n' : '\n';
  const original = file.content.replace(/\r\n/g, '\n');
  const [text, setText] = useState(original);
  const [phase, setPhase] = useState<Phase>('edit');
  const [proposal, setProposal] = useState<Proposal | null>(null);
  const [auditId, setAuditId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string; stale?: boolean } | null>(null);
  const area = useRef<HTMLTextAreaElement>(null);
  const gutter = useRef<HTMLDivElement>(null);
  const changed = useRef(false);
  const dirty = phase === 'edit' && text !== original;

  useEffect(() => onDirty(dirty), [dirty, onDirty]);
  useEffect(() => area.current?.focus(), []);

  const run = async (work: () => Promise<void>, undoing: boolean) => {
    setBusy(true);
    setMessage(null);
    try {
      await work();
    } catch (error) {
      setMessage({ ok: false, ...failure(error, undoing) });
    } finally {
      setBusy(false);
    }
  };

  const review = () =>
    run(async () => {
      if (text.includes(MARKER)) {
        setMessage({ ok: false, text: INVALID['redacted-value'] });
        return;
      }
      const next = await api.proposeFile(file.id, dir, text.replace(/\n/g, eol), file.edit.sha256 ?? '');
      setProposal(next);
      setPhase('review');
    }, false);

  const apply = (undoing: boolean) =>
    run(async () => {
      const result = await api.apply(proposal!.id, proposal!.confirm);
      changed.current = true;
      store.refresh();
      const done = `${undoing ? 'Undone' : 'Applied'}. Audit entry ${result.auditId}.`;
      setAuditId(result.auditId);
      setPhase(undoing ? 'undone' : 'applied');
      setMessage({ ok: true, text: done });
      announce(done);
    }, undoing);

  const undo = () =>
    run(async () => {
      const next = await api.revert(auditId!);
      setProposal(next);
      setPhase('undo-review');
    }, true);

  const lines = text.split('\n').length;
  const reviewing = phase === 'review' || phase === 'undo-review';

  return (
    <div className="space-y-3">
      {file.edit.gitTracked && (
        <p className="tint-warn rounded-md border border-warn px-3 py-1.5 text-sm">
          This file is tracked by git. Applying a change leaves the repository with uncommitted changes; cfgdash never commits.
        </p>
      )}
      {message && (
        <div role={message.ok ? 'status' : 'alert'} className={cn('space-y-2 text-sm font-medium', message.ok ? 'text-ok' : 'text-danger')}>
          <p>{message.text}</p>
          {message.stale && (
            <Button size="sm" onClick={() => onDone(true)}>
              Reload the file
            </Button>
          )}
        </div>
      )}

      {phase === 'edit' && (
        <>
          <div className="flex h-[60vh] rounded-md border border-border-strong bg-raised focus-within:outline-2 focus-within:outline-focus">
            <div ref={gutter} aria-hidden="true" className={cn(LINE, 'w-10 shrink-0 overflow-hidden border-r border-border py-2 pr-2 text-right text-muted select-none')}>
              {Array.from({ length: lines }, (_, i) => (
                <div key={i}>{i + 1}</div>
              ))}
            </div>
            <textarea
              ref={area}
              aria-label={`Edit ${file.path.split('/').pop() ?? file.path}`}
              value={text}
              onChange={(event) => setText(event.target.value)}
              onScroll={(event) => {
                if (gutter.current) {
                  gutter.current.scrollTop = event.currentTarget.scrollTop;
                }
              }}
              wrap="off"
              spellCheck={false}
              autoCapitalize="off"
              autoComplete="off"
              className={cn(LINE, 'h-full min-w-0 flex-1 resize-none overflow-auto bg-transparent px-2 py-2 whitespace-pre text-text outline-none')}
            />
          </div>
          <div className="flex flex-wrap gap-2">
            <Button variant="primary" disabled={busy || !dirty} aria-busy={busy} onClick={() => void review()}>
              {busy ? 'Preparing the review...' : 'Review changes'}
            </Button>
            <Button disabled={busy} onClick={() => onDone(changed.current)}>
              {dirty ? 'Discard and stop editing' : 'Stop editing'}
            </Button>
          </div>
        </>
      )}

      {reviewing && proposal && (
        <>
          <h3 className="font-semibold">{phase === 'undo-review' ? `Review undo of ${auditId}` : 'Review changes'}</h3>
          {proposal.files.map((f) => (
            <DiffCard key={f.path} file={f} scopeLabel="File" gitTracked={file.edit.gitTracked === true} />
          ))}
          <div className="flex flex-wrap gap-2">
            <Button variant="primary" disabled={busy} aria-busy={busy} onClick={() => void apply(phase === 'undo-review')}>
              {phase === 'undo-review' ? 'Apply undo' : 'Apply change'}
            </Button>
            <Button
              disabled={busy}
              onClick={() => {
                setMessage(null);
                setPhase(phase === 'undo-review' ? 'applied' : 'edit');
              }}
            >
              {phase === 'undo-review' ? 'Keep the change' : 'Back to editing'}
            </Button>
          </div>
        </>
      )}

      {(phase === 'applied' || phase === 'undone') && (
        <div className="flex flex-wrap gap-2">
          {phase === 'applied' && (
            <Button disabled={busy} aria-busy={busy} onClick={() => void undo()}>
              {busy ? 'Preparing undo...' : 'Undo'}
              <span className="sr-only"> change {auditId}</span>
            </Button>
          )}
          <Button onClick={() => onDone(true)}>Done</Button>
        </div>
      )}
    </div>
  );
}
