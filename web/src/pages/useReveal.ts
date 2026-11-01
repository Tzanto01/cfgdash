import { useEffect, useRef, useState } from 'react';
import { useLocation } from 'react-router';
import { normalizeItemId } from '../lib/format';
import type { Section } from '../lib/models';
import { readFragment } from '../lib/url';

/** A resolved jump target: the owning block's anchor and the normalized item id (if any). */
export interface RevealTarget {
  blockAnchor: string;
  itemId?: string;
}

/** The block with the longest anchor that the fragment names, with the item id after it. */
export function resolveFragment(sections: Section[], fragment: string): RevealTarget | null {
  let best: RevealTarget | null = null;
  for (const section of sections) {
    for (const block of section.blocks) {
      const anchor = `${section.collectorId}/${block.id}`;
      if (best && best.blockAnchor.length >= anchor.length) {
        continue;
      }
      if (fragment === anchor) {
        best = { blockAnchor: anchor };
      } else if (fragment.startsWith(`${anchor}/`)) {
        best = { blockAnchor: anchor, itemId: normalizeItemId(fragment.slice(anchor.length + 1)) };
      }
    }
  }
  return best;
}

function jumpTo(target: RevealTarget): void {
  const item = target.itemId ? document.getElementById(`${target.blockAnchor}/${target.itemId}`) : null;
  const element = item ?? document.getElementById(target.blockAnchor);
  if (!element) {
    return;
  }
  const reduce = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  element.scrollIntoView({ block: 'center', behavior: reduce ? 'instant' : 'smooth' });
  element.focus({ preventScroll: true });
  const highlight = reduce ? 'jump-static' : 'jump-flash';
  element.classList.add(highlight);
  setTimeout(() => element.classList.remove(highlight), reduce ? 2000 : 400);
}

/**
 * Follows the URL fragment for a page showing `sections`: resolves it to a block item, hands it to the blocks so they
 * reveal it (`onTarget` runs first, e.g. to clear a filter that hides it), then scrolls to it, focuses it and flashes it.
 */
export function useReveal(pageKey: string, sections: Section[] | undefined, onTarget?: (target: RevealTarget) => void): RevealTarget | null {
  const location = useLocation();
  const fragment = readFragment(location.hash);
  const { search, key: navigation } = location;
  const [reveal, setReveal] = useState<RevealTarget | null>(null);
  const handled = useRef<{ key: string; search: string; navigation: string } | null>(null);
  const pending = useRef<RevealTarget | null>(null);

  useEffect(() => {
    if (!fragment) {
      handled.current = null;
      return;
    }
    if (!sections) {
      return;
    }
    const key = `${pageKey}#${fragment}`;
    const last = handled.current;
    handled.current = { key, search, navigation };
    if (last?.key === key && (last.search !== search || last.navigation === navigation)) {
      return;
    }
    const target = resolveFragment(sections, fragment);
    if (target) {
      onTarget?.(target);
    }
    pending.current = target;
    setReveal(target);
  }, [fragment, search, navigation, pageKey, sections, onTarget]);

  useEffect(() => {
    if (reveal && pending.current === reveal) {
      pending.current = null;
      jumpTo(reveal);
    }
  }, [reveal]);

  return reveal;
}
