/**
 * Dev-only mock of the file edits routes (docs/contracts/edits-v2.md) on the mock snapshot's file contents.
 * Edited contents live in memory per file id and are shared by every target dir; the Origin check is skipped,
 * `X-Cfgdash-Request: 1` and the 320 KiB body cap are enforced.
 *
 * - `GET /api/v1/files/{id}` gains `edit` through {@link withEdit}: instruction, agent, skill and command `.md` files and
 *   Claude Code `settings.json` / `settings.local.json` are editable; company files answer `company`, hook scripts and other files
 *   `not-editable-kind`, content holding `[redacted]` (the user settings.json) `contains-redacted-values`, without `sha256`.
 * - `POST /api/v2/files/{id}/proposals?dir=` with `{ content, sha256Before }`; apply and revert through the usual
 *   `/api/v2/proposals/{id}/apply` and `/api/v2/audit/{auditId}/revert`, which this module answers for file proposals only.
 *
 * Trigger: content with a line `mock: stale` proposes fine and answers its apply with 409 `stale`.
 */
import { createHash, randomBytes } from 'node:crypto';
import { buildSnapshot } from './data.mjs';
import { resolveDir } from './dirs.mjs';
import { audit, confirmFor, unifiedDiff, TTL_MS } from './edits.mjs';

const MAX_BODY = 320 * 1024;
const MAX_FILE = 256 * 1024;
const MARKER = '[redacted]';
const KIND_DIRS = { agent: '/agents/', skill: '/skills/', command: '/commands/' };
const INSTRUCTION = /(^|\/)(CLAUDE|CLAUDE\.local|AGENTS|AGENTS\.override)\.md$/;
const SETTINGS = /\/\.claude\/settings(\.local)?\.json$/;

const edited = new Map();
const proposals = new Map();
const backups = new Map();

const sha256 = (text) => createHash('sha256').update(text).digest('hex');

function current(file) {
  return edited.get(file.ref.id) ?? file.content;
}

function reason(file) {
  const { path, kind, company } = file.ref;
  if (company) return 'company';
  const md = /\.md$/i.test(path) && (INSTRUCTION.test(path) || kind === 'instruction' || (KIND_DIRS[kind] && path.includes(KIND_DIRS[kind])));
  if (!md && !SETTINGS.test(path)) return 'not-editable-kind';
  const text = current(file);
  if (Buffer.byteLength(text) > MAX_FILE || file.truncated) return 'too-large';
  if (text.includes(MARKER)) return 'contains-redacted-values';
  return null;
}

/** The api-v1 file answer for `file` with the edits-v2 `edit` object and any edited content. */
export function withEdit(file, body) {
  const why = reason(file);
  return why
    ? { ...body, edit: { editable: false, reason: why } }
    : { ...body, content: current(file), edit: { editable: true, sha256: sha256(current(file)), gitTracked: !/settings\.local\.json$/.test(file.ref.path) } };
}

const invalid = (why) => [400, { error: 'invalid-change', key: null, reason: why }];

function propose(file, body) {
  if (typeof body?.content !== 'string' || typeof body?.sha256Before !== 'string') return invalid('bad-request');
  const why = reason(file);
  if (why) return invalid(why);
  const { content } = body;
  if (content.includes('\0') || /[\ud800-\udfff]/.test(content.replace(/[\ud800-\udbff][\udc00-\udfff]/g, ''))) return invalid('not-text');
  if (Buffer.byteLength(content) > MAX_FILE) return invalid('too-large');
  if (content.includes(MARKER)) return invalid('redacted-value');
  if (SETTINGS.test(file.ref.path)) {
    try {
      const root = JSON.parse(content);
      if (typeof root !== 'object' || root === null || Array.isArray(root)) return invalid('parse-error');
    } catch {
      return invalid('parse-error');
    }
  }
  const before = current(file);
  if (body.sha256Before !== sha256(before)) return [409, { error: 'stale' }];
  if (content === before) return invalid('no-change');
  return store(file, before, content, null);
}

function store(file, before, after, revertOf) {
  const now = Date.now();
  for (const [id, p] of proposals) if (p.expires <= now) proposals.delete(id);
  const f = { path: file.ref.path, scope: 'file', before, after, sha256Before: sha256(before) };
  const proposal = { id: randomBytes(16).toString('hex'), expires: now + TTL_MS, file, f, confirm: confirmFor([f]), revertOf, stale: /^mock: stale$/m.test(after) };
  proposals.set(proposal.id, proposal);
  return [200, {
    id: proposal.id,
    expiresAt: new Date(proposal.expires).toISOString(),
    files: [{ path: f.path, scope: 'file', sha256Before: f.sha256Before, diff: unifiedDiff(f.path, before, after) }],
    confirm: proposal.confirm,
  }];
}

function apply(proposal, body) {
  if (proposal.expires <= Date.now()) {
    proposals.delete(proposal.id);
    return [404, { error: 'not-found' }];
  }
  if (body?.confirm !== proposal.confirm) return [400, { error: 'confirm-mismatch' }];
  proposals.delete(proposal.id);
  if (proposal.stale || current(proposal.file) !== proposal.f.before) return [409, { error: 'stale' }];
  edited.set(proposal.file.ref.id, proposal.f.after);
  const at = new Date().toISOString();
  const auditId = `${at.slice(0, 19).replace(/[-:]/g, '')}Z-${randomBytes(4).toString('hex')}`;
  backups.set(auditId, proposal);
  audit.unshift({
    auditId, at, listener: 'ui',
    files: [{ path: proposal.f.path, scope: 'file', sha256Before: proposal.f.sha256Before, sha256After: sha256(proposal.f.after) }],
    changes: [{ file: proposal.file.ref.id, action: proposal.revertOf ? 'revert' : 'edit' }],
    ...(proposal.revertOf ? { revertOf: proposal.revertOf } : {}),
  });
  return [200, { applied: true, auditId }];
}

function revert(auditId) {
  const applied = backups.get(auditId);
  if (current(applied.file) !== applied.f.after) return [409, { error: 'stale' }];
  return store(applied.file, applied.f.after, applied.f.before, auditId);
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

/** Answers the file proposal route and the apply and revert of file proposals as `[status, body]`; null for anything else. */
export async function filesEditApi(req, url) {
  if (req.method !== 'POST') return null;
  const proposeMatch = /^\/api\/v2\/files\/([^/]+)\/proposals$/.exec(url.pathname);
  const applyMatch = /^\/api\/v2\/proposals\/([^/]+)\/apply$/.exec(url.pathname);
  const revertMatch = /^\/api\/v2\/audit\/([^/]+)\/revert$/.exec(url.pathname);
  if (!proposeMatch && !(applyMatch && proposals.has(applyMatch[1])) && !(revertMatch && backups.has(revertMatch[1]))) return null;
  if (req.headers['x-cfgdash-request'] !== '1') return [403, { error: 'forbidden' }];
  const body = await readBody(req);
  if (!body) return [403, { error: 'forbidden' }];
  if (applyMatch) return apply(proposals.get(applyMatch[1]), body.json);
  if (revertMatch) return revert(revertMatch[1]);
  const raw = url.searchParams.get('dir');
  const dir = raw === null ? null : resolveDir(raw);
  if (!dir) return [400, { error: 'bad-dir' }];
  let id;
  try {
    id = decodeURIComponent(proposeMatch[1]);
  } catch {
    return [404, { error: 'not-found' }];
  }
  const file = buildSnapshot(dir).files.get(id);
  return file ? propose(file, body.json) : [404, { error: 'not-found' }];
}
