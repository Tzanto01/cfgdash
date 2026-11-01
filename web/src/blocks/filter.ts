import type { Cell, KvRow, TableRow } from '../lib/models';

export type FlagChip = 'all' | 'overridden' | 'redacted' | 'company';

/** The view toolbar filter: case-insensitive text plus one flag chip. */
export interface RowFilter {
  text: string;
  chip: FlagChip;
}

export const NO_FILTER: RowFilter = { text: '', chip: 'all' };

export function isFiltering(filter: RowFilter): boolean {
  return filter.text.trim() !== '' || filter.chip !== 'all';
}

export function cellText(cell: Cell | undefined): string {
  if (cell === null || cell === undefined) {
    return '';
  }
  return typeof cell === 'object' ? cell.text : String(cell);
}

function textMatches(parts: string[], text: string): boolean {
  const needle = text.trim().toLowerCase();
  return !needle || parts.some((part) => part.toLowerCase().includes(needle));
}

function flagMatches(chip: FlagChip, flags: string[], overridden: boolean, redacted: boolean): boolean {
  switch (chip) {
    case 'all':
      return true;
    case 'overridden':
      return overridden || flags.includes('overridden');
    case 'redacted':
      return redacted || flags.includes('redacted');
    case 'company':
      return flags.includes('company');
  }
}

/** Matches key and value text; a redacted row only matches by its key, never by a value. */
export function kvRowMatches(row: KvRow, filter: RowFilter): boolean {
  const value = row.value === undefined || row.value === null ? '' : typeof row.value === 'string' ? row.value : JSON.stringify(row.value);
  return (
    textMatches(row.redacted ? [row.key] : [row.key, value], filter.text) &&
    flagMatches(
      filter.chip,
      row.flags ?? [],
      !!row.contributions?.some((c) => c.status === 'overridden'),
      !!row.redacted
    )
  );
}

export function tableRowMatches(row: TableRow, filter: RowFilter): boolean {
  return (
    textMatches(Object.values(row.cells).map(cellText), filter.text) &&
    flagMatches(filter.chip, row.flags ?? [], false, false)
  );
}
