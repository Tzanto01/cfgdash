/**
 * Dev-only mock of the config assistant routes (`/api/v2/assistant/*`, docs/contracts/assistant-v1.md). No model runs:
 * every turn streams canned events. The `proposal` event is a real proposal of the edits mock, so it can be applied
 * through `POST /api/v2/proposals/{id}/apply` with its `confirm`.
 *
 * Triggers:
 * - `GET /api/v2/assistant/status?variant=unavailable` answers `available: false`;
 * - a session created with model `"unavailable"` answers 503 `unavailable`;
 * - a message whose text contains `fail` ends its turn with a mapped `error` event (`message` plus redacted `detail`) instead of `done`;
 * - with `assistant.applyEnabled` on in the platform mock, the turn applies its change and emits `applied` (audited `mode: "auto"`)
 *   instead of `proposal`;
 * - context items answer per-reference states: past the 8th `too-many`; a bad kind or an id containing `missing` `not-found`;
 *   an id containing `company` or a `engram.memory`, `sessions.transcripts` or `usage.metrics` row `not-allowed`; else `added`;
 * - `check` takes `{ findingIds }` (at most 50 strings); its first delta names how many findings it was limited to.
 */
import { randomBytes } from 'node:crypto';
import { Readable } from 'node:stream';
import { resolveDir } from './dirs.mjs';
import { audit, editsApi } from './edits.mjs';
import { assistantSettings } from './platform.mjs';

const MAX_BODY = 64 * 1024;
const MAX_LIVE = 2;
const MAX_CONTEXT = 8;
const MAX_TEXT = 16000;
const STEP_MS = 120;
const CLAUDE_MODELS = [
  { id: 'sonnet', label: 'Sonnet' }, { id: 'opus', label: 'Opus' }, { id: 'haiku', label: 'Haiku' }, { id: 'fable', label: 'Fable' },
  { id: 'sonnet[1m]', label: 'Sonnet (1M context)' }, { id: 'opus[1m]', label: 'Opus (1M context)' },
];
const MODELS = CLAUDE_MODELS.map((m) => m.id);
const MAX_FINDING_IDS = 50;
const HIDDEN_COLLECTORS = ['engram.memory', 'sessions.transcripts', 'usage.metrics'];
const EFFORTS = ['low', 'medium', 'high', 'xhigh', 'max'];
const MODEL_RE = /^[A-Za-z0-9][A-Za-z0-9.\-[\]]{0,99}$/;
const CONTEXT_KINDS = ['row', 'file', 'finding'];
const sessions = new Map();

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const liveCount = () => [...sessions.values()].filter((s) => s.live).length;

const provider = (available, reason) => ({
  id: 'claude', label: 'Claude', available, ...(reason ? { reason } : {}), models: CLAUDE_MODELS, efforts: EFFORTS,
});

function status(url) {
  if (url.searchParams.get('variant') === 'unavailable') {
    return [200, { available: false, reason: 'claude not found on PATH', models: [], providers: [provider(false, 'claude not found on PATH')], sessions: [] }];
  }
  return [200, {
    available: true,
    providers: [provider(true)],
    claudePath: '/home/user/.local/bin/claude',
    claudeVersion: '2.1.316 (Claude Code)',
    models: MODELS,
    sessions: [...sessions.values()].map((s) => ({ id: s.id, dir: s.dir, model: s.model, effort: s.effort, live: s.live, costUsd: s.costUsd })),
  }];
}

function create(body) {
  if (!isObject(body)) return [400, { error: 'bad-request' }];
  const dir = typeof body.dir === 'string' ? resolveDir(body.dir) : null;
  if (!dir) return [400, { error: 'bad-dir' }];
  const model = body.model ?? 'sonnet';
  const effort = body.effort ?? 'medium';
  if (model === 'unavailable') return [503, { error: 'unavailable', reason: 'claude not found on PATH' }];
  if (typeof model !== 'string' || !MODEL_RE.test(model) || !EFFORTS.includes(effort)) return [400, { error: 'bad-request' }];
  if (liveCount() >= MAX_LIVE) return [429, { error: 'too-many-sessions' }];
  const id = randomBytes(16).toString('hex');
  sessions.set(id, { id, dir, model, effort, live: true, costUsd: 0, running: false, queue: [], listeners: new Set() });
  return [200, { id }];
}

