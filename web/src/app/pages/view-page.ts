import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CollectorStatus, Section } from '../models';
import { SnapshotStore } from '../snapshot-store';
import { FolderPickerState } from '../ui-state';
import { UrlState } from '../url-state';
import { ViewModel } from '../views';
import { revealFromFragment, SectionView } from './section-view';

interface SectionGroup {
  viewId: string;
  sections: Section[];
}

/** Generic view page for a known view id, `other`, or an unknown id ("Nothing here"). */
@Component({
  selector: 'app-view-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, SectionView],
  templateUrl: './view-page.html'
})
export class ViewPage {
  protected readonly store = inject(SnapshotStore);
  protected readonly url = inject(UrlState);
  private readonly picker = inject(FolderPickerState);

  readonly view = input<string>('');

  protected readonly model = computed<ViewModel | undefined>(() =>
    this.store.views().find((v) => v.id === this.view())
  );
  protected readonly collectorsById = computed(
    () => new Map<string, CollectorStatus>((this.store.snapshot()?.collectors ?? []).map((c) => [c.id, c]))
  );
  protected readonly needsFolder = computed(() => !!this.model()?.collectors.some((c) => c.requirements?.targetDir));
  protected readonly unavailable = computed(() => this.model()?.collectors.filter((c) => c.state === 'unavailable') ?? []);
  protected readonly notPresent = computed(() => this.model()?.collectors.filter((c) => c.state === 'notPresent') ?? []);
  protected readonly notPresentText = computed(() =>
    this.notPresent()
      .map((c) => (c.reason ? `${c.title} (${c.reason})` : c.title))
      .join(', ')
  );
  /** Sections grouped by their own view id: one group for a known view, one per unknown id for `other`. */
  protected readonly groups = computed<SectionGroup[]>(() => {
    const model = this.model();
    if (!model) {
      return [];
    }
    if (model.id !== 'other') {
      return [{ viewId: model.id, sections: model.sections }];
    }
    const groups = new Map<string, Section[]>();
    for (const collector of model.collectors) {
      groups.set(collector.view, groups.get(collector.view) ?? []);
    }
    for (const section of model.sections) {
      groups.set(section.view, [...(groups.get(section.view) ?? []), section]);
    }
    return [...groups].map(([viewId, sections]) => ({ viewId, sections }));
  });
  protected readonly reveal = revealFromFragment(
    () => this.view(),
    () => this.model()?.sections
  );

  protected openPicker(): void {
    this.picker.open.set(true);
  }
}
