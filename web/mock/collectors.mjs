/**
 * Mock sections for the v0.4 tool collectors `t3.settings`, `codex.config` and `opencode.config`
 * (docs/contracts/collector-v1.md). Synthetic data only; secrets appear already redacted, as the core emits them.
 */

const HOME = 'C:/Users/user';
const T3 = `${HOME}/.t3/userdata`;
const CODEX = `${HOME}/.codex`;
const OPENCODE = `${HOME}/.config/opencode`;
const json = (obj) => JSON.stringify(obj, null, 2) + '\n';
const src = (label, fileId) => (fileId ? { label, fileId } : { label });
const kv = (key, value, label, fileId, extra = {}) => ({ key, value, source: src(label, fileId), ...extra });
const link = (text, fileId) => (fileId ? { text, fileId } : { text });
const cols = (pairs) => pairs.map(([key, label]) => ({ key, label }));
const collector = (id, title, view, requirements, durationMs) => ({ id, title, version: '1.0.0', view, state: 'active', requirements, durationMs });

/**
 * Appends the three tool collectors and their sections to a mock snapshot.
 * @param {object} snap the snapshot being built
 * @param {string} base the target dir without a trailing slash
 * @param {(path: string, kind: string, content: string) => string} add registers a mock file and returns its id
 */
