import { useEffect, useState, useSyncExternalStore } from 'react';

/** A coarse clock for relative times ("2 min ago"), ticking every 30 s. */
export function useNow(): number {
  const [now, setNow] = useState(Date.now);
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), 30_000);
    return () => clearInterval(id);
  }, []);
  return now;
}

/** Whether `query` matches, following changes. */
export function useMediaQuery(query: string): boolean {
  return useSyncExternalStore(
    (onChange) => {
      const list = window.matchMedia(query);
      list.addEventListener('change', onChange);
      return () => list.removeEventListener('change', onChange);
    },
    () => window.matchMedia(query).matches,
    () => false
  );
}

/** True below 768px, where the shell switches to the top and bottom bars. */
export function useNarrow(): boolean {
  return useMediaQuery('(max-width: 767px)');
}

export function useReducedMotion(): boolean {
  return useMediaQuery('(prefers-reduced-motion: reduce)');
}
