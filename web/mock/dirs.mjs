/** Synthetic folder tree for the mock's dir validation and GET /api/v1/dirs. */
import { posix } from 'node:path';
import { START_DIR } from './data.mjs';

const archive = Object.fromEntries(Array.from({ length: 34 }, (_, i) => [`project-${String(i + 1).padStart(2, '0')}`, {}]));
const TREE = {
  'C:/': {
    Users: {
      user: { '.cache': {}, '.config': {}, '.vscode': {}, Documents: {}, Downloads: {}, notes: {}, source: { repos: {} } },
    },
  },
  'D:/': {
    ws: {
      '.cache': {},
      '.tools': { bin: {} },
      acme: { agents: {}, docs: {}, skills: { 'release-notes': {} } },
      app: {
        '.claude': { agents: {}, commands: {}, rules: {}, skills: { 'app-conventions': {} } },
        '.git': {}, '.vscode': {}, docs: {}, src: { api: {}, core: {} }, tests: {}, web: { public: {}, src: {} },
      },
      archive,
      config: {
        '.claude': { agents: {}, commands: {}, data: {}, guides: {}, hooks: { lib: {} }, 'output-styles': {}, rules: {}, skills: {}, statusline: {} },
        '.engram': {}, '.git': {}, ci: {}, docs: {}, scripts: {},
      },
      notes: {},
      site: { '.claude': {}, '.git': {}, public: {}, src: { pages: {} } },
      tools: { cli: { '.github': { workflows: {} }, docs: {}, src: { commands: {} }, test: {} } },
    },
  },
};

const dirs = new Map();
const walk = (path, parent, children) => {
  const names = Object.keys(children).sort((a, b) => a.toLowerCase().localeCompare(b.toLowerCase(), 'en'));
  dirs.set(path.toLowerCase(), { path, parent, names });
  for (const n of names) walk(path.endsWith('/') ? path + n : `${path}/${n}`, path, children[n]);
};
for (const [root, children] of Object.entries(TREE)) walk(root, null, children);

/**
 * Resolves an absolute path (either separator) to the canonical path of an existing synthetic dir.
 * @returns {string | null} null when relative, malformed or unknown
 */
export function resolveDir(input) {
  if (typeof input !== 'string') return null;
  const slashed = input.replace(/\\/g, '/');
  if (!/^[A-Za-z]:\//.test(slashed)) return null;
  const norm = posix.normalize(slashed[0].toUpperCase() + slashed.slice(1));
  if (!/^[A-Z]:\//.test(norm)) return null;
  const key = norm.length > 3 ? norm.replace(/\/+$/, '') : norm;
  return dirs.get(key.toLowerCase())?.path ?? null;
}

const SUGGESTIONS = [
  { label: 'Start folder', path: START_DIR },
  { label: 'Workspace root', path: 'D:/ws' },
  { label: 'Home', path: 'C:/Users/user' },
  { label: 'Config repo', path: 'D:/ws/config' },
  { label: 'app', path: 'D:/ws/app' },
  { label: 'site', path: 'D:/ws/site' },
  { label: 'cli', path: 'D:/ws/tools/cli' },
].filter((s, i, all) => all.findIndex((o) => o.path === s.path) === i);

/** GET /api/v1/dirs answer for a canonical dir. */
export function listDirs(path) {
  const d = dirs.get(path.toLowerCase());
  return {
    path: d.path,
    parent: d.parent,
    entries: d.names.slice(0, 500).map((name) => ({ name, path: d.path.endsWith('/') ? d.path + name : `${d.path}/${name}` })),
    suggestions: SUGGESTIONS,
  };
}
