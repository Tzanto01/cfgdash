import { highlight, itemDomId, normalizeItemId } from '../lib/format';
import type { Block, KvBlock, KvRow, SearchHit, Snapshot, TableBlock, TreeBlock, TreeNode } from '../lib/models';
import { linkTo } from '../lib/url';
import { navViewId } from '../lib/views';

function findNode(nodes: TreeNode[], id: string): TreeNode | undefined {
  for (const node of nodes) {
    if (normalizeItemId(node.id) === id) {
      return node;
    }
    const found = findNode(node.children ?? [], id);
    if (found) {
      return found;
    }
  }
  return undefined;
}

function itemLabel(block: Block, itemId: string): string | undefined {
  if (block.type === 'kv') {
    return ((block as KvBlock).rows ?? []).find((row) => normalizeItemId(row.key) === itemId)?.key;
  }
  if (block.type === 'table') {
    const table = block as TableBlock;
    const row = (table.rows ?? []).find((r) => normalizeItemId(r.id) === itemId);
    const column = table.columns?.[0];
    const first = row && column ? row.cells[column.key] : undefined;
    if (first === null || first === undefined) {
      return row?.id;
    }
    return typeof first === 'object' ? first.text : String(first);
  }
  if (block.type === 'tree') {
    return findNode((block as TreeBlock).nodes ?? [], itemId)?.label;
  }
  return undefined;
}

function findBlock(snapshot: Snapshot | null, hit: SearchHit) {
  const section = snapshot?.sections.find((s) => s.collectorId === hit.collectorId);
  return { section, block: section?.blocks.find((b) => b.id === hit.blockId) };
}

/** "Section › Block › Item" for row and node hits, "line n" (or "File") for file hits. */
export function hitLocation(snapshot: Snapshot | null, hit: SearchHit): string {
  if (hit.kind === 'file') {
    return hit.line ? `line ${hit.line}` : 'File';
  }
  const { section, block } = findBlock(snapshot, hit);
  const parts = [section?.title ?? hit.collectorId, block?.title ?? hit.blockId];
  const label = block && hit.itemId ? itemLabel(block, normalizeItemId(hit.itemId)) : undefined;
  if (label ?? hit.itemId) {
    parts.push(label ?? hit.itemId ?? '');
  }
  return parts.join(' › ');
}

/** The view link of a row or node hit with its item (or block) fragment, carrying `dir`. */
export function hitHref(hit: SearchHit, dir: string | undefined): string {
  const fragment = hit.itemId ? itemDomId(hit.collectorId, hit.blockId, hit.itemId) : `${hit.collectorId}/${hit.blockId}`;
  return `${linkTo(`/${navViewId(hit.view)}`, dir)}#${encodeURI(fragment)}`;
}

/** The kv row a row hit points at, if the snapshot has it. */
export function hitKvRow(snapshot: Snapshot | null, hit: SearchHit): KvRow | undefined {
  const { block } = findBlock(snapshot, hit);
  if (hit.kind !== 'row' || !hit.itemId || block?.type !== 'kv') {
    return undefined;
  }
  const itemId = normalizeItemId(hit.itemId);
  return ((block as KvBlock).rows ?? []).find((row) => normalizeItemId(row.key) === itemId);
}

/** `text` (trimmed) with every case-insensitive occurrence of `query` in a `<mark>`. */
export function Highlight({ text, query }: { text: string; query: string }) {
  return (
    <>
      {highlight(text.trim(), query).map((segment, index) =>
        segment.match ? (
          <mark key={index} className="bg-transparent font-semibold text-inherit underline decoration-2 underline-offset-2">
            {segment.text}
          </mark>
        ) : (
          <span key={index}>{segment.text}</span>
        )
      )}
    </>
  );
}
