/**
 * Dev-only mock of cfgdash's own settings and the audit revert route (docs/contracts/cfgdash-config-v1.md, edits-v1.md),
 * sharing the edits mock's in-memory state, proposals and audit.
 *
 * Routes:
 * - GET /api/v2/cfgdash-settings, PUT /api/v2/cfgdash-settings (partial values, nested as in GET);
 * - POST /api/v2/audit/{auditId}/revert (body `{}`), answered with a proposal applied through /api/v2/proposals/{id}/apply.
 * PUT and POST need `X-Cfgdash-Request: 1` and a body of at most 64 KiB.
 *
 * Triggers:
 * - env `MOCK_AUTOSTART=fail`: any PUT with `startAtLogin` answers 500 `autostart-failed` and nothing changes;
 * - env `MOCK_AUTOSTART=unsupported`: `startAtLogin.actual` is `unsupported` and any PUT with `startAtLogin` answers 500 `autostart-failed`;
 * - apply A, apply B on the same scope, revert A: 409 `stale`.
 */
import { randomBytes } from 'node:crypto';
import { resolveDir } from './dirs.mjs';
import {
  state, proposals, audit, backups, sha256, text, redact, unifiedDiff, confirmFor, readBody, TTL_MS,
} from './edits.mjs';

const CONFIG_FILE = 'C:/Users/user/AppData/Roaming/cfgdash/config.json';
const OVERRIDES_FILE = 'C:/Users/user/AppData/Roaming/cfgdash/overrides.json';
const MODEL = /^[A-Za-z0-9][A-Za-z0-9._:[\]/@-]{0,127}$/;
const UNSUPPORTED = 'no user systemd (mock)';
const isObject = (v) => typeof v === 'object' && v !== null && !Array.isArray(v);
const invalid = (key, reason) => [400, { error: 'invalid-setting', key: key ?? null, reason }];

const values = {
  startAtLogin: false, theme: 'system', defaultDir: null, tailnet: false, fleet: { enabled: false },
  assistant: { model: 'sonnet', effort: 'medium', maxBudgetUsd: 2.0 },
};
let configText = null;

const bool = (v) => (typeof v === 'boolean' ? null : 'bad-type');
const oneOf = (options) => (v) => (typeof v !== 'string' ? 'bad-type' : options.includes(v) ? null : 'bad-value');
const RULES = {
  startAtLogin: bool,
  theme: oneOf(['system', 'light', 'dark']),
  defaultDir: (v) => (v === null ? null : typeof v !== 'string' ? 'bad-type' : resolveDir(v) ? null : 'bad-value'),
  tailnet: bool,
  'fleet.enabled': bool,
  'assistant.model': (v) => (typeof v !== 'string' ? 'bad-type' : MODEL.test(v) ? null : 'bad-value'),
  'assistant.effort': oneOf(['low', 'medium', 'high', 'xhigh', 'max']),
  'assistant.maxBudgetUsd': (v) => (!Number.isFinite(v) ? 'bad-type' : v >= 0.1 && v <= 50 ? null : 'bad-value'),
};
const GROUPS = new Set(['fleet', 'assistant']);

function view() {
  const mode = process.env.MOCK_AUTOSTART;
  return {
    values: structuredClone(values),
    startAtLogin: {
      configured: values.startAtLogin,
      actual: mode === 'unsupported' ? 'unsupported' : values.startAtLogin ? 'on' : 'off',
      ...(mode === 'unsupported' ? { detail: UNSUPPORTED } : {}),
    },
    paths: { config: CONFIG_FILE, overrides: OVERRIDES_FILE },
  };
}

