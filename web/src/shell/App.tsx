import { LazyMotion, MotionConfig } from 'motion/react';
import { useEffect, useRef } from 'react';
import { createBrowserRouter, Navigate, useLocation, type RouteObject } from 'react-router';
import { TooltipProvider } from '../components/ui/tooltip';
import { setAccessProblem, useAccessProblem } from '../lib/access';
import { VIEW_DEFINITIONS } from '../lib/views';
import { OverviewPage } from '../pages/OverviewPage';
import { ViewPage } from '../pages/ViewPage';
import { AnnouncerProvider } from '../state/announcer';
import { SnapshotProvider, useSnapshotStore } from '../state/snapshot';
import { applyTheme, useTheme } from '../state/theme';
import { UiProvider, useUi } from '../state/ui';
import { AccessPanel } from './AccessPanel';
import { focusMainHeading, Shell } from './Shell';

function isTyping(target: EventTarget | null): boolean {
  return target instanceof HTMLElement && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName));
}

/** `/` and unknown multi-segment paths go to the overview. */
function needsRedirect(pathname: string): boolean {
  return pathname.split('/').filter(Boolean).length !== 1;
}

function pageTitle(pathname: string): string {
  const id = pathname.split('/').filter(Boolean)[0];
  return VIEW_DEFINITIONS.find((v) => v.id === id)?.label ?? 'Nothing here';
}

function Root() {
  const problem = useAccessProblem();
  const store = useSnapshotStore();
  const ui = useUi();
  const location = useLocation();
  const theme = useTheme();
  const redirect = needsRedirect(location.pathname);
  const lastPath = useRef<string | null>(null);
  const { setPickerOpen, setPaletteOpen, paletteOpen } = ui;

  useEffect(() => applyTheme(theme), [theme]);

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
      focusMainHeading();
    }
  }, [problem, setPickerOpen, setPaletteOpen]);

  useEffect(() => {
    const onKeydown = (event: KeyboardEvent) => {
      const slash = event.key === '/' && !event.ctrlKey && !event.metaKey && !event.altKey && !isTyping(event.target);
      const commandK = event.key.toLowerCase() === 'k' && (event.ctrlKey || event.metaKey) && !event.altKey;
      if ((!slash && !commandK) || problem || paletteOpen || document.querySelector('[role="dialog"], dialog[open]')) {
        return;
      }
      event.preventDefault();
      setPaletteOpen(true);
    };
    document.addEventListener('keydown', onKeydown);
    return () => document.removeEventListener('keydown', onKeydown);
  }, [problem, paletteOpen, setPaletteOpen]);

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
  return <Shell />;
}

const loadMotionFeatures = () => import('../lib/motion-features').then((m) => m.default);

/** Providers plus the root: access panel or shell, theme, focus rules, document title and the palette shortcut. */
export function App() {
  return (
    <AnnouncerProvider>
      <SnapshotProvider>
        <UiProvider>
          <TooltipProvider delayDuration={300}>
            <LazyMotion features={loadMotionFeatures} strict>
              <MotionConfig reducedMotion="user">
                <Root />
              </MotionConfig>
            </LazyMotion>
          </TooltipProvider>
        </UiProvider>
      </SnapshotProvider>
    </AnnouncerProvider>
  );
}

/** The route table: `/overview`, `/:view`, and redirects to `/overview` (keeping the query) for `/` and deeper paths. */
export const routes: RouteObject[] = [
  {
    path: '/',
    element: <App />,
    children: [
      { index: true, element: null },
      { path: 'overview', element: <OverviewPage /> },
      { path: ':view', element: <ViewPage /> },
      { path: '*', element: null }
    ]
  }
];

/** Creates the browser router at the current `window.location`. */
export function createAppRouter() {
  return createBrowserRouter(routes);
}
