import { Copy } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { Badges } from '../blocks/Badges';
import { Button } from '../components/ui/button';
import { Sheet, SheetContent } from '../components/ui/sheet';
import { api, classifyError, isAbort } from '../lib/api';
import { formatBytes, formatTokens } from '../lib/format';
import type { DetailContent, FileContent } from '../lib/models';
import { useUrl } from '../lib/url';
import { cn } from '../lib/utils';
import { useSnapshotStore, type RequestState } from '../state/snapshot';
import { LoadingPanel } from './Shell';
import { AddToContext } from './AddToContext';
import { useReturnFocus } from './surface';

type Content = FileContent | DetailContent;

function splitLines(content: string): string[] {
  const lines = content.split(/\r?\n/);
  if (lines.length > 1 && lines[lines.length - 1] === '') {
    lines.pop();
  }
  return lines;
}

/** Right-side file and detail viewer driven by the `file`, `detail` and `line` query params (`file` wins). */
export default function FileSheet() {
  const url = useUrl();
  const store = useSnapshotStore();
  const focus = useReturnFocus();
  const heading = useRef<HTMLHeadingElement>(null);
  const body = useRef<HTMLDivElement>(null);
  const copyTimer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const [attempt, setAttempt] = useState(0);
  const [result, setResult] = useState<{ key: string; state: RequestState<Content> } | null>(null);
  const [wrap, setWrap] = useState(true);
  const [copied, setCopied] = useState('');

  const fileId = url.file;
  const detailId = fileId ? undefined : url.detail;
  const dir = url.dir;
  const noun = detailId ? 'detail' : 'file';
  const key = JSON.stringify([fileId, detailId, dir, attempt]);
  const state: RequestState<Content> = result?.key === key ? result.state : { phase: 'loading' };

  useEffect(() => {
    const controller = new AbortController();
    const request: Promise<Content> | null = fileId ? api.file(fileId, dir, controller.signal) : detailId ? api.detail(detailId, dir, controller.signal) : null;
    setCopied('');
    request?.then(
      (value) => {
        if (!controller.signal.aborted) {
          setResult({ key, state: { phase: 'content', value } });
        }
      },
      (error: unknown) => {
        if (!controller.signal.aborted && !isAbort(error)) {
          setResult({ key, state: { phase: 'error', kind: classifyError(error) } });
        }
      }
    );
    return () => controller.abort();
  }, [fileId, detailId, dir, key]);

  useEffect(() => () => clearTimeout(copyTimer.current), []);

  const value = state.phase === 'content' ? state.value : undefined;
  const lines = value ? splitLines(value.content) : [];
  const ref = store.file(fileId);
  const path = value && 'path' in value ? value.path : (ref?.path ?? '');
  const name = value && 'title' in value ? value.title : path.split('/').pop() || fileId || detailId || 'File';
  const kind = value && 'kind' in value ? value.kind : ref?.kind;
  const line = url.line;
  const hasLines = lines.length > 0;

  useEffect(() => {
    if (hasLines && line) {
      body.current?.querySelector(`[data-line="${line}"]`)?.scrollIntoView({ block: 'center' });
    }
  }, [hasLines, line, value]);

  const showCopyFeedback = (text: string) => {
    setCopied(text);
    clearTimeout(copyTimer.current);
    copyTimer.current = setTimeout(() => setCopied(''), 2000);
  };

  const copyPath = () => {
    Promise.resolve()
      .then(() => navigator.clipboard.writeText(path))
      .then(
        () => showCopyFeedback('Copied'),
        () => showCopyFeedback("Couldn't copy")
      );
  };

  return (
    <Sheet
      open
      onOpenChange={(open) => {
        if (!open) {
          url.closeFile();
        }
      }}
    >
      <SheetContent
        side="right"
        aria-labelledby="file-viewer-title"
        aria-describedby={undefined}
        closeLabel={`Close ${noun}`}
        onOpenAutoFocus={(event) => {
          event.preventDefault();
          heading.current?.focus();
        }}
        onCloseAutoFocus={focus.onCloseAutoFocus}
      >
        <div className="flex items-start gap-3 border-b border-border p-4 pr-12">
          <div className="min-w-0 flex-1 space-y-1">
            <h2 id="file-viewer-title" ref={heading} tabIndex={-1} className="font-mono text-sm font-semibold break-all">
              {name}
            </h2>
            {!detailId && <p className="mono-value break-all text-muted">{path}</p>}
            <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted tabular">
              {kind && <span>{kind}</span>}
              {ref && <span>{formatBytes(ref.sizeBytes)}</span>}
              {ref && <span>{formatTokens(ref.tokens)}</span>}
              {ref?.company && <Badges items={['company']} />}
            </div>
          </div>
          {!detailId && (
            <>
              <Button variant="ghost" size="sm" disabled={!path} onClick={copyPath}>
                <Copy strokeWidth={1.75} aria-hidden="true" />
                {copied || 'Copy path'}
              </Button>
              {fileId && <AddToContext item={{ ref: { kind: 'file', fileId }, label: path.split('/').pop() || fileId }} />}
              <span className="sr-only" aria-live="polite">
                {copied}
              </span>
            </>
          )}
        </div>
        <div ref={body} className="min-h-0 flex-1 overflow-y-auto p-4">
          {state.phase === 'loading' && <LoadingPanel text={`Loading the ${noun}...`} className="py-6" />}
          {state.phase === 'error' &&
            (state.kind === 'not-found' ? (
              <p>This {noun} isn't available for the selected folder.</p>
            ) : (
              <div className="space-y-3">
                <p>Couldn't load the {noun}.</p>
                <Button onClick={() => setAttempt(attempt + 1)}>Try again</Button>
              </div>
            ))}
          {state.phase === 'content' && (
            <>
              <div className="mb-3 flex flex-wrap items-center gap-3">
                {state.value.truncated && (
                  <p className="tint-warn min-w-0 flex-1 rounded-md border border-warn px-3 py-1.5">
                    Showing the first 512 KB; the rest of the {noun} is cut off.
                  </p>
                )}
                <Button size="sm" className="ml-auto" aria-pressed={wrap} onClick={() => setWrap(!wrap)}>
                  Wrap lines
                </Button>
              </div>
              <ol
                aria-label={detailId ? 'Detail content' : 'File content'}
                tabIndex={0}
                className={cn(
                  'mono-value list-decimal pl-10 marker:text-muted',
                  wrap ? 'break-words whitespace-pre-wrap' : 'overflow-x-auto whitespace-pre'
                )}
              >
                {lines.map((text, index) => (
                  <li key={index} data-line={index + 1} className={cn('min-h-[18px] pl-2', line === index + 1 && 'tint-accent')}>
                    {text}
                  </li>
                ))}
              </ol>
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}
