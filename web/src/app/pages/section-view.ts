import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  Injector,
  input,
  Signal,
  signal,
  untracked
} from '@angular/core';
import { BlockView } from '../blocks/block';
import { normalizeItemId } from '../format';
import { CollectorStatus, Section } from '../models';
import { UrlState } from '../url-state';

/** A resolved jump target: the owning block's anchor and the normalized item id (if any). */
export interface RevealTarget {
  blockAnchor: string;
  itemId?: string;
}

/**
 * Follows the URL fragment for a page showing `sections`: resolves it to a block item, lets the blocks reveal it,
 * then scrolls to it, focuses it and fades a highlight. Must run in an injection context.
 */
export function revealFromFragment(pageKey: () => string, sections: () => Section[] | undefined): Signal<RevealTarget | null> {
  const url = inject(UrlState);
  const injector = inject(Injector);
  const reveal = signal<RevealTarget | null>(null);
  let handled = '';
  effect(() => {
    const fragment = url.fragment();
    const list = sections();
    const key = `${pageKey()}#${fragment}`;
    untracked(() => {
      if (!fragment) {
        handled = '';
        return;
      }
      if (!list || key === handled) {
        return;
      }
      handled = key;
      const target = resolveFragment(list, fragment);
      reveal.set(target);
      if (target) {
        afterNextRender(() => jumpTo(target), { injector });
      }
    });
  });
  return reveal.asReadonly();
}

function resolveFragment(sections: Section[], fragment: string): RevealTarget | null {
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
  const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
  element.scrollIntoView?.({ block: 'center', behavior: reduce ? 'instant' : 'smooth' });
  element.focus({ preventScroll: true });
  element.classList.add('jump-highlight');
  setTimeout(() => element.classList.remove('jump-highlight'), 2000);
}

/** One active section: heading, collector notes, blocks. */
@Component({
  selector: 'app-section-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BlockView],
  template: `
    <section class="section">
      @if (headingLevel() === 2) {
        <h2 class="section-title">{{ section().title }}</h2>
      } @else {
        <h3 class="section-title">{{ section().title }}</h3>
      }
      @for (note of collector()?.notes ?? []; track $index) {
        <p class="note muted">{{ note }}</p>
      }
      @for (block of section().blocks; track block.id) {
        <app-block
          [block]="block"
          [collectorId]="section().collectorId"
          [headingLevel]="headingLevel() === 2 ? 3 : 4"
          [showTitle]="block.title !== section().title"
          [revealItemId]="reveal()?.blockAnchor === section().collectorId + '/' + block.id ? reveal()?.itemId : undefined"
        />
      }
    </section>
  `
})
export class SectionView {
  readonly section = input.required<Section>();
  readonly collector = input<CollectorStatus | undefined>();
  readonly headingLevel = input<2 | 3>(2);
  readonly reveal = input<RevealTarget | null>(null);
}
