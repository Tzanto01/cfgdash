import { ChevronRight } from 'lucide-react';
import { useState } from 'react';
import { formatBytes, formatCount, formatTokens, itemDomId, normalizeItemId } from '../lib/format';
import type { JsonValue, TreeBlock as TreeBlockModel, TreeNode } from '../lib/models';
import { useUrl } from '../lib/url';
import { cn } from '../lib/utils';
import { useSnapshotStore } from '../state/snapshot';
import { Badges } from './Badges';

const KNOWN_META = new Set(['order', 'sizeBytes', 'tokens', 'error']);

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

function initialExpanded(block: TreeBlockModel, reveal: string | undefined): Set<string> {
  const ids = new Set(block.nodes.filter((n) => n.children?.length).map((n) => n.id));
  for (const id of reveal ? (ancestorsOf(block.nodes, reveal) ?? []) : []) {
    ids.add(id);
  }
  return ids;
}

function flatten(nodes: TreeNode[], include: (node: TreeNode) => boolean, level = 0, out: { node: TreeNode; level: number }[] = []) {
  for (const node of nodes) {
    out.push({ node, level });
    if (node.children?.length && include(node)) {
      flatten(node.children, include, level + 1, out);
    }
  }
  return out;
}

function isNotCounted(node: TreeNode): boolean {
  return !!node.badges?.some((b) => b === 'lazy' || b === 'referenced');
}

function text(value: JsonValue | undefined): string {
  return typeof value === 'string' ? value : JSON.stringify(value ?? null);
}

function tokensOf(node: TreeNode): number | undefined {
  const tokens = node.meta?.['tokens'];
  return typeof tokens === 'number' ? tokens : undefined;
}

