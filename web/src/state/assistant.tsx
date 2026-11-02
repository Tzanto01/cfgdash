import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react';
import type { ContextRef } from '../lib/models';

/** The most context references one assistant message carries (assistant-v1). */
export const MAX_CONTEXT = 8;

export interface ContextItem {
  ref: ContextRef;
  label: string;
}

/** The assistant side panel: open flag and the context chips collected from rows, files and findings. */
export interface AssistantState {
  open: boolean;
  setOpen(open: boolean): void;
  items: ContextItem[];
  /** Adds a reference; `present` when it is already there, `full` at {@link MAX_CONTEXT}. */
  add(item: ContextItem): 'added' | 'present' | 'full';
  remove(index: number): void;
  clear(): void;
}

const AssistantContext = createContext<AssistantState | null>(null);
const same = (a: ContextRef, b: ContextRef) => JSON.stringify(a) === JSON.stringify(b);

export function AssistantProvider({ children }: { children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const [items, setItems] = useState<ContextItem[]>([]);
  const add = useCallback(
    (item: ContextItem) => {
      if (items.some((i) => same(i.ref, item.ref))) {
        return 'present' as const;
      }
      if (items.length >= MAX_CONTEXT) {
        return 'full' as const;
      }
      setItems([...items, item]);
      return 'added' as const;
    },
    [items]
  );
  const remove = useCallback((index: number) => setItems((prev) => prev.filter((_, i) => i !== index)), []);
  const clear = useCallback(() => setItems([]), []);
  const value = useMemo(() => ({ open, setOpen, items, add, remove, clear }), [open, items, add, remove, clear]);
  return <AssistantContext.Provider value={value}>{children}</AssistantContext.Provider>;
}

export function useAssistant(): AssistantState {
  const state = useContext(AssistantContext);
  if (!state) {
    throw new Error('useAssistant needs an AssistantProvider');
  }
  return state;
}
