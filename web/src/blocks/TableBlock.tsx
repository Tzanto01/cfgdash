import { ArrowDown, ArrowUp, ArrowUpDown } from 'lucide-react';
import { useState } from 'react';
import { itemDomId, normalizeItemId } from '../lib/format';
import type { Cell, LinkCell, TableBlock as TableBlockModel, TableColumn, TableRow } from '../lib/models';
import { useUrl } from '../lib/url';
import { cn } from '../lib/utils';
import { Badges } from './Badges';
import { cellText, NO_FILTER, tableRowMatches, type RowFilter } from './filter';

const PREVIEW_ROWS = 50;
const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

interface Sort {
  key: string;
  direction: 'ascending' | 'descending';
}

function link(cell: Cell | undefined): LinkCell | null {
  return cell !== null && typeof cell === 'object' ? cell : null;
}

function compareCells(a: Cell | undefined, b: Cell | undefined): number {
  const aEmpty = a === null || a === undefined;
  const bEmpty = b === null || b === undefined;
  if (aEmpty || bEmpty) {
    return aEmpty === bEmpty ? 0 : aEmpty ? 1 : -1;
  }
  if (typeof a === 'number' && typeof b === 'number') {
    return a - b;
  }
  return collator.compare(cellText(a), cellText(b));
}

/** Link cells and strings that look like absolute paths render in mono. */
function isMono(cell: Cell | undefined): boolean {
  return !!link(cell) || (typeof cell === 'string' && /^([A-Za-z]:[\\/]|\/|~\/)/.test(cell));
}

const LINK = 'touch-target text-left font-mono text-accent underline-offset-2 hover:underline';

/** table block: sortable semantic table, link cells, row files, flags, 50-row preview, stacked cards on phones. */
export function TableBlock({
  block,
  collectorId,
  revealItemId,
  filter = NO_FILTER
}: {
  block: TableBlockModel;
  collectorId: string;
  revealItemId?: string;
  filter?: RowFilter;
}) {
  const url = useUrl();
  const [sortState, setSortState] = useState<{ block: TableBlockModel; sort: Sort | null }>({ block, sort: null });
  const [shownAllFor, setShownAllFor] = useState<TableBlockModel | null>(null);
  const sort = sortState.block === block ? sortState.sort : null;
  const showAll =
    shownAllFor === block || (!!revealItemId && block.rows.some((row) => normalizeItemId(row.id) === revealItemId));

  if (!block.rows.length) {
    return <p className="px-4 py-2 text-muted">No rows.</p>;
  }

  const hasFlags = block.rows.some((row) => (row.flags?.length ?? 0) > 0);
  const filtered = block.rows.filter((row) => tableRowMatches(row, filter));
  const sorted = sort
    ? [...filtered].sort((a, b) => (sort.direction === 'ascending' ? 1 : -1) * compareCells(a.cells[sort.key], b.cells[sort.key]))
    : filtered;
  const visible = showAll ? sorted : sorted.slice(0, PREVIEW_ROWS);
  const ariaSort = (column: TableColumn) => (sort?.key === column.key ? sort.direction : undefined);
  const cycleSort = (column: TableColumn) =>
    setSortState({
      block,
      sort:
        sort?.key !== column.key
          ? { key: column.key, direction: 'ascending' }
          : sort.direction === 'ascending'
            ? { key: column.key, direction: 'descending' }
            : null
    });
  const firstCellFile = (row: TableRow) => {
    const first = block.columns.at(0);
    return link(first ? row.cells[first.key] : undefined)?.fileId ?? row.fileId;
  };

  return (
    <>
      <div className="overflow-x-auto">
        <table className="data-table stack w-full border-collapse mono-value">
          <caption className="sr-only">{block.title}</caption>
          <thead>
            <tr className="border-b border-border">
              {block.columns.map((column) => {
                const direction = ariaSort(column);
                const Icon = direction === 'ascending' ? ArrowUp : direction === 'descending' ? ArrowDown : ArrowUpDown;
                return (
                  <th key={column.key} scope="col" aria-sort={direction} className="font-sans text-xs font-medium text-muted">
                    <button type="button" className="touch-target inline-flex items-center gap-1 hover:text-text" onClick={() => cycleSort(column)}>
                      {column.label}
                      <Icon className="size-3.5" strokeWidth={1.75} aria-hidden="true" />
                    </button>
                  </th>
                );
              })}
              {hasFlags && (
                <th scope="col" className="font-sans text-xs font-medium text-muted">
                  Flags
                </th>
              )}
            </tr>
          </thead>
          <tbody>
            {visible.map((row) => {
              const rowFile = firstCellFile(row);
              return (
                <tr
                  key={row.id}
                  id={itemDomId(collectorId, block.id, row.id)}
                  tabIndex={-1}
                  className="border-b border-border transition-colors duration-100 last:border-b-0 hover:bg-hover"
                >
                  {block.columns.map((column, index) => {
                    const cell = row.cells[column.key];
                    const first = index === 0;
                    const cellFile = link(cell)?.fileId;
                    const fileId = first ? rowFile : cellFile === rowFile ? undefined : cellFile;
                    const detailId = first ? row.detailId : undefined;
                    return (
                      <td key={column.key} data-label={column.label} className={cn(typeof cell === 'number' && 'num')}>
                        {fileId ? (
                          <button type="button" className={LINK} onClick={() => url.openFile(fileId)}>
                            {cellText(cell) || 'Open file'}
                          </button>
                        ) : detailId ? (
                          <button type="button" className={LINK} onClick={() => url.openDetail(detailId)}>
                            {cellText(cell) || 'Open detail'}
                          </button>
                        ) : cell === null || cell === undefined ? (
                          <>
                            <span className="text-muted" aria-hidden="true">
                              -
                            </span>
                            <span className="sr-only">empty</span>
                          </>
                        ) : (
                          <span className={isMono(cell) || typeof cell === 'number' ? 'font-mono' : 'font-sans'}>{cellText(cell)}</span>
                        )}
                      </td>
                    );
                  })}
                  {hasFlags && (
                    <td data-label="Flags">
                      <Badges items={row.flags} />
                    </td>
                  )}
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {!filtered.length && <p className="px-4 py-2 text-muted">No rows match the filter.</p>}
      {filtered.length > PREVIEW_ROWS && !showAll && (
        <button type="button" className="touch-target px-4 py-2 text-sm text-accent hover:underline" onClick={() => setShownAllFor(block)}>
          Show all {filtered.length} rows
        </button>
      )}
    </>
  );
}
