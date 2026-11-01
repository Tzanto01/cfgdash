import { Fragment, type CSSProperties } from 'react';
import { inlineValue } from '../lib/format';
import type { Contribution, ContributionStatus } from '../lib/models';
import { cn } from '../lib/utils';

export type LayerKey = 'managed' | 'local' | 'project' | 'user';

export const LAYERS: readonly { key: LayerKey; glyph: string }[] = [
  { key: 'managed', glyph: 'M' },
  { key: 'local', glyph: 'L' },
  { key: 'project', glyph: 'P' },
  { key: 'user', glyph: 'U' }
];

/** The settings layer a source label belongs to: "managed", "local", "project", "user" in that priority. */
export function layerOf(label: string): LayerKey | undefined {
  const text = label.toLowerCase();
  return LAYERS.find((layer) => text.includes(layer.key))?.key;
}

function statuses(contributions: Contribution[]): Map<LayerKey, ContributionStatus> {
  const map = new Map<LayerKey, ContributionStatus>();
  for (const c of contributions) {
    const layer = layerOf(c.source.label);
    if (layer && !map.has(layer)) {
      map.set(layer, c.status);
    }
  }
  return map;
}

/** "Effective from project (local); overridden: user". */
export function lineageLabel(contributions: Contribution[]): string {
  const labels = (status: ContributionStatus) =>
    contributions.filter((c) => c.status === status).map((c) => c.source.label).join(', ');
  const parts: string[] = [];
  if (labels('applied')) {
    parts.push(`Effective from ${labels('applied')}`);
  }
  if (labels('merged')) {
    parts.push(`${parts.length ? 'merged' : 'Merged'} from ${labels('merged')}`);
  }
  if (labels('overridden')) {
    parts.push(`overridden: ${labels('overridden')}`);
  }
  return parts.join('; ');
}

/** One layer node: filled when applied, outlined and struck when overridden, half filled when merged, dotted when not set. */
export function LayerNode({ layer, status, size = 18 }: { layer: LayerKey; status?: ContributionStatus; size?: number }) {
  const glyph = LAYERS.find((l) => l.key === layer)?.glyph;
  const style = {
    '--layer': `var(--layer-${layer})`,
    width: size,
    height: size,
    background:
      status === 'applied'
        ? 'var(--layer)'
        : status === 'merged'
          ? 'linear-gradient(135deg, var(--layer) 50%, transparent 50%)'
          : undefined
  } as CSSProperties;
  return (
    <span
      aria-hidden="true"
      style={style}
      className={cn(
        'inline-flex shrink-0 items-center justify-center rounded-[5px] font-mono text-[10.5px] leading-none font-bold',
        status ? 'border-[1.5px] border-(--layer)' : 'border-[1.5px] border-dotted border-border-strong text-muted',
        status === 'applied' && 'text-layer-fg',
        status === 'merged' && 'text-text',
        status === 'overridden' && 'text-(--layer) line-through'
      )}
    >
      {glyph}
    </span>
  );
}

/**
 * Lineage of a kv row across the managed, local, project and user layers: a compact strip or, as `route`, the
 * full route with connectors (dashed into overridden layers). Renders nothing when no contribution maps to a layer.
 * Values only appear in the tooltip, and never for redacted rows.
 */
export function Lineage({
  contributions,
  redacted = false,
  route = false
}: {
  contributions?: Contribution[];
  redacted?: boolean;
  route?: boolean;
}) {
  const list = contributions ?? [];
  const map = statuses(list);
  if (!map.size) {
    return null;
  }
  const title = redacted ? undefined : list.map((c) => `${c.source.label}: ${inlineValue(c.value)}`).join('\n');
  return (
    <span role="img" aria-label={lineageLabel(list)} title={title} className={cn('inline-flex items-center', route ? '' : 'gap-0.5')}>
      {LAYERS.map((layer, index) => (
        <Fragment key={layer.key}>
          {route && index > 0 && (
            <span
              aria-hidden="true"
              className={cn('w-3 border-t-[1.5px]', map.get(layer.key) === 'overridden' ? 'border-dashed border-border-strong' : 'border-border-strong')}
            />
          )}
          <LayerNode layer={layer.key} status={map.get(layer.key)} size={route ? 20 : 18} />
        </Fragment>
      ))}
    </span>
  );
}
