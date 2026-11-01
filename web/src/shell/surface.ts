import { useEffect, useRef, useState } from 'react';
import { useLocation, useNavigate } from 'react-router';
import { mergeSearch } from '../lib/url';
import { useAnnounce } from '../state/announcer';
import { useSnapshotStore } from '../state/snapshot';

let handedOver: HTMLElement | null = null;

/**
 * Focus return for a dialog surface that is mounted while open: remembers the element focused at mount (or the trigger
 * handed over by the surface that opened this one) and focuses it again after the surface unmounts, if still present.
 */
export function useReturnFocus() {
  const [trigger] = useState<HTMLElement | null>(() => {
    const active = document.activeElement;
    return handedOver ?? (active instanceof HTMLElement ? active : null);
  });
  const restore = useRef(true);
  const mounted = useRef(false);

  useEffect(() => {
    handedOver = null;
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  return {
    /** For Radix `onCloseAutoFocus`. */
    onCloseAutoFocus(event: Event): void {
      event.preventDefault();
      if (!mounted.current && restore.current && trigger?.isConnected) {
        trigger.focus();
      }
    },
    /** Closes without restoring focus: the next surface takes over the trigger, or the page moves focus itself. */
    handOff(toNextSurface: boolean): void {
      restore.current = false;
      handedOver = toNextSurface ? trigger : null;
    }
  };
}

/** Makes `path` the target folder (dropping `file` and `line`) unless it already is; returns whether it changed. */
export function useChooseFolder(): (path: string) => boolean {
  const store = useSnapshotStore();
  const location = useLocation();
  const navigate = useNavigate();
  const announce = useAnnounce();
  return (path) => {
    if (path === store.snapshot?.context.targetDir) {
      return false;
    }
    void navigate({ pathname: location.pathname, search: mergeSearch(location.search, { dir: path, file: undefined, line: undefined }) });
    announce(`Folder changed to ${path}.`);
    return true;
  };
}
