import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import type { ContextRef } from '../lib/models';
import { useSnapshotStore } from './snapshot';

/** The most context references one assistant message carries (assistant-v1). */
export const MAX_CONTEXT = 8;

export interface ContextItem {
  ref: ContextRef;
  label: string;
}

/** The assistant side panel: open flag and the context chips collected from rows, files and findings of the current folder. */
export interface AssistantState {
  open: boolean;
  setOpen(open: boolean): void;
  items: ContextItem[];
  /** Adds a reference; `present` when it is already there, `full` at {@link MAX_CONTEXT}. */
  add(item: ContextItem): 'added' | 'present' | 'full';
  remove(index: number): void;
  /** Removes these references (the ones a message just sent). */
  drop(refs: ContextRef[]): void;
}

const AssistantContext = createContext<AssistantState | null>(null);
const same = (a: ContextRef, b: ContextRef) => JSON.stringify(a) === JSON.stringify(b);

/** Chips belong to the folder they were added in; another folder starts with none. */
export function AssistantProvider({ children }: { children: ReactNode }) {
  const dir = useSnapshotStore().snapshot?.context.targetDir;
  const [open, setOpen] = useState(false);
  const [held, setHeld] = useState<{ dir?: string; items: ContextItem[] }>({ items: [] });
  const items = useMemo(() => (held.dir === dir ? held.items : []), [held, dir]);
  const add = useCallback(
    (item: ContextItem) => {
      if (items.some((i) => same(i.ref, item.ref))) {
        return 'present' as const;
      }
      if (items.length >= MAX_CONTEXT) {
        return 'full' as const;
      }
      setHeld({ dir, items: [...items, item] });
      return 'added' as const;
    },
    [items, dir]
  );
  const remove = useCallback((index: number) => setHeld((prev) => ({ ...prev, items: prev.items.filter((_, i) => i !== index) })), []);
  const drop = useCallback(
    (refs: ContextRef[]) => setHeld((prev) => ({ ...prev, items: prev.items.filter((i) => !refs.some((r) => same(r, i.ref))) })),
    []
  );
  const value = useMemo(() => ({ open, setOpen, items, add, remove, drop }), [open, items, add, remove, drop]);
  return <AssistantContext.Provider value={value}>{children}</AssistantContext.Provider>;
}

export function useAssistant(): AssistantState {
  const state = useContext(AssistantContext);
  if (!state) {
    throw new Error('useAssistant needs an AssistantProvider');
  }
  return state;
}
