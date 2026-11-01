/**
 * Dev-only mock of the cfgdash HTTP API v1 (docs/contracts/api-v1.md), optionally serving a built bundle under the
 * security-v1 headers. Binds 127.0.0.1 only.
 *
 * node mock/server.mjs [--port 4311] [--latency 300] [--auth] [--forbid] [--dist <dir>]
 */
import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { parseArgs } from 'node:util';
import { buildSnapshot, START_DIR } from './data.mjs';
import { listDirs, resolveDir } from './dirs.mjs';
import { editsApi } from './edits.mjs';
import { search } from './search.mjs';
import { findings } from './health.mjs';

const { values: opts } = parseArgs({
  options: {
    port: { type: 'string', default: '4311' },
    latency: { type: 'string', default: '300' },
    auth: { type: 'boolean', default: false },
    forbid: { type: 'boolean', default: false },
    dist: { type: 'string' },
  },
});
const port = Number(opts.port);
const latency = Number(opts.latency);
if (!Number.isInteger(port) || port < 1 || port > 65535) fail(`--port must be 1-65535, got ${opts.port}`);
if (!Number.isFinite(latency) || latency < 0) fail(`--latency must be >= 0, got ${opts.latency}`);
const distRoot = opts.dist ? path.resolve(opts.dist) : null;
if (distRoot && !(await stat(path.join(distRoot, 'index.html')).catch(() => null))?.isFile()) fail(`no index.html in ${distRoot}; build first (npm run build)`);

const SECURITY = {
  'Content-Security-Policy': "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'",
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
};
const TYPES = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8', '.map': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml', '.png': 'image/png', '.jpg': 'image/jpeg', '.ico': 'image/x-icon', '.webp': 'image/webp',
  '.woff2': 'font/woff2', '.woff': 'font/woff', '.txt': 'text/plain; charset=utf-8', '.webmanifest': 'application/manifest+json',
};
const ROUTES = [
  { re: /^\/api\/v1\/health$/, method: 'GET', handle: () => [200, { status: 'ok', version: '0.1.0', schemaVersion: 1, edits: true }] },
  { re: /^\/api\/v1\/snapshot$/, method: 'GET', handle: (url) => withDir(url, (dir) => [200, snapshotFor(dir, url.searchParams.get('refresh') === 'true').snapshot]) },
  { re: /^\/api\/v1\/findings$/, method: 'GET', handle: (url) => withDir(url, (dir) => [200, findings(snapshotFor(dir, url.searchParams.get('refresh') === 'true').snapshot)]) },
  { re: /^\/api\/v1\/files\/([^/]+)$/, method: 'GET', handle: (url, m) => withDir(url, (dir) => fileFor(dir, m[1])) },
  { re: /^\/api\/v1\/search$/, method: 'GET', handle: (url) => searchFor(url) },
  { re: /^\/api\/v1\/dirs$/, method: 'GET', handle: (url) => withDir(url, (dir) => [200, listDirs(dir)], 'path') },
  { re: /^\/api\/v1\/shutdown$/, method: 'POST', handle: (url, m, req) => (isBearer(req) ? [202, null] : [401, { error: 'unauthenticated' }]) },
];

const cache = new Map();
const snapshotFor = (dir, refresh = false) => {
  if (refresh || !cache.has(dir)) cache.set(dir, buildSnapshot(dir));
  return cache.get(dir);
};

function withDir(url, fn, param = 'dir') {
  const raw = url.searchParams.get(param);
  const dir = raw === null ? START_DIR : resolveDir(raw);
  return dir ? fn(dir) : [400, { error: 'bad-dir' }];
}

function fileFor(dir, id) {
  const f = snapshotFor(dir).files.get(id);
  if (!f) return [404, { error: 'not-found' }];
  return [200, { id, path: f.ref.path, kind: f.ref.kind, contentType: f.contentType, content: f.content, truncated: f.truncated }];
}

function searchFor(url) {
  const q = url.searchParams.get('q') ?? '';
  if (q.length < 2) return [400, { error: 'bad-query' }];
  return withDir(url, (dir) => [200, search(snapshotFor(dir), q)]);
}

const isBearer = (req) => /^Bearer\s+\S/i.test(req.headers.authorization ?? '');
const hasCookie = (req) => /(?:^|;\s*)cfgdash_session=mock(?:;|$)/.test(req.headers.cookie ?? '');

