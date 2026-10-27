import { ChangeDetectionStrategy, Component, computed, forwardRef, inject, input, linkedSignal } from '@angular/core';
import { formatBytes, formatCount, formatTokens, itemDomId, normalizeItemId } from '../format';
import { JsonValue, TreeBlock, TreeNode } from '../models';
import { Badges } from '../ui/chips';
import { Icon } from '../ui/icon';
import { UrlState } from '../url-state';

const KNOWN_META = new Set(['order', 'sizeBytes', 'tokens', 'error']);

/** tree block: summary, expand/collapse all, nested disclosure list of nodes. */
@Component({
  selector: 'app-tree-block',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [forwardRef(() => TreeNodesView)],
  template: `
    @let s = block().summary;
    @if (s || hasNested()) {
      <div class="tree-head">
        @if (s) {
          <p class="tree-summary muted">{{ summary() }}</p>
        }
        @if (hasNested()) {
          <div class="tree-actions">
            <button type="button" class="button button-quiet" (click)="expandAll()">Expand all</button>
            <button type="button" class="button button-quiet" (click)="collapseAll()">Collapse all</button>
          </div>
        }
      </div>
    }
    @if (block().nodes.length) {
      <app-tree-nodes [nodes]="block().nodes" [level]="0" />
    } @else {
      <p class="muted">No entries.</p>
    }
  `
})
export class TreeBlockView {
  readonly block = input.required<TreeBlock>();
  readonly collectorId = input.required<string>();
  readonly revealItemId = input<string | undefined>();

  readonly expanded = linkedSignal({
    source: () => ({ block: this.block(), reveal: this.revealItemId() }),
    computation: ({ block, reveal }) => {
      const ids = new Set(block.nodes.filter((n) => n.children?.length).map((n) => n.id));
      for (const id of reveal ? ancestorsOf(block.nodes, reveal) ?? [] : []) {
        ids.add(id);
      }
      return ids;
    }
  });
  protected readonly hasNested = computed(() => this.block().nodes.some((n) => n.children?.length));
  protected readonly summary = computed(() => {
    const s = this.block().summary;
    if (!s) {
      return '';
    }
    return `${formatCount(s.files)} ${s.files === 1 ? 'file' : 'files'} · ${formatBytes(s.sizeBytes)} · ${formatTokens(s.tokens)} (estimate)`;
  });

  domId(node: TreeNode): string {
    return itemDomId(this.collectorId(), this.block().id, node.id);
  }

  toggle(id: string): void {
    const next = new Set(this.expanded());
    if (!next.delete(id)) {
      next.add(id);
    }
    this.expanded.set(next);
  }

  protected expandAll(): void {
    const ids = new Set<string>();
    const walk = (nodes: TreeNode[]) => {
      for (const node of nodes) {
        if (node.children.length) {
          ids.add(node.id);
          walk(node.children);
        }
      }
    };
    walk(this.block().nodes);
    this.expanded.set(ids);
  }

  protected collapseAll(): void {
    this.expanded.set(new Set());
  }
}

/** One level of tree nodes; renders itself for children. */
@Component({
  selector: 'app-tree-nodes',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Badges, Icon],
  template: `
    <ul class="tree-list" [class.tree-root]="level() === 0">
      @for (node of nodes(); track node.id) {
        @let notCounted = isNotCounted(node);
        @let open = tree.expanded().has(node.id);
        @let meta = node.meta ?? {};
        <li class="tree-node">
          <div class="node-row item" [id]="tree.domId(node)" tabindex="-1" [class.not-counted]="notCounted">
            @if (node.children.length) {
              <button
                type="button"
                class="disclosure"
                [attr.aria-expanded]="open"
                [attr.aria-label]="'Children of ' + node.label"
                (click)="tree.toggle(node.id)"
              >
                <app-icon name="chevron-right" />
              </button>
            } @else {
              <span class="disclosure-spacer" aria-hidden="true"></span>
            }
            <div class="node-main">
              @if (meta['order'] !== undefined) {
                <span class="node-order">{{ meta['order'] }}.</span>
              }
              @if (node.fileId) {
                <button type="button" class="link-button mono node-label" (click)="url.openFile(node.fileId)">{{ node.label }}</button>
              } @else {
                <span class="mono node-label">{{ node.label }}</span>
              }
              <app-badges [items]="node.badges" />
              @if (meta['error'] !== undefined) {
                <span class="danger-text">{{ text(meta['error']) }}</span>
              }
              @for (entry of otherMeta(node); track entry.key) {
                <span class="muted meta-entry">{{ entry.key }}: {{ entry.value }}</span>
              }
            </div>
            <div class="node-size">
              @if (meta['sizeBytes'] !== undefined) {
                <span>{{ bytes(meta['sizeBytes']) }}</span>
              }
              @if (meta['tokens'] !== undefined) {
                <span>{{ tokens(meta['tokens']) }}</span>
              }
              @if (notCounted) {
                <span>not counted</span>
              }
            </div>
          </div>
          @if (node.children.length && open) {
            <app-tree-nodes [nodes]="node.children" [level]="level() + 1" />
          }
        </li>
      }
    </ul>
  `
})
export class TreeNodesView {
  protected readonly tree = inject(TreeBlockView);
  protected readonly url = inject(UrlState);
  readonly nodes = input.required<TreeNode[]>();
  readonly level = input(0);

  protected isNotCounted(node: TreeNode): boolean {
    return !!node.badges?.some((b) => b === 'lazy' || b === 'referenced');
  }

  protected otherMeta(node: TreeNode): { key: string; value: string }[] {
    return Object.entries(node.meta ?? {})
      .filter(([key]) => !KNOWN_META.has(key))
      .map(([key, value]) => ({ key, value: this.text(value) }));
  }

  protected text(value: JsonValue | undefined): string {
    return typeof value === 'string' ? value : JSON.stringify(value ?? null);
  }

  protected bytes(value: JsonValue | undefined): string {
    return typeof value === 'number' ? formatBytes(value) : this.text(value);
  }

  protected tokens(value: JsonValue | undefined): string {
    return typeof value === 'number' ? `${formatCount(value)} tokens` : this.text(value);
  }
}

function ancestorsOf(nodes: TreeNode[], target: string, path: string[] = []): string[] | null {
  for (const node of nodes) {
    if (normalizeItemId(node.id) === target) {
      return path;
    }
    const found = ancestorsOf(node.children ?? [], target, [...path, node.id]);
    if (found) {
      return found;
    }
  }
  return null;
}
