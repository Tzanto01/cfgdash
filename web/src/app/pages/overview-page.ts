import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { formatCount, formatRelative, formatTime } from '../format';
import { CollectorStatus } from '../models';
import { SnapshotStore } from '../snapshot-store';
import { StateChip } from '../ui/chips';
import { Clock } from '../ui-state';
import { UrlState } from '../url-state';
import { navViewId } from '../views';
import { revealFromFragment, SectionView } from './section-view';

/** Overview: identity, sources summary, views grid, context paths, the overview sections. */
@Component({
  selector: 'app-overview-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, SectionView, StateChip],
  templateUrl: './overview-page.html'
})
export class OverviewPage {
  protected readonly store = inject(SnapshotStore);
  protected readonly url = inject(UrlState);
  private readonly clock = inject(Clock);

  protected readonly snapshot = this.store.snapshot;
  protected readonly overview = computed(() => this.store.views().find((v) => v.id === 'overview'));
  protected readonly cards = computed(() => this.store.navViews().filter((v) => v.id !== 'overview'));
  protected readonly collectorsById = computed(
    () => new Map<string, CollectorStatus>((this.snapshot()?.collectors ?? []).map((c) => [c.id, c]))
  );
  protected readonly nonActive = computed(() => this.snapshot()?.collectors.filter((c) => c.state !== 'active') ?? []);
  protected readonly sourcesSummary = computed(() => {
    const collectors = this.snapshot()?.collectors ?? [];
    const count = (state: string) => collectors.filter((c) => c.state === state).length;
    const parts = [`${count('active')} active`];
    if (count('unavailable')) {
      parts.push(`${count('unavailable')} unavailable`);
    }
    if (count('notPresent')) {
      parts.push(`${count('notPresent')} not on this machine`);
    }
    return `${collectors.length} ${collectors.length === 1 ? 'source' : 'sources'}: ${parts.join(', ')}`;
  });
  protected readonly collected = computed(() => {
    const at = this.snapshot()?.machine.collectedAt ?? '';
    return `Collected ${formatTime(at)} (${formatRelative(at, this.clock.now())})`;
  });
  protected readonly reveal = revealFromFragment(
    () => 'overview',
    () => this.overview()?.sections
  );

  protected viewLink(collector: CollectorStatus): string {
    return `/${navViewId(collector.view)}`;
  }

  protected count(value: number): string {
    return `${formatCount(value)} ${value === 1 ? 'item' : 'items'}`;
  }
}
