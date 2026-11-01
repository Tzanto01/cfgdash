import { screen } from '@testing-library/react';
import type { Block, CollectorStatus, KvBlock, Snapshot, TableBlock, TableRow } from '../lib/models';
import { makeSnapshot } from '../test/fixtures';
import { startApp, type AppHarness } from '../test/harness';

const COLLECTOR = 'usage.metrics';

const kv = (key: string, value: string | number): { key: string; value: string | number } => ({ key, value });

const totals: KvBlock = {
  type: 'kv',
  id: 'totals',
  title: 'Totals',
  rows: [
    kv('cost-today', 1.5),
    kv('cost-7d', 12.25),
    kv('cost-30d', 40),
    kv('tokens-30d', 1234567),
    kv('sessions-30d', 1234),
    kv('active-time-30d', '41.5 h'),
    kv('source', 'metrics.example:8428'),
    kv('range', '2026-10-01 to 2026-10-03 (UTC)')
  ]
};

const dayRows = (costs: (number | null)[]): TableRow[] =>
  costs.map((cost, index) => ({ id: `2026-10-0${costs.length - index}`, cells: { date: `2026-10-0${costs.length - index}`, cost, sessions: 2 } }));

const byDay = (costs: (number | null)[] = [2, 1.5, 0]): TableBlock => ({
  type: 'table',
  id: 'by-day',
  title: 'By day',
  columns: [
    { key: 'date', label: 'Date' },
    { key: 'cost', label: 'Cost (USD)' },
    { key: 'sessions', label: 'Sessions' }
  ],
  rows: dayRows(costs)
});

const grouped = (id: string, title: string, key: string, names: string[]): TableBlock => ({
  type: 'table',
  id,
  title,
  columns: [
    { key, label: title },
    { key: 'cost', label: 'Cost (USD)' }
  ],
  rows: names.map((name) => ({ id: name, cells: { [key]: name, cost: 3 } }))
});

const allBlocks = (): Block[] => [
  totals,
  byDay(),
  grouped('by-model', 'By model', 'model', ['claude-opus-4-1', 'claude-haiku-4-5']),
  grouped('by-machine', 'By machine', 'machine', ['devbox', 'laptop.example.ts.net']),
  grouped('by-project', 'By project', 'project', ['alpha-app', 'beta-site'])
];

function usageSnapshot(state: CollectorStatus['state'], blocks: Block[], reason?: string): Snapshot {
  const snapshot = makeSnapshot();
  snapshot.collectors.push({ id: COLLECTOR, title: 'Usage and cost', version: '1.0.0', view: 'usage', state, reason, requirements: {}, durationMs: 1 });
  snapshot.sections.push({ collectorId: COLLECTOR, view: 'usage', title: 'Usage and cost', blocks });
  return snapshot;
}

async function openUsage(snapshot: Snapshot): Promise<AppHarness> {
  const h = await startApp('/usage', snapshot);
  await screen.findByRole('heading', { level: 1, name: 'Usage' });
  return h;
}

function tile(label: string): string | undefined {
  const heading = [...document.querySelectorAll('main p')].find((p) => p.textContent === label);
  return heading?.parentElement?.querySelector('div')?.textContent ?? undefined;
}

const table = (name: string) => screen.queryByRole('table', { name });

describe('usage page', () => {
  it('formats the totals tiles and shows the source and range line', async () => {
    const h = await openUsage(usageSnapshot('active', allBlocks()));

    expect(tile('Cost today')).toBe('$1.50');
    expect(tile('Cost, 7 days')).toBe('$12.25');
    expect(tile('Cost, 30 days')).toBe('$40.00');
    expect(tile('Tokens, 30 days')).toBe('1.2M');
    expect(tile('Sessions, 30 days')).toBe('1,234');
    expect(tile('Active time, 30 days')).toBe('41.5 h');
    expect(h.text()).toContain('Source metrics.example:8428 · 2026-10-01 to 2026-10-03 (UTC)');
  });

  it('draws the cost chart with a label that summarises the range and total', async () => {
    await openUsage(usageSnapshot('active', allBlocks()));

    const chart = screen.getByRole('img', { name: /^Cost per day/ });

    expect(chart.tagName.toLowerCase()).toBe('svg');
    expect(chart.getAttribute('aria-label')).toBe('Cost per day for the last 3 days, from $0.00 to $2.00, total $3.50');
  });

  it('renders the by-day, by-model, by-machine and by-project tables with their rows', async () => {
    await openUsage(usageSnapshot('active', allBlocks()));

    expect(table('By day')?.querySelectorAll('tbody tr')).toHaveLength(3);
    expect(table('By model')?.textContent).toContain('claude-opus-4-1');
    expect(table('By machine')?.textContent).toContain('laptop.example.ts.net');
    expect(table('By project')?.textContent).toContain('beta-site');
  });

  it('omits the tables, the chart and the tiles the collector did not send', async () => {
    await openUsage(usageSnapshot('active', [totals, byDay()]));

    expect(table('By day')).not.toBeNull();
    expect(table('By model')).toBeNull();
    expect(table('By machine')).toBeNull();
    expect(table('By project')).toBeNull();
    expect(tile('Cost today')).toBe('$1.50');
  });

  it('shows no chart and says so when no day has a cost, but still lists the days', async () => {
    await openUsage(usageSnapshot('active', [totals, byDay([0, null])]));

    expect(screen.queryByRole('img', { name: /^Cost per day/ })).toBeNull();
    expect(document.querySelector('main')?.textContent).toContain('No cost recorded in this range.');
    expect(table('By day')?.querySelectorAll('tbody tr')).toHaveLength(2);
  });

  it('says the collector is not on this machine, with its reason, and shows no tiles', async () => {
    const h = await openUsage(usageSnapshot('notPresent', allBlocks(), 'no metrics endpoint configured'));

    expect(h.text()).toContain('Not on this machine: Usage and cost (no metrics endpoint configured)');
    expect(tile('Cost today')).toBeUndefined();
    expect(table('By day')).toBeNull();
  });

  it('says the collector is unavailable, with its reason, and shows no tiles', async () => {
    const h = await openUsage(usageSnapshot('unavailable', allBlocks(), 'connection refused'));

    expect(h.text()).toContain('Usage and cost: unavailable (connection refused)');
    expect(tile('Cost today')).toBeUndefined();
    expect(table('By day')).toBeNull();
  });
});
