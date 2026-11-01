import { Command } from 'cmdk';
import { FileText, Folder, FolderSearch, ListTree, Palette, RefreshCw, Search, type LucideIcon } from 'lucide-react';
import { m } from 'motion/react';
import { useEffect, useState } from 'react';
import { useNavigate } from 'react-router';
import { JsonValue } from '../blocks/JsonValue';
import { Lineage } from '../blocks/Lineage';
import { Dialog, DialogContent, DialogTitle } from '../components/ui/dialog';
import { SPRING } from '../components/ui/sheet';
import { api, isAbort } from '../lib/api';
import type { DirSuggestion, SearchHit, Snapshot } from '../lib/models';
import { mergeSearch, useUrl } from '../lib/url';
import { Highlight, hitHref, hitKvRow, hitLocation } from '../pages/hits';
import { useNarrow } from '../state/hooks';
import { useSnapshotStore } from '../state/snapshot';
import { nextTheme, setTheme, useTheme } from '../state/theme';
import { useUi } from '../state/ui';
import { useChooseFolder, useReturnFocus } from './surface';

interface Entry {
  value: string;
  label: string;
  detail?: string;
  description: string;
  icon: LucideIcon;
  run(): void;
}

const TOP_HITS = 8;
const ITEM =
  'touch-row flex min-h-9 cursor-pointer items-center gap-2 rounded-lg px-2 py-1.5 data-[selected=true]:tint-accent [&_svg]:size-4 [&_svg]:shrink-0 [&_svg]:text-muted';
const GROUP =
  'px-2 pb-1 [&_[cmdk-group-heading]]:px-2 [&_[cmdk-group-heading]]:pt-2 [&_[cmdk-group-heading]]:pb-1 [&_[cmdk-group-heading]]:text-xs [&_[cmdk-group-heading]]:font-medium [&_[cmdk-group-heading]]:text-muted';

function matches(text: string, needle: string): boolean {
  return !needle || text.toLowerCase().includes(needle);
}

function HitPreview({ hit, snapshot, query }: { hit: SearchHit; snapshot: Snapshot | null; query: string }) {
  const row = hitKvRow(snapshot, hit);
  return (
    <div className="space-y-3">
      <p className="text-xs text-muted">{hitLocation(snapshot, hit)}</p>
      {row?.redacted ? (
        <JsonValue redacted />
      ) : row ? (
        <>
          <div className="mono-value break-words">
            <JsonValue value={row.value} />
          </div>
          <Lineage contributions={row.contributions} route />
        </>
      ) : (
        <p className="mono-value break-words">
          <Highlight text={hit.snippet} query={query} />
        </p>
      )}
    </div>
  );
}