function emit(session, event, data) {
  const frame = `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
  if (session.listeners.size === 0) {
    session.queue.push(frame);
    if (session.queue.length > 512) session.queue.shift();
    return;
  }
  for (const res of session.listeners) res.write(frame);
}

async function post(path, json) {
  const payload = Buffer.from(JSON.stringify(json));
  const req = Object.assign(Readable.from([payload]), {
    method: 'POST',
    headers: { 'x-cfgdash-request': '1', 'content-length': String(payload.length) },
  });
  return editsApi(req, new URL(`http://127.0.0.1${path}`));
}

async function proposeModel(dir) {
  const [code, body] = await post('/api/v2/proposals', { dir, changes: [{ key: 'model', scope: 'project', value: 'sonnet' }] });
  return code === 200 ? body : null;
}

function contextStates(context) {
  return context.map((item, index) => {
    if (index >= MAX_CONTEXT) return { index, state: 'too-many' };
    const ids = isObject(item) ? Object.entries(item).filter(([k]) => k !== 'kind').map(([, v]) => String(v)) : [];
    if (!isObject(item) || !CONTEXT_KINDS.includes(item.kind) || ids.length === 0 || ids.some((id) => id.includes('missing'))) {
      return { index, state: 'not-found' };
    }
    if (ids.some((id) => id.includes('company')) || HIDDEN_COLLECTORS.includes(item.collectorId)) return { index, state: 'not-allowed' };
    const title = item.kind === 'file' ? `File ${item.fileId}` : item.kind === 'finding' ? `Finding: ${item.findingId}` : ids.join(' / ');
    return { index, state: 'added', title };
  });
}

async function turn(session, text, check, findingIds) {
  session.running = true;
  const wait = () => new Promise((r) => setTimeout(r, STEP_MS));
  const lines = check
    ? [findingIds ? `Check of ${findingIds.length} findings in ` : 'Full check of ', '`', session.dir, '`', ':\n', '1. Two findings are warnings.\n', '2. The project has no default model; I proposed one.\n']
    : ['I read your effective settings.\n', 'The project scope sets no `model`, ', 'so new sessions use the user default.\n', 'I proposed pinning it for this folder.\n'];
  try {
    for (const chunk of lines.slice(0, 2)) {
      emit(session, 'delta', { text: chunk });
      await wait();
    }
    emit(session, 'tool', { name: check ? 'get_findings' : 'get_snapshot', status: 'started' });
    await wait();
    emit(session, 'tool', { name: check ? 'get_findings' : 'get_snapshot', status: 'done' });
    for (const chunk of lines.slice(2)) {
      emit(session, 'delta', { text: chunk });
      await wait();
    }
    if (!check && /fail/i.test(text)) {
      emit(session, 'error', {
        error: 'turn-failed',
        message: 'Claude is overloaded right now. Try again in a moment.',
        detail: 'error_during_execution: API Error: 529 Overloaded',
      });
      return;
    }
    emit(session, 'tool', { name: 'propose_change', status: 'started' });
    const proposal = await proposeModel(session.dir);
    const rationale = 'Pin the model for this folder so every session here starts on the same model.';
    let applied = null;
    if (proposal && assistantSettings().applyEnabled) {
      const [code, body] = await post(`/api/v2/proposals/${proposal.id}/apply`, { confirm: proposal.confirm });
      if (code === 200) {
        Object.assign(audit[0], { origin: 'assistant', sessionId: session.id, mode: 'auto' });
        applied = body;
      }
    }
    emit(session, 'tool', { name: 'propose_change', status: proposal && (applied || !assistantSettings().applyEnabled) ? 'done' : 'error' });
    if (applied) {
      emit(session, 'applied', { auditId: applied.auditId, files: proposal.files, rationale });
    } else if (proposal && !assistantSettings().applyEnabled) {
      emit(session, 'proposal', {
        proposalId: proposal.id,
        expiresAt: proposal.expiresAt,
        files: proposal.files,
        confirm: proposal.confirm,
        rationale,
      });
    }
    session.costUsd = Math.round((session.costUsd + 0.0123) * 1e4) / 1e4;
    emit(session, 'cost', { costUsd: session.costUsd });
    emit(session, 'done', {});
  } finally {
    session.running = false;
  }
}

