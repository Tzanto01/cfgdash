import type { Block as BlockModel, KvBlock as KvBlockModel, TableBlock as TableBlockModel, TreeBlock as TreeBlockModel } from '../lib/models';
import type { RowFilter } from './filter';
import { KvBlock } from './KvBlock';
import { TableBlock } from './TableBlock';
import { TreeBlock } from './TreeBlock';

/** Renders any block by `type`; unknown types get a fallback note. Container id: `<collectorId>/<blockId>`. */
export function Block({
  block,
  collectorId,
  headingLevel = 3,
  showTitle = true,
  revealItemId,
  filter
}: {
  block: BlockModel;
  collectorId: string;
  headingLevel?: 3 | 4;
  /** False when the block title repeats its section title. */
  showTitle?: boolean;
  revealItemId?: string;
  filter?: RowFilter;
}) {
  const Heading = headingLevel === 4 ? 'h4' : 'h3';
  return (
    <div id={`${collectorId}/${block.id}`} tabIndex={-1} className="border-t border-border first:border-t-0">
      {showTitle && (
        <Heading className="flex min-h-8 items-center border-b border-border px-4 text-sm font-medium">{block.title}</Heading>
      )}
      {block.description && <p className="px-4 pt-2 text-xs text-muted">{block.description}</p>}
      {block.type === 'kv' ? (
        <KvBlock block={block as KvBlockModel} collectorId={collectorId} filter={filter} />
      ) : block.type === 'table' ? (
        <TableBlock block={block as TableBlockModel} collectorId={collectorId} revealItemId={revealItemId} filter={filter} />
      ) : block.type === 'tree' ? (
        <TreeBlock block={block as TreeBlockModel} collectorId={collectorId} revealItemId={revealItemId} />
      ) : (
        <p className="px-4 py-2 text-muted">This block type ({block.type}) needs a newer cfgdash UI.</p>
      )}
    </div>
  );
}
