import { LazyMotion, MotionConfig } from 'motion/react';
import { lazy, Suspense, useEffect, useRef, useState, type ComponentType } from 'react';
import { createBrowserRouter, Navigate, useLocation, type RouteObject } from 'react-router';
import { Button } from '../components/ui/button';
import { TooltipProvider } from '../components/ui/tooltip';
import { setAccessProblem, useAccessProblem } from '../lib/access';
import { mergeSearch, readParams } from '../lib/url';
import { VIEW_DEFINITIONS } from '../lib/views';
import { OverviewPage } from '../pages/OverviewPage';
import { ViewPage } from '../pages/ViewPage';
import { AnnouncerProvider, useAnnounce } from '../state/announcer';
import { FindingsProvider } from '../state/findings';
import { SnapshotProvider, useSnapshotStore } from '../state/snapshot';
import { applyTheme, useTheme } from '../state/theme';
import { UiProvider, useUi } from '../state/ui';
import { AccessPanel } from './AccessPanel';
import { focusMainHeading, LoadingPanel, Shell } from './Shell';

const FileSheet = lazy(() => import('./FileSheet'));
const FolderPicker = lazy(() => import('./FolderPicker'));
const loadPalette = () => import('./CommandPalette');
const CommandPalette = lazy(loadPalette);
const SearchPage = lazy(() => import('../pages/SearchPage'));
const HealthPage = lazy(() => import('../pages/HealthPage'));
const UsagePage = lazy(() => import('../pages/UsagePage'));
const FleetPage = lazy(() => import('../pages/FleetPage'));
const CfgdashSettingsPage = lazy(() => import('../pages/CfgdashSettingsPage'));

function isTyping(target: EventTarget | null): boolean {
  return target instanceof HTMLElement && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName));
}

/** `/` and unknown multi-segment paths go to the overview. */
function needsRedirect(pathname: string): boolean {
  return pathname.split('/').filter(Boolean).length !== 1;
}

function pageTitle(pathname: string): string {
  const id = pathname.split('/').filter(Boolean)[0];
  return id === 'search' ? 'Search' : id === 'cfgdash' ? 'cfgdash settings' : (VIEW_DEFINITIONS.find((v) => v.id === id)?.label ?? 'Nothing here');
}

function Root() {
  const problem = useAccessProblem();
  const store = useSnapshotStore();
  const ui = useUi();
  const location = useLocation();
  const theme = useTheme();
  const announce = useAnnounce();
  const redirect = needsRedirect(location.pathname);
  const lastPath = useRef<string | null>(null);
  const { setPickerOpen, setPaletteOpen, paletteOpen, pickerOpen } = ui;
  const { pathname } = location;
  const params = readParams(location.search);

  useEffect(() => applyTheme(theme), [theme]);

  const loaded = store.snapshot !== null;
  const [Palette, setPalette] = useState<ComponentType | null>(null);
  useEffect(() => {
    if (loaded) {
      void loadPalette()
        .then((m) => setPalette(() => m.default))
        .catch(() => undefined);
    }
  }, [loaded]);
  const [paletteMode, setPaletteMode] = useState<'ready' | 'lazy' | null>(null);
  const openPaletteMode: 'ready' | 'lazy' | null = paletteOpen ? (paletteMode ?? (Palette ? 'ready' : 'lazy')) : null;
  if (openPaletteMode !== paletteMode) {
    setPaletteMode(openPaletteMode);
  }

  useEffect(() => {
    if (redirect) {
      return;
    }
    document.title = `${pageTitle(location.pathname)} · cfgdash`;
    const changed = lastPath.current !== null && lastPath.current !== location.pathname;
    lastPath.current = location.pathname;
    if (changed && !location.hash) {
      focusMainHeading();
    }
  }, [redirect, location.pathname, location.hash]);

  useEffect(() => {
    if (store.dirLoads > 0) {
      focusMainHeading();
    }
  }, [store.dirLoads]);

  useEffect(() => {
    if (problem) {
      setPickerOpen(false);
      setPaletteOpen(false);
      announce('');
      focusMainHeading();
    }
  }, [problem, setPickerOpen, setPaletteOpen, announce]);

  useEffect(() => {
    const onKeydown = (event: KeyboardEvent) => {
      const slash = event.key === '/' && !event.ctrlKey && !event.metaKey && !event.altKey && !isTyping(event.target);
      const commandK = event.key?.toLowerCase() === 'k' && (event.ctrlKey || event.metaKey) && !event.altKey;
      if ((!slash && !commandK) || problem || paletteOpen || document.querySelector('[role="dialog"], dialog[open]')) {
        return;
      }
      event.preventDefault();
      const input = slash && pathname === '/search' ? document.getElementById('search-page-input') : null;
      if (input instanceof HTMLInputElement) {
        input.focus();
        input.select();
      } else {
        setPaletteOpen(true);
      }
    };
    document.addEventListener('keydown', onKeydown);
    return () => document.removeEventListener('keydown', onKeydown);
  }, [problem, paletteOpen, setPaletteOpen, pathname]);

  if (redirect) {
    return <Navigate to={{ pathname: '/overview', search: location.search }} replace />;
  }
  if (problem) {
    return (
      <AccessPanel
        problem={problem}
        onRetry={() => {
          setAccessProblem(null);
          store.reload();
        }}
      />
    );
  }
  return (
    <>
      <Shell />
      {(params.file || params.detail) && (
        <Suspense fallback={null}>
          <FileSheet />
        </Suspense>
      )}
      {pickerOpen && (
        <Suspense fallback={null}>
          <FolderPicker />
        </Suspense>
      )}
      {openPaletteMode === 'ready' && Palette && <Palette />}
      {openPaletteMode === 'lazy' && (
        <Suspense fallback={null}>
          <CommandPalette />
        </Suspense>
      )}
    </>
  );
}