function message(session, body, check) {
  let answer = null;
  let findingIds = null;
  if (check) {
    const ids = isObject(body) ? body.findingIds : body === null ? undefined : null;
    if (ids !== undefined && (!Array.isArray(ids) || ids.length > MAX_FINDING_IDS || !ids.every((i) => typeof i === 'string'))) {
      return [400, { error: 'bad-request' }];
    }
    if (ids?.length) findingIds = ids;
  } else {
    if (!isObject(body) || typeof body.text !== 'string' || body.text.length < 1 || body.text.length > MAX_TEXT) return [400, { error: 'bad-request' }];
    const context = body.context ?? [];
    if (!Array.isArray(context)) return [400, { error: 'bad-request' }];
    answer = { context: contextStates(context) };
  }
  if (session.running) return [409, { error: 'turn-running' }];
  if (!session.live) {
    if (liveCount() >= MAX_LIVE) return [429, { error: 'too-many-sessions' }];
    session.live = true;
  }
  void turn(session, check ? '' : body.text, check, findingIds);
  return [202, answer];
}

function events(req, res, session) {
  res.writeHead(200, { 'Content-Type': 'text/event-stream; charset=utf-8', 'Cache-Control': 'no-store', Connection: 'keep-alive' });
  res.write(': connected\n\n');
  for (const frame of session.queue.splice(0)) res.write(frame);
  session.listeners.add(res);
  const keepAlive = setInterval(() => res.write(': keep-alive\n\n'), 15000);
  req.on('close', () => {
    clearInterval(keepAlive);
    session.listeners.delete(res);
  });
  return true;
}

async function readJson(req) {
  if (Number(req.headers['content-length'] ?? 0) > MAX_BODY) return null;
  const chunks = [];
  let size = 0;
  for await (const chunk of req) {
    size += chunk.length;
    if (size <= MAX_BODY) chunks.push(chunk);
  }
  if (size > MAX_BODY) return null;
  try {
    return { json: JSON.parse(Buffer.concat(chunks).toString('utf8') || 'null') };
  } catch {
    return { json: undefined };
  }
}

/**
 * Answers `/api/v2/assistant/*`: null for any other path, `true` when it wrote the response itself (the SSE stream),
 * otherwise `[status, body]`. Non-GET routes need `X-Cfgdash-Request: 1` and a body of at most 64 KiB.
 */
export async function assistantApi(req, res, url) {
  const path = url.pathname;
  if (!path.startsWith('/api/v2/assistant/')) return null;
  if (req.method === 'GET' && path === '/api/v2/assistant/status') return status(url);
  const m = /^\/api\/v2\/assistant\/sessions(?:\/([0-9a-f]{32})(?:\/(messages|events|check))?)?$/.exec(path);
  if (!m) return [404, { error: 'not-found' }];
  const [, id, action] = m;
  const session = id ? sessions.get(id) : null;
  if (id && !session) return [404, { error: 'not-found' }];
  if (req.method === 'GET') return action === 'events' ? events(req, res, session) : [404, { error: 'not-found' }];
  if (req.headers['x-cfgdash-request'] !== '1') return [403, { error: 'forbidden' }];
  if (req.method === 'DELETE' && id && !action) {
    session.live = false;
    for (const listener of session.listeners) listener.end();
    sessions.delete(id);
    return [204, null];
  }
  if (req.method !== 'POST' || action === 'events' || (id && !action)) return [404, { error: 'not-found' }];
  const body = await readJson(req);
  if (!body) return [403, { error: 'forbidden' }];
  if (!id) return create(body.json);
  return message(session, body.json, action === 'check');
}
