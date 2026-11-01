import { ChevronDown, Folder, RefreshCw } from 'lucide-react';
import { Link } from 'react-router';
import { Button } from '../components/ui/button';
import { Tooltip } from '../components/ui/tooltip';
import { formatRelative, formatTime } from '../lib/format';
import { useUrl } from '../lib/url';
import { cn } from '../lib/utils';
import { VIEW_GROUPS } from '../lib/views';
import { useNow } from '../state/hooks';
import { useSnapshotStore } from '../state/snapshot';
import { useUi } from '../state/ui';

/** The view navigation, grouped; `collapsed` renders the icon rail with tooltips. */
export function NavList({ collapsed = false, onNavigate }: { collapsed?: boolean; onNavigate?(): void }) {
  const store = useSnapshotStore();
  const url = useUrl();
  return (
    <nav aria-label="Views" className="space-y-4">
      {VIEW_GROUPS.map((group) => {
        const views = store.navViews.filter((v) => v.group === group.id);
        if (!views.length) {
          return null;
        }
        return (
          <div key={group.id}>
            {group.label && (
              <p className={cn('mb-1 px-2.5 text-xs font-medium text-muted', collapsed && 'sr-only')}>{group.label}</p>
            )}
            <ul className="space-y-0.5">
              {views.map((view) => {
                const Icon = view.icon;
                const active = url.path === `/${view.id}`;
                const link = (
                  <Link
                    to={url.link(`/${view.id}`)}
                    onClick={onNavigate}
                    aria-current={active ? 'page' : undefined}
                    className={cn(
                      'touch-row flex h-9 items-center gap-2 rounded-lg px-2 text-sm transition-colors duration-100',
                      collapsed && 'justify-center px-0',
                      active ? 'tint-accent font-semibold text-accent' : 'hover:bg-hover'
                    )}
                  >
                    <Icon className="size-4 shrink-0" strokeWidth={1.75} aria-hidden="true" />
                    <span className={collapsed ? 'sr-only' : 'min-w-0 flex-1 truncate'}>{view.label}</span>
                    {view.hasUnavailable && (
                      <>
                        <span className="size-2 shrink-0 rounded-full bg-warn" aria-hidden="true" />
                        <span className="sr-only">, has an unavailable source</span>
                      </>
                    )}
                    {!collapsed && (
                      <span className="shrink-0 rounded-full border border-border bg-raised px-1 font-mono text-[10px] leading-4 text-muted tabular">
                        {view.itemCount}
                        <span className="sr-only"> items</span>
                      </span>
                    )}
                  </Link>
                );
                return <li key={view.id}>{collapsed ? <Tooltip content={view.label}>{link}</Tooltip> : link}</li>;
              })}
            </ul>
          </div>
        );
      })}
    </nav>
  );
}

/** The selected folder; opens the folder picker. */
export function FolderPill({ className }: { className?: string }) {
  const store = useSnapshotStore();
  const ui = useUi();
  const dir = store.snapshot?.context.targetDir ?? '';
  return (
    <button
      type="button"
      aria-haspopup="dialog"
      aria-label={`Folder ${dir}, choose another folder`}
      onClick={() => ui.setPickerOpen(true)}
      className={cn(
        'touch-target inline-flex h-8 min-w-0 items-center gap-2 rounded-md border border-border-strong bg-raised px-2.5 hover:bg-hover',
        className
      )}
    >
      <Folder className="size-4 shrink-0" strokeWidth={1.75} aria-hidden="true" />
      <span className="mono-value truncate">{dir}</span>
      <ChevronDown className="size-4 shrink-0 text-muted" strokeWidth={1.75} aria-hidden="true" />
    </button>
  );
}

/** "Collected 10:00 · 2 min ago". */
export function CollectedLine({ className }: { className?: string }) {
  const at = useSnapshotStore().snapshot?.machine.collectedAt;
  const now = useNow();
  if (!at) {
    return null;
  }
  return <p className={cn('text-xs text-muted tabular', className)}>{`Collected ${formatTime(at)} · ${formatRelative(at, now)}`}</p>;
}

/** Refresh as an icon button, or with its text when `withLabel` is set. */
export function RefreshButton({ withLabel = false }: { withLabel?: boolean }) {
  const store = useSnapshotStore();
  const icon = <RefreshCw className={cn(store.refreshing && 'animate-spin')} strokeWidth={1.75} aria-hidden="true" />;
  if (withLabel) {
    return (
      <Button aria-busy={store.refreshing} onClick={store.refresh}>
        {icon}
        {store.refreshing ? 'Refreshing...' : 'Refresh'}
      </Button>
    );
  }
  return (
    <Button
      variant="ghost"
      size="icon"
      aria-label={store.refreshing ? 'Refreshing' : 'Refresh'}
      aria-busy={store.refreshing}
      onClick={store.refresh}
    >
      {icon}
    </Button>
  );
}