function send(res, status, body, type = 'application/json; charset=utf-8') {
  if (body === null || body === undefined) {
    res.writeHead(status);
    return res.end();
  }
  const data = typeof body === 'string' || Buffer.isBuffer(body) ? body : JSON.stringify(body);
  res.writeHead(status, { 'Content-Type': type });
  res.end(data);
}

async function api(req, res, url) {
  res.setHeader('Cache-Control', 'no-store');
  if (latency) await new Promise((r) => setTimeout(r, latency));
  if (opts.auth && !hasCookie(req) && !isBearer(req)) return send(res, 401, { error: 'unauthenticated' });
  if (opts.forbid) return send(res, 403, { error: 'forbidden', reason: 'tailnet-identity' });
  const v2 = await editsApi(req, url);
  if (v2) return send(res, v2[0], v2[1]);
  for (const route of ROUTES) {
    const m = route.re.exec(url.pathname);
    if (!m) continue;
    if (req.method !== route.method) {
      res.setHeader('Allow', route.method);
      return send(res, 405, { error: 'method-not-allowed' });
    }
    const [status, body] = route.handle(url, m, req);
    return send(res, status, body);
  }
  return send(res, 404, { error: 'not-found' });
}

async function web(req, res, url) {
  if (url.pathname === '/' && url.searchParams.get('token') === 'mock') {
    res.writeHead(302, { 'Set-Cookie': 'cfgdash_session=mock; HttpOnly; SameSite=Strict; Path=/', Location: '/' });
    return res.end();
  }
  if (req.method !== 'GET' && req.method !== 'HEAD') {
    res.setHeader('Allow', 'GET, HEAD');
    return send(res, 405, 'Method not allowed\n', 'text/plain; charset=utf-8');
  }
  if (!distRoot) {
    return url.pathname === '/'
      ? send(res, 200, 'cfgdash mock API. Start the UI with npm start, or pass --dist to serve a build.\n', 'text/plain; charset=utf-8')
      : send(res, 404, 'Not found\n', 'text/plain; charset=utf-8');
  }
  let rel;
  try {
    rel = decodeURIComponent(url.pathname);
  } catch {
    return send(res, 400, 'Bad request\n', 'text/plain; charset=utf-8');
  }
  const file = path.resolve(distRoot, '.' + rel);
  if (rel.includes('\0') || (file !== distRoot && !file.startsWith(distRoot + path.sep))) return send(res, 404, 'Not found\n', 'text/plain; charset=utf-8');
  let target = file;
  if (!(await stat(file).catch(() => null))?.isFile()) {
    if (path.extname(rel)) return send(res, 404, 'Not found\n', 'text/plain; charset=utf-8');
    target = path.join(distRoot, 'index.html');
  }
  const body = await readFile(target);
  res.writeHead(200, { 'Content-Type': TYPES[path.extname(target).toLowerCase()] ?? 'application/octet-stream', 'Content-Length': body.length });
  res.end(req.method === 'HEAD' ? undefined : body);
}

const server = http.createServer(async (req, res) => {
  let url;
  try {
    url = new URL(`http://127.0.0.1${req.url.startsWith('/') ? req.url : '/'}`);
  } catch {
    url = new URL('http://127.0.0.1/');
  }
  res.on('finish', () => console.log(`${req.method} ${url.pathname} ${res.statusCode}`));
  for (const [k, v] of Object.entries(SECURITY)) res.setHeader(k, v);
  try {
    await (url.pathname === '/api' || url.pathname.startsWith('/api/') ? api(req, res, url) : web(req, res, url));
  } catch (err) {
    console.error(err);
    if (!res.headersSent) send(res, 500, { error: 'internal' });
    else res.destroy();
  }
});
server.on('error', (err) => fail(err.message));
server.listen(port, '127.0.0.1', () => {
  console.log(`cfgdash mock on http://127.0.0.1:${port}/ (start dir ${START_DIR}, latency ${latency} ms${opts.auth ? ', --auth: open /?token=mock' : ''}${opts.forbid ? ', --forbid' : ''}${distRoot ? `, dist ${distRoot}` : ''})`);
});

function fail(message) {
  console.error(`mock: ${message}`);
  process.exit(1);
}
