import { Folder, FolderUp, X } from 'lucide-react';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { Button } from '../components/ui/button';
import { Dialog, DialogClose, DialogContent } from '../components/ui/dialog';
import { api, classifyError, isAbort, type ErrorKind } from '../lib/api';
import type { DirsResponse } from '../lib/models';
import { cn } from '../lib/utils';
import { useSnapshotStore } from '../state/snapshot';
import { useUi } from '../state/ui';
import { LoadingPanel } from './Shell';
import { useChooseFolder, useReturnFocus } from './surface';

const FIELD_ERROR = "That folder doesn't exist or isn't an absolute path.";
const ITEM = 'touch-target flex min-h-9 w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left hover:bg-hover';
const PART_TITLE = 'mb-1 text-xs font-medium text-muted';

/** Centered folder picker opened by the `pickerOpen` flag; browsing never changes `dir` until "Use this folder". */
export default function FolderPicker() {
  const ui = useUi();
  const store = useSnapshotStore();
  const focus = useReturnFocus();
  const chooseFolder = useChooseFolder();
  const heading = useRef<HTMLHeadingElement>(null);
  const controller = useRef<AbortController | null>(null);
  const last = useRef<{ path?: string; fromField: boolean }>({ fromField: false });
  const [start] = useState(store.snapshot?.context.targetDir);
  const [typed, setTyped] = useState(start ?? '');
  const [fieldError, setFieldError] = useState(false);
  const [filter, setFilter] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<ErrorKind | null>(null);
  const [current, setCurrent] = useState<DirsResponse | null>(null);

  const close = () => ui.setPickerOpen(false);

  const choose = (path: string) => {
    if (chooseFolder(path)) {
      focus.handOff(false);
    }
    close();
  };

  const browse = (path: string | undefined, fromField = false, thenUse = false) => {
    controller.current?.abort();
    const request = new AbortController();
    controller.current = request;
    last.current = { path, fromField };
    setLoading(true);
    setError(null);
    api.dirs(path, request.signal).then(
      (value) => {
        if (request.signal.aborted) {
          return;
        }
        setCurrent(value);
        setTyped(value.path);
        setFilter('');
        setFieldError(false);
        setLoading(false);
        if (thenUse) {
          choose(value.path);
        }
      },
      (failure: unknown) => {
        if (request.signal.aborted || isAbort(failure)) {
          return;
        }
        setLoading(false);
        const kind = classifyError(failure);
        if (kind === 'bad-dir' && fromField) {
          setFieldError(true);
        } else if (kind === 'bad-dir' && path) {
          browse(undefined);
        } else {
          setError(kind);
        }
      }
    );
  };

  useEffect(() => {
    browse(start);
    return () => controller.current?.abort();
  }, []);

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    const path = typed.trim();
    if (!path) {
      setFieldError(true);
      return;
    }
    browse(path, true);
  };

  const use = () => {
    const path = typed.trim();
    if (path && path !== current?.path) {
      browse(path, true, true);
    } else if (current) {
      choose(current.path);
    }
  };

  const needle = filter.trim().toLowerCase();
  const entries = (current?.entries ?? []).filter((e) => !needle || e.name.toLowerCase().includes(needle));

  return (
    <Dialog
      open
      onOpenChange={(open) => {
        if (!open) {
          close();
        }
      }}
    >
      <DialogContent
        aria-labelledby="folder-picker-title"
        aria-describedby={undefined}
        onOpenAutoFocus={(event) => {
          event.preventDefault();
          heading.current?.focus();
        }}
        onCloseAutoFocus={focus.onCloseAutoFocus}
      >
        <div className="flex items-center gap-3 border-b border-border px-4 py-3">
          <h2 id="folder-picker-title" ref={heading} tabIndex={-1} className="flex-1 font-semibold">
            Choose a folder
          </h2>
          <DialogClose asChild>
            <Button variant="ghost" size="icon" aria-label="Close">
              <X strokeWidth={1.75} aria-hidden="true" />
            </Button>
          </DialogClose>
        </div>

        <div className="min-h-0 flex-1 space-y-4 overflow-y-auto p-4">
          <form onSubmit={onSubmit} className="space-y-1">
            <label htmlFor="folder-path" className="block text-xs font-medium text-muted">
              Folder path
            </label>
            <div className="flex gap-2">
              <input
                id="folder-path"
                type="text"
                autoComplete="off"
                spellCheck={false}
                value={typed}
                onChange={(event) => {
                  setTyped(event.target.value);
                  setFieldError(false);
                }}
                aria-invalid={fieldError || undefined}
                aria-describedby={fieldError ? 'folder-path-error' : undefined}
                className={cn(
                  'touch-target mono-value h-8 min-w-0 flex-1 rounded-md border bg-raised px-2.5',
                  fieldError ? 'border-danger' : 'border-border-strong'
                )}
              />
              <Button type="submit">Open</Button>
            </div>
            {fieldError && (
              <p id="folder-path-error" className="text-sm text-danger">
                {FIELD_ERROR}
              </p>
            )}
          </form>

          {!!current?.suggestions.length && (
            <section aria-labelledby="picker-suggestions-title">
              <h3 id="picker-suggestions-title" className={PART_TITLE}>
                Suggestions
              </h3>
              <ul>
                {current.suggestions.map((s, index) => (
                  <li key={index}>
                    <button type="button" className={cn(ITEM, 'flex-wrap gap-x-2 gap-y-0')} onClick={() => browse(s.path)}>
                      <span>{s.label}</span>
                      <span className="mono-value break-all text-muted">{s.path}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </section>
          )}

          <section aria-labelledby="picker-listing-title">
            <h3 id="picker-listing-title" className={PART_TITLE}>
              Folders in <span className="font-mono break-all">{current?.path ?? '...'}</span>
            </h3>
            {loading ? (
              <LoadingPanel text="Loading folders..." className="py-4" />
            ) : error ? (
              <div className="space-y-3 py-2">
                {error === 'unreachable' ? (
                  <p>
                    cfgdash isn't answering. It may have stopped; start it again with <code className="font-mono">cfgdash</code>.
                  </p>
                ) : (
                  <p>Couldn't list this folder.</p>
                )}
                <Button onClick={() => browse(last.current.path, last.current.fromField)}>Try again</Button>
              </div>
            ) : (
              current && (
                <>
                  {current.entries.length > 20 && (
                    <div className="mb-2 space-y-1">
                      <label htmlFor="folder-filter" className="block text-xs font-medium text-muted">
                        Filter folders
                      </label>
                      <input
                        id="folder-filter"
                        type="text"
                        autoComplete="off"
                        value={filter}
                        onChange={(event) => setFilter(event.target.value)}
                        className="touch-target h-8 w-full rounded-md border border-border-strong bg-raised px-2.5"
                      />
                    </div>
                  )}
                  <ul>
                    {current.parent && (
                      <li>
                        <button type="button" className={ITEM} onClick={() => browse(current.parent ?? undefined)}>
                          <FolderUp className="size-4 shrink-0" strokeWidth={1.75} aria-hidden="true" />
                          <span>Up one level</span>
                        </button>
                      </li>
                    )}
                    {entries.map((e) => (
                      <li key={e.path}>
                        <button type="button" className={cn(ITEM, e.name.startsWith('.') && 'text-muted')} onClick={() => browse(e.path)}>
                          <Folder className="size-4 shrink-0" strokeWidth={1.75} aria-hidden="true" />
                          <span className="mono-value break-all">{e.name}</span>
                        </button>
                      </li>
                    ))}
                  </ul>
                  {!current.entries.length ? (
                    <p className="text-muted">No subfolders.</p>
                  ) : (
                    !entries.length && <p className="text-muted">No folders match the filter.</p>
                  )}
                </>
              )
            )}
          </section>
        </div>

        <div className="flex justify-end gap-2 border-t border-border px-4 py-3">
          <Button variant="ghost" onClick={close}>
            Cancel
          </Button>
          <Button variant="primary" disabled={!current || loading} onClick={use}>
            Use this folder
          </Button>
        </div>
      </DialogContent>
    </Dialog>
  );
}
