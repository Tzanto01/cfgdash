import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useLocation } from 'react-router';
import { api, classifyError, isAbort, type ErrorKind } from '../lib/api';
import type { FileRef, Snapshot } from '../lib/models';
import { readParams } from '../lib/url';
import { buildViews, type ViewModel } from '../lib/views';
import { useAnnounce } from './announcer';

export type RequestState<T> =
  | { phase: 'loading' }
  | { phase: 'content'; value: T }
  | { phase: 'error'; kind: ErrorKind };

interface StoreState {
  request: RequestState<Snapshot>;
  refreshing: boolean;
  /** True when the last refresh failed and the previous content is still shown. */
  refreshFailed: boolean;
  /** Increments after each load caused by a folder change settles, with content or an error. */
  dirLoads: number;
  /** Increments after each successful refresh. */
  refreshes: number;
}

export interface SnapshotStore extends StoreState {
  snapshot: Snapshot | null;
  views: ViewModel[];
  navViews: ViewModel[];
  file(id: string | undefined): FileRef | undefined;
  refresh(): void;
  reload(): void;
}

const INITIAL: StoreState = { request: { phase: 'loading' }, refreshing: false, refreshFailed: false, dirLoads: 0, refreshes: 0 };
const SnapshotContext = createContext<SnapshotStore | null>(null);

/** Loads the snapshot for the `dir` query param; refresh keeps the current content visible. */
export function SnapshotProvider({ children }: { children: ReactNode }) {
  const { dir } = readParams(useLocation().search);
  const announce = useAnnounce();
  const [state, setState] = useState(INITIAL);
  const current = useRef(INITIAL);
  const controller = useRef<AbortController | null>(null);
  const loadedDir = useRef<string | undefined | null>(null);
  const dirRef = useRef(dir);
  dirRef.current = dir;

  const commit = useCallback((patch: Partial<StoreState>) => {
    current.current = { ...current.current, ...patch };
    setState(current.current);
  }, []);

  const load = useCallback(
    (target: string | undefined, refresh: boolean, dirChanged: boolean) => {
      controller.current?.abort();
      const request = new AbortController();
      controller.current = request;
      const refreshing = refresh && current.current.refreshing;
      commit(refreshing ? { refreshFailed: false } : { refreshFailed: false, refreshing: false, request: { phase: 'loading' } });
      api.snapshot(target, refresh, request.signal).then(
        (value) => {
          if (request.signal.aborted) {
            return;
          }
          const { dirLoads, refreshes } = current.current;
          commit({
            request: { phase: 'content', value },
            refreshing: false,
            dirLoads: dirChanged ? dirLoads + 1 : dirLoads,
            refreshes: refresh ? refreshes + 1 : refreshes
          });
          announce(
            refresh
              ? 'Snapshot refreshed.'
              : dirChanged
                ? `Folder changed to ${value.context.targetDir}. Snapshot loaded.`
                : 'Snapshot loaded.'
          );
        },
        (error: unknown) => {
          if (request.signal.aborted || isAbort(error)) {
            return;
          }
          const kind = classifyError(error);
          const keepContent = current.current.refreshing && kind !== 'access';
          if (keepContent) {
            commit({ refreshing: false, refreshFailed: true });
            announce("Couldn't refresh. Showing the previous snapshot.");
            return;
          }
          const { dirLoads } = current.current;
          commit({ refreshing: false, request: { phase: 'error', kind }, dirLoads: dirChanged ? dirLoads + 1 : dirLoads });
          announce(
            kind === 'bad-dir'
              ? "This folder can't be used."
              : kind === 'unreachable'
                ? "Couldn't load the snapshot: cfgdash isn't answering."
                : "Couldn't load the snapshot."
          );
        }
      );
    },
    [announce, commit]
  );

  useEffect(() => {
    if (dir !== loadedDir.current) {
      const changed = loadedDir.current !== null;
      loadedDir.current = dir;
      load(dir, false, changed);
    }
  }, [dir, load]);

  useEffect(
    () => () => {
      controller.current?.abort();
      loadedDir.current = null;
    },
    []
  );

  const refresh = useCallback(() => {
    const { refreshing, request } = current.current;
    if (refreshing || request.phase === 'loading') {
      return;
    }
    if (request.phase === 'content') {
      commit({ refreshing: true });
    }
    load(dirRef.current, true, false);
  }, [commit, load]);

  const reload = useCallback(() => load(dirRef.current, false, false), [load]);

  const store = useMemo<SnapshotStore>(() => {
    const snapshot = state.request.phase === 'content' ? state.request.value : null;
    const views = snapshot ? buildViews(snapshot) : [];
    const files = new Map((snapshot?.files ?? []).map((f) => [f.id, f]));
    return {
      ...state,
      snapshot,
      views,
      navViews: views.filter((view) => view.visible),
      file: (id) => (id ? files.get(id) : undefined),
      refresh,
      reload
    };
  }, [state, refresh, reload]);

  return <SnapshotContext.Provider value={store}>{children}</SnapshotContext.Provider>;
}

export function useSnapshotStore(): SnapshotStore {
  const store = useContext(SnapshotContext);
  if (!store) {
    throw new Error('useSnapshotStore needs a SnapshotProvider');
  }
  return store;
}
