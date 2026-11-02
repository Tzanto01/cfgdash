/**
 * Dev-only mock of the guarded edit routes (`/api/v2`, docs/contracts/edits-v1.md) on synthetic in-memory settings.
 * One state is shared by every target dir; the Origin check is skipped (the dev origin differs), the
 * `X-Cfgdash-Request: 1` header and the 64 KiB body cap are enforced.
 *
 * Triggers:
 * - a proposal that sets `model` to `"stale"` answers its apply with 409 `stale`;
 * - a proposal that sets `model` to `"mismatch"` answers its apply with 400 `confirm-mismatch`, even with the echoed confirm.
 */
import { createHash, randomBytes } from 'node:crypto';
import { START_DIR } from './data.mjs';
import { resolveDir } from './dirs.mjs';

const MAX_BODY = 64 * 1024;
const TTL_MS = 10 * 60 * 1000;
const ALL = ['user', 'project', 'local'];
const PRECEDENCE = ['local', 'project', 'user'];
const SECRET = /token|secret|passw(or)?d|api[_-]?key|credential|private[_-]?key/i;
const ENV_NAME = /^[A-Za-z_][A-Za-z0-9_]{0,127}$/;
const CATEGORIES = [
  ['general', 'General'], ['permissions', 'Permissions'], ['env', 'Environment'], ['config', 'Config repo'], ['other', 'Other'],
];
const CATALOG = [
  { key: 'model', category: 'general', label: 'Model', description: 'Model used for new sessions.', type: 'string', scopes: ALL },
  { key: 'alwaysThinkingEnabled', category: 'general', label: 'Always think', description: 'Turn on extended thinking by default.', type: 'bool', scopes: ALL },
  { key: 'cleanupPeriodDays', category: 'general', label: 'Cleanup period', description: 'Days to keep local session transcripts.', type: 'number', scopes: ALL },
  {
    key: 'permissions.defaultMode', category: 'permissions', label: 'Default permission mode', description: 'Permission mode a session starts in.',
    type: 'enum', options: ['default', 'acceptEdits', 'plan', 'bypassPermissions'], scopes: ALL,
  },
  { key: 'permissions.allow', category: 'permissions', label: 'Allow rules', description: 'Tool uses allowed without asking.', type: 'stringList', scopes: ALL },
  { key: 'permissions.deny', category: 'permissions', label: 'Deny rules', description: 'Tool uses that are always refused.', type: 'stringList', scopes: ALL },
  {
    key: 'configAutoPull', category: 'config', label: 'Config auto-pull', type: 'bool', scopes: ['machine'],
    description: 'Pull the config repo when a session starts. CC_CONFIG_AUTOPULL overrides this switch when it is set.',
  },
];
const CATALOG_ROOTS = new Set(['env', ...CATALOG.map((d) => d.key.split('.')[0])]);

const USER_FILE = 'C:/Users/user/.claude/settings.json';
const MACHINE_FILE = 'D:/ws/config/.claude/machine.json';
const state = {
  user: {
    model: 'sonnet',
    permissions: { defaultMode: 'default', allow: ['Bash(npm test:*)', 'Read(D:/ws/**)'] },
    env: { HTTPS_PROXY: 'http://proxy.example:3128', EXAMPLE_API_TOKEN: 'synthetic-token-value' },
    alwaysThinkingEnabled: true,
    customTheme: 'dusk',
  },
  project: { permissions: { deny: ['Read(./secrets/**)'] }, env: { DEMO_MODE: '1' } },
  local: null,
  machine: { configAutoPull: true },
};
const proposals = new Map();
const audit = [];
const backups = new Map();

const sha256 = (text) => createHash('sha256').update(text).digest('hex');
const text = (obj) => (obj === null ? null : JSON.stringify(obj, null, 2) + '\n');
const isObject = (v) => typeof v === 'object' && v !== null && !Array.isArray(v);
const invalid = (key, reason) => [400, { error: 'invalid-change', key: key ?? null, reason }];

function filePath(scope, dir) {
  if (scope === 'user') return USER_FILE;
  if (scope === 'machine') return MACHINE_FILE;
  return `${dir.replace(/\/+$/, '')}/.claude/${scope === 'local' ? 'settings.local.json' : 'settings.json'}`;
}

