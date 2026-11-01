import type { TableBlock } from '../lib/models';
import { dailyCosts, shortDate } from './UsageChart';

const block = (rows: TableBlock['rows']): TableBlock => ({
  type: 'table',
  id: 'by-day',
  title: 'By day',
  columns: [
    { key: 'date', label: 'Date' },
    { key: 'cost', label: 'Cost (USD)' }
  ],
  rows
});

describe('dailyCosts', () => {
  it('returns the newest-first rows oldest first and maps a cost that is not a finite number to null', () => {
    const days = dailyCosts(
      block([
        { id: 'r3', cells: { date: '2026-10-03', cost: 3 } },
        { id: 'r2', cells: { date: '2026-10-02', cost: 'n/a' } },
        { id: 'r1', cells: { date: '2026-10-01', cost: null } },
        { id: 'r0', cells: { date: '2026-09-30', cost: Number.NaN } }
      ])
    );

    expect(days).toEqual([
      { date: '2026-09-30', cost: null },
      { date: '2026-10-01', cost: null },
      { date: '2026-10-02', cost: null },
      { date: '2026-10-03', cost: 3 }
    ]);
  });

  it('falls back to the row id when the date cell is not text', () => {
    expect(dailyCosts(block([{ id: '2026-10-05', cells: { cost: 1 } }]))).toEqual([{ date: '2026-10-05', cost: 1 }]);
  });
});

describe('shortDate', () => {
  it.each([
    ['2026-10-03', 'Oct 3'],
    ['2026-01-31', 'Jan 31'],
    ['2026-13-01', '2026-13-01'],
    ['yesterday', 'yesterday']
  ])('turns %s into %s', (text, expected) => {
    expect(shortDate(text)).toBe(expected);
  });
});

