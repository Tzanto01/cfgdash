/**
 * Mock of `GET /api/v1/findings` (docs/contracts/health-v1.md): the health-v1 rules evaluated over the mock snapshot,
 * so every finding's evidence points at a row, node or collector that exists in it. Mock kv rows carry overridden
 * contributions without the `overridden` flag the core's settings merge adds, so those count as flagged here.
 */

const SEVERITY = { critical: 0, warning: 1, info: 2 };
const TITLES = {
  'missing-script': 'Hook script not found',
  'missing-command': 'MCP server command not found',
  'import-error': 'Instruction import failed',
  error: 'Entry reports an error',
  duplicate: 'Duplicate entry',
  'collector-error': 'Collector failed',
  'collector-unavailable': 'Collector unavailable',
  overridden: 'Setting overridden',
};

const cellText = (v) => (typeof v === 'string' ? v : v && typeof v === 'object' && typeof v.text === 'string' ? v.text : null) || null;

/**
 * @param {object} snapshot a mock snapshot from buildSnapshot
 * @returns {{ dir: string, collectedAt: string, counts: { critical: number, warning: number, info: number }, findings: object[] }}
 */
export function findings(snapshot) {
  const list = [];
  const ids = new Set();
  const add = (id, rule, severity, detail, evidence, view) => {
    if (ids.has(id)) return;
    ids.add(id);
    list.push({ id, rule, severity, title: TITLES[rule], detail, evidence, view });
  };

  for (const c of snapshot.collectors) {
    if (c.state !== 'unavailable') continue;
    const reason = c.reason ?? '';
    const rule = reason.endsWith(' not installed') ? 'collector-unavailable' : 'collector-error';
    add(`${rule}:${c.id}`, rule, rule === 'collector-error' ? 'warning' : 'info', `${c.title}: ${reason}`, { collectorId: c.id }, c.view);
  }

  for (const section of snapshot.sections) {
    for (const block of section.blocks) {
      const item = (flags, { itemId, key, fileId, label, row, node }) => {
        for (const flag of flags ?? []) {
          let rule;
          let severity = 'warning';
          let detail;
          if (flag === 'missing-script') {
            rule = 'missing-script';
            severity = 'critical';
            const script = cellText(row?.cells.script);
            detail = script ? `${label} runs ${script}, which does not exist.` : `${label} in ${block.title} points at a script that does not exist.`;
          } else if (flag === 'missing-command') {
            rule = 'missing-command';
            const target = cellText(row?.cells.target);
            detail = target ? `${label} runs ${target}, which is neither an existing absolute path nor on PATH.` : `${label} in ${block.title} has a command that was not found.`;
          } else if (flag === 'error' && node && section.collectorId === 'instructions.chain') {
            rule = 'import-error';
            detail = typeof node.meta?.error === 'string' ? `${label}: ${node.meta.error}` : `${label} could not be loaded.`;
          } else if (flag === 'error') {
            rule = 'error';
            const status = cellText(row?.cells.status);
            detail = status ? `${label} in ${block.title}: ${status}` : `${label} in ${block.title} reports an error.`;
          } else if (flag === 'duplicate' && !node) {
            rule = 'duplicate';
            detail = `${label} in ${block.title} is defined more than once; one entry silently loses.`;
          } else if (flag === 'overridden' && !row && !node) {
            rule = 'overridden';
            severity = 'info';
            detail = `${label} in ${block.title} is set in more than one layer; the highest-precedence layer applies.`;
          } else continue;
          const evidence = { collectorId: section.collectorId, blockId: block.id, ...(itemId ? { itemId } : {}), ...(key ? { key } : {}), ...(fileId ? { fileId } : {}) };
          add(`${rule}:${section.collectorId}:${block.id}:${itemId ?? key}`, rule, severity, detail, evidence, section.view);
        }
      };
      const walk = (nodes) => {
        for (const node of nodes) {
          item(node.badges, { itemId: node.id, fileId: node.fileId, label: node.label, node });
          walk(node.children ?? []);
        }
      };
      if (block.type === 'kv') {
        for (const row of block.rows) {
          const overridden = !row.flags?.includes('overridden') && row.contributions?.some((c) => c.status === 'overridden');
          item(overridden ? [...(row.flags ?? []), 'overridden'] : row.flags, { key: row.key, fileId: row.source?.fileId, label: row.key });
        }
      } else if (block.type === 'table') {
        const first = block.columns[0]?.key;
        for (const row of block.rows) item(row.flags, { itemId: row.id, fileId: row.fileId, label: cellText(row.cells[first]) ?? row.id, row });
      } else if (block.type === 'tree') {
        walk(block.nodes);
      }
    }
  }

  list.sort((a, b) => SEVERITY[a.severity] - SEVERITY[b.severity] || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  const counts = { critical: 0, warning: 0, info: 0 };
  for (const f of list) counts[f.severity]++;
  return { dir: snapshot.context.targetDir, collectedAt: snapshot.machine.collectedAt, counts, findings: list };
}
