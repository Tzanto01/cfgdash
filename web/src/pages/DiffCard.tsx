import { ChevronDown } from 'lucide-react';
import { useId, useState } from 'react';
import { showHidden } from '../lib/markdown';
import type { ProposalFile } from '../lib/models';
import { cn } from '../lib/utils';

const REFORMAT = '# cfgdash: file will be reformatted';
const HUNK = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/;
const GUTTER = 'w-10 shrink-0 pr-2 text-right text-muted select-none';

interface DiffLine {
  kind: 'hunk' | 'note' | 'add' | 'del' | 'ctx';
  text: string;
  before?: number;
  after?: number;
}

function parseDiff(diff: string) {
  const lines: DiffLine[] = [];
  let reformat = false;
  let newFile = false;
  let inHunk = false;
  let before = 0;
  let after = 0;
  let added = 0;
  let removed = 0;
  for (const [index, raw] of diff.replace(/\n$/, '').split('\n').entries()) {
    const line = raw.replace(/\r$/, '');
    const hunk = HUNK.exec(line);
    if (index === 0 && line.startsWith(REFORMAT)) {
      reformat = true;
    } else if (hunk) {
      inHunk = true;
      before = Number(hunk[1]);
      after = Number(hunk[2]);
      lines.push({ kind: 'hunk', text: line });
    } else if (!inHunk) {
      newFile ||= line === '--- /dev/null';
    } else if (line.startsWith('+')) {
      added++;
      lines.push({ kind: 'add', text: line.slice(1), after: after++ });
    } else if (line.startsWith('-')) {
      removed++;
      lines.push({ kind: 'del', text: line.slice(1), before: before++ });
    } else if (line.startsWith('\\')) {
      lines.push({ kind: 'note', text: line });
    } else {
      lines.push({ kind: 'ctx', text: line.slice(1), before: before++, after: after++ });
    }
  }
  return { lines, reformat, newFile, added, removed };
}

/** One proposal file of the review: sticky header (path, scope, dry run, counts) and the parsed unified diff. */
export function DiffCard({ file, scopeLabel, gitTracked }: { file: ProposalFile; scopeLabel: string; gitTracked: boolean }) {
  const [open, setOpen] = useState(true);
  const bodyId = useId();
  const diff = parseDiff(file.diff);
  const newFile = diff.newFile || file.sha256Before === null;

  return (
    <section aria-label={`Changes to ${file.path}`} className="card rounded-lg border border-border bg-raised">
      <header
        className={cn(
          'sticky top-12 z-10 flex md:top-[52px] min-h-10 flex-wrap items-center gap-x-2 gap-y-1 bg-raised px-3 py-1.5',
          open ? 'rounded-t-lg border-b border-border' : 'rounded-lg'
        )}
      >
        <span className="mono-value min-w-0 flex-1 truncate" title={file.path}>
          {file.path}
        </span>
        <span className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs">
          <span className="rounded-full border border-border px-2 leading-5">{scopeLabel}</span>
          {newFile && <span className="text-muted">new file</span>}
          <span className="text-muted">dry run</span>
          {gitTracked && <span className="text-warn">tracked by git</span>}
          <span className="tabular">
            <span aria-hidden="true">
              <span className="text-ok">+{diff.added}</span> <span className="text-danger">−{diff.removed}</span>
            </span>
            <span className="sr-only">
              {diff.added} added, {diff.removed} removed
            </span>
          </span>
          <button
            type="button"
            aria-expanded={open}
            aria-controls={bodyId}
            aria-label={`Diff of ${file.path}`}
            onClick={() => setOpen(!open)}
            className="touch-target inline-flex size-7 items-center justify-center rounded-md hover:bg-hover"
          >
            <ChevronDown className={cn('size-4 transition-transform duration-100', !open && '-rotate-90')} strokeWidth={1.75} aria-hidden="true" />
          </button>
        </span>
      </header>
      <div id={bodyId} hidden={!open}>
        {diff.reformat && (
          <p className="border-b border-border px-3 py-2 text-xs text-muted">cfgdash will reformat this file: key order kept, 2-space indentation</p>
        )}
        <div className="overflow-x-auto">
          <div className="mono-value inline-block min-w-full py-1">
            {diff.lines.map((line, index) =>
              line.kind === 'hunk' || line.kind === 'note' ? (
                <div key={index} className="px-3 whitespace-pre text-muted">
                  {showHidden(line.text)}
                </div>
              ) : (
                <div key={index} className={cn('flex', line.kind === 'add' && 'tint-ok', line.kind === 'del' && 'tint-danger')}>
                  <span aria-hidden="true" className={GUTTER}>
                    {line.before}
                  </span>
                  <span aria-hidden="true" className={GUTTER}>
                    {line.after}
                  </span>
                  <span aria-hidden="true" className="w-4 shrink-0 text-center select-none">
                    {line.kind === 'add' ? '+' : line.kind === 'del' ? '-' : ' '}
                  </span>
                  <span className="pr-4 whitespace-pre text-text">
                    {line.kind !== 'ctx' && <span className="sr-only">{line.kind === 'add' ? 'added ' : 'removed '}</span>}
                    {showHidden(line.text)}
                  </span>
                </div>
              )
            )}
          </div>
        </div>
      </div>
    </section>
  );
}
