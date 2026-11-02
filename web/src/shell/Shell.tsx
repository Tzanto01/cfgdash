import { BotMessageSquare, Ellipsis, LayoutGrid, LoaderCircle, Monitor, PanelLeftClose, PanelLeftOpen, Search, Settings, SlidersHorizontal } from 'lucide-react';
import { lazy, Suspense, useEffect, useState, type MouseEvent, type ReactNode } from 'react';
import { Link, NavLink, Outlet, useLocation } from 'react-router';
import { Button } from '../components/ui/button';
import { Sheet, SheetContent, SheetTitle } from '../components/ui/sheet';
import { formatTime } from '../lib/format';
import { mergeSearch, useUrl } from '../lib/url';
import { cn } from '../lib/utils';
import { VIEW_DEFINITIONS } from '../lib/views';
import { useAssistant } from '../state/assistant';
import { useMediaQuery, useNarrow } from '../state/hooks';
import { useSnapshotStore } from '../state/snapshot';
import { useUi } from '../state/ui';
import { CollectedLine, FolderPill, NavList, RefreshButton } from './Nav';
import { ThemeToggle } from './ThemeToggle';

const loadAssistant = () => import('./AssistantPanel');
const AssistantPanel = lazy(loadAssistant);

let headingPending = false;

/** Focuses the page heading inside `main`. */
export function focusMainHeading(): void {
  const heading = document.querySelector<HTMLElement>('main h1');
  headingPending = !heading;
  heading?.focus();
}

/** Focuses the page heading when the last {@link focusMainHeading} found none because a lazy page was still loading. */
export function focusPendingHeading(): void {
  if (headingPending) {
    focusMainHeading();
  }
}

function skipToContent(event: MouseEvent<HTMLAnchorElement>): void {
  event.preventDefault();
  const main = document.getElementById('content');
  main?.focus();
  main?.scrollIntoView();
}

/** Spinner plus text in a status region. */
export function LoadingPanel({ text, className }: { text: string; className?: string }) {
  return (
    <div role="status" className={cn('flex items-center gap-3 py-12 text-muted', className)}>
      <LoaderCircle className="size-5 animate-spin" strokeWidth={1.75} aria-hidden="true" />
      <p>{text}</p>
    </div>
  );
}

const H1 = 'text-2xl leading-[30px] font-semibold';

function ErrorPanel({ kind }: { kind: string }) {
  const store = useSnapshotStore();
  const ui = useUi();
  const url = useUrl();
  const location = useLocation();
  if (kind === 'bad-dir') {
    return (
      <div className="space-y-3 py-6">
        <h1 tabIndex={-1} className={H1}>
          This folder can't be used
        </h1>
        <p className="mono-value break-all text-muted">{url.dir}</p>
        <div className="flex flex-wrap gap-2">
          <Link
            to={{ pathname: location.pathname, search: mergeSearch(location.search, { dir: undefined, file: undefined, line: undefined }) }}
            className="touch-target inline-flex h-8 items-center rounded-md border border-border-strong bg-raised px-3 text-sm font-medium hover:bg-hover"
          >
            Back to the start folder
          </Link>
          <Button onClick={() => ui.setPickerOpen(true)}>Choose another folder</Button>
        </div>
      </div>
    );
  }
  return (
    <div className="space-y-3 py-6">
      <h1 tabIndex={-1} className={H1}>
        {kind === 'unreachable' ? "cfgdash isn't answering" : "Couldn't load the snapshot"}
      </h1>
      {kind === 'unreachable' && (
        <p>
          It may have stopped; start it again with <code className="font-mono">cfgdash</code>.
        </p>
      )}
      <Button onClick={store.reload}>Try again</Button>
    </div>
  );
}

function MainContent({ className }: { className?: string }) {
  const store = useSnapshotStore();
  const request = store.request;
  return (
    <main id="content" tabIndex={-1} className={className}>
      {request.phase === 'loading' && <LoadingPanel text="Reading this machine's harness..." />}
      {request.phase === 'error' && <ErrorPanel kind={request.kind} />}
      {request.phase === 'content' && (
        <>
          {store.refreshFailed && (
            <div className="tint-warn mb-4 flex flex-wrap items-center gap-3 rounded-lg border border-warn px-3 py-2">
              <p>Couldn't refresh. Showing the snapshot from {formatTime(request.value.machine.collectedAt)}.</p>
              <Button
                size="sm"
                onClick={() => {
                  store.refresh();
                  focusMainHeading();
                }}
              >
                Try again
              </Button>
            </div>
          )}
          <Outlet />
        </>
      )}
    </main>
  );
}

