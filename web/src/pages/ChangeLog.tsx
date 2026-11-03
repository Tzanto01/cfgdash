import { useState } from 'react';
import { Button } from '../components/ui/button';
import { formatDateTime } from '../lib/format';
import type { AuditEntry } from '../lib/models';
import { LoadingPanel } from '../shell/Shell';

const LABEL = 'tint-accent rounded-full border border-accent px-2 text-xs leading-5 font-medium text-accent';
const FIRST = 10;

interface ChangeLogProps {
  /** Entries from `GET /api/v2/audit`, newest first; null while loading. */
  entries: AuditEntry[] | null;
  failed: boolean;
  /** The entry whose undo is being prepared. */
  busyId: string | null;
  /** An undo that could not be prepared, in words, shown under its entry. */
  error: { auditId: string; text: string } | null;
  onUndo(auditId: string): void;
  onRetry(): void;
}

/** The change log of applied edits (edits-v1 audit) with an Undo per harness edit. */
export function ChangeLog({ entries, failed, busyId, error, onUndo, onRetry }: ChangeLogProps) {
  const [all, setAll] = useState(false);
  const shown = entries && !all ? entries.slice(0, FIRST) : entries;
  return (
    <section aria-labelledby="change-log-title" className="card rounded-lg border border-border bg-surface">
      <h2 id="change-log-title" className="flex h-10 items-center border-b border-border px-4 font-semibold">
        Change log
      </h2>
      {failed ? (
        <div className="space-y-2 px-4 py-3">
          <p>Couldn't load the change log.</p>
          <Button size="sm" onClick={onRetry}>
            Try again
          </Button>
        </div>
      ) : !shown ? (
        <LoadingPanel text="Reading the change log..." className="px-4 py-3" />
      ) : shown.length === 0 ? (
        <p className="px-4 py-3 text-muted">No changes applied yet.</p>
      ) : (
        <>
          <ul>
            {shown.map((entry) => (
              <li key={entry.auditId} className="space-y-1 border-b border-border px-4 py-2 last:border-b-0">
                <div className="flex flex-wrap items-center gap-2">
                  <span className="font-medium">{formatDateTime(entry.at)}</span>
                  {entry.revertOf && <span className={LABEL}>Undo of {entry.revertOf}</span>}
                  {entry.origin === 'assistant' && <span className={LABEL}>Assistant proposal</span>}
                  {entry.kind === 'cfgdash-settings' && <span className={LABEL}>cfgdash settings</span>}
                  <span className="mono-value text-muted">{entry.auditId}</span>
                  {!entry.kind && (
                    <Button size="sm" className="ml-auto" disabled={busyId !== null} aria-busy={busyId === entry.auditId} onClick={() => onUndo(entry.auditId)}>
                      {busyId === entry.auditId ? 'Preparing undo...' : 'Undo'}
                      <span className="sr-only"> change {entry.auditId}</span>
                    </Button>
                  )}
                </div>
                <ul className="mono-value text-muted">
                  {entry.changes.map((change, index) => (
                    <li key={index} className="wrap-anywhere">
                      {change.file ? `${change.action} ${entry.files[index]?.path ?? change.file}` : `${change.action} ${change.key} (${change.scope})`}
                    </li>
                  ))}
                </ul>
                {error?.auditId === entry.auditId && (
                  <p role="alert" className="text-xs font-medium text-danger">
                    {error.text}
                  </p>
                )}
              </li>
            ))}
          </ul>
          {!all && entries && entries.length > FIRST && (
            <div className="border-t border-border px-4 py-2">
              <Button size="sm" variant="ghost" onClick={() => setAll(true)}>
                Show all {entries.length} entries
              </Button>
            </div>
          )}
        </>
      )}
    </section>
  );
}
