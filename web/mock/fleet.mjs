/**
 * Dev-only mock of the fleet routes (`/api/v1/exports`, `/api/v1/compare`, `/api/v1/fleet`, docs/contracts/fleet-v1.md)
 * and of the health-v1 rules `mcp-drift` and `instructions-drift` over the mock snapshot. Synthetic data only.
 *
 * Peers: `laptop` answers compares, `homeserver` is refused and `buildbox` is absent (502 `peer-unavailable`).
 */
import { START_DIR } from './data.mjs';
import { resolveDir } from './dirs.mjs';

const SELF = { name: 'devbox', dnsName: 'devbox.example.ts.net', os: 'windows' };
const EXPORTS = [
  { id: 'devbox-20261101T080000Z', machine: 'devbox', collectedAt: '2026-11-01T08:00:00Z', dir: START_DIR },
  { id: 'devbox-20261020T170000Z', machine: 'devbox', collectedAt: '2026-10-20T17:00:00Z', dir: START_DIR },
];
const PEERS = [
  { name: 'laptop', dnsName: 'laptop.example.ts.net', os: 'windows', online: true, cfgdash: 'ok', version: '0.5.0' },
  { name: 'homeserver', dnsName: 'homeserver.example.ts.net', os: 'linux', online: true, cfgdash: 'refused', reason: 'port 7820 not reachable on the tailnet' },
  { name: 'buildbox', dnsName: 'buildbox.example.ts.net', os: 'linux', online: true, cfgdash: 'absent', reason: 'cfgdash not running on the peer' },
];
const EXPORT_ID = /^[A-Za-z0-9._-]+$/;
const SEVERITY = { critical: 0, warning: 1, info: 2 };
const active = { left: 'active', right: 'active' };

const exportRows = [
  { id: 'settings.effective', state: active, rows: [
    { blockId: 'effective', itemId: 'effortLevel', change: 'changed', left: 'high', right: 'medium' },
  ] },
  { id: 'mcp.servers', state: active, rows: [
    { blockId: 'servers', itemId: 'user-engram', change: 'onlyLeft', left: { name: 'engram', scope: 'user', transport: 'stdio', target: 'engram mcp --tools=agent' } },
  ] },
];

const peerRows = [
  { id: 'settings.effective', state: active, rows: [
    { blockId: 'effective', itemId: 'model', change: 'changed', left: 'opus', right: 'sonnet' },
    { blockId: 'effective', itemId: 'env.CLAUDE_CODE_USE_POWERSHELL_TOOL', change: 'onlyLeft', left: '1' },
    { blockId: 'effective', itemId: 'statusLine.command', change: 'changed', left: '<claudeConfigDir>/statusline.ps1', right: '<claudeConfigDir>/statusline.sh' },
  ] },
  { id: 'agents.definitions', state: active, rows: [
    { blockId: 'agents', itemId: 'user-writer', change: 'onlyLeft', left: { name: 'writer', scope: 'user', model: 'sonnet', path: '<claudeConfigDir>/agents/writer.md' } },
    { blockId: 'agents', itemId: 'user-x', change: 'onlyRight', right: { name: 'x', scope: 'user', model: 'haiku', path: '<claudeConfigDir>/agents/x.md' } },
  ] },
  { id: 'mcp.servers', state: active, rows: [
    { blockId: 'servers', itemId: 'user-browser', change: 'changed', left: { target: 'npx -y @example/browser-mcp@1.4.0' }, right: { target: 'npx -y @example/browser-mcp@1.5.0' } },
    { blockId: 'servers', itemId: 'user-notes', change: 'onlyRight', right: { name: 'notes', scope: 'user', transport: 'stdio', target: '<workspaceRoot>/tools/notes-mcp' } },
  ] },
  { id: 'codex.config', state: { left: 'active', right: 'notPresent' }, rows: [
    { blockId: 'settings', itemId: 'model', change: 'onlyLeft', left: 'gpt-5-codex' },
    { blockId: 'mcp', itemId: 'docs', change: 'onlyLeft', left: { name: 'docs', transport: 'stdio', target: 'npx -y docs-mcp' } },
  ] },
];

function diff(dir, right, collectors) {
  const counts = { changed: 0, onlyLeft: 0, onlyRight: 0 };
  for (const c of collectors) for (const r of c.rows) counts[r.change]++;
  return { left: { machine: SELF.name, collectedAt: new Date().toISOString(), dir }, right, counts, collectors: structuredClone(collectors) };
}

