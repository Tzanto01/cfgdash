import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { useLocation } from 'react-router';
import { api, classifyError, isAbort, type ErrorKind } from '../lib/api';
import type { FindingsResponse } from '../lib/models';
import { readParams } from '../lib/url';
import { useSnapshotStore } from './snapshot';

export type FindingsRequest =
  | { phase: 'loading' }
  | { phase: 'content'; value: FindingsResponse }
  | { phase: 'error'; kind: ErrorKind };

export interface FindingsStore {
  request: FindingsRequest;
  /** The last findings for the current folder, kept while a refresh's findings load. */
  findings: FindingsResponse | null;
  reload(): void;
}

const FindingsContext = createContext<FindingsStore | null>(null);

/** Loads `GET /api/v1/findings` for the `dir` query param after every new snapshot (load, folder change, refresh). */
export function FindingsProvider({ children }: { children: ReactNode }) {
  const { dir } = readParams(useLocation().search);
  const snapshot = useSnapshotStore().snapshot;
  const [state, setState] = useState<{ request: FindingsRequest; last: FindingsResponse | null }>({ request: { phase: 'loading' }, last: null });
  const [reloads, setReloads] = useState(0);
  const dirRef = useRef(dir);
  dirRef.current = dir;
  const targetDir = snapshot?.context.targetDir;

  useEffect(() => {
    if (!snapshot) {
      return;
    }
    const request = new AbortController();
    setState((s) => ({ request: { phase: 'loading' }, last: s.last?.dir === snapshot.context.targetDir ? s.last : null }));
    api.findings(dirRef.current, request.signal).then(
      (value) => {
        if (!request.signal.aborted) {
          setState({ request: { phase: 'content', value }, last: value });
        }
      },
      (error: unknown) => {
        if (!request.signal.aborted && !isAbort(error)) {
          setState((s) => ({ ...s, request: { phase: 'error', kind: classifyError(error) } }));
        }
      }
    );
    return () => request.abort();
  }, [snapshot, reloads]);

  const reload = useCallback(() => setReloads((n) => n + 1), []);

  const store = useMemo<FindingsStore>(
    () => ({ request: state.request, findings: state.last?.dir === targetDir ? state.last : null, reload }),
    [state, targetDir, reload]
  );
  return <FindingsContext.Provider value={store}>{children}</FindingsContext.Provider>;
}

export function useFindings(): FindingsStore {
  const store = useContext(FindingsContext);
  if (!store) {
    throw new Error('useFindings needs a FindingsProvider');
  }
  return store;
}
