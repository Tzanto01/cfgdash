import { useEffect, useRef, useState } from 'react';
import type { TableBlock } from '../lib/models';

/** One day of the `by-day` block; `cost` is null when the cell is not a finite number. */
export interface DayCost {
  date: string;
  cost: number | null;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
const HEIGHT = 220;
const TOP = 20;
const BOTTOM = 28;
const LEFT = 40;
const RIGHT = 8;
const usdFormat = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD', minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** 12.3 -> "$12.30". */
export function formatUsd(value: number): string {
  return usdFormat.format(value);
}

/** "2026-10-03" -> "Oct 3" without time-zone shifting; anything else is returned unchanged. */
export function shortDate(text: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  const month = match ? MONTHS[Number(match[2]) - 1] : undefined;
  return match && month ? `${month} ${Number(match[3])}` : text;
}

/** The `by-day` rows oldest first. */
export function dailyCosts(block: TableBlock): DayCost[] {
  return [...block.rows].reverse().map((row) => {
    const date = row.cells['date'];
    const cost = row.cells['cost'];
    return {
      date: typeof date === 'string' ? date : row.id,
      cost: typeof cost === 'number' && Number.isFinite(cost) ? cost : null
    };
  });
}

function niceTicks(max: number): { ticks: number[]; top: number; decimals: number } {
  const raw = max / 4;
  const power = 10 ** Math.floor(Math.log10(raw));
  const step = ([1, 2, 5, 10].find((m) => m * power >= raw) ?? 10) * power;
  const count = Math.max(2, Math.ceil(max / step - 1e-9));
  const decimals = Math.max(0, -Math.floor(Math.log10(step) + 1e-9));
  return { ticks: Array.from({ length: count + 1 }, (_, i) => i * step), top: count * step, decimals };
}

function columnPath(x: number, base: number, width: number, height: number): string {
  const r = Math.min(4, width / 2, height);
  const top = base - height;
  return `M${x} ${base}V${top + r}A${r} ${r} 0 0 1 ${x + r} ${top}H${x + width - r}A${r} ${r} 0 0 1 ${x + width} ${top + r}V${base}Z`;
}

/** Cost per day as columns, oldest left. Not focusable: the `by-day` table next to it is the keyboard and screen-reader path. */
export function UsageChart({ days }: { days: DayCost[] }) {
  const ref = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(640);
  const [hovered, setHovered] = useState<number | null>(null);

  useEffect(() => {
    const element = ref.current;
    if (!element || typeof ResizeObserver === 'undefined') {
      return;
    }
    const observer = new ResizeObserver(([entry]) => {
      if (entry) {
        setWidth(Math.max(240, Math.round(entry.contentRect.width)));
      }
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, []);

  const costs = days.map((d) => d.cost).filter((c): c is number => c !== null);
  const max = Math.max(...costs);
  const { ticks, top, decimals } = niceTicks(max);
  const plotWidth = width - LEFT - RIGHT;
  const plotHeight = HEIGHT - TOP - BOTTOM;
  const base = TOP + plotHeight;
  const slot = plotWidth / days.length;
  const barWidth = Math.max(1, Math.min(24, slot * 0.7, slot - 2));
  const y = (value: number) => base - (value / top) * plotHeight;
  const last = days.length - 1;
  const total = costs.reduce((sum, c) => sum + c, 0);
  const label = `Cost per day for the last ${days.length} ${days.length === 1 ? 'day' : 'days'}, from ${formatUsd(Math.min(...costs))} to ${formatUsd(max)}, total ${formatUsd(total)}`;
  const active = hovered === null ? undefined : days[hovered];
  const activeCenter = hovered === null ? 0 : LEFT + slot * (hovered + 0.5);
  const activeTop = active?.cost ? y(active.cost) : base;

  return (
    <div ref={ref} className="relative">
      <svg role="img" aria-label={label} viewBox={`0 0 ${width} ${HEIGHT}`} width="100%" className="block" onPointerLeave={() => setHovered(null)}>
        <text x={0} y={10} fontSize={11} className="fill-muted">
          USD
        </text>
        {ticks.map((tick) => (
          <g key={tick}>
            <line x1={LEFT} x2={width - RIGHT} y1={y(tick)} y2={y(tick)} strokeWidth={1} shapeRendering="crispEdges" className="stroke-border" />
            <text x={LEFT - 6} y={y(tick)} fontSize={11} textAnchor="end" dominantBaseline="middle" className="fill-muted tabular">
              {tick.toFixed(decimals)}
            </text>
          </g>
        ))}
        {days.map((day, index) => {
          const x = LEFT + slot * index;
          const height = day.cost ? base - y(day.cost) : 0;
          const showDate = (last - index) % 7 === 0;
          const endLabel = index === last && days.length > 1;
          return (
            <g key={`${day.date}-${index}`}>
              {height > 0 && (
                <path
                  d={columnPath(x + (slot - barWidth) / 2, base, barWidth, height)}
                  className={hovered === index ? 'fill-accent opacity-75' : 'fill-accent'}
                />
              )}
              {showDate && (
                <text
                  x={endLabel ? x + slot : x + slot / 2}
                  y={base + 18}
                  fontSize={11}
                  textAnchor={endLabel ? 'end' : 'middle'}
                  className="fill-muted"
                >
                  {shortDate(day.date)}
                </text>
              )}
              <rect x={x} y={TOP} width={slot} height={plotHeight} fill="transparent" onPointerEnter={() => setHovered(index)} />
            </g>
          );
        })}
      </svg>
      {active && (
        <div
          aria-hidden="true"
          className="pointer-events-none absolute rounded-md border border-border bg-raised px-2 py-1 text-xs whitespace-nowrap"
          style={{
            left: `${(Math.min(Math.max(activeCenter, 44), width - 44) / width) * 100}%`,
            top: `${(Math.max(0, activeTop - 44) / HEIGHT) * 100}%`,
            transform: 'translateX(-50%)'
          }}
        >
          <span className="block font-semibold tabular">{active.cost === null ? 'No value' : formatUsd(active.cost)}</span>
          <span className="block text-muted">{shortDate(active.date)}</span>
        </div>
      )}
    </div>
  );
}
