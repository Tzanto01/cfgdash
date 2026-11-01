/**
 * Synthetic `usage.metrics` collector and section (docs/contracts/usage-v1.md) for the dev mock. Deterministic: values
 * derive from the day index, dates count back from the snapshot's collection day in UTC.
 */
const DAYS = 30;
const MODELS = [['claude-opus-4-1', 0.62], ['claude-sonnet-4-5', 0.31], ['claude-haiku-4-5', 0.07]];
const MACHINES = [['devbox', 0.7], ['laptop.example.ts.net', 0.3]];
const PROJECTS = [['cfgdash', 0.55], ['website', 0.3], ['config', 0.15]];
const round2 = (n) => Math.round(n * 100) / 100;

function days(collectedAt) {
  const today = new Date(collectedAt.slice(0, 10) + 'T00:00:00Z');
  return Array.from({ length: DAYS }, (_, i) => {
    const date = new Date(today.getTime() - i * 86400000).toISOString().slice(0, 10);
    const weekend = [0, 6].includes(new Date(date).getUTCDay());
    const load = weekend ? 0.2 : 0.6 + ((i * 7) % 5) / 10;
    return {
      date,
      cost: round2(18 * load),
      input: Math.round(140000 * load),
      output: Math.round(52000 * load),
      cacheRead: Math.round(2600000 * load),
      cacheCreation: Math.round(310000 * load),
      sessions: Math.max(1, Math.round(9 * load)),
    };
  });
}

/**
 * Appends the `usage.metrics` collector status and section to a mock snapshot.
 * @param {object} snap the snapshot under construction
 */
export function addUsageMetrics(snap) {
  const rows = days(snap.machine.collectedAt);
  const sum = (list, key) => list.reduce((acc, r) => acc + r[key], 0);
  const tokens = (list) => sum(list, 'input') + sum(list, 'output') + sum(list, 'cacheRead') + sum(list, 'cacheCreation');
  const cost30 = round2(sum(rows, 'cost'));
  const tokens30 = tokens(rows);
  const sessions30 = sum(rows, 'sessions');
  const kv = (key, value, source) => ({ key, value, ...(source ? { source: { label: source } } : {}) });
  const share = ([name, part], key) => ({
    id: name,
    cells: { [key]: name, cost: round2(cost30 * part), tokens: Math.round(tokens30 * part), sessions: Math.round(sessions30 * part) },
  });

  snap.collectors.push({
    id: 'usage.metrics', title: 'Usage and cost', version: '1.0.0', view: 'usage', state: 'active', requirements: { targetDir: false },
    durationMs: 182, notes: ['Counters read as delta temporality (Claude Code default)'],
  });
  snap.sections.push({
    collectorId: 'usage.metrics', view: 'usage', title: 'Usage and cost',
    blocks: [
      { type: 'kv', id: 'totals', title: 'Totals', rows: [
        kv('cost-today', rows[0].cost),
        kv('cost-7d', round2(sum(rows.slice(0, 7), 'cost'))),
        kv('cost-30d', cost30),
        kv('tokens-30d', tokens30),
        kv('sessions-30d', sessions30),
        kv('active-time-30d', '41.5 h'),
        kv('source', 'metrics.example:8428', 'metrics store'),
        kv('range', `${rows[DAYS - 1].date} to ${rows[0].date} (UTC)`),
      ] },
      { type: 'table', id: 'by-day', title: 'By day', description: 'Newest first, days in UTC',
        columns: [{ key: 'date', label: 'Date' }, { key: 'cost', label: 'Cost (USD)' }, { key: 'input', label: 'Input' }, { key: 'output', label: 'Output' },
          { key: 'cache-read', label: 'Cache read' }, { key: 'cache-creation', label: 'Cache creation' }, { key: 'sessions', label: 'Sessions' }],
        rows: rows.map((r) => ({ id: r.date, cells: { date: r.date, cost: r.cost, input: r.input, output: r.output, 'cache-read': r.cacheRead, 'cache-creation': r.cacheCreation, sessions: r.sessions } })) },
      { type: 'table', id: 'by-model', title: 'By model', description: 'Last 30 days',
        columns: [{ key: 'model', label: 'Model' }, { key: 'cost', label: 'Cost (USD)' }, { key: 'input', label: 'Input' }, { key: 'output', label: 'Output' },
          { key: 'cache-read', label: 'Cache read' }, { key: 'cache-creation', label: 'Cache creation' }],
        rows: MODELS.map(([model, part]) => ({ id: model, cells: {
          model, cost: round2(cost30 * part), input: Math.round(sum(rows, 'input') * part), output: Math.round(sum(rows, 'output') * part),
          'cache-read': Math.round(sum(rows, 'cacheRead') * part), 'cache-creation': Math.round(sum(rows, 'cacheCreation') * part),
        } })) },
      { type: 'table', id: 'by-machine', title: 'By machine', description: 'Last 30 days per host.name',
        columns: [{ key: 'machine', label: 'Machine' }, { key: 'cost', label: 'Cost (USD)' }, { key: 'tokens', label: 'Tokens' }, { key: 'sessions', label: 'Sessions' }],
        rows: MACHINES.map((m) => share(m, 'machine')) },
      { type: 'table', id: 'by-project', title: 'By project', description: 'Last 30 days per project',
        columns: [{ key: 'project', label: 'Project' }, { key: 'cost', label: 'Cost (USD)' }, { key: 'tokens', label: 'Tokens' }, { key: 'sessions', label: 'Sessions' }],
        rows: PROJECTS.map((p) => share(p, 'project')) },
    ],
  });
}
