import {
  Building,
  Check,
  CircleX,
  Clock,
  Copy,
  EyeOff,
  File,
  FileExclamationPoint,
  FileSymlink,
  Link,
  Network,
  Split,
  Strikethrough,
  type LucideIcon
} from 'lucide-react';
import type { CollectorState, SourceRef } from '../lib/models';
import { useUrl } from '../lib/url';
import { cn } from '../lib/utils';
import { stateLabel } from '../lib/views';
import { useSnapshotStore } from '../state/snapshot';

type Tone = 'danger' | 'warn' | 'ok' | 'company' | 'neutral';

const TONES: Record<Tone, string> = {
  danger: 'tint-danger border-danger text-danger',
  warn: 'tint-warn border-warn text-warn',
  ok: 'tint-ok border-ok text-ok',
  company: 'tint-company border-company text-company',
  neutral: 'tint-neutral border-border-strong text-text'
};

const FLAGS: Record<string, { tone: Tone; icon?: LucideIcon }> = {
  'missing-script': { tone: 'danger', icon: FileExclamationPoint },
  error: { tone: 'danger', icon: CircleX },
  duplicate: { tone: 'warn', icon: Copy },
  overridden: { tone: 'warn', icon: Strikethrough },
  'network-path': { tone: 'warn', icon: Network },
  conditional: { tone: 'warn', icon: Split },
  company: { tone: 'company', icon: Building },
  redacted: { tone: 'neutral', icon: EyeOff },
  link: { tone: 'neutral', icon: Link },
  lazy: { tone: 'neutral', icon: Clock },
  referenced: { tone: 'neutral', icon: FileSymlink },
  loaded: { tone: 'ok', icon: Check },
  applied: { tone: 'ok', icon: Check }
};

const PILL = 'inline-flex h-[22px] items-center gap-1 rounded-full border px-2 text-xs font-medium whitespace-nowrap';

/** One flag or tree badge; known vocabulary gets its tone and icon, anything else is neutral. Text is always shown. */
export function Badge({ flag }: { flag: string }) {
  const known = FLAGS[flag];
  const Icon = known?.icon;
  return (
    <li className={cn('badge', PILL, TONES[known?.tone ?? 'neutral'])}>
      {Icon && <Icon className="size-3.5" strokeWidth={1.75} aria-hidden="true" />}
      {flag}
    </li>
  );
}

/** A list of flags; renders nothing when empty. */
export function Badges({ items }: { items?: string[] }) {
  if (!items?.length) {
    return null;
  }
  return (
    <ul className="flex flex-wrap gap-1">
      {items.map((item, index) => (
        <Badge key={`${item}-${index}`} flag={item} />
      ))}
    </ul>
  );
}

/** Source label; a button opening the file viewer when the source has a file. */
export function SourceChip({ source }: { source?: SourceRef }) {
  const store = useSnapshotStore();
  const url = useUrl();
  if (!source) {
    return null;
  }
  const chip = 'inline-flex h-[22px] max-w-full items-center gap-1 rounded-full border border-border-strong px-2 text-xs font-medium';
  if (!source.fileId) {
    return <span className={cn(chip, 'text-muted')}>{source.label}</span>;
  }
  const fileId = source.fileId;
  const path = store.file(fileId)?.path ?? source.label;
  return (
    <button
      type="button"
      className={cn(chip, 'touch-target bg-raised text-text hover:bg-hover')}
      aria-label={`${source.label}, open ${path}`}
      onClick={() => url.openFile(fileId)}
    >
      <File className="size-3.5 shrink-0" strokeWidth={1.75} aria-hidden="true" />
      <span className="truncate">{source.label}</span>
    </button>
  );
}

const STATE_DOT: Record<CollectorState, string> = {
  active: 'bg-ok',
  unavailable: 'bg-warn',
  notPresent: 'bg-muted'
};

/** Collector state: dot plus plain text (active, unavailable, not on this machine). */
export function StateChip({ state }: { state: CollectorState }) {
  return (
    <span className={cn('inline-flex items-center gap-1.5 text-xs font-medium', state === 'unavailable' ? 'text-warn' : 'text-muted')}>
      <span className={cn('size-2 rounded-full', STATE_DOT[state])} aria-hidden="true" />
      {stateLabel(state)}
    </span>
  );
}
