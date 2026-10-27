import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { CollectorState, SourceRef } from '../models';
import { SnapshotStore } from '../snapshot-store';
import { UrlState } from '../url-state';
import { stateLabel } from '../views';
import { Icon } from './icon';

const BADGE_TONES: Record<string, string> = {
  'missing-script': 'danger',
  error: 'danger',
  overridden: 'warn',
  conditional: 'warn',
  company: 'company',
  loaded: 'ok',
  applied: 'ok'
};

/** Flags and tree badges; known vocabulary is toned by meaning, everything else is neutral. Text is always shown. */
@Component({
  selector: 'app-badges',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  template: `
    @if (items().length) {
      <ul class="badges">
        @for (item of items(); track $index) {
          <li class="badge" [attr.data-tone]="tone(item)">
            @if (item === 'redacted') {
              <app-icon name="lock" />
            }
            {{ item }}
          </li>
        }
      </ul>
    }
  `
})
export class Badges {
  readonly items = input<string[], string[] | undefined>([], { transform: (v) => v ?? [] });

  tone(item: string): string {
    return BADGE_TONES[item] ?? 'neutral';
  }
}

/** Source label; a button opening the file viewer when the source has a file. */
@Component({
  selector: 'app-source-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  template: `
    @let src = source();
    @if (src) {
      @if (src.fileId) {
        <button type="button" class="chip chip-button" [attr.aria-label]="'Open ' + path()" (click)="url.openFile(src.fileId)">
          <app-icon name="file" />{{ src.label }}
        </button>
      } @else {
        <span class="chip">{{ src.label }}</span>
      }
    }
  `
})
export class SourceChip {
  protected readonly url = inject(UrlState);
  private readonly store = inject(SnapshotStore);
  readonly source = input<SourceRef | undefined>();
  protected readonly path = computed(() => {
    const src = this.source();
    return this.store.file(src?.fileId)?.path ?? src?.label ?? '';
  });
}

/** Collector state: dot plus plain text (active, unavailable, not on this machine). */
@Component({
  selector: 'app-state-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="state" [attr.data-state]="state()"><span class="dot" aria-hidden="true"></span>{{ label() }}</span>`
})
export class StateChip {
  readonly state = input.required<CollectorState>();
  protected readonly label = computed(() => stateLabel(this.state()));
}