/** tree block: summary header with the token bar, expand/collapse all, tree table of nodes. */
export function TreeBlock({ block, collectorId, revealItemId }: { block: TreeBlockModel; collectorId: string; revealItemId?: string }) {
  const url = useUrl();
  const targetDir = useSnapshotStore().snapshot?.context.targetDir;
  const [state, setState] = useState(() => ({ block, reveal: revealItemId, ids: initialExpanded(block, revealItemId) }));
  const expanded = state.block === block && state.reveal === revealItemId ? state.ids : initialExpanded(block, revealItemId);
  const setExpanded = (ids: Set<string>) => setState({ block, reveal: revealItemId, ids });
  const toggle = (id: string) => {
    const next = new Set(expanded);
    if (!next.delete(id)) {
      next.add(id);
    }
    setExpanded(next);
  };

  const summary = block.summary;
  const hasNested = block.nodes.some((n) => n.children?.length);
  const counted = flatten(block.nodes, () => true)
    .map(({ node }) => node)
    .filter((node) => !isNotCounted(node) && tokensOf(node) !== undefined);
  const total = summary?.tokens || counted.reduce((sum, node) => sum + (tokensOf(node) ?? 0), 0);
  const rows = flatten(block.nodes, (node) => expanded.has(node.id));

  return (
    <div>
      {(summary || hasNested) && (
        <div className="flex flex-wrap items-end gap-x-6 gap-y-3 border-b border-border px-4 py-3">
          {summary && (
            <div className="min-w-0 flex-1">
              <p className="text-xs text-muted">
                Loaded for <span className="font-mono">{targetDir}</span>
              </p>
              <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
                <p>
                  <span className="text-[28px] leading-8 font-semibold tabular">{formatCount(summary.tokens)}</span>{' '}
                  <span className="text-muted">tok est.</span>
                </p>
                {total > 0 && counted.length > 0 && (
                  <div className="flex h-2.5 min-w-40 flex-1 gap-0.5 overflow-hidden rounded-[5px]" aria-hidden="true">
                    {counted.map((node, index) => (
                      <span
                        key={node.id}
                        className="h-full bg-accent"
                        style={{ flexGrow: tokensOf(node) ?? 0, opacity: Math.max(0.35, 1 - index * 0.15) }}
                      />
                    ))}
                  </div>
                )}
              </div>
              <p className="text-muted">
                {`${formatCount(summary.files)} ${summary.files === 1 ? 'file' : 'files'} · ${formatBytes(summary.sizeBytes)} · ${formatTokens(summary.tokens)} (estimate)`}
              </p>
            </div>
          )}
          {hasNested && (
            <div className="flex gap-2">
              <button type="button" className="touch-target h-7 rounded-md border border-border-strong px-2 text-xs hover:bg-hover" onClick={() => setExpanded(new Set(flatten(block.nodes, () => true).filter(({ node }) => node.children?.length).map(({ node }) => node.id)))}>
                Expand all
              </button>
              <button type="button" className="touch-target h-7 rounded-md border border-border-strong px-2 text-xs hover:bg-hover" onClick={() => setExpanded(new Set())}>
                Collapse all
              </button>
            </div>
          )}
        </div>
      )}
      {block.nodes.length ? (
        <div className="overflow-x-auto">
          <table className="data-table w-full border-collapse mono-value">
            <caption className="sr-only">{block.title}</caption>
            <thead>
              <tr className="border-b border-border font-sans text-xs text-muted">
                <th scope="col" className="w-10 font-medium">#</th>
                <th scope="col" className="font-medium">Path</th>
                <th scope="col" className="font-medium">Flags</th>
                <th scope="col" className="w-28 font-medium">Share</th>
                <th scope="col" className="w-20 text-right font-medium">Size</th>
                <th scope="col" className="w-24 text-right font-medium">Tokens</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(({ node, level }) => {
                const meta = node.meta ?? {};
                const notCounted = isNotCounted(node);
                const tokens = tokensOf(node);
                const error = meta['error'];
                const size = meta['sizeBytes'];
                const hasChildren = !!node.children?.length;
                const fileId = node.fileId;
                const detailId = node.detailId;
                return (
                  <tr
                    key={node.id}
                    id={itemDomId(collectorId, block.id, node.id)}
                    tabIndex={-1}
                    className={cn('node-row border-b border-border last:border-b-0 hover:bg-hover', notCounted && 'text-muted')}
                  >
                    <td className="tabular">{meta['order'] !== undefined ? text(meta['order']) : ''}</td>
                    <td>
                      <div className="flex min-w-0 items-start gap-1" style={{ paddingLeft: level * 16 }}>
                        {hasChildren ? (
                          <button
                            type="button"
                            className="touch-target -my-0.5 inline-flex size-5 shrink-0 items-center justify-center rounded text-muted hover:bg-hover hover:text-text"
                            aria-expanded={expanded.has(node.id)}
                            aria-label={`Children of ${node.label}`}
                            onClick={() => toggle(node.id)}
                          >
                            <ChevronRight
                              className={cn('size-4 transition-transform duration-100', expanded.has(node.id) && 'rotate-90')}
                              strokeWidth={1.75}
                              aria-hidden="true"
                            />
                          </button>
                        ) : (
                          <span className="size-5 shrink-0" aria-hidden="true" />
                        )}
                        <div className="min-w-0">
                          {fileId ? (
                            <button type="button" className="node-label touch-target text-left text-accent hover:underline" onClick={() => url.openFile(fileId)}>
                              {node.label}
                            </button>
                          ) : detailId ? (
                            <button type="button" className="node-label touch-target text-left text-accent hover:underline" onClick={() => url.openDetail(detailId)}>
                              {node.label}
                            </button>
                          ) : (
                            <span className="node-label">{node.label}</span>
                          )}
                          {error !== undefined && <p className="font-sans text-xs text-danger">{text(error)}</p>}
                          {Object.entries(meta)
                            .filter(([key]) => !KNOWN_META.has(key))
                            .map(([key, value]) => (
                              <p key={key} className="font-sans text-xs text-muted">
                                {key}: {text(value)}
                              </p>
                            ))}
                        </div>
                      </div>
                    </td>
                    <td>
                      <Badges items={node.badges} />
                    </td>
                    <td>
                      {error !== undefined ? (
                        <span className="block h-1.5 w-24 rounded-[3px] border border-dashed border-danger" aria-hidden="true" />
                      ) : tokens !== undefined && total > 0 && !notCounted ? (
                        <span className="block h-1.5 w-24 overflow-hidden rounded-[3px] bg-border" aria-hidden="true">
                          <span className="block h-full bg-accent/70" style={{ width: `${Math.min(100, (tokens / total) * 100)}%` }} />
                        </span>
                      ) : null}
                    </td>
                    <td className="num">{size === undefined ? '' : typeof size === 'number' ? formatBytes(size) : text(size)}</td>
                    <td className="num">
                      {tokens !== undefined ? formatCount(tokens) : meta['tokens'] !== undefined ? text(meta['tokens']) : ''}
                      {notCounted && <span className="block font-sans text-xs">not counted</span>}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      ) : (
        <p className="px-4 py-2 text-muted">No entries.</p>
      )}
    </div>
  );
}