export function addToolCollectors(snap, base, add) {
  const t3Settings = add(`${T3}/settings.json`, 'settings', json({
    idleCompaction: '30m', linkedEnvironmentsEnabled: true, projectSettingsFolded: false,
    favorites: [{ provider: 'claudeAgent', model: 'opus-5-5' }, { provider: 'codex', model: 'gpt-5-codex' }],
    providerModelPreferences: { claudeAgent: { hiddenModels: ['haiku-4-5'], modelOrder: [] } },
    providerInstances: {
      claudeAgent: { driver: 'claudeAgent', displayName: 'Claude', accentColor: '#c96442', config: { autoCompactWindow: '200k' } },
      codex: { driver: 'codex', enabled: true, config: { apiKey: '[redacted]' } },
    },
  }));
  const t3 = {
    collectorId: 't3.settings', view: 't3', title: 'T3 Code',
    blocks: [
      { type: 'kv', id: 'runtime', title: 'T3 server', rows: [
        kv('version', '0.9.4', 'server-runtime.json'), kv('port', 3773, 'server-runtime.json'), kv('origin', 'http://127.0.0.1:3773', 'server-runtime.json'),
        kv('startedAt', '2026-10-31T21:36:00.000Z', 'server-runtime.json'), kv('serviceManaged', true, 'server-runtime.json'), kv('pid', 4242, 'server-runtime.json'),
      ] },
      { type: 'kv', id: 'settings', title: 'Settings', rows: [
        kv('idleCompaction', '30m', 'settings.json', t3Settings), kv('linkedEnvironmentsEnabled', true, 'settings.json', t3Settings),
        kv('projectSettingsFolded', false, 'settings.json', t3Settings),
      ] },
      { type: 'table', id: 'providers', title: 'Providers',
        columns: cols([['instance', 'Instance'], ['driver', 'Driver'], ['displayName', 'Display name'], ['enabled', 'Enabled'], ['config', 'Config'], ['hiddenModels', 'Hidden models']]),
        rows: [
          { id: 'claudeAgent', cells: { instance: 'claudeAgent', driver: 'claudeAgent', displayName: 'Claude', enabled: null, config: 'autoCompactWindow=200k', hiddenModels: 1 }, fileId: t3Settings },
          { id: 'codex', cells: { instance: 'codex', driver: 'codex', displayName: null, enabled: true, config: 'apiKey=[redacted]', hiddenModels: null }, fileId: t3Settings },
        ] },
      { type: 'table', id: 'models', title: 'Models', columns: cols([['provider', 'Provider'], ['model', 'Model'], ['status', 'Status']]),
        rows: [
          { id: 'favorite:claudeAgent:opus-5-5', cells: { provider: 'claudeAgent', model: 'opus-5-5', status: 'favorite' }, fileId: t3Settings },
          { id: 'favorite:codex:gpt-5-codex', cells: { provider: 'codex', model: 'gpt-5-codex', status: 'favorite' }, fileId: t3Settings },
          { id: 'hidden:claudeAgent:haiku-4-5', cells: { provider: 'claudeAgent', model: 'haiku-4-5', status: 'hidden' }, fileId: t3Settings },
        ] },
      { type: 'table', id: 'projects', title: 'Projects',
        columns: cols([['project', 'Project'], ['path', 'Path'], ['defaultModel', 'Default model'], ['envMode', 'Env mode'], ['autoPull', 'Auto pull'], ['scripts', 'Scripts'], ['pinned', 'Pinned'], ['archived', 'Archived'], ['updated', 'Updated']]),
        rows: [
          { id: 'p-1', cells: { project: base.split('/').pop(), path: base, defaultModel: 'claudeAgent/opus-5-5', envMode: 'worktree', autoPull: true, scripts: 2, pinned: true, archived: false, updated: '2026-11-01T21:10:00.000Z' } },
          { id: 'p-2', cells: { project: 'config', path: 'D:/ws/config', defaultModel: null, envMode: 'local', autoPull: false, scripts: 0, pinned: false, archived: false, updated: '2026-10-30T08:00:00.000Z' } },
          { id: 'p-3', cells: { project: 'acme-portal', path: 'D:/ws/acme/portal', defaultModel: 'codex/gpt-5-codex', envMode: 'worktree', autoPull: false, scripts: 1, pinned: false, archived: true, updated: '2026-10-12T15:30:00.000Z' }, flags: ['company'] },
        ] },
    ],
  };

  const codexConfig = add(`${CODEX}/config.toml`, 'settings',
    'model = "gpt-5-codex"\napproval_policy = "on-request"\nsandbox_mode = "workspace-write"\nmodel_reasoning_effort = "high"\n\n'
    + '[profiles.fast]\nmodel = "gpt-5-mini"\napproval_policy = "never"\n\n'
    + '[mcp_servers.docs]\ncommand = "npx"\nargs = ["-y", "docs-mcp"]\nenv = { DOCS_TOKEN = "[redacted]", DOCS_REGION = "eu" }\n\n'
    + '[mcp_servers.tracker]\nurl = "https://mcp.tracker.example/v1"\nhttp_headers = { Authorization = "[redacted]" }\n');
  const codexAgents = add(`${CODEX}/AGENTS.md`, 'instruction', '# Codex defaults\n\nRun the tests before reporting done.\n');
  const projectAgents = add(`${base}/AGENTS.md`, 'instruction', '# Project notes\n\nBuild with the scripts in package.json.\n');
  const reviewPrompt = add(`${CODEX}/prompts/review.md`, 'command', '---\ndescription: Review the staged diff\n---\nReview the staged changes for bugs.\n');
  const releaseSkill = add(`${CODEX}/skills/release-notes/SKILL.md`, 'skill', '---\nname: release-notes\ndescription: Draft release notes from merged changes\n---\nCollect merged changes and group them.\n');
  const codex = {
    collectorId: 'codex.config', view: 'codex', title: 'Codex',
    blocks: [
      { type: 'kv', id: 'settings', title: 'config.toml', rows: [
        kv('model', 'gpt-5-codex', 'config.toml', codexConfig), kv('approval_policy', 'on-request', 'config.toml', codexConfig),
        kv('sandbox_mode', 'workspace-write', 'config.toml', codexConfig), kv('model_reasoning_effort', 'high', 'config.toml', codexConfig),
        kv('profiles.fast.model', 'gpt-5-mini', 'config.toml', codexConfig), kv('profiles.fast.approval_policy', 'never', 'config.toml', codexConfig),
      ] },
      { type: 'table', id: 'mcp', title: 'MCP servers',
        columns: cols([['name', 'Name'], ['transport', 'Transport'], ['target', 'Command or URL'], ['env', 'Env'], ['headers', 'Headers'], ['enabled', 'Enabled'], ['source', 'Source']]),
        rows: [
          { id: 'docs', cells: { name: 'docs', transport: 'stdio', target: 'npx -y docs-mcp', env: 'DOCS_TOKEN=[redacted]; DOCS_REGION=eu', headers: null, enabled: null, source: link('config.toml', codexConfig) }, fileId: codexConfig },
          { id: 'tracker', cells: { name: 'tracker', transport: 'http', target: 'https://mcp.tracker.example/v1', env: null, headers: 'Authorization=[redacted]', enabled: null, source: link('config.toml', codexConfig) }, fileId: codexConfig },
        ] },
      { type: 'table', id: 'instructions', title: 'Instruction files', columns: cols([['order', 'Order'], ['scope', 'Scope'], ['file', 'File']]),
        rows: [
          { id: '1', cells: { order: 1, scope: 'global', file: link(`${CODEX}/AGENTS.md`, codexAgents) }, fileId: codexAgents },
          { id: '2', cells: { order: 2, scope: 'project', file: link(`${base}/AGENTS.md`, projectAgents) }, fileId: projectAgents },
        ] },
      { type: 'table', id: 'prompts', title: 'Prompts', columns: cols([['name', 'Name'], ['description', 'Description'], ['file', 'File']]),
        rows: [{ id: reviewPrompt, cells: { name: 'review', description: 'Review the staged diff', file: link(`${CODEX}/prompts/review.md`, reviewPrompt) }, fileId: reviewPrompt }] },
      { type: 'table', id: 'skills', title: 'Skills', columns: cols([['name', 'Name'], ['description', 'Description'], ['file', 'File']]),
        rows: [{ id: releaseSkill, cells: { name: 'release-notes', description: 'Draft release notes from merged changes', file: link(`${CODEX}/skills/release-notes/SKILL.md`, releaseSkill) }, fileId: releaseSkill }] },
    ],
  };

  const ocGlobal = add(`${OPENCODE}/opencode.json`, 'settings', json({
    model: 'anthropic/model-a', theme: 'system', autoupdate: true,
    provider: { anthropic: { options: { apiKey: '[redacted]' } } },
  }));
  const ocProject = add(`${base}/opencode.jsonc`, 'settings', '// project overrides\n' + json({
    model: 'anthropic/model-b', instructions: ['docs/conventions.md', 'docs/*.md'],
    mcp: { files: { type: 'local', command: ['npx', 'files-server'], environment: { MODE: '{env:MODE}' } } },
    command: { test: { description: 'Run tests', agent: 'build', template: 'Run the test suite.' } },
  }));
  const ocAgent = add(`${base}/.opencode/agent/planner.md`, 'agent', '---\ndescription: Plans work\nmode: primary\nmodel: anthropic/model-a\n---\nPlan before building.\n');
  const ocConventions = add(`${base}/docs/conventions.md`, 'instruction', '# Conventions\n\nSmall commits, clear messages.\n');
  const opencode = {
    collectorId: 'opencode.config', view: 'opencode', title: 'OpenCode',
    blocks: [
      { type: 'kv', id: 'settings', title: 'Effective settings', rows: [
        kv('model', 'anthropic/model-b', 'project/opencode.jsonc', ocProject, { contributions: [
          { value: 'anthropic/model-a', source: src('global/opencode.json', ocGlobal), status: 'overridden' },
          { value: 'anthropic/model-b', source: src('project/opencode.jsonc', ocProject), status: 'applied' },
        ] }),
        kv('theme', 'system', 'global/opencode.json', ocGlobal), kv('autoupdate', true, 'global/opencode.json', ocGlobal),
      ] },
      { type: 'table', id: 'providers', title: 'Providers and models',
        columns: cols([['provider', 'Provider'], ['name', 'Name'], ['package', 'Package'], ['baseUrl', 'Base URL'], ['models', 'Models'], ['options', 'Options']]),
        rows: [{ id: 'anthropic', cells: { provider: 'anthropic', name: null, package: null, baseUrl: null, models: null, options: 'apiKey=[redacted]' }, fileId: ocGlobal }] },
      { type: 'table', id: 'agents', title: 'Agents', columns: cols([['name', 'Name'], ['mode', 'Mode'], ['model', 'Model'], ['description', 'Description'], ['source', 'Source']]),
        rows: [{ id: `file:${ocAgent}`, cells: { name: 'planner', mode: 'primary', model: 'anthropic/model-a', description: 'Plans work', source: link(`${base}/.opencode/agent/planner.md`, ocAgent) }, fileId: ocAgent }] },
      { type: 'table', id: 'commands', title: 'Commands', columns: cols([['name', 'Name'], ['description', 'Description'], ['agent', 'Agent'], ['model', 'Model'], ['source', 'Source']]),
        rows: [{ id: 'config:test', cells: { name: 'test', description: 'Run tests', agent: 'build', model: null, source: 'config' }, fileId: ocProject }] },
      { type: 'table', id: 'mcp', title: 'MCP servers',
        columns: cols([['name', 'Name'], ['transport', 'Transport'], ['target', 'Command or URL'], ['env', 'Env'], ['headers', 'Headers'], ['enabled', 'Enabled'], ['source', 'Source']]),
        rows: [{ id: 'files', cells: { name: 'files', transport: 'local', target: 'npx files-server', env: 'MODE={env:MODE}', headers: null, enabled: null, source: link(`${base}/opencode.jsonc`, ocProject) }, fileId: ocProject }] },
      { type: 'table', id: 'instructions', title: 'Instruction files', columns: cols([['order', 'Order'], ['scope', 'Scope'], ['entry', 'Entry'], ['status', 'Status']]),
        rows: [
          { id: '1', cells: { order: 1, scope: 'project', entry: link(`${base}/AGENTS.md`, projectAgents), status: 'loaded' }, fileId: projectAgents },
          { id: '2', cells: { order: 2, scope: 'config', entry: link('docs/conventions.md', ocConventions), status: 'loaded' }, fileId: ocConventions },
          { id: '3', cells: { order: 3, scope: 'config', entry: 'docs/*.md', status: 'glob (not expanded)' } },
        ] },
    ],
  };

  snap.collectors.push(
    collector('t3.settings', 'T3 Code', 't3', { targetDir: false }, 38),
    collector('codex.config', 'Codex', 'codex', { targetDir: true }, 9),
    collector('opencode.config', 'OpenCode', 'opencode', { targetDir: true }, 7),
  );
  snap.sections.push(t3, codex, opencode);
}