function SearchButton() {
  const ui = useUi();
  return (
    <button
      type="button"
      aria-label="Search"
      aria-haspopup="dialog"
      aria-keyshortcuts="Control+K Meta+K /"
      onClick={() => ui.setPaletteOpen(true)}
      className="touch-target inline-flex h-8 w-56 items-center gap-2 rounded-md border border-border-strong bg-raised px-2.5 text-muted hover:bg-hover"
    >
      <Search className="size-4" strokeWidth={1.75} aria-hidden="true" />
      <span className="flex-1 text-left">Search</span>
      <kbd className="rounded border border-border px-1 font-mono text-[11px]">Ctrl K</kbd>
    </button>
  );
}

/** The header gear: cfgdash's own settings (cfgdash-config-v1); only when health says `edits: true`. */
function GearLink() {
  const ui = useUi();
  const url = useUrl();
  if (!ui.edits) {
    return null;
  }
  return (
    <Link
      to={url.link('/cfgdash')}
      aria-label="cfgdash settings"
      aria-current={url.path === '/cfgdash' ? 'page' : undefined}
      className="touch-target inline-flex size-8 items-center justify-center rounded-md hover:bg-hover [&_svg]:size-4"
    >
      <Settings strokeWidth={1.75} aria-hidden="true" />
    </Link>
  );
}

/** Opens and closes the assistant panel; only where edits are available. Preloads the panel so it opens without delay. */
function AssistantButton() {
  const ui = useUi();
  const assistant = useAssistant();
  useEffect(() => {
    if (ui.edits) {
      loadAssistant().catch(() => undefined);
    }
  }, [ui.edits]);
  if (!ui.edits) {
    return null;
  }
  return (
    <Button id="assistant-toggle" variant="ghost" size="icon" aria-label="Assistant" aria-expanded={assistant.open} onClick={() => assistant.setOpen(!assistant.open)}>
      <BotMessageSquare strokeWidth={1.75} aria-hidden="true" />
    </Button>
  );
}

function AssistantSlot() {
  const ui = useUi();
  const assistant = useAssistant();
  if (!ui.edits || !assistant.open) {
    return null;
  }
  return (
    <Suspense fallback={null}>
      <AssistantPanel />
    </Suspense>
  );
}

function WideShell() {
  const store = useSnapshotStore();
  const tablet = useMediaQuery('(max-width: 1023px)');
  const [collapsed, setCollapsed] = useState(tablet);
  const snapshot = store.snapshot;
  return (
    <div className="flex min-h-screen">
      {snapshot && (
        <aside
          className={cn('sticky top-0 flex h-screen shrink-0 flex-col border-r border-border bg-surface', collapsed ? 'w-16' : 'w-52')}
        >
          <div className="flex-1 overflow-y-auto p-2 pt-4">
            <NavList collapsed={collapsed} />
          </div>
          <div className="space-y-2 border-t border-border p-3">
            {!collapsed && <CollectedLine />}
            <Button
              variant="ghost"
              className={cn('w-full', collapsed ? 'justify-center px-0' : 'justify-start')}
              aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
              aria-expanded={!collapsed}
              onClick={() => setCollapsed(!collapsed)}
            >
              {collapsed ? <PanelLeftOpen strokeWidth={1.75} aria-hidden="true" /> : <PanelLeftClose strokeWidth={1.75} aria-hidden="true" />}
              {!collapsed && 'Collapse'}
            </Button>
          </div>
        </aside>
      )}
      <div className="flex min-w-0 flex-1 flex-col">
        <header className="sticky top-0 z-30 flex h-[52px] items-center gap-3 border-b border-border bg-surface px-4">
          {snapshot && (
            <>
              <FolderPill className="max-w-[45%]" />
              <span className="flex min-w-0 items-center gap-1.5 text-muted">
                <Monitor className="size-4 shrink-0" strokeWidth={1.75} aria-hidden="true" />
                <span className="truncate">{snapshot.machine.hostname}</span>
              </span>
            </>
          )}
          <span className="flex-1" />
          <SearchButton />
          {snapshot && <AssistantButton />}
          {snapshot && <RefreshButton />}
          <ThemeToggle />
          {snapshot && <GearLink />}
        </header>
        <MainContent className="mx-auto w-full max-w-[1248px] flex-1 p-6" />
      </div>
      {snapshot && <AssistantSlot />}
    </div>
  );
}