/** Command palette opened by the `paletteOpen` flag: views, folders, search results and actions, with a preview pane. */
export default function CommandPalette() {
  const ui = useUi();
  const store = useSnapshotStore();
  const url = useUrl();
  const navigate = useNavigate();
  const mode = useTheme();
  const narrow = useNarrow();
  const focus = useReturnFocus();
  const chooseFolder = useChooseFolder();
  const [input, setInput] = useState('');
  const [selected, setSelected] = useState('');
  const [folders, setFolders] = useState<DirSuggestion[]>([]);
  const [found, setFound] = useState<{ query: string; hits: SearchHit[]; failed: boolean } | null>(null);
  const query = input.trim();
  const needle = query.toLowerCase();
  const dir = url.dir;

  useEffect(() => {
    const controller = new AbortController();
    api.dirs(undefined, controller.signal).then(
      (value) => {
        if (!controller.signal.aborted) {
          setFolders(value.suggestions);
        }
      },
      () => undefined
    );
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (query.length < 2) {
      return;
    }
    const controller = new AbortController();
    const timer = setTimeout(() => {
      api.search(query, dir, controller.signal).then(
        (value) => {
          if (!controller.signal.aborted) {
            setFound({ query, hits: value.results, failed: false });
          }
        },
        (error: unknown) => {
          if (!controller.signal.aborted && !isAbort(error)) {
            setFound({ query, hits: [], failed: true });
          }
        }
      );
    }, 200);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [query, dir]);

  const close = () => ui.setPaletteOpen(false);
  const go = (to: string) => {
    focus.handOff(false);
    close();
    void navigate(to);
  };

  const next = nextTheme(mode);
  const views: Entry[] = store.navViews
    .filter((view) => matches(view.label, needle))
    .map((view) => ({
      value: `view:${view.id}`,
      label: view.label,
      description: view.description,
      icon: view.icon,
      run: () => (url.path === `/${view.id}` ? close() : go(url.link(`/${view.id}`)))
    }));
  const folderEntries: Entry[] = [
    ...folders
      .map((folder, index) => ({ folder, index }))
      .filter(({ folder }) => matches(folder.label, needle) || matches(folder.path, needle))
      .map(({ folder, index }) => ({
        value: `folder:${index}`,
        label: folder.label,
        detail: folder.path,
        description: `Inspect ${folder.path}.`,
        icon: Folder,
        run: () => {
          if (chooseFolder(folder.path)) {
            focus.handOff(false);
          }
          close();
        }
      })),
    ...(matches('Choose a folder...', needle)
      ? [
          {
            value: 'folder:choose',
            label: 'Choose a folder...',
            description: 'Browse the folders on this machine and pick the one to inspect.',
            icon: FolderSearch,
            run: () => {
              focus.handOff(true);
              close();
              ui.setPickerOpen(true);
            }
          }
        ]
      : [])
  ];
  const actions: Entry[] = [
    {
      value: 'action:refresh',
      label: 'Refresh',
      description: 'Collect the snapshot again.',
      icon: RefreshCw,
      run: () => {
        close();
        store.refresh();
      }
    },
    {
      value: 'action:theme',
      label: `Switch theme to ${next}`,
      description: `The theme is ${mode} now.`,
      icon: Palette,
      run: () => {
        close();
        setTheme(next);
      }
    }
  ].filter((action) => matches(action.label, needle));

  const searching = query.length >= 2;
  const current = searching && found?.query === query ? found : null;
  const hits = current?.hits.slice(0, TOP_HITS) ?? [];
  const openHit = (hit: SearchHit) => {
    if (hit.kind === 'file' && hit.fileId) {
      focus.handOff(true);
      close();
      url.openFile(hit.fileId, hit.line);
    } else {
      go(hitHref(hit, dir));
    }
  };

  const local = [...views, ...folderEntries, ...actions];
  const selectedEntry = local.find((entry) => entry.value === selected);
  const selectedHit = selected.startsWith('hit:') ? hits[Number(selected.slice(4))] : undefined;

  const renderEntry = (entry: Entry) => (
    <Command.Item key={entry.value} value={entry.value} onSelect={entry.run} className={ITEM}>
      <entry.icon strokeWidth={1.75} aria-hidden="true" />
      <span className="truncate">{entry.label}</span>
      {entry.detail && <span className="mono-value min-w-0 truncate text-muted">{entry.detail}</span>}
    </Command.Item>
  );

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
        aria-describedby={undefined}
        onCloseAutoFocus={focus.onCloseAutoFocus}
        className="top-[12vh] max-h-[76vh] w-[min(640px,calc(100%-2rem))] translate-y-0 overflow-hidden"
        asChild
      >
        <m.div initial={{ y: 4, opacity: 0 }} animate={{ y: 0, opacity: 1 }} transition={SPRING}>
          <DialogTitle className="sr-only">Search cfgdash</DialogTitle>
          <Command shouldFilter={false} loop label="Search cfgdash" value={selected} onValueChange={setSelected} className="flex min-h-0 flex-1 flex-col">
            <div className="flex items-center gap-2 border-b border-border px-4 focus-within:shadow-[inset_0_-2px_var(--focus)]">
              <Search className="size-4 shrink-0 text-muted" strokeWidth={1.75} aria-hidden="true" />
              <Command.Input
                aria-label="Search cfgdash"
                placeholder="Search views, folders, settings..."
                value={input}
                onValueChange={setInput}
                className="h-[52px] min-w-0 flex-1 bg-transparent text-[15px] placeholder:text-muted focus-visible:outline-hidden"
              />
            </div>
            <div className="flex min-h-0 flex-1">
              <Command.List className="min-h-0 flex-1 overflow-y-auto py-1">
                {!searching && <Command.Empty className="px-4 py-6 text-muted">No matches.</Command.Empty>}
                {views.length > 0 && (
                  <Command.Group heading="Views" className={GROUP}>
                    {views.map(renderEntry)}
                  </Command.Group>
                )}
                {folderEntries.length > 0 && (
                  <Command.Group heading="Folders" className={GROUP}>
                    {folderEntries.map(renderEntry)}
                  </Command.Group>
                )}
                {searching && (
                  <Command.Group heading="Search results" className={GROUP}>
                    {!current && (
                      <Command.Loading label="Searching" className="px-2 py-1.5 text-muted">
                        Searching...
                      </Command.Loading>
                    )}
                    {current?.failed && <p className="px-2 py-1.5 text-muted">Couldn't search.</p>}
                    {current && !current.failed && !hits.length && <p className="px-2 py-1.5 text-muted">No results for "{query}".</p>}
                    {hits.map((hit, index) => (
                      <Command.Item key={`hit:${index}`} value={`hit:${index}`} onSelect={() => openHit(hit)} className={ITEM}>
                        {hit.kind === 'file' ? <FileText strokeWidth={1.75} aria-hidden="true" /> : <ListTree strokeWidth={1.75} aria-hidden="true" />}
                        <span className="min-w-0 flex-1">
                          <span className="block truncate text-xs text-muted">{hitLocation(store.snapshot, hit)}</span>
                          <span className="mono-value block truncate">
                            <Highlight text={hit.snippet} query={query} />
                          </span>
                        </span>
                      </Command.Item>
                    ))}
                    {hits.length > 0 && (
                      <Command.Item
                        value="search:all"
                        onSelect={() => go(`/search${mergeSearch('', { dir, q: query })}`)}
                        className={ITEM}
                      >
                        <Search strokeWidth={1.75} aria-hidden="true" />
                        Show all results
                      </Command.Item>
                    )}
                  </Command.Group>
                )}
                {actions.length > 0 && (
                  <Command.Group heading="Actions" className={GROUP}>
                    {actions.map(renderEntry)}
                  </Command.Group>
                )}
              </Command.List>
              {!narrow && (
                <aside aria-label="Preview" className="w-60 shrink-0 overflow-y-auto border-l border-border bg-surface p-4 text-sm">
                  {selectedEntry && <p className="text-muted">{selectedEntry.description}</p>}
                  {selected === 'search:all' && <p className="text-muted">Open the search page with every result for "{query}".</p>}
                  {selectedHit && <HitPreview hit={selectedHit} snapshot={store.snapshot} query={query} />}
                </aside>
              )}
            </div>
            <div className="border-t border-border px-4 py-2 font-mono text-[11px] text-muted">
              ↑↓ move, ↵ open, esc close
            </div>
          </Command>
        </m.div>
      </DialogContent>
    </Dialog>
  );
}