function redact(value, secret = false) {
  if (secret) return '[redacted]';
  if (Array.isArray(value)) return value.map((v) => redact(v));
  if (isObject(value)) return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, redact(v, SECRET.test(k))]));
  return value;
}

function get(obj, key) {
  let node = obj;
  for (const part of key.split('.')) {
    if (!isObject(node) || !(part in node)) return undefined;
    node = node[part];
  }
  return node;
}

function definition(key) {
  const def = CATALOG.find((d) => d.key === key);
  if (def) return def;
  const name = key.startsWith('env.') ? key.slice(4) : null;
  if (name === null || !ENV_NAME.test(name)) return null;
  const secret = SECRET.test(name);
  return {
    key, category: 'env', label: name, description: 'Environment variable set for every session.', type: 'string', scopes: ALL,
    ...(secret ? { editable: false, reason: 'secret' } : {}),
  };
}

function row(def) {
  const scopes = def.scopes;
  const secret = def.reason === 'secret';
  const values = Object.fromEntries(scopes.map((s) => [s, get(state[s], def.key) ?? null]));
  const set = (scopes.length === 1 ? scopes : PRECEDENCE).filter((s) => values[s] !== null);
  let effective = set.length ? values[set[0]] : null;
  let effectiveSource = set[0] ?? null;
  if (def.type === 'stringList' && set.length > 1) {
    effective = [...new Set([...set].reverse().flatMap((s) => values[s]))];
    effectiveSource = 'merged';
  }
  return {
    key: def.key, label: def.label, description: def.description, type: def.type, ...(def.options ? { options: def.options } : {}),
    scopes, values: Object.fromEntries(scopes.map((s) => [s, values[s] === null ? null : redact(values[s], secret)])),
    effective: effective === null ? null : redact(effective, secret), effectiveSource,
    editable: def.editable ?? true, ...(def.reason ? { reason: def.reason } : {}),
  };
}

function inferType(value) {
  if (typeof value === 'boolean') return 'bool';
  if (typeof value === 'number') return 'number';
  if (Array.isArray(value) && value.every((v) => typeof v === 'string')) return 'stringList';
  return 'string';
}

function settings(url) {
  const raw = url.searchParams.get('dir');
  const dir = raw === null ? START_DIR : resolveDir(raw);
  if (!dir) return [400, { error: 'bad-dir' }];
  const envNames = [...new Set(ALL.flatMap((s) => Object.keys(state[s]?.env ?? {})))].sort();
  const otherKeys = [...new Set(ALL.flatMap((s) => Object.keys(state[s] ?? {})))].filter((k) => !CATALOG_ROOTS.has(k)).sort();
  const defs = [
    ...CATALOG,
    ...envNames.map((name) => definition(`env.${name}`)),
    ...otherKeys.map((key) => {
      const value = PRECEDENCE.map((s) => state[s]?.[key]).find((v) => v !== undefined);
      return {
        key, category: 'other', label: key, description: 'Not in the settings catalog; shown read-only.', type: inferType(value), scopes: ALL,
        editable: false, reason: 'not-in-catalog',
      };
    }),
  ];
  return [200, {
    categories: CATEGORIES.map(([id, label]) => ({ id, label, settings: defs.filter((d) => d.category === id).map(row) })),
    files: ['user', 'project', 'local', 'machine'].map((scope) => ({
      scope, path: filePath(scope, dir), exists: state[scope] !== null, gitTracked: scope === 'project',
    })),
  }];
}

function checkValue(def, value) {
  switch (def.type) {
    case 'bool': return typeof value === 'boolean' ? null : 'bad-type';
    case 'number': return Number.isFinite(value) ? null : 'bad-type';
    case 'string': return typeof value === 'string' ? null : 'bad-type';
    case 'stringList': return Array.isArray(value) && value.every((v) => typeof v === 'string') ? null : 'bad-type';
    case 'enum': return typeof value !== 'string' ? 'bad-type' : def.options.includes(value) ? null : 'bad-option';
    default: return 'bad-type';
  }
}

function setPath(obj, key, value) {
  const parts = key.split('.');
  let node = obj;
  for (const part of parts.slice(0, -1)) {
    if (!(part in node)) node[part] = {};
    if (!isObject(node[part])) return false;
    node = node[part];
  }
  node[parts.at(-1)] = value;
  return true;
}

