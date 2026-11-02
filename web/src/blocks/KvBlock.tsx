import { useState } from 'react';
import { inlineValue, itemDomId } from '../lib/format';
import type { ContextItem } from '../state/assistant';
import type { ContributionStatus, KvBlock as KvBlockModel, KvRow } from '../lib/models';
import { cn } from '../lib/utils';
import { AddToContext } from '../shell/AddToContext';
import { Badges, SourceChip } from './Badges';
import { kvRowMatches, NO_FILTER, type RowFilter } from './filter';
import { JsonValue } from './JsonValue';
import { LayerNode, layerOf, Lineage } from './Lineage';

const STATUS_TONE: Record<ContributionStatus, string> = {
  applied: 'text-ok',
  overridden: 'text-warn',
  merged: 'text-muted'
};

const EMPTY = new Set<string>();

/** kv block: ruled rows with key, value, lineage, source and flags; a row with contributions expands to its route. */
export function KvBlock({
  block,
  collectorId,
  filter = NO_FILTER
}: {
  block: KvBlockModel;
  collectorId: string;
  filter?: RowFilter;
}) {
  const [openState, setOpenState] = useState({ block, keys: EMPTY });
  const open = openState.block === block ? openState.keys : EMPTY;
  const toggle = (key: string) => {
    const next = new Set(open);
    if (!next.delete(key)) {
      next.add(key);
    }
    setOpenState({ block, keys: next });
  };

  if (!block.rows.length) {
    return <p className="px-4 py-2 text-muted">No rows.</p>;
  }
  const rows = block.rows.filter((row) => kvRowMatches(row, filter));
  if (!rows.length) {
    return <p className="px-4 py-2 text-muted">No rows match the filter.</p>;
  }
  return (
    <ul>
      {rows.map((row) => (
        <KvRowView
          key={row.key}
          row={row}
          id={itemDomId(collectorId, block.id, row.key)}
          context={{ ref: { kind: 'row', collectorId, blockId: block.id, itemId: row.key }, label: `${block.title || block.id} / ${row.key}` }}
          open={open.has(row.key)}
          onToggle={() => toggle(row.key)}
        />
      ))}
    </ul>
  );
}

function KvRowView({ row, id, context, open, onToggle }: { row: KvRow; id: string; context: ContextItem; open: boolean; onToggle(): void }) {
  const contributions = row.contributions ?? [];
  const redacted = !!row.redacted;
  return (
    <li id={id} tabIndex={-1} className="border-b border-border last:border-b-0">
      <div className={cn('kv-row transition-colors duration-100 hover:bg-hover', open && 'bg-hover shadow-[inset_2px_0_0_var(--accent)]')}>
        <div className="mono-value min-w-0 [grid-area:key]">
          {contributions.length ? (
            <button
              type="button"
              className="touch-row block max-w-full truncate text-left hover:text-accent"
              aria-expanded={open}
              onClick={onToggle}
            >
              {row.key}
            </button>
          ) : (
            <span className="block truncate">{row.key}</span>
          )}
        </div>
        <div className="mono-value min-w-0 [grid-area:value]">
          <JsonValue value={row.value} redacted={redacted} />
          {contributions
            .filter((c) => c.status === 'overridden')
            .map((c, index) => (
              <p key={index} className="font-sans text-xs text-muted">
                Overrides{' '}
                {redacted ? (
                  <s>
                    <span aria-hidden="true">[redacted]</span>
                    <span className="sr-only">a redacted value</span>
                  </s>
                ) : (
                  <s className="font-mono">{inlineValue(c.value)}</s>
                )}{' '}
                from {c.source.label}
              </p>
            ))}
        </div>
        <div className="flex items-center [grid-area:lineage]">
          <Lineage contributions={contributions} redacted={redacted} />
        </div>
        <div className="flex min-w-0 flex-wrap items-center gap-1 [grid-area:meta]">
          <SourceChip source={row.source} />
          <Badges items={row.flags} />
          <AddToContext row item={context} />
        </div>
      </div>
      {open && (
        <div className="bg-raised pt-2.5 pr-4 pb-3 pl-[30px]">
          <Lineage contributions={contributions} redacted={redacted} route />
          <ol className="mt-2 space-y-1">
            {contributions.map((c, index) => {
              const layer = layerOf(c.source.label);
              return (
                <li key={index} className="flex flex-wrap items-center gap-x-3 gap-y-1">
                  {layer ? <LayerNode layer={layer} status={c.status} /> : <span className="w-[18px]" aria-hidden="true" />}
                  <span className={cn('w-20 text-xs font-medium', STATUS_TONE[c.status])}>{c.status}</span>
                  <span className={cn('mono-value min-w-0 flex-1', c.status === 'overridden' && 'line-through')}>
                    <JsonValue value={c.value} redacted={redacted} />
                  </span>
                  <SourceChip source={c.source} />
                </li>
              );
            })}
          </ol>
        </div>
      )}
    </li>
  );
}
