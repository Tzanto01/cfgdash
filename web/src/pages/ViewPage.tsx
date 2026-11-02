import { Search } from 'lucide-react';
import { lazy, Suspense, useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router';
import { isFiltering, kvRowMatches, NO_FILTER, tableRowMatches, type FlagChip, type RowFilter } from '../blocks/filter';
import { Button } from '../components/ui/button';
import { formatCount, normalizeItemId } from '../lib/format';
import type { KvBlock, Section, TableBlock } from '../lib/models';
import { useUrl } from '../lib/url';
import { cn } from '../lib/utils';
import { useAnnounce } from '../state/announcer';
import { useSnapshotStore } from '../state/snapshot';
import { LoadingPanel } from '../shell/Shell';
import { useUi } from '../state/ui';
import { EditToggle } from './EditToggle';
import { SectionCard } from './SectionCard';
import { useReveal, type RevealTarget } from './useReveal';

const SettingsEditor = lazy(() => import('./SettingsEditor'));

const CHIPS: readonly { id: FlagChip; label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'overridden', label: 'Overridden' },
  { id: 'redacted', label: 'Redacted' },
  { id: 'company', label: 'Company' }
];

function filterableBlocks(sections: Section[]) {
  return sections.flatMap((section) =>
    section.blocks
      .filter((block) => block.type === 'kv' || block.type === 'table')
      .map((block) => ({ anchor: `${section.collectorId}/${block.id}`, block: block as KvBlock | TableBlock }))
  );
}

function rowCounts(sections: Section[], filter: RowFilter): { shown: number; total: number } {
  let shown = 0;
  let total = 0;
  for (const { block } of filterableBlocks(sections)) {
    total += block.rows.length;
    shown +=
      block.type === 'kv'
        ? block.rows.filter((row) => kvRowMatches(row, filter)).length
        : block.rows.filter((row) => tableRowMatches(row, filter)).length;
  }
  return { shown, total };
}

function targetHidden(sections: Section[], target: RevealTarget, filter: RowFilter): boolean {
  const found = filterableBlocks(sections).find((entry) => entry.anchor === target.blockAnchor);
  if (!found || !target.itemId) {
    return false;
  }
  const { block } = found;
  if (block.type === 'kv') {
    const row = block.rows.find((r) => normalizeItemId(r.key) === target.itemId);
    return !!row && !kvRowMatches(row, filter);
  }
  const row = block.rows.find((r) => normalizeItemId(r.id) === target.itemId);
  return !!row && !tableRowMatches(row, filter);
}