function unsetPath(obj, parts) {
  if (parts.length === 1) return void delete obj[parts[0]];
  const child = obj[parts[0]];
  if (!isObject(child)) return;
  unsetPath(child, parts.slice(1));
  if (Object.keys(child).length === 0) delete obj[parts[0]];
}

function unifiedDiff(path, before, after) {
  const lines = (t) => (t === null ? [] : t.replace(/\n$/, '').split('\n'));
  const a = lines(before);
  const b = lines(after);
  const dp = Array.from({ length: a.length + 1 }, () => new Array(b.length + 1).fill(0));
  for (let i = a.length - 1; i >= 0; i--) {
    for (let j = b.length - 1; j >= 0; j--) dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
  }
  const ops = [];
  for (let i = 0, j = 0; i < a.length || j < b.length;) {
    if (i < a.length && j < b.length && a[i] === b[j]) ops.push([' ', a[i++], i - 1, j++]);
    else if (i < a.length && (j === b.length || dp[i + 1][j] >= dp[i][j + 1])) ops.push(['-', a[i++], i - 1, j]);
    else ops.push(['+', b[j++], i, j - 1]);
  }
  const out = [before === null ? '--- /dev/null' : `--- a/${path}`, `+++ b/${path}`];
  for (let k = 0; k < ops.length;) {
    if (ops[k][0] === ' ') {
      k++;
      continue;
    }
    const start = Math.max(0, k - 3);
    let end = k;
    while (end < ops.length) {
      if (ops[end][0] !== ' ') {
        end++;
        continue;
      }
      let run = end;
      while (run < ops.length && ops[run][0] === ' ') run++;
      if (run === ops.length || run - end > 6) {
        end = Math.min(run, end + 3);
        break;
      }
      end = run;
    }
    const hunk = ops.slice(start, end);
    const oldCount = hunk.filter((o) => o[0] !== '+').length;
    const newCount = hunk.filter((o) => o[0] !== '-').length;
    out.push(`@@ -${hunk[0][2] + (oldCount ? 1 : 0)},${oldCount} +${hunk[0][3] + (newCount ? 1 : 0)},${newCount} @@`, ...hunk.map(([t, l]) => t + l));
    k = end;
  }
  return out.join('\n') + '\n';
}

function confirmFor(files) {
  const hash = createHash('sha256');
  for (const f of files) {
    for (const part of [f.path, f.sha256Before ?? '', f.after]) hash.update(`${Buffer.byteLength(part)}:${part}`);
  }
  return hash.digest('hex');
}

function propose(body) {
  if (!isObject(body)) return invalid(null, 'bad-request');
  const dir = resolveDir(body.dir);
  if (!dir) return [400, { error: 'bad-dir' }];
  const changes = body.changes;
  if (!Array.isArray(changes) || changes.length < 1 || changes.length > 100) return invalid(null, 'bad-request');
  const seen = new Set();
  const next = {};
  for (const c of changes) {
    if (!isObject(c) || typeof c.key !== 'string' || typeof c.scope !== 'string' || (c.unset === true) === ('value' in c)) return invalid(c?.key, 'bad-request');
    const def = definition(c.key);
    if (!def) return invalid(c.key, 'unknown-key');
    if (def.editable === false) return invalid(c.key, 'not-editable');
    if (!def.scopes.includes(c.scope)) return invalid(c.key, 'bad-scope');
    if (seen.has(`${c.scope}\n${c.key}`)) return invalid(c.key, 'duplicate');
    seen.add(`${c.scope}\n${c.key}`);
    const reason = c.unset ? null : checkValue(def, c.value);
    if (reason) return invalid(c.key, reason);
    next[c.scope] ??= structuredClone(state[c.scope] ?? {});
    if (c.unset) unsetPath(next[c.scope], c.key.split('.'));
    else if (!setPath(next[c.scope], c.key, c.value)) return invalid(c.key, 'not-an-object');
  }
  const files = Object.entries(next)
    .map(([scope, obj]) => ({ scope, path: filePath(scope, dir), before: text(state[scope]), after: text(obj), obj }))
    .filter((f) => f.before !== f.after && !(f.before === null && Object.keys(f.obj).length === 0))
    .map((f) => ({ ...f, sha256Before: f.before === null ? null : sha256(f.before) }));
  if (!files.length) return invalid(null, 'no-change');
  const now = Date.now();
  for (const [id, p] of proposals) if (p.expires <= now) proposals.delete(id);
  const model = changes.find((c) => c.key === 'model' && !c.unset)?.value;
  const proposal = {
    id: randomBytes(16).toString('hex'),
    expires: now + TTL_MS,
    files,
    confirm: confirmFor(files),
    changes: changes.map((c) => ({ key: c.key, scope: c.scope, action: c.unset ? 'unset' : 'set' })),
    trigger: model === 'stale' || model === 'mismatch' ? model : null,
  };
  proposals.set(proposal.id, proposal);
  return [200, {
    id: proposal.id,
    expiresAt: new Date(proposal.expires).toISOString(),
    files: files.map((f) => ({
      path: f.path, scope: f.scope, sha256Before: f.sha256Before,
      diff: unifiedDiff(f.path, text(redact(state[f.scope])), text(redact(f.obj))),
    })),
    confirm: proposal.confirm,
  }];
}

