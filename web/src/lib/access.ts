import { useSyncExternalStore } from 'react';

export type AccessProblem =
  | { kind: 'signed-out' }
  | { kind: 'forbidden'; reason: string }
  | { kind: 'bad-host' };

let problem: AccessProblem | null = null;
const listeners = new Set<() => void>();

/** App-level access state; while set, the app shows only the access panel. */
export function getAccessProblem(): AccessProblem | null {
  return problem;
}

export function setAccessProblem(next: AccessProblem | null): void {
  problem = next;
  listeners.forEach((listener) => listener());
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** The current access problem, re-rendering on change. */
export function useAccessProblem(): AccessProblem | null {
  return useSyncExternalStore(subscribe, getAccessProblem, getAccessProblem);
}
