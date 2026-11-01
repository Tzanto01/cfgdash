import { Info, OctagonAlert, TriangleAlert, type LucideIcon } from 'lucide-react';
import type { Severity, SeverityCounts } from '../lib/models';
import { cn } from '../lib/utils';

interface SeverityMeta {
  id: Severity;
  label: string;
  icon: LucideIcon;
  text: string;
  fill: string;
}

/** The three severities in API order with their word, icon and token classes. */
export const SEVERITIES: readonly SeverityMeta[] = [
  { id: 'critical', label: 'Critical', icon: OctagonAlert, text: 'text-danger', fill: 'bg-danger' },
  { id: 'warning', label: 'Warning', icon: TriangleAlert, text: 'text-warn', fill: 'bg-warn' },
  { id: 'info', label: 'Info', icon: Info, text: 'text-muted', fill: 'bg-border-strong' }
];

function metaOf(severity: Severity): SeverityMeta {
  return SEVERITIES.find((s) => s.id === severity) ?? SEVERITIES[2];
}

/** Severity icon plus word in the severity's colour. */
export function SeverityLabel({ severity, className }: { severity: Severity; className?: string }) {
  const meta = metaOf(severity);
  const Icon = meta.icon;
  return (
    <span className={cn('inline-flex items-center gap-1.5 font-sans', meta.text, className)}>
      <Icon className="size-4 shrink-0" strokeWidth={1.75} aria-hidden="true" />
      {meta.label}
    </span>
  );
}

/** Segmented severity bar (decorative) with the counts as text, or "No findings" when all are zero. */
export function SeverityBar({ counts, className }: { counts: SeverityCounts; className?: string }) {
  const parts = SEVERITIES.filter((s) => counts[s.id] > 0);
  if (!parts.length) {
    return <p className={cn('text-muted', className)}>No findings</p>;
  }
  return (
    <div className={cn('space-y-2', className)}>
      <div className="flex h-2 gap-0.5 overflow-hidden rounded-full" aria-hidden="true">
        {parts.map((s) => (
          <span key={s.id} className={cn('h-full basis-0', s.fill)} style={{ flexGrow: counts[s.id] }} />
        ))}
      </div>
      <p className="text-xs font-medium tabular">
        {SEVERITIES.map((s, index) => (
          <span key={s.id}>
            {index > 0 && <span aria-hidden="true"> · </span>}
            <span className={counts[s.id] > 0 ? s.text : 'text-muted'}>
              {counts[s.id]} {s.label.toLowerCase()}
            </span>
            {index < SEVERITIES.length - 1 && <span className="sr-only">,</span>}
          </span>
        ))}
      </p>
    </div>
  );
}