function apply(id, body) {
  const proposal = proposals.get(id);
  if (!proposal || proposal.expires <= Date.now()) {
    proposals.delete(id);
    return [404, { error: 'not-found' }];
  }
  if (!isObject(body) || body.confirm !== proposal.confirm || proposal.trigger === 'mismatch') return [400, { error: 'confirm-mismatch' }];
  proposals.delete(id);
  if (proposal.trigger === 'stale' || proposal.files.some((f) => f.before !== text(state[f.scope]))) return [409, { error: 'stale' }];
  for (const f of proposal.files) state[f.scope] = f.obj;
  const at = new Date().toISOString();
  const auditId = `${at.slice(0, 19).replace(/[-:]/g, '')}Z-${randomBytes(4).toString('hex')}`;
  backups.set(auditId, proposal.files.map((f) => ({ scope: f.scope, path: f.path, before: f.before })));
  audit.unshift({
    auditId, at, listener: 'ui',
    files: proposal.files.map((f) => ({ path: f.path, sha256Before: f.sha256Before, sha256After: f.after === null ? null : sha256(f.after) })),
    changes: proposal.changes,
    ...(proposal.revertOf ? { revertOf: proposal.revertOf } : {}),
  });
  return [200, { applied: true, auditId }];
}

async function readBody(req) {
  if (Number(req.headers['content-length'] ?? 0) > MAX_BODY) return null;
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size <= MAX_BODY) chunks.push(chunk);
  }
  if (size > MAX_BODY) return null;
  try {
    return { json: JSON.parse(Buffer.concat(chunks).toString('utf8')) };
  } catch {
    return { json: undefined };
  }
}

export { state, proposals, audit, backups, sha256, text, redact, unifiedDiff, confirmFor, readBody, TTL_MS };

/** Answers `/api/v2/*` as `[status, body]`; null for any other path. */
export async function editsApi(req, url) {
  if (url.pathname !== '/api/v2' && !url.pathname.startsWith('/api/v2/')) return null;
  const path = url.pathname;
  if (req.method === 'GET' && path === '/api/v2/settings') return settings(url);
  if (req.method === 'GET' && path === '/api/v2/audit') {
    const raw = url.searchParams.get('limit') ?? '';
    return [200, { entries: audit.slice(0, /^-?\d{1,9}$/.test(raw) ? Math.min(Math.max(Number(raw), 1), 500) : 50) }];
  }
  const applyMatch = /^\/api\/v2\/proposals\/([^/]+)\/apply$/.exec(path);
  if (req.method !== 'POST' || (path !== '/api/v2/proposals' && !applyMatch)) return [404, { error: 'not-found' }];
  if (req.headers['x-cfgdash-request'] !== '1') return [403, { error: 'forbidden' }];
  const body = await readBody(req);
  if (!body) return [403, { error: 'forbidden' }];
  if (applyMatch) return body.json === undefined ? [400, { error: 'confirm-mismatch' }] : apply(applyMatch[1], body.json);
  return propose(body.json);
}
