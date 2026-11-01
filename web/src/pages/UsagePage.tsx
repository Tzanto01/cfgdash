import { useEffect, type ReactNode } from 'react';
import { Block } from '../blocks/Block';
import { JsonValue } from '../blocks/JsonValue';
import { formatCount, itemDomId } from '../lib/format';
import type { Block as BlockModel, KvBlock, KvRow, Section, TableBlock } from '../lib/models';
import { focusPendingHeading } from '../shell/Shell';
import { useSnapshotStore } from '../state/snapshot';
import { dailyCosts, formatUsd, UsageChart } from './UsageChart';
import { useReveal, type RevealTarget } from './useReveal';

const TILES: readonly { key: string; label: string; kind: 'usd' | 'compact' | 'count' }[] = [
  { key: 'cost-today', label: 'Cost today', kind: 'usd' },
  { key: 'cost-7d', label: 'Cost, 7 days', kind: 'usd' },
  { key: 'cost-30d', label: 'Cost, 30 days', kind: 'usd' },
  { key: 'tokens-30d', label: 'Tokens, 30 days', kind: 'compact' },
  { key: 'sessions-30d', label: 'Sessions, 30 days', kind: 'count' },
  { key: 'active-time-30d', label: 'Active time, 30 days', kind: 'count' }
];
const TABLES = ['by-model', 'by-machine', 'by-project'];
const compactFormat = new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 });
const CARD = 'card overflow-hidden rounded-lg border border-border bg-surface';

function tileValue(row: KvRow, kind: 'usd' | 'compact' | 'count'): ReactNode {
  if (!row.redacted && typeof row.value === 'number' && Number.isFinite(row.value)) {
    return kind === 'usd' ? formatUsd(row.value) : kind === 'compact' ? compactFormat.format(row.value) : formatCount(row.value);
  }
  if (!row.redacted && typeof row.value === 'string') {
    return row.value;
  }
  return (
    <span className="block text-sm font-normal">
      <JsonValue value={row.value} redacted={row.redacted} />
    </span>
  );
}

function Totals({ block, collectorId }: { block: KvBlock; collectorId: string }) {
  const row = (key: string) => block.rows.find((r) => r.key === key);
  const source = row('source');
  const range = row('range');
  return (
    <div id={`${collectorId}/${block.id}`} tabIndex={-1}>
      <div className="grid grid-cols-2 gap-3 sm:grid-cols-3 2xl:grid-cols-6">
        {TILES.map((tile) => {
          const found = row(tile.key);
          return (
            found && (
              <div key={tile.key} id={itemDomId(collectorId, block.id, tile.key)} tabIndex={-1} className="min-w-0 rounded-xl border border-border bg-surface p-4">
                <p className="text-xs leading-4 text-muted">{tile.label}</p>
                <div className="mt-1 text-2xl leading-[30px] font-semibold break-words tabular sm:text-[32px] sm:leading-9">{tileValue(found, tile.kind)}</div>
              </div>
            )
          );
        })}
      </div>
      {(source || range) && (
        <div className="mt-2 text-xs text-muted">
          {source && (
            <>
              Source <JsonValue value={source.value} redacted={source.redacted} />
            </>
          )}
          {source && range && ' · '}
          {range && <JsonValue value={range.value} redacted={range.redacted} />}
        </div>
      )}
    </div>
  );
}

function BlockCard({ block, title, collectorId, reveal, children }: { block: BlockModel; title: string; collectorId: string; reveal: RevealTarget | null; children?: ReactNode }) {
  return (
    <section className={CARD}>
      <div className="border-b border-border px-4 py-2.5">
        <h2 className="text-sm font-semibold">{title}</h2>
      </div>
      {children}
      <Block
        block={block}
        collectorId={collectorId}
        headingLevel={3}
        showTitle={block.title !== title}
        revealItemId={reveal?.blockAnchor === `${collectorId}/${block.id}` ? reveal.itemId : undefined}
      />
    </section>
  );
}

function UsageSection({ section, reveal }: { section: Section; reveal: RevealTarget | null }) {
  const { collectorId, blocks } = section;
  const totals = blocks.find((b) => b.id === 'totals' && b.type === 'kv') as KvBlock | undefined;
  const byDay = blocks.find((b) => b.id === 'by-day' && b.type === 'table') as TableBlock | undefined;
  const tables = TABLES.flatMap((id) => blocks.filter((b) => b.id === id));
  const others = blocks.filter((b) => b !== totals && b !== byDay && !tables.includes(b));
  const days = byDay ? dailyCosts(byDay) : [];
  const charted = days.some((d) => d.cost !== null && d.cost > 0);
  return (
    <>
      {totals && <Totals block={totals} collectorId={collectorId} />}
      {byDay && (
        <BlockCard block={byDay} title="Cost per day (USD)" collectorId={collectorId} reveal={reveal}>
          {charted ? (
            <div className="px-4 py-3">
              <UsageChart days={days} />
            </div>
          ) : (
            <p className="px-4 py-3 text-muted">No cost recorded in this range.</p>
          )}
        </BlockCard>
      )}
      {[...tables, ...others].map((block) => (
        <BlockCard key={block.id} block={block} title={block.title} collectorId={collectorId} reveal={reveal} />
      ))}
    </>
  );
}

/** The Usage view: totals tiles, the cost-per-day chart and the usage tables of the `usage` view's collectors. */
export default function UsagePage() {
  const store = useSnapshotStore();
  const model = store.views.find((v) => v.id === 'usage');
  const collectors = model?.collectors ?? [];
  const inactive = new Set(collectors.filter((c) => c.state !== 'active').map((c) => c.id));
  const reveal = useReveal('usage', model?.sections);
  const sections = model?.sections.filter((s) => !inactive.has(s.collectorId));
  const unavailable = collectors.filter((c) => c.state === 'unavailable');
  const notPresent = collectors.filter((c) => c.state === 'notPresent');
  const empty = !sections?.some((s) => s.blocks.length > 0);

  useEffect(() => {
    focusPendingHeading();
  }, []);

  return (
    <>
      <header className="mb-4">
        <h1 tabIndex={-1} className="text-2xl leading-[30px] font-semibold">
          Usage
        </h1>
        {model?.description && <p className="mt-1 text-muted">{model.description}</p>}
        {collectors.flatMap((c) => c.notes ?? []).map((note, index) => (
          <p key={index} className="text-xs text-muted">
            {note}
          </p>
        ))}
      </header>

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

      {!empty ? (
        <div className="space-y-4">
          {sections?.map((section) => (
            <UsageSection key={section.collectorId} section={section} reveal={reveal} />
          ))}
        </div>
      ) : (
        !unavailable.length && !notPresent.length && <p className="text-muted">Nothing to show for this view.</p>
      )}

      {notPresent.length > 0 && (
        <p className="mt-6 text-muted">
          Not on this machine: {notPresent.map((c) => (c.reason ? `${c.title} (${c.reason})` : c.title)).join(', ')}
        </p>
      )}
    </>
  );
}