type TabItemProps = { icon: ReactNode; label: string } & ({ to: string } | { onClick(): void; active?: boolean });

function TabItem(props: TabItemProps) {
  const { icon, label } = props;
  const className = (on: boolean) =>
    cn(
      'flex flex-1 flex-col items-center justify-center gap-0.5 text-xs font-medium [&_svg]:size-5',
      on ? 'text-accent' : 'text-muted hover:text-text'
    );
  const content = (
    <>
      {icon}
      {label}
    </>
  );
  return 'to' in props ? (
    <NavLink to={props.to} className={({ isActive }) => className(isActive)}>
      {content}
    </NavLink>
  ) : (
    <button type="button" className={className(!!props.active)} onClick={props.onClick}>
      {content}
    </button>
  );
}

function NarrowShell() {
  const store = useSnapshotStore();
  const ui = useUi();
  const url = useUrl();
  const [moreOpen, setMoreOpen] = useState(false);
  const snapshot = store.snapshot;
  const viewId = url.path.split('/').filter(Boolean)[0] ?? '';
  const title = viewId === 'search' ? 'Search' : viewId === 'cfgdash' ? 'cfgdash settings' : (VIEW_DEFINITIONS.find((v) => v.id === viewId)?.label ?? '');
  const settingsVisible = store.navViews.some((v) => v.id === 'settings');
  return (
    <div className="min-h-screen">
      <header className="sticky top-0 z-30 flex h-12 items-center gap-2 border-b border-border bg-surface px-3">
        <Link to={url.link('/overview')} className="flex items-center gap-1.5 font-semibold">
          <img src="/favicon.svg" alt="" className="size-5" />
          cfgdash
        </Link>
        <span className="min-w-0 flex-1 truncate text-muted">{title}</span>
        <Button variant="ghost" size="icon" aria-label="Search" aria-haspopup="dialog" onClick={() => ui.setPaletteOpen(true)}>
          <Search strokeWidth={1.75} aria-hidden="true" />
        </Button>
        {snapshot && <AssistantButton />}
        <ThemeToggle />
        {snapshot && <GearLink />}
      </header>
      <MainContent className="p-4 pb-[calc(72px+env(safe-area-inset-bottom))]" />
      {snapshot && (
        <>
          <div
            role="group"
            aria-label="Quick links"
            className="fixed inset-x-0 bottom-0 z-30 flex h-[calc(56px+env(safe-area-inset-bottom))] border-t border-border bg-surface pb-[env(safe-area-inset-bottom)]"
          >
            <TabItem to={url.link('/overview')} icon={<LayoutGrid strokeWidth={1.75} aria-hidden="true" />} label="Overview" />
            {settingsVisible && (
              <TabItem to={url.link('/settings')} icon={<SlidersHorizontal strokeWidth={1.75} aria-hidden="true" />} label="Settings" />
            )}
            <TabItem onClick={() => ui.setPaletteOpen(true)} icon={<Search strokeWidth={1.75} aria-hidden="true" />} label="Search" />
            <TabItem onClick={() => setMoreOpen(true)} active={moreOpen} icon={<Ellipsis strokeWidth={1.75} aria-hidden="true" />} label="More" />
          </div>
          <Sheet open={moreOpen} onOpenChange={setMoreOpen}>
            <SheetContent side="left" aria-describedby={undefined} className="gap-4 overflow-y-auto p-4">
              <SheetTitle className="font-semibold">cfgdash</SheetTitle>
              <FolderPill className="w-full" />
              <NavList onNavigate={() => setMoreOpen(false)} />
              <CollectedLine />
              <RefreshButton withLabel />
            </SheetContent>
          </Sheet>
          <AssistantSlot />
        </>
      )}
    </div>
  );
}

/** The app shell: skip link, wide (header and sidebar) or narrow (top bar, tab bar, More sheet) layout, main states. */
export function Shell() {
  const narrow = useNarrow();
  return (
    <>
      <a
        href="#content"
        onClick={skipToContent}
        className="sr-only focus:not-sr-only focus:fixed focus:top-2 focus:left-2 focus:z-50 focus:rounded-md focus:bg-raised focus:px-3 focus:py-2"
      >
        Skip to content
      </a>
      {narrow ? <NarrowShell /> : <WideShell />}
    </>
  );
}