const loadMotionFeatures = () => import('../lib/motion-features').then((m) => m.default);

/** Providers plus the root: access panel or shell, theme, focus rules, document title and the palette shortcut. */
export function App() {
  return (
    <AnnouncerProvider>
      <SnapshotProvider>
        <FindingsProvider>
          <UiProvider>
            <TooltipProvider delayDuration={300}>
              <LazyMotion features={loadMotionFeatures} strict>
                <MotionConfig reducedMotion="user">
                  <Root />
                </MotionConfig>
              </LazyMotion>
            </TooltipProvider>
          </UiProvider>
        </FindingsProvider>
      </SnapshotProvider>
    </AnnouncerProvider>
  );
}

/** `/edit` (v0.4 links) opens the Settings view in edit mode, keeping the query. */
function EditRedirect() {
  const location = useLocation();
  return <Navigate to={{ pathname: '/settings', search: mergeSearch(location.search, { edit: '1' }) }} replace />;
}

/** Replaces the app when a route fails to render, typically a lazy chunk gone after cfgdash was updated or restarted. */
function ReloadPanel() {
  return (
    <main id="content" className="flex min-h-screen items-center justify-center p-6">
      <div className="w-full max-w-lg space-y-3 rounded-xl border border-border bg-surface p-6">
        <h1 className="text-2xl leading-[30px] font-semibold">cfgdash needs a reload</h1>
        <p>It was updated or restarted while this page was open.</p>
        <Button onClick={() => window.location.reload()}>Reload</Button>
      </div>
    </main>
  );
}

/** The route table: `/overview`, `/:view`, and redirects to `/overview` (keeping the query) for `/` and deeper paths. */
export const routes: RouteObject[] = [
  {
    path: '/',
    element: <App />,
    errorElement: <ReloadPanel />,
    children: [
      { index: true, element: null },
      { path: 'overview', element: <OverviewPage /> },
      {
        path: 'search',
        element: (
          <Suspense fallback={<LoadingPanel text="Loading search..." />}>
            <SearchPage />
          </Suspense>
        )
      },
      {
        path: 'health',
        element: (
          <Suspense fallback={<LoadingPanel text="Loading health..." />}>
            <HealthPage />
          </Suspense>
        )
      },
      {
        path: 'usage',
        element: (
          <Suspense fallback={<LoadingPanel text="Loading usage..." />}>
            <UsagePage />
          </Suspense>
        )
      },
      { path: 'edit', element: <EditRedirect /> },
      {
        path: 'fleet',
        element: (
          <Suspense fallback={<LoadingPanel text="Loading fleet..." />}>
            <FleetPage />
          </Suspense>
        )
      },
      {
        path: 'cfgdash',
        element: (
          <Suspense fallback={<LoadingPanel text="Loading cfgdash settings..." />}>
            <CfgdashSettingsPage />
          </Suspense>
        )
      },
      { path: ':view', element: <ViewPage /> },
      { path: '*', element: null }
    ]
  }
];

/** Creates the browser router at the current `window.location`. */
export function createAppRouter() {
  return createBrowserRouter(routes);
}