function compare(url) {
  const raw = url.searchParams.get('dir');
  const dir = raw === null ? START_DIR : resolveDir(raw);
  if (!dir) return [400, { error: 'bad-dir' }];
  const against = url.searchParams.get('against') ?? '';
  if (against.startsWith('export:')) {
    const id = against.slice(7);
    if (!EXPORT_ID.test(id)) return [400, { error: 'bad-against' }];
    const exp = EXPORTS.find((e) => e.id === id);
    if (!exp) return [404, { error: 'not-found' }];
    return [200, diff(dir, { machine: exp.machine, collectedAt: exp.collectedAt, dir: exp.dir }, exportRows)];
  }
  if (against.startsWith('peer:')) {
    const name = against.slice(5).toLowerCase().replace(/\.$/, '');
    if (!name) return [400, { error: 'bad-against' }];
    const peer = PEERS.find((p) => p.dnsName === name);
    if (!peer) return [404, { error: 'not-found' }];
    if (peer.cfgdash !== 'ok') return [502, { error: 'peer-unavailable', reason: peer.reason }];
    return [200, diff(dir, { machine: peer.name, collectedAt: new Date(Date.now() - 90_000).toISOString(), dir: 'C:/Users/user/ws/app' }, peerRows)];
  }
  return [400, { error: 'bad-against' }];
}

/**
 * @param {import('node:http').IncomingMessage} req
 * @param {URL} url
 * @returns {[number, object] | null} the response, or null when the path is not a fleet route
 */
export function fleetApi(req, url) {
  const route = { '/api/v1/exports': () => [200, { exports: structuredClone(EXPORTS) }], '/api/v1/compare': () => compare(url), '/api/v1/fleet': () => [200, { self: { ...SELF }, peers: structuredClone(PEERS) }] }[url.pathname];
  if (!route) return null;
  return req.method === 'GET' ? route() : [405, { error: 'method-not-allowed' }];
}

/**
 * Appends mock `mcp-drift` and `instructions-drift` findings for rows present in the snapshot.
 * @param {{ counts: object, findings: object[] }} report the findings report from health.mjs
 * @param {object} snapshot the same mock snapshot
 * @returns {object} a new report, re-sorted, with counts recomputed
 */
export function withDriftFindings(report, snapshot) {
  const section = (id) => snapshot.sections.find((s) => s.collectorId === id);
  const row = (collectorId, blockId, itemId) => {
    const s = section(collectorId);
    const block = s?.blocks.find((b) => b.id === blockId);
    const r = block?.rows?.find((x) => x.id === itemId);
    return r ? { s, r } : null;
  };
  const extra = [];
  const add = (rule, title, detail, s, blockId, itemId, fileId) => extra.push({
    id: `${rule}:${s.collectorId}:${blockId}:${itemId}`, rule, severity: 'info', title, detail,
    evidence: { collectorId: s.collectorId, blockId, itemId, ...(fileId ? { fileId } : {}) }, view: s.view,
  });

  const claudeDocs = row('mcp.servers', 'servers', 'project-docs');
  const codexDocs = row('codex.config', 'mcp', 'docs');
  if (claudeDocs && codexDocs && claudeDocs.r.cells.target !== codexDocs.r.cells.target) {
    add('mcp-drift', 'MCP server defined differently', `docs runs ${claudeDocs.r.cells.target} in Claude Code and ${codexDocs.r.cells.target} in Codex.`, claudeDocs.s, 'servers', 'project-docs', claudeDocs.r.fileId);
  }
  const codexGlobal = row('codex.config', 'instructions', '2');
  if (codexGlobal && section('opencode.config')) {
    add('instructions-drift', 'Instruction file not shared', `${codexGlobal.r.cells.file.text} is loaded by Codex but not by Claude Code.`, codexGlobal.s, 'instructions', '2', codexGlobal.r.fileId);
  }

  const findings = [...report.findings, ...extra.filter((f) => !report.findings.some((x) => x.id === f.id))];
  findings.sort((a, b) => SEVERITY[a.severity] - SEVERITY[b.severity] || (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  const counts = { critical: 0, warning: 0, info: 0 };
  for (const f of findings) counts[f.severity]++;
  return { ...report, counts, findings };
}