/** Generic view page for a known view id, `other`, or an unknown id ("Nothing here"). */
export function ViewPage() {
  const { view = '' } = useParams();
  const store = useSnapshotStore();
  const url = useUrl();
  const ui = useUi();
  const announce = useAnnounce();
  const model = store.views.find((v) => v.id === view);
  const sections = model?.sections;

  const [filterState, setFilterState] = useState({ view, filter: NO_FILTER });
  const filter = filterState.view === view ? filterState.filter : NO_FILTER;
  const setFilter = (next: RowFilter) => setFilterState({ view, filter: next });
  const onTarget = useCallback(
    (target: RevealTarget) =>
      setFilterState((state) =>
        state.view === view && sections && targetHidden(sections, target, state.filter) ? { view, filter: NO_FILTER } : state
      ),
    [view, sections]
  );
  const reveal = useReveal(view, sections, onTarget);

  const filtering = isFiltering(filter);
  const counts = sections ? rowCounts(sections, filter) : { shown: 0, total: 0 };
  useEffect(() => {
    if (filtering) {
      announce(`${counts.shown} of ${counts.total} rows`);
    }
  }, [announce, filtering, counts.shown, counts.total]);

  if (view === 'settings' && url.edit) {
    return (
      <Suspense fallback={<LoadingPanel text="Loading settings..." />}>
        <SettingsEditor />
      </Suspense>
    );
  }

  if (!model || !sections) {
    return (
      <>
        <header className="mb-6">
          <h1 tabIndex={-1} className="text-2xl leading-[30px] font-semibold">
            Nothing here
          </h1>
          <p className="text-muted">There is no view called "{view}".</p>
        </header>
        <p>
          <Link to={url.link('/overview')} className="text-accent underline-offset-2 hover:underline">
            Go to the overview
          </Link>
        </p>
      </>
    );
  }

  const collectorsById = new Map((store.snapshot?.collectors ?? []).map((c) => [c.id, c]));
  const needsFolder = model.collectors.some((c) => c.requirements?.targetDir);
  const unavailable = model.collectors.filter((c) => c.state === 'unavailable');
  const notPresent = model.collectors.filter((c) => c.state === 'notPresent');
  const groups: { viewId: string; sections: Section[] }[] = [];
  if (model.id !== 'other') {
    groups.push({ viewId: model.id, sections });
  } else {
    const byView = new Map<string, Section[]>();
    for (const collector of model.collectors) {
      byView.set(collector.view, byView.get(collector.view) ?? []);
    }
    for (const section of sections) {
      byView.set(section.view, [...(byView.get(section.view) ?? []), section]);
    }
    groups.push(...[...byView].map(([viewId, list]) => ({ viewId, sections: list })));
  }
  const hasToolbar = filterableBlocks(sections).length > 0;

  return (
    <>
      <header className="mb-4">
        <div className="flex flex-wrap items-baseline gap-x-3">
          <h1 tabIndex={-1} className="text-2xl leading-[30px] font-semibold">
            {model.label}
          </h1>
          <span className="text-xs font-medium text-muted tabular">
            {formatCount(model.itemCount)} {model.itemCount === 1 ? 'item' : 'items'}
          </span>
          {model.id === 'settings' && (
            <span className="ml-auto self-center">
              <EditToggle />
            </span>
          )}
        </div>
        <p className="mt-1 text-muted">{model.description}</p>
        {needsFolder && (
          <div className="mt-3 flex flex-wrap items-center gap-x-3 gap-y-2">
            <span className="text-xs font-medium text-muted">Folder</span>
            <span className="mono-value min-w-0 break-all">{store.snapshot?.context.targetDir}</span>
            <Button size="sm" onClick={() => ui.setPickerOpen(true)}>
              Change folder
            </Button>
          </div>
        )}
      </header>

      {hasToolbar && (
        <div className="mb-4 flex flex-wrap items-center gap-2">
          <div className="relative w-full sm:w-64">
            <Search className="pointer-events-none absolute top-1/2 left-2 z-10 size-4 -translate-y-1/2 text-muted" strokeWidth={1.75} aria-hidden="true" />
            <input
              type="search"
              aria-label="Filter rows"
              placeholder="Filter"
              autoComplete="off"
              spellCheck={false}
              value={filter.text}
              onChange={(event) => setFilter({ ...filter, text: event.target.value })}
              className="touch-target h-7 w-full rounded-md border border-border-strong bg-raised pr-2 pl-8 text-sm placeholder:text-muted"
            />
          </div>
          <div role="group" aria-label="Show rows" className="flex flex-wrap gap-1">
            {CHIPS.map((chip) => {
              const pressed = filter.chip === chip.id;
              return (
                <button
                  key={chip.id}
                  type="button"
                  aria-pressed={pressed}
                  onClick={() => setFilter({ ...filter, chip: pressed ? 'all' : chip.id })}
                  className={cn(
                    'touch-target h-7 rounded-md border px-2.5 text-sm transition-colors duration-100',
                    pressed ? 'tint-accent border-accent text-accent' : 'border-border bg-surface hover:bg-hover'
                  )}
                >
                  {chip.label}
                </button>
              );
            })}
          </div>
          {filtering && (
            <p className="text-xs text-muted tabular">
              {counts.shown} of {counts.total} rows
            </p>
          )}
        </div>
      )}

      {unavailable.length > 0 && (
        <ul className="mb-4 space-y-1">
          {unavailable.map((c) => (
            <li key={c.id} className="tint-warn flex items-center gap-2 rounded-lg border border-warn px-3 py-2 text-warn">
              <span className="size-2 shrink-0 rounded-full bg-warn" aria-hidden="true" />
              {c.title}: unavailable{c.reason ? ` (${c.reason})` : ''}
            </li>
          ))}
        </ul>
      )}

      {sections.length ? (
        <div className="space-y-4">
          {groups.map((group) => (
            <div key={group.viewId} className="space-y-4">
              {model.id === 'other' && (
                <>
                  <h2 className="pt-2 font-mono text-sm font-semibold">{group.viewId}</h2>
                  {!group.sections.length && <p className="text-muted">Nothing to show for this view.</p>}
                </>
              )}
              {group.sections.map((section) => (
                <SectionCard
                  key={section.collectorId}
                  section={section}
                  collector={collectorsById.get(section.collectorId)}
                  headingLevel={model.id === 'other' ? 3 : 2}
                  reveal={reveal}
                  filter={filter}
                />
              ))}
            </div>
          ))}
        </div>
      ) : (
        <p className="text-muted">Nothing to show for this view.</p>
      )}

      {notPresent.length > 0 && (
        <p className="mt-6 text-muted">
          Not on this machine: {notPresent.map((c) => (c.reason ? `${c.title} (${c.reason})` : c.title)).join(', ')}
        </p>
      )}
    </>
  );
}
