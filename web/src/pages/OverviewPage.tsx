import { CircleDot, Monitor } from 'lucide-react';
import { useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router';
import { StateChip } from '../blocks/Badges';
import { formatCount, formatRelative, formatTime } from '../lib/format';
import { useUrl } from '../lib/url';
import { cn } from '../lib/utils';
import { navViewId, type ViewModel } from '../lib/views';
import { useNow, useReducedMotion } from '../state/hooks';
import { useSnapshotStore } from '../state/snapshot';
import { SectionCard } from './SectionCard';
import { useReveal } from './useReveal';

const TILE = 'rounded-xl border border-border bg-surface p-4';
const TILE_LABEL = 'flex items-center gap-2 text-xs font-medium text-muted [&_svg]:size-5';

/** Previous item counts of the views whose count changed with the last refresh of folder `dir`; empty on first paint and after a folder change. */
function useChangedCounts(views: ViewModel[], refreshes: number, dir: string | undefined): Map<string, number> {
  const counts = useMemo(() => new Map(views.map((v) => [v.id, v.itemCount])), [views]);
  const previous = useRef({ refreshes, counts, dir });
  const [changed, setChanged] = useState({ dir, counts: new Map<string, number>() });
  useEffect(() => {
    const before = previous.current;
    previous.current = { refreshes, counts, dir };
    if (before.dir !== dir) {
      setChanged({ dir, counts: new Map() });
      return;
    }
    if (before.refreshes === refreshes) {
      return;
    }
    const next = new Map<string, number>();
    for (const [id, count] of counts) {
      const old = before.counts.get(id);
      if (old !== undefined && old !== count) {
        next.set(id, old);
      }
    }
    setChanged({ dir, counts: next });
  }, [refreshes, counts, dir]);
  return changed.dir === dir ? changed.counts : new Map<string, number>();
}

function Ticker({ from, to, run }: { from?: number; to: number; run: number }) {
  const reduce = useReducedMotion();
  const [value, setValue] = useState<number | null>(null);
  useEffect(() => {
    if (from === undefined || reduce) {
      return;
    }
    const start = performance.now();
    let frame = requestAnimationFrame(function step(now: number) {
      const t = Math.min(1, (now - start) / 600);
      setValue(t < 1 ? Math.round(from + (to - from) * t) : null);
      if (t < 1) {
        frame = requestAnimationFrame(step);
      }
    });
    return () => {
      cancelAnimationFrame(frame);
      setValue(null);
    };
  }, [from, to, run, reduce]);
  return <>{formatCount(value ?? to)}</>;
}

/** Overview: identity, count tiles, collectors, context paths, then the overview sections. */
export function OverviewPage() {
  const store = useSnapshotStore();
  const url = useUrl();
  const now = useNow();
  const reduce = useReducedMotion();
  const overview = store.views.find((v) => v.id === 'overview');
  const reveal = useReveal('overview', overview?.sections);
  const changed = useChangedCounts(store.views, store.refreshes, store.snapshot?.context.targetDir);
  const s = store.snapshot;
  if (!s) {
    return null;
  }

  const collectorsById = new Map(s.collectors.map((c) => [c.id, c]));
  const tiles = store.navViews.filter((v) => v.id !== 'overview');
  const active = s.collectors.filter((c) => c.state === 'active').length;
  const nonActive = s.collectors.filter((c) => c.state !== 'active');
  const collected = formatTime(s.machine.collectedAt);
  const tailnet = s.machine.tailnet;

  return (
    <>
      <header className="mb-4 flex flex-wrap items-baseline gap-x-3">
        <h1 tabIndex={-1} className="text-2xl leading-[30px] font-semibold">
          Overview
        </h1>
        <span className="text-xs font-medium text-muted">
          {s.machine.hostname} · collected {collected}
        </span>
      </header>

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <section className={cn(TILE, 'col-span-2')} aria-labelledby="identity-title">
          <h2 id="identity-title" className={TILE_LABEL}>
            <Monitor strokeWidth={1.75} aria-hidden="true" />
            Identity
          </h2>
          <p className="mono-value mt-3 flex flex-wrap gap-x-4 gap-y-1">
            <span className="font-bold">{s.machine.hostname}</span>
            <span>
              {s.machine.os.description}, {s.machine.os.arch}
            </span>
            {tailnet && (
              <span>
                tailnet {tailnet.dnsName}
                {tailnet.tags.length > 0 && ` ${tailnet.tags.join(' ')}`}
              </span>
            )}
            <span>
              Collected {collected} ({formatRelative(s.machine.collectedAt, now)})
            </span>
            <span>cfgdash {s.machine.cfgdashVersion}</span>
          </p>
        </section>

        {tiles.map((view) => {
          const Icon = view.icon;
          const from = changed.get(view.id);
          const unavailable = view.collectors.filter((c) => c.state === 'unavailable').length;
          return (
            <Link
              key={`${view.id}-${from === undefined ? 0 : store.refreshes}`}
              to={url.link(`/${view.id}`)}
              className={cn(TILE, 'block transition-colors duration-100 hover:bg-hover', from !== undefined && !reduce && 'pulse-once')}
            >
              <span className="flex items-start justify-between gap-2">
                <span className={TILE_LABEL}>
                  <Icon strokeWidth={1.75} aria-hidden="true" />
                  {view.label}
                </span>
                {from !== undefined && reduce && (
                  <span className="flex items-center gap-1 text-xs text-muted">
                    <span className="size-1.5 rounded-full bg-accent" aria-hidden="true" />
                    changed
                  </span>
                )}
              </span>
              <span className="mt-3 block text-[32px] leading-9 font-semibold tabular">
                <Ticker from={from} to={view.itemCount} run={store.refreshes} />
              </span>
              {unavailable > 0 && (
                <span className="mt-1 flex items-center gap-1.5 text-xs text-warn">
                  <span className="size-2 rounded-full bg-warn" aria-hidden="true" />
                  {unavailable} unavailable
                </span>
              )}
            </Link>
          );
        })}

        <section className={cn(TILE, 'col-span-2')} aria-labelledby="collectors-title">
          <h2 id="collectors-title" className={TILE_LABEL}>
            <CircleDot strokeWidth={1.75} aria-hidden="true" />
            Collectors
          </h2>
          <p className="mt-3">
            <span className="text-[32px] leading-9 font-semibold tabular">{active}</span>{' '}
            <span className="text-muted">of {s.collectors.length} active</span>
          </p>
          {nonActive.length > 0 && (
            <ul className="mt-2">
              {nonActive.map((c) => (
                <li key={c.id} className="flex flex-wrap items-center gap-x-3 gap-y-1 border-t border-border py-1.5">
                  <span className="mono-value">{c.title}</span>
                  <StateChip state={c.state} />
                  {c.reason && <span className="text-xs text-muted">{c.reason}</span>}
                  <Link to={url.link(`/${navViewId(c.view)}`)} className="touch-target ml-auto text-xs text-accent hover:underline">
                    View<span className="sr-only"> {c.title}</span>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>

      <section className="card mt-4 overflow-hidden rounded-lg border border-border bg-surface" aria-labelledby="paths-title">
        <h2 id="paths-title" className="border-b border-border px-4 py-2.5 text-sm font-semibold">
          Context paths
        </h2>
        <ul>
          <ContextRow name="Target folder" value={s.context.targetDir} source="selected folder" exists />
          {s.context.paths.map((p) => (
            <ContextRow key={p.key} name={p.key} value={p.value} source={p.source ?? ''} exists={p.exists} mono />
          ))}
        </ul>
      </section>

      {(overview?.sections ?? []).length > 0 && (
        <div className="mt-4 space-y-4">
          {overview?.sections.map((section) => (
            <SectionCard key={section.collectorId} section={section} collector={collectorsById.get(section.collectorId)} reveal={reveal} />
          ))}
        </div>
      )}
    </>
  );
}

function ContextRow({
  name,
  value,
  source,
  exists,
  mono = false
}: {
  name: string;
  value?: string | null;
  source: string;
  exists: boolean;
  mono?: boolean;
}) {
  return (
    <li className="grid grid-cols-1 gap-x-4 gap-y-0.5 border-b border-border px-4 py-1.5 last:border-b-0 md:grid-cols-[minmax(0,240px)_minmax(0,1fr)_minmax(0,180px)_auto] md:items-center">
      <span className={mono ? 'mono-value break-all' : 'text-sm font-medium'}>{name}</span>
      {value ? <span className="mono-value break-all">{value}</span> : <span className="text-muted">not set</span>}
      <span className="text-xs text-muted">{source}</span>
      <span>
        {!exists && (
          <span className="tint-neutral inline-flex h-[22px] items-center rounded-full border border-border-strong px-2 text-xs font-medium">
            missing
          </span>
        )}
      </span>
    </li>
  );
}