function put(body) {
  if (!isObject(body) || Object.keys(body).length === 0) return invalid(null, 'bad-request');
  const changes = [];
  for (const [key, value] of Object.entries(body)) {
    if (GROUPS.has(key)) {
      if (!isObject(value)) return invalid(key, 'bad-type');
      if (Object.keys(value).length === 0) return invalid(key, 'bad-request');
      for (const [sub, v] of Object.entries(value)) changes.push([`${key}.${sub}`, v]);
    } else changes.push([key, value]);
  }
  for (const [key, value] of changes) {
    if (!Object.hasOwn(RULES, key)) return invalid(key, 'unknown-key');
    const reason = RULES[key](value);
    if (reason) return invalid(key, reason);
  }
  if ('startAtLogin' in body) {
    const mode = process.env.MOCK_AUTOSTART;
    if (mode === 'fail') return [500, { error: 'autostart-failed', reason: 'systemctl --user enable failed (mock)' }];
    if (mode === 'unsupported') return [500, { error: 'autostart-failed', reason: UNSUPPORTED }];
  }
  for (const [key, value] of changes) {
    const [head, sub] = key.split('.');
    if (sub) values[head][sub] = value;
    else values[head] = key === 'defaultDir' && value !== null ? resolveDir(value) : value;
  }
  const before = configText;
  configText = text(values);
  const at = new Date().toISOString();
  audit.unshift({
    auditId: `${at.slice(0, 19).replace(/[-:]/g, '')}Z-${randomBytes(4).toString('hex')}`, at, listener: 'ui', kind: 'cfgdash-settings',
    files: [{ path: CONFIG_FILE, sha256Before: before === null ? null : sha256(before), sha256After: sha256(configText) }],
    changes: changes.map(([key]) => ({ key, scope: 'cfgdash', action: 'set' })),
  });
  return [200, view()];
}

function revert(auditId) {
  const entry = audit.find((e) => e.auditId === auditId);
  const backup = backups.get(auditId);
  if (!entry || entry.kind || !backup) return [404, { error: 'not-found' }];
  const hash = (t) => (t === null ? null : sha256(t));
  if (backup.some((b) => hash(text(state[b.scope])) !== entry.files.find((f) => f.path === b.path)?.sha256After)) return [409, { error: 'stale' }];
  const files = backup.map((b) => {
    const current = text(state[b.scope]);
    return { scope: b.scope, path: b.path, before: current, after: b.before, obj: b.before === null ? null : JSON.parse(b.before), sha256Before: hash(current) };
  });
  const now = Date.now();
  for (const [id, p] of proposals) if (p.expires <= now) proposals.delete(id);
  const proposal = {
    id: randomBytes(16).toString('hex'),
    expires: now + TTL_MS,
    files,
    confirm: confirmFor(files.map((f) => ({ ...f, after: f.after ?? '' }))),
    changes: entry.changes.map((c) => ({ ...c, action: 'revert' })),
    trigger: null,
    revertOf: auditId,
  };
  proposals.set(proposal.id, proposal);
  return [200, {
    id: proposal.id,
    expiresAt: new Date(proposal.expires).toISOString(),
    files: files.map((f) => {
      const diff = unifiedDiff(f.path, text(redact(state[f.scope])), text(redact(f.obj)));
      return { path: f.path, scope: f.scope, sha256Before: f.sha256Before, diff: f.obj === null ? diff.replace(/^\+\+\+ .*$/m, '+++ /dev/null') : diff };
    }),
    confirm: proposal.confirm,
  }];
}

/** Answers the cfgdash-settings and audit revert routes as `[status, body]`; null for any other request. */
export async function platformApi(req, url) {
  const path = url.pathname;
  const revertMatch = /^\/api\/v2\/audit\/([^/]+)\/revert$/.exec(path);
  if (path === '/api/v2/cfgdash-settings' && req.method === 'GET') return [200, view()];
  if (!(path === '/api/v2/cfgdash-settings' && req.method === 'PUT') && !(revertMatch && req.method === 'POST')) return null;
  if (req.headers['x-cfgdash-request'] !== '1') return [403, { error: 'forbidden' }];
  const body = await readBody(req);
  if (!body) return [403, { error: 'forbidden' }];
  if (revertMatch) return isObject(body.json) ? revert(revertMatch[1]) : [400, { error: 'bad-request' }];
  return put(body.json);
}
