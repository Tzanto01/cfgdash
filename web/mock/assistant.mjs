/**
 * Dev-only mock of the config assistant routes (`/api/v2/assistant/*`, docs/contracts/assistant-v1.md). No model runs:
 * every turn streams canned events. The `proposal` event is a real proposal of the edits mock, so it can be applied
 * through `POST /api/v2/proposals/{id}/apply` with its `confirm`.
 *
 * Triggers:
 * - `GET /api/v2/assistant/status?variant=unavailable` answers `available: false`;
 * - a session created with model `"unavailable"` answers 503 `unavailable`;
 * - a message whose text contains `fail` ends its turn with an `error` event instead of `done`.
 */
import { randomBytes } from 'node:crypto';
import { Readable } from 'node:stream';
import { resolveDir } from './dirs.mjs';
import { editsApi } from './edits.mjs';

const MAX_BODY = 64 * 1024;
const MAX_LIVE = 2;
const MAX_CONTEXT = 8;
const MAX_TEXT = 16000;
const STEP_MS = 120;
const MODELS = ['sonnet', 'opus', 'haiku'];
const EFFORTS = ['low', 'medium', 'high', 'xhigh', 'max'];
const MODEL_RE = /^[A-Za-z0-9][A-Za-z0-9.\-[\]]{0,99}$/;
const CONTEXT_KINDS = ['row', 'file', 'finding'];
const sessions = new Map();

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const liveCount = () => [...sessions.values()].filter((s) => s.live).length;

function status(url) {
  if (url.searchParams.get('variant') === 'unavailable') {
    return [200, { available: false, reason: 'claude not found on PATH', models: [], sessions: [] }];
  }
  return [200, {
    available: true,
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

async function proposeModel(dir) {
  const payload = Buffer.from(JSON.stringify({ dir, changes: [{ key: 'model', scope: 'project', value: 'sonnet' }] }));
  const req = Object.assign(Readable.from([payload]), {
    method: 'POST',
    headers: { 'x-cfgdash-request': '1', 'content-length': String(payload.length) },
  });
  const [code, body] = await editsApi(req, new URL('http://127.0.0.1/api/v2/proposals'));
  return code === 200 ? body : null;
}

async function turn(session, text, check) {
  session.running = true;
  const wait = () => new Promise((r) => setTimeout(r, STEP_MS));
  const lines = check
    ? ['Full check of ', '`', session.dir, '`', ':\n', '1. Two findings are warnings.\n', '2. The project has no default model; I proposed one.\n']
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
      emit(session, 'error', { error: 'process-exited', message: 'the assistant process ended unexpectedly' });
      return;
    }
    emit(session, 'tool', { name: 'propose_change', status: 'started' });
    const proposal = await proposeModel(session.dir);
    emit(session, 'tool', { name: 'propose_change', status: proposal ? 'done' : 'error' });
    if (proposal) {
      emit(session, 'proposal', {
        proposalId: proposal.id,
        expiresAt: proposal.expiresAt,
        files: proposal.files,
        confirm: proposal.confirm,
        rationale: 'Pin the model for this folder so every session here starts on the same model.',
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
  if (!check) {
    if (!isObject(body) || typeof body.text !== 'string' || body.text.length < 1 || body.text.length > MAX_TEXT) return [400, { error: 'bad-request' }];
    const context = body.context ?? [];
    if (!Array.isArray(context) || context.length > MAX_CONTEXT || !context.every((c) => isObject(c) && CONTEXT_KINDS.includes(c.kind))) {
      return [400, { error: 'bad-request' }];
    }
  }
  if (session.running) return [409, { error: 'turn-running' }];
  if (!session.live) {
    if (liveCount() >= MAX_LIVE) return [429, { error: 'too-many-sessions' }];
    session.live = true;
  }
  void turn(session, check ? '' : body.text, check);
  return [202, null];
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
  if (req.method !== 'POST' || action === 'events') return [404, { error: 'not-found' }];
  const body = await readJson(req);
  if (!body) return [403, { error: 'forbidden' }];
  if (!id) return create(body.json);
  return message(session, body.json, action === 'check');
}
