import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from 'react';

const AnnounceContext = createContext<(text: string) => void>(() => undefined);

/** Owns the single polite live region; children announce through {@link useAnnounce}. */
export function AnnouncerProvider({ children }: { children: ReactNode }) {
  const [message, setMessage] = useState('');
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);

  const announce = useCallback((text: string) => {
    clearTimeout(timer.current);
    setMessage('');
    timer.current = setTimeout(() => setMessage(text), 100);
  }, []);

  useEffect(() => () => clearTimeout(timer.current), []);

  return (
    <AnnounceContext.Provider value={announce}>
      {children}
      <div className="sr-only" role="status" aria-live="polite">
        {message}
      </div>
    </AnnounceContext.Provider>
  );
}

/** Announces `text` in the polite live region. */
export function useAnnounce(): (text: string) => void {
  return useContext(AnnounceContext);
}
