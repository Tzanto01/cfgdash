import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { Block, KvBlock, TableBlock, TreeBlock } from '../models';
import { KvBlockView } from './kv-block';
import { TableBlockView } from './table-block';
import { TreeBlockView } from './tree-block';

/** Renders any block by `type`; unknown types get a fallback note. Container id: `<collectorId>/<blockId>`. */
@Component({
  selector: 'app-block',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [KvBlockView, TableBlockView, TreeBlockView],
  template: `
    <div class="block" [id]="anchor()" tabindex="-1">
      @if (headingLevel() === 4) {
        <h4 class="block-title">{{ block().title }}</h4>
      } @else {
        <h3 class="block-title">{{ block().title }}</h3>
      }
      @if (block().description) {
        <p class="block-description muted">{{ block().description }}</p>
      }
      @if (kv(); as b) {
        <app-kv-block [block]="b" [collectorId]="collectorId()" />
      } @else if (table(); as b) {
        <app-table-block [block]="b" [collectorId]="collectorId()" [revealItemId]="revealItemId()" />
      } @else if (tree(); as b) {
        <app-tree-block [block]="b" [collectorId]="collectorId()" [revealItemId]="revealItemId()" />
      } @else {
        <p class="muted">This block type ({{ block().type }}) needs a newer cfgdash UI.</p>
      }
    </div>
  `
})
export class BlockView {
  readonly block = input.required<Block>();
  readonly collectorId = input.required<string>();
  readonly revealItemId = input<string | undefined>();
  readonly headingLevel = input<3 | 4>(3);

  protected readonly anchor = computed(() => `${this.collectorId()}/${this.block().id}`);
  protected readonly kv = computed(() => (this.block().type === 'kv' ? (this.block() as KvBlock) : null));
  protected readonly table = computed(() => (this.block().type === 'table' ? (this.block() as TableBlock) : null));
  protected readonly tree = computed(() => (this.block().type === 'tree' ? (this.block() as TreeBlock) : null));
}
