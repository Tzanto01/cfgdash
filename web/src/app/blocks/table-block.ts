import { ChangeDetectionStrategy, Component, computed, inject, input, linkedSignal } from '@angular/core';
import { itemDomId, normalizeItemId } from '../format';
import { Cell, LinkCell, TableBlock, TableColumn, TableRow } from '../models';
import { Badges } from '../ui/chips';
import { Icon } from '../ui/icon';
import { UrlState } from '../url-state';

const PREVIEW_ROWS = 50;
const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

interface Sort {
  key: string;
  direction: 'ascending' | 'descending';
}

/** table block: sortable semantic table, link cells, row files, flags, stacked cards on phones. */
@Component({
  selector: 'app-table-block',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Badges, Icon],
  templateUrl: './table-block.html'
})
export class TableBlockView {
  protected readonly url = inject(UrlState);
  readonly block = input.required<TableBlock>();
  readonly collectorId = input.required<string>();
  readonly revealItemId = input<string | undefined>();

  protected readonly sort = linkedSignal<TableBlock, Sort | null>({ source: this.block, computation: () => null });
  protected readonly showAll = linkedSignal({
    source: () => ({ block: this.block(), reveal: this.revealItemId() }),
    computation: ({ block, reveal }) => !!reveal && block.rows.some((row) => normalizeItemId(row.id) === reveal)
  });
  protected readonly hasFlags = computed(() => this.block().rows.some((row) => (row.flags?.length ?? 0) > 0));
  protected readonly sortedRows = computed(() => {
    const rows = this.block().rows;
    const sort = this.sort();
    if (!sort) {
      return rows;
    }
    const factor = sort.direction === 'ascending' ? 1 : -1;
    return [...rows].sort((a, b) => factor * compareCells(a.cells[sort.key], b.cells[sort.key]));
  });
  protected readonly visibleRows = computed(() =>
    this.showAll() ? this.sortedRows() : this.sortedRows().slice(0, PREVIEW_ROWS)
  );
  protected readonly preview = PREVIEW_ROWS;

  protected domId(row: TableRow): string {
    return itemDomId(this.collectorId(), this.block().id, row.id);
  }

  protected ariaSort(column: TableColumn): string | null {
    const sort = this.sort();
    return sort?.key === column.key ? sort.direction : null;
  }

  protected cycleSort(column: TableColumn): void {
    const sort = this.sort();
    if (sort?.key !== column.key) {
      this.sort.set({ key: column.key, direction: 'ascending' });
    } else if (sort.direction === 'ascending') {
      this.sort.set({ key: column.key, direction: 'descending' });
    } else {
      this.sort.set(null);
    }
  }

  protected link(cell: Cell | undefined): LinkCell | null {
    return cell !== null && typeof cell === 'object' ? cell : null;
  }

  /** The file the first cell opens: its own link, else the row file. */
  protected firstCellFile(row: TableRow): string | undefined {
    const first = this.block().columns.at(0);
    return this.link(first ? row.cells[first.key] : undefined)?.fileId ?? row.fileId;
  }

  protected text(cell: Cell | undefined): string {
    return cellText(cell);
  }

  /** Link cells and strings that look like absolute paths render in mono. */
  protected isMono(cell: Cell | undefined): boolean {
    return !!this.link(cell) || (typeof cell === 'string' && /^([A-Za-z]:[\\/]|\/|~\/)/.test(cell));
  }

  protected isNumber(cell: Cell | undefined): boolean {
    return typeof cell === 'number';
  }

  protected isEmpty(cell: Cell | undefined): boolean {
    return cell === null || cell === undefined;
  }
}

function cellText(cell: Cell | undefined): string {
  if (cell === null || cell === undefined) {
    return '';
  }
  return typeof cell === 'object' ? cell.text : String(cell);
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
