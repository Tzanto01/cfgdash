import { ChangeDetectionStrategy, Component, input, linkedSignal } from '@angular/core';
import { inlineValue, itemDomId } from '../format';
import { Contribution, KvBlock, KvRow } from '../models';
import { Badges, SourceChip } from '../ui/chips';
import { JsonValueView } from './json-value';

/** kv block: key, value with provenance (overridden line, layers disclosure), source chip and flags. */
@Component({
  selector: 'app-kv-block',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Badges, SourceChip, JsonValueView],
  template: `
    @if (block().rows.length) {
      <ul class="kv">
        @for (row of block().rows; track row.key) {
          <li class="kv-row item" [id]="domId(row.key)" tabindex="-1">
            <div class="kv-key mono">{{ row.key }}</div>
            <div class="kv-value mono">
              <app-json-value [value]="row.value" [redacted]="!!row.redacted" />
              @for (c of overridden(row); track $index) {
                <p class="overrides muted">
                  Overrides <s>{{ inline(c) }}</s> from {{ c.source.label }}
                </p>
              }
              @if ((row.contributions?.length ?? 0) > 1) {
                <button
                  type="button"
                  class="link-button layers-toggle"
                  [attr.aria-expanded]="open().has(row.key)"
                  (click)="toggle(row.key)"
                >
                  {{ row.contributions!.length }} layers
                </button>
                @if (open().has(row.key)) {
                  <ol class="layers">
                    @for (c of row.contributions!; track $index) {
                      <li class="layer">
                        <span class="badge" [attr.data-tone]="tone(c)">{{ c.status }}</span>
                        <span class="layer-value" [class.struck]="c.status === 'overridden'">
                          <app-json-value [value]="c.value" [redacted]="!!row.redacted" />
                        </span>
                        <app-source-chip [source]="c.source" />
                      </li>
                    }
                  </ol>
                }
              }
            </div>
            <div class="kv-meta">
              <app-source-chip [source]="row.source" />
              <app-badges [items]="row.flags" />
            </div>
          </li>
        }
      </ul>
    } @else {
      <p class="muted">No rows.</p>
    }
  `
})
export class KvBlockView {
  readonly block = input.required<KvBlock>();
  readonly collectorId = input.required<string>();
  protected readonly open = linkedSignal({ source: this.block, computation: () => new Set<string>() });

  protected domId(key: string): string {
    return itemDomId(this.collectorId(), this.block().id, key);
  }

  protected overridden(row: KvRow): Contribution[] {
    return (row.contributions ?? []).filter((c) => c.status === 'overridden');
  }

  protected inline(c: Contribution): string {
    return inlineValue(c.value);
  }

  protected tone(c: Contribution): string {
    return c.status === 'applied' ? 'ok' : c.status === 'overridden' ? 'warn' : 'neutral';
  }

  protected toggle(key: string): void {
    const next = new Set(this.open());
    if (!next.delete(key)) {
      next.add(key);
    }
    this.open.set(next);
  }
}
