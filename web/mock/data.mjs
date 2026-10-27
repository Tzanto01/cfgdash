/**
 * Synthetic snapshot data for the cfgdash dev mock. Starts from docs/contracts/snapshot-v1.example.json
 * (read once, never written) and extends it per target dir. Never imported by the app.
 */
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';

const exampleText = readFileSync(new URL('../../docs/contracts/snapshot-v1.example.json', import.meta.url), 'utf8');
const example = JSON.parse(exampleText);

/** The run's start dir and the rich dataset. */
export const START_DIR = example.context.targetDir;
/** Target dir whose instructions.chain collector reports a timeout. */
export const TIMEOUT_DIR = 'D:/ws/site';

const CFG = 'D:/ws/config/.claude';
const REPO = 'D:/ws/config';
const ACME = 'D:/ws/acme';
const HOME = 'C:/Users/user';
const MAX_BYTES = 512 * 1024;
const exampleIds = new Map(example.files.map((f) => [f.path, f.id]));

const fid = (p) => exampleIds.get(p) ?? createHash('sha256').update(p.toLowerCase()).digest('hex').slice(0, 16);
const src = (label, fileId) => (fileId ? { label, fileId } : { label });
const kv = (key, value, label, fileId, extra = {}) => ({ key, value, source: src(label, fileId), ...extra });
const contrib = (value, label, fileId, status) => ({ value, source: src(label, fileId), status });
const link = (text, fileId) => (fileId ? { text, fileId } : { text });
const json = (obj) => JSON.stringify(obj, null, 2) + '\n';

function pad(text, size, prefix = '- Detail') {
  let out = text;
  for (let i = 1; out.length < size; i++) out += `${prefix} ${i}: synthetic filler line so the file reaches its recorded size.\n`;
  return out;
}
const doc = (title, lines, size = 0) => pad(`# ${title}\n\n${lines.join('\n')}\n\n`, size);
const agentDoc = (name, description, model, tools, body) =>
  `---\nname: ${name}\ndescription: ${description}\nmodel: ${model}\ntools: ${tools}\n---\n\n${body}\n`;
const ps1 = (purpose, size = 0) =>
  pad(`#Requires -Version 7\n<#\n.SYNOPSIS\n    ${purpose}\n.NOTES\n    Synthetic script for the cfgdash mock.\n#>\nparam()\n$ErrorActionPreference = 'Stop'\n\n`, size, '# Rule');

let bigContent;
function usageExport() {
  if (bigContent) return bigContent;
  const tools = ['Read', 'Edit', 'Bash', 'Grep', 'Write'];
  const lines = [];
  let len = 0;
  for (let i = 0; len < 1_400_000; i++) {
    const line = JSON.stringify({
      ts: new Date(Date.UTC(2026, 9, 1) + i * 60_000).toISOString(),
      session: `s-${String(i % 97).padStart(3, '0')}`,
      model: i % 3 ? 'sonnet' : 'opus',
      tool: tools[i % 5],
      inputTokens: 1000 + ((i * 37) % 9000),
      outputTokens: 200 + ((i * 13) % 1800),
    });
    lines.push(line);
    len += line.length + 1;
  }
  return (bigContent = lines.join('\n') + '\n');
}

function exampleFor(dir, base) {
  let text = exampleText.split(`"${START_DIR}"`).join(JSON.stringify(dir)).split(START_DIR).join(base);
  for (const f of example.files) {
    const p = f.path.split(START_DIR).join(base);
    if (p !== f.path) text = text.split(f.id).join(fid(p));
  }
  return JSON.parse(text);
}

function summarize(nodes) {
  const sum = { files: 0, sizeBytes: 0, tokens: 0 };
  const walk = (list) => {
    for (const n of list) {
      const b = n.badges ?? [];
      if (['lazy', 'referenced', 'conditional', 'error'].some((x) => b.includes(x))) continue;
      if (n.meta?.sizeBytes) {
        sum.files++;
        sum.sizeBytes += n.meta.sizeBytes;
        sum.tokens += n.meta.tokens ?? 0;
      }
      walk(n.children ?? []);
    }
  };
  walk(nodes);
  return sum;
}

const USER_ALLOW = [
  ...['npm run build', 'npm run lint *', 'npm ci', 'npx ng *', 'dotnet build *', 'dotnet test *', 'dotnet format *', 'git status',
    'git diff *', 'git log *', 'git show *', 'git branch *', 'git add *', 'git commit *', 'git fetch *', 'git worktree list',
    'gh pr view *', 'gh run list *', 'gh run watch *', 'pwsh -NoProfile -File ./scripts/*', 'node mock/server.mjs *', 'rg *',
    'ls *', 'Get-ChildItem *'].map((c) => `Bash(${c})`),
  'Read(./docs/**)', 'Read(./src/**)', 'Read(./web/**)', 'Read(D:/ws/config/**)', 'Read(C:/Users/user/notes/**)', 'Edit(./src/**)',
  'Edit(./web/src/**)', 'Edit(./docs/**)', 'Write(./web/mock/**)', 'WebFetch(domain:docs.example)', 'WebFetch(domain:learn.example)',
  'mcp__engram__mem_search', 'mcp__engram__mem_context', 'mcp__docs__search', 'Skill(coding-workflow)',
];
const USER_ASK = ['Bash(git push *)', 'Bash(git rebase *)', 'Bash(git reset *)', 'Bash(npm install *)', 'Bash(winget install *)',
  'Edit(./.github/**)', 'WebFetch(domain:*)', 'mcp__issues__create_issue'];
const USER_DENY = ['Bash(rm -rf *)', 'Bash(git push --force *)', 'Bash(git clean -fdx)', 'Bash(curl * | sh)', 'Bash(scp *)',
  'Read(./.env)', 'Read(./.env.*)', 'Read(**/*.pem)', 'Read(**/*.key)', 'Read(**/.ssh/**)', 'Read(**/.git-credentials)',
  'Read(**/.credentials.json)', 'Edit(**/.claude/settings.json)', 'mcp__issues__delete_issue'];
const EXTRA_DIRS = Array.from({ length: 30 }, (_, i) => `D:/ws/archive/project-${String(i + 1).padStart(2, '0')}`);
const STATUS_CMD = 'pwsh -NoProfile -NonInteractive -File "${CLAUDE_CONFIG_DIR}/statusline/statusline.ps1" -ShowModel -ShowBranch '
  + '-ShowContextPercent -ShowCost -ShowDuration -Separator " | " -BranchIcon "git:" -MaxBranchLength 40 -ContextWarnPercent 70 '
  + '-ContextDangerPercent 90 -CostCurrency EUR -DurationFormat "mm:ss" -Theme muted -LogFile "D:/ws/config/.claude/statusline/statusline.log"';
const ORCHESTRATOR = agentDoc('orchestrator', 'Plans the work and coordinates the team.', 'opus', 'Read, Grep, Glob, Task', 'You are orchestrator. Plans the work and coordinates the team.');
const PLUGINS = { 'docs-tools@example-marketplace': true, 'lint-helpers@example-marketplace': false };
const MARKETPLACES = { 'example-marketplace': { source: { source: 'github', repo: 'example/marketplace' } } };
const AUTO_MODE = { environment: ['Local developer workstation', 'Source control: git on the task branch', 'Network: tailnet example.ts.net only'] };

/**
 * Builds the snapshot and the file contents for one target dir.
 * @param {string} dir canonical absolute target dir
 * @returns {{ snapshot: object, files: Map<string, { ref: object, contentType: string, content: string, truncated: boolean }> }}
 */
export function buildSnapshot(dir) {
  const rich = dir === START_DIR;
  const base = dir.replace(/\/$/, '');
  const name = base.split('/').pop() || dir;
  const snap = exampleFor(dir, base);
  snap.machine.collectedAt = new Date().toISOString();
  const ex = Object.fromEntries(snap.collectors.map((c) => [c.id, c]));
  const sx = Object.fromEntries(snap.sections.map((s) => [s.collectorId, s]));
  const files = new Map();

  const add = (path, kind, content, opts = {}) => {
    const id = fid(path);
    if (!files.has(id)) {
      const truncated = content.length > MAX_BYTES;
      const contentType = opts.contentType ?? (/\.md$/i.test(path) ? 'markdown' : /\.json$/i.test(path) ? 'json' : 'text');
      const ref = { id, path, kind, sizeBytes: Buffer.byteLength(content), tokens: Math.ceil(content.length / 4), company: path.startsWith(ACME + '/') };
      files.set(id, { ref, contentType, content: truncated ? content.slice(0, MAX_BYTES) : content, truncated });
    }
    return id;
  };
  const fileNode = (id, label, path, kind, content, badges, meta = {}, children = []) => {
    const fileId = add(path, kind, content);
    const { sizeBytes, tokens } = files.get(fileId).ref;
    return { id, label, fileId, badges, meta: { ...meta, sizeBytes, tokens }, children };
  };

  const U = fid(`${CFG}/settings.json`);
  const L = fid(`${base}/.claude/settings.local.json`);
  const P = fid(`${base}/.claude/settings.json`);
  const projectAllow = ['Bash(npm test *)', ...(rich ? ['Bash(npm run e2e *)', 'Bash(npx playwright test *)', 'Read(./tests/**)', 'Edit(./tests/**)'] : [])];
  const projectDeny = rich ? ['Edit(./web/dist/**)'] : [];
  const userEnv = {
    CLAUDE_CODE_EFFORT_LEVEL: 'medium', CLAUDE_CODE_USE_POWERSHELL_TOOL: '1', CLAUDE_CODE_SUBAGENT_MODEL: 'sonnet',
    CLAUDE_CODE_ENABLE_TELEMETRY: '1', OTEL_EXPORTER_OTLP_ENDPOINT: 'http://collector.example:4318',
    OTEL_EXPORTER_OTLP_HEADERS: '[redacted]', OTEL_RESOURCE_ATTRIBUTES: 'host.name=devbox,team=platform',
  };
  const hookCmd = (script) => ({ type: 'command', command: `pwsh -NoProfile -File "$CLAUDE_CONFIG_DIR/hooks/${script}"` });

  add(`${CFG}/settings.json`, 'settings', json({
    model: 'sonnet', effortLevel: 'high', outputStyle: 'Concise', agent: 'orchestrator', fallbackModel: 'sonnet', language: 'english',
    cleanupPeriodDays: 30, includeCoAuthoredBy: false, alwaysThinkingEnabled: true, spinnerTipsEnabled: false, respectGitignore: true,
    apiKeyHelper: '[redacted]',
    permissions: { defaultMode: 'default', allow: USER_ALLOW, ask: USER_ASK, deny: USER_DENY, additionalDirectories: EXTRA_DIRS, disableBypassPermissionsMode: 'disable' },
    env: userEnv, statusLine: { type: 'command', command: STATUS_CMD }, enabledPlugins: PLUGINS, extraKnownMarketplaces: MARKETPLACES, autoMode: AUTO_MODE,
    hooks: {
      SessionStart: [{ matcher: 'startup|resume', hooks: [hookCmd('session-context.ps1')] }],
      UserPromptSubmit: [{ hooks: [hookCmd('prompt-tags.ps1')] }],
      PreToolUse: [{ matcher: 'Bash', hooks: [hookCmd('shell-guard.ps1')] }, { matcher: 'Edit|Write|MultiEdit', hooks: [hookCmd('write-guard.ps1')] }],
      PostToolUse: [{ matcher: 'Edit|Write', hooks: [hookCmd('format.ps1')] }],
      Notification: [{ hooks: [{ type: 'http', url: 'https://hooks.example/notify', timeout: 5 }] }],
      SubagentStop: [{ hooks: [{ type: 'prompt', prompt: 'Check the subagent report for open TODOs.' }] }],
      TeammateIdle: [{ hooks: [hookCmd('idle.ps1')] }],
    },
  }));
  add(`${base}/.claude/settings.local.json`, 'settings', json({ model: 'opus', permissions: { defaultMode: 'acceptEdits' } }));
  add(`${base}/.claude/settings.json`, 'settings', json({
    permissions: { allow: projectAllow, ...(rich ? { deny: projectDeny } : {}) },
    ...(rich ? { env: { CLAUDE_CODE_EFFORT_LEVEL: 'high' } } : {}),
    hooks: {
      Stop: [{ matcher: '*', hooks: [{ type: 'command', command: 'pwsh -NoProfile -File hooks/gone.ps1' }] }],
      ...(rich ? { PreToolUse: [{ matcher: 'Bash', hooks: [hookCmd('shell-guard.ps1')] }] } : {}),
    },
  }));

  const MANAGED_BAD = 'C:/Program Files/ClaudeCode/managed-settings.d/20-team.json';
  const M = add(MANAGED_BAD, 'settings', '{\n  "permissions": {\n    "deny": [\n      "Bash(rm -rf *)",\n    ]\n  },\n}\n', { contentType: 'text' });

  const ID = {
    userClaude: fid(`${CFG}/CLAUDE.md`), projClaude: fid(`${base}/CLAUDE.md`), agentsMd: fid(`${base}/AGENTS.md`),
    override: fid(`${base}/docs/AGENTS.override.md`), lazy: fid(`${base}/web/CLAUDE.md`), shellGuard: fid(`${CFG}/hooks/shell-guard.ps1`),
  };
  const BIG = add(`${CFG}/data/usage-export.jsonl`, 'other', usageExport());
  const GIT_RULE = add(`${CFG}/rules/git.md`, 'instruction', doc('Git rules', ['Keep commits small and focused.', 'Never force-push shared branches.', 'Write the subject in the imperative mood.'], 1400));

  const machine = sx['overview.machine'];
  const [hostRow, versionRow] = machine.blocks[0].rows;
  machine.blocks[0].rows = [
    hostRow,
    kv('os', snap.machine.os.description, 'system'),
    kv('arch', snap.machine.os.arch, 'system'),
    versionRow,
    ...snap.context.paths.map((p) => kv(p.key, p.value, p.source ?? 'not set')),
  ];

  const configRepo = {
    collectorId: 'overview.config-repo', view: 'overview', title: 'Config repo',
    blocks: [
      { type: 'kv', id: 'git', title: 'Git state', description: `Read-only git status of ${REPO}`, rows: [
        kv('branch', 'main', 'git rev-parse'), kv('upstream', 'origin/main', 'git rev-parse'), kv('ahead', 2, 'git status'), kv('behind', 0, 'git status'),
        kv('dirty', 3, 'git status'), kv('lastCommit.hash', '3f9c2a1', 'git log'), kv('lastCommit.date', '2026-10-26T18:42:00Z', 'git log'),
        kv('lastCommit.subject', 'Tighten hook guards for worktree paths', 'git log'),
      ] },
      { type: 'table', id: 'dirty', title: 'Dirty files',
        columns: [{ key: 'status', label: 'Status' }, { key: 'path', label: 'Path' }],
        rows: [
          { id: 'settings', cells: { status: 'M', path: link('.claude/settings.json', U) }, fileId: U },
          { id: 'git-rule', cells: { status: 'M', path: link('.claude/rules/git.md', GIT_RULE) }, fileId: GIT_RULE },
          { id: 'usage-export', cells: { status: '??', path: link('.claude/data/usage-export.jsonl', BIG) }, fileId: BIG },
        ] },
    ],
  };

  const settings = sx['settings.effective'];
  const [effective, layers] = settings.blocks;
  const [modelRow, allowRow, otelRow] = effective.rows;
  allowRow.value = [...projectAllow, ...USER_ALLOW];
  allowRow.contributions = [contrib(projectAllow, 'project', P, 'merged'), contrib(USER_ALLOW, 'user', U, 'merged')];
  effective.rows = [
    modelRow, allowRow, otelRow,
    kv('permissions.defaultMode', 'acceptEdits', 'project (local)', L, { contributions: [contrib('acceptEdits', 'project (local)', L, 'applied'), contrib('default', 'user', U, 'overridden')] }),
    rich
      ? kv('permissions.deny', [...projectDeny, ...USER_DENY], '2 layers', undefined, { contributions: [contrib(projectDeny, 'project', P, 'merged'), contrib(USER_DENY, 'user', U, 'merged')] })
      : kv('permissions.deny', USER_DENY, 'user', U),
    kv('permissions.ask', USER_ASK, 'user', U),
    kv('permissions.additionalDirectories', EXTRA_DIRS, 'user', U),
    kv('permissions.disableBypassPermissionsMode', 'disable', 'user', U),
    kv('effortLevel', 'high', 'user', U),
    kv('outputStyle', 'Concise', 'user', U),
    kv('agent', 'orchestrator', 'user', U),
    kv('fallbackModel', 'sonnet', 'user', U),
    kv('language', 'english', 'user', U),
    kv('statusLine.type', 'command', 'user', U),
    kv('statusLine.command', STATUS_CMD, 'user', U),
    kv('enabledPlugins', PLUGINS, 'user', U),
    kv('extraKnownMarketplaces', MARKETPLACES, 'user', U),
    kv('autoMode', AUTO_MODE, 'user', U),
    kv('cleanupPeriodDays', 30, 'user', U),
    kv('includeCoAuthoredBy', false, 'user', U),
    kv('alwaysThinkingEnabled', true, 'user', U),
    kv('spinnerTipsEnabled', false, 'user', U),
    kv('respectGitignore', true, 'user', U),
    kv('apiKeyHelper', '[redacted]', 'user', U, { redacted: true, flags: ['redacted'] }),
    rich
      ? kv('env.CLAUDE_CODE_EFFORT_LEVEL', 'high', 'project', P, { contributions: [contrib('high', 'project', P, 'applied'), contrib('medium', 'user', U, 'overridden')] })
      : kv('env.CLAUDE_CODE_EFFORT_LEVEL', 'medium', 'user', U),
    kv('env.CLAUDE_CODE_USE_POWERSHELL_TOOL', '1', 'user', U),
    kv('env.OTEL_EXPORTER_OTLP_ENDPOINT', 'http://collector.example:4318', 'user', U),
  ];
  layers.rows = [
    { id: 'managed', cells: { layer: 'managed', file: link(MANAGED_BAD, M), status: 'parse error: trailing comma (line 5)' }, flags: ['error'], fileId: M },
    { id: 'managed-programdata', cells: { layer: 'managed (ignored)', file: 'C:/ProgramData/ClaudeCode/managed-settings.json', status: 'ignored, not read' } },
    { id: 'cli', cells: { layer: 'command line', file: null, status: 'not observable' } },
    ...layers.rows.slice(1),
    ...(P === U
      ? []
      : [{ id: 'user-junction', cells: { layer: 'user (via C:/Users/user/.claude junction)', file: link(`${CFG}/settings.json`, U), status: 'same file, merged once' }, flags: ['duplicate'], fileId: U }]),
  ];
  if (P === U) layers.rows.find((r) => r.id === 'user').flags = ['duplicate'];
  ex['settings.effective'].notes = [
    'Command-line settings are not observable.',
    'Object merge is not documented; objects are merged per leaf key.',
    'permissions.allow and env from project files apply only after the folder is trusted.',
  ];

  const deepNames = ['a', 'b', 'c', 'd'];
  let deep = { id: 'n1.4.e', label: '@e.md', badges: ['import', 'error'], meta: { error: 'depth' }, children: [] };
  for (let i = deepNames.length - 1; i >= 0; i--) {
    const n = deepNames[i];
    const next = i === deepNames.length - 1 ? 'e' : deepNames[i + 1];
    deep = fileNode(`n1.4.${n}`, i === 0 ? '@deep/a.md' : `@${n}.md`, `${CFG}/deep/${n}.md`, 'instruction', doc(`Deep import ${n}`, [`@${next}.md`]), ['import', 'loaded'], {}, [deep]);
  }
  const userClaude = fileNode('n1', `${CFG}/CLAUDE.md`, `${CFG}/CLAUDE.md`, 'instruction',
    doc('Global rules', ['Answer briefly and lead with the outcome.', '@guides/tone.md', '@workflow.md', '@~/notes/shared-glossary.md', '@deep/a.md'], 9000),
    ['user', 'loaded'], { order: 1 }, [
      fileNode('n1.1', '@guides/tone.md', `${CFG}/guides/tone.md`, 'instruction', doc('Tone', ['Plain words, no filler.', '@tone-examples.md']), ['import', 'loaded'], {}, [
        fileNode('n1.1.1', '@tone-examples.md', `${CFG}/guides/tone-examples.md`, 'instruction', doc('Tone examples', ['Say "Done: tests pass" instead of a paragraph.']), ['import', 'loaded']),
      ]),
      fileNode('n1.2', '@workflow.md', `${CFG}/workflow.md`, 'instruction', doc('Workflow', ['Plan, build, verify, report.', '@workflow-steps.md']), ['import', 'loaded'], {}, [
        fileNode('n1.2.1', '@workflow-steps.md', `${CFG}/workflow-steps.md`, 'instruction', doc('Workflow steps', ['Step details live here.', '@workflow.md']), ['import', 'loaded'], {}, [
          { id: 'n1.2.1.1', label: '@workflow.md', badges: ['import', 'error'], meta: { error: 'cycle' }, children: [] },
        ]),
      ]),
      fileNode('n1.3', '@~/notes/shared-glossary.md', `${HOME}/notes/shared-glossary.md`, 'instruction', doc('Shared glossary', ['Harness: the agent runtime and its configuration.']), ['import', 'external', 'loaded']),
      deep,
    ]);
  const projChildren = [
    fileNode('n2.1', '@AGENTS.md', `${base}/AGENTS.md`, 'instruction', doc(`${name} agent notes`, ['Build with the scripts in package.json.', 'Run the tests before reporting done.'], 4000), ['import', 'loaded']),
    fileNode('n2.2', 'docs/AGENTS.override.md', `${base}/docs/AGENTS.override.md`, 'instruction', doc('Override notes', ['Temporary rules while the migration runs.'], 800), ['referenced']),
    { id: 'n2.3', label: '@docs/missing.md', badges: ['import', 'error'], meta: { error: 'missing' }, children: [] },
    ...(rich ? [fileNode('n2.4', '@D:/ws/acme/docs/conventions.md', `${ACME}/docs/conventions.md`, 'instruction', doc('Acme conventions', ['Release notes go through the release-check agent.', 'Every change links its ticket in the branch name.']), ['import', 'external', 'company', 'loaded'])] : []),
  ];
  const projClaude = fileNode('n2', `${base}/CLAUDE.md`, `${base}/CLAUDE.md`, 'instruction',
    doc(`${name} project rules`, ['@AGENTS.md', 'See [override notes](docs/AGENTS.override.md) during the migration.', '@docs/missing.md', ...(rich ? ['@D:/ws/acme/docs/conventions.md'] : [])], 5200),
    ['project', 'loaded'], {}, projChildren);
  const chainNodes = [
    userClaude,
    fileNode('n5', `${CFG}/rules/git.md`, `${CFG}/rules/git.md`, 'instruction', '', ['user', 'rule', 'loaded']),
    fileNode('n6', `${CFG}/rules/testing.md`, `${CFG}/rules/testing.md`, 'instruction', doc('Testing rules', ['Every test protects a real requirement.', 'Synthetic data only.'], 1200), ['user', 'rule', 'loaded']),
    projClaude,
    ...(rich ? [
      fileNode('n7', `${base}/.claude/rules/api.md`, `${base}/.claude/rules/api.md`, 'instruction', doc('API rules', ['Version every route under /api/v1.']), ['project', 'rule', 'loaded']),
      fileNode('n8', `${base}/.claude/rules/frontend.md`, `${base}/.claude/rules/frontend.md`, 'instruction', `---\npaths:\n  - "web/**/*.ts"\n  - "web/**/*.html"\n---\n\n# Frontend rules\n\nSignals first, OnPush everywhere.\n`, ['project', 'rule', 'conditional'], { paths: 'web/**/*.ts, web/**/*.html' }),
    ] : []),
    fileNode('n9', 'Main-thread agent: orchestrator', `${CFG}/agents/orchestrator.md`, 'agent', ORCHESTRATOR, ['agent', 'loaded']),
    { id: 'n3', label: 'Output style: Concise', badges: ['built-in', 'loaded'], meta: {}, children: [] },
    fileNode('n4', `${base}/web/CLAUDE.md`, `${base}/web/CLAUDE.md`, 'instruction', doc('Web folder rules', ['Run npm test in web/.'], 200), ['lazy']),
  ];
  let order = 0;
  for (const n of chainNodes) if (!n.badges.some((b) => ['lazy', 'conditional'].includes(b))) n.meta = { order: ++order, ...n.meta };
  const instructions = sx['instructions.chain'];
  instructions.blocks[0].nodes = chainNodes;
  instructions.blocks[0].summary = summarize(chainNodes);

  const agentRows = [];
  const agentTree = { project: [], user: [], plugin: [] };
  const addAgent = (scope, nm, path, model, tools, description, extra = {}) => {
    const id = add(path, 'agent', agentDoc(nm, description, model, tools, `You are ${nm}. ${description}`));
    agentRows.push({ id: `${scope}-${nm}`, cells: { name: nm, scope, model, tools, hooks: extra.hooks ?? null, description, core: extra.core ?? false }, ...(extra.flags ? { flags: extra.flags } : {}), fileId: id });
    const { sizeBytes, tokens } = files.get(id).ref;
    (agentTree[scope === 'plugin' ? 'plugin' : scope === 'project' ? 'project' : 'user']).push({ id: `${scope}-${nm}`, label: nm, fileId: id, badges: extra.flags ?? [], meta: { sizeBytes, tokens }, children: [] });
  };
  addAgent('user', 'orchestrator', `${CFG}/agents/orchestrator.md`, 'opus', 'Read, Grep, Glob, Task', 'Plans the work and coordinates the team.');
  if (rich) {
    addAgent('project', 'implementer', `${base}/.claude/agents/implementer.md`, 'opus', 'Read, Edit, Write, Bash', 'Implements slices for this project.');
    addAgent('project', 'api-docs', `${base}/.claude/agents/api-docs.md`, 'haiku', 'Read, Grep', 'Keeps the API reference in sync.');
  }
  addAgent('user', 'implementer', `${CFG}/agents/implementer.md`, 'sonnet', 'Read, Edit, Write, Bash', 'Executes a defined plan.', rich ? { flags: ['overridden'] } : {});
  addAgent('user', 'explorer', `${CFG}/agents/explorer.md`, 'haiku', 'Read, Grep, Glob', 'Finds code and context fast.');
  addAgent('user', 'tester', `${CFG}/agents/tester.md`, 'sonnet', 'Read, Edit, Bash', 'Writes and runs tests.', { hooks: 'PreToolUse: 1' });
  addAgent('user', 'reviewer', `${CFG}/agents/reviewer.md`, 'opus', 'Read, Grep', 'Reviews diffs for correctness and risk.');
  addAgent('user', 'designer', `${CFG}/agents/designer.md`, 'sonnet', 'Read, Edit, Write', 'Builds UI components and styles.');
  addAgent('user', 'writer', `${CFG}/agents/writer.md`, 'sonnet', 'Read, Edit', 'Drafts docs and release notes.');
  addAgent('user', 'release-check', `${ACME}/agents/release-check.md`, 'sonnet', 'Read, Grep', 'Checks a release against the company checklist.', { flags: ['company', 'link'], core: true });
  addAgent('user', 'compliance-review', `${ACME}/agents/compliance-review.md`, 'opus', 'Read, Grep', 'Reviews changes for company compliance rules.', { flags: ['company'], core: true });
  addAgent('plugin', 'docs-tools:link-check', `${CFG}/plugins/cache/docs-tools/agents/link-check.md`, 'haiku', 'Read, WebFetch', 'Finds broken links in docs.');
  const agents = {
    collectorId: 'agents.definitions', view: 'agents', title: 'Agent definitions',
    blocks: [
      { type: 'table', id: 'agents', title: 'Agents', description: 'Same name: the higher location wins.',
        columns: [{ key: 'name', label: 'Name' }, { key: 'scope', label: 'Scope' }, { key: 'model', label: 'Model' }, { key: 'tools', label: 'Tools' },
          { key: 'hooks', label: 'Hooks' }, { key: 'description', label: 'Description' }, { key: 'core', label: 'Company core' }],
        rows: agentRows },
      { type: 'tree', id: 'scopes', title: 'Agents by location', summary: { files: agentRows.length, sizeBytes: agentRows.reduce((s, r) => s + files.get(r.fileId).ref.sizeBytes, 0), tokens: agentRows.reduce((s, r) => s + files.get(r.fileId).ref.tokens, 0) },
        nodes: [
          { id: 'managed', label: 'Managed', badges: ['empty'], meta: {}, children: [] },
          { id: 'project', label: `Project ${base}/.claude/agents`, badges: ['project'], meta: {}, children: agentTree.project },
          { id: 'user', label: `User ${CFG}/agents`, badges: ['user'], meta: {}, children: agentTree.user },
          { id: 'plugin', label: 'Plugin docs-tools', badges: ['plugin'], meta: {}, children: agentTree.plugin },
        ] },
    ],
  };

  const skillRow = (nm, description, scope, path, linkTarget, flags) => {
    const id = path ? add(path, path.includes('/commands/') ? 'command' : path.includes('output-styles') ? 'outputStyle' : 'skill', `---\nname: ${nm}\ndescription: ${description}\n---\n\n# ${nm}\n\n${description}\n`) : undefined;
    return { id: `${scope}-${nm}`.replace(/[\s/]/g, '_'), cells: { name: nm, description, scope, path: path ? link(path, id) : null, link: linkTarget ?? null }, ...(flags ? { flags } : {}), ...(id ? { fileId: id } : {}) };
  };
  const cols = [{ key: 'name', label: 'Name' }, { key: 'description', label: 'Description' }, { key: 'scope', label: 'Scope' }, { key: 'path', label: 'Path' }, { key: 'link', label: 'Link target' }];
  const skills = {
    collectorId: 'skills.catalog', view: 'skills', title: 'Skills and commands',
    blocks: [
      { type: 'table', id: 'skills', title: 'Skills', columns: cols, rows: [
        skillRow('coding-workflow', 'Plan, build, verify and report non-trivial code changes.', 'user', `${CFG}/skills/coding-workflow/SKILL.md`),
        skillRow('humanizer', 'Rewrites drafted prose so it reads naturally.', 'user', `${CFG}/skills/humanizer/SKILL.md`),
        skillRow('department-lead', 'Runs a multi-team change from plan to merge.', 'user', `${CFG}/skills/department-lead/SKILL.md`),
        skillRow('release-notes', 'Writes release notes in the company format.', 'user', `${ACME}/skills/release-notes/SKILL.md`, `${ACME}/skills/release-notes`, ['link', 'company']),
        ...(rich ? [skillRow('app-conventions', 'Project conventions for the app repo.', 'project', `${base}/.claude/skills/app-conventions/SKILL.md`)] : []),
        skillRow('docs-tools:changelog', 'Drafts a changelog from merged branches.', 'plugin', `${CFG}/plugins/cache/docs-tools/skills/changelog/SKILL.md`),
      ] },
      { type: 'table', id: 'commands', title: 'Slash commands', columns: cols, rows: [
        skillRow('/review', 'Review the current diff.', 'user', `${CFG}/commands/review.md`),
        skillRow('/status', 'Summarize the working tree.', 'user', `${CFG}/commands/status.md`),
        ...(rich ? [skillRow('/ship', 'Run checks and prepare the branch for review.', 'project', `${base}/.claude/commands/ship.md`)] : []),
        skillRow('/docs-tools:check', 'Check docs links and anchors.', 'plugin', `${CFG}/plugins/cache/docs-tools/commands/check.md`, `${CFG}/plugins/marketplaces/example-marketplace/docs-tools/commands/check.md`, ['link']),
      ] },
      { type: 'table', id: 'output-styles', title: 'Output styles', columns: cols, rows: [
        skillRow('Concise', 'Short answers, outcome first.', 'built-in', null),
        skillRow('Proactive', 'Suggests next steps.', 'built-in', null),
        skillRow('Explanatory', 'Explains choices while working.', 'built-in', null),
        skillRow('Learning', 'Leaves small tasks for you to do.', 'built-in', null),
        skillRow('Terse', 'Answers in as few words as possible.', 'user', `${CFG}/output-styles/terse.md`),
      ] },
    ],
  };

  const hooks = sx['hooks.registered'];
  const hookBlock = hooks.blocks[0];
  add(`${CFG}/hooks/shell-guard.ps1`, 'hookScript', ps1('Blocks destructive shell commands.', 85000));
  const hookRow = (id, event, matcher, script, sourceText, sourceId, purpose, flags) => {
    const sid = script.path ? add(script.path, 'hookScript', ps1(purpose ?? 'Synthetic hook.')) : undefined;
    return { id, cells: { event, matcher, script: sid ? link(script.text, sid) : script.text, source: link(sourceText, sourceId), purpose }, ...(flags ? { flags } : {}), ...(sid ? { fileId: sid } : {}) };
  };
  const TESTER = fid(`${CFG}/agents/tester.md`);
  hookBlock.rows = [
    hookRow('SessionStart-0-0', 'SessionStart', 'startup|resume', { text: 'hooks/session-context.ps1', path: `${CFG}/hooks/session-context.ps1` }, 'user settings', U, 'Prints the workspace map at session start.'),
    hookRow('UserPromptSubmit-0-0', 'UserPromptSubmit', '*', { text: 'hooks/prompt-tags.ps1', path: `${CFG}/hooks/prompt-tags.ps1` }, 'user settings', U, 'Adds ticket and folder tags to the prompt context.'),
    hookBlock.rows[0],
    hookRow('PreToolUse-1-0', 'PreToolUse', 'Edit|Write|MultiEdit', { text: 'hooks/write-guard.ps1', path: `${CFG}/hooks/write-guard.ps1` }, 'user settings', U, 'Blocks writes to agent instruction files.'),
    ...(rich ? [{ ...structuredClone(hookBlock.rows[0]), id: 'PreToolUse-0-0-project', cells: { ...hookBlock.rows[0].cells, source: link('project settings', P) }, flags: ['duplicate'] }] : []),
    hookRow('PreToolUse-agent-tester', 'PreToolUse', 'Bash', { text: 'hooks/test-guard.ps1', path: `${CFG}/hooks/test-guard.ps1` }, 'agent: tester', TESTER, 'Keeps test runs inside the worktree.'),
    hookRow('PostToolUse-0-0', 'PostToolUse', 'Edit|Write', { text: 'hooks/format.ps1', path: `${CFG}/hooks/format.ps1` }, 'user settings', U, 'Formats edited files.'),
    hookRow('Notification-0-0', 'Notification', '*', { text: 'http: https://hooks.example/notify' }, 'user settings', U, null),
    hookRow('SubagentStop-0-0', 'SubagentStop', '*', { text: 'prompt: Check the subagent report for open TODOs.' }, 'user settings', U, null),
    hookRow('TeammateIdle-0-0', 'TeammateIdle', '*', { text: 'hooks/idle.ps1', path: `${CFG}/hooks/idle.ps1` }, 'user settings', U, 'Logs idle teammates.', ['unknown-event']),
    hookBlock.rows[1],
  ];
  ex['hooks.registered'].notes = ['1 hook script not found', 'TeammateIdle is not a documented event; shown as registered.'];

  const MCP = add(`${base}/.mcp.json`, 'mcp', json({ mcpServers: {
    docs: { type: 'http', url: 'https://mcp.docs.example/mcp', headers: { Authorization: '[redacted]' } },
    ...(rich ? { metrics: { type: 'sse', url: '${METRICS_URL}', headers: { Authorization: '[redacted]' } } } : {}),
  } }));
  const mcpRow = (id, nm, scope, transport, target, env, headers, source, sourceId, flags) =>
    ({ id, cells: { name: nm, scope, transport, target, env, headers, source: link(source, sourceId) }, ...(flags ? { flags } : {}), ...(sourceId ? { fileId: sourceId } : {}) });
  const mcp = {
    collectorId: 'mcp.servers', view: 'mcp-permissions', title: 'MCP servers',
    blocks: [{ type: 'table', id: 'servers', title: 'MCP servers', description: 'Same name: the whole entry from the higher scope is used.',
      columns: [{ key: 'name', label: 'Name' }, { key: 'scope', label: 'Scope' }, { key: 'transport', label: 'Transport' }, { key: 'target', label: 'Command or URL' },
        { key: 'env', label: 'Env' }, { key: 'headers', label: 'Headers' }, { key: 'source', label: 'Source' }],
      rows: [
        mcpRow('local-issues', 'issues', 'local', 'stdio', 'npx -y @example/issues-mcp', 'ISSUES_TOKEN=[redacted]', null, `.claude.json (projects[${dir}])`, undefined, ['redacted']),
        mcpRow('project-docs', 'docs', 'project', 'http', 'https://mcp.docs.example/mcp', null, 'Authorization: [redacted]', `${base}/.mcp.json`, MCP, ['redacted']),
        ...(rich ? [mcpRow('project-metrics', 'metrics', 'project', 'sse', '${METRICS_URL}', null, 'Authorization: [redacted]', `${base}/.mcp.json`, MCP, ['redacted'])] : []),
        mcpRow('user-issues', 'issues', 'user', 'stdio', 'npx -y @example/issues-mcp@1.2.0', 'ISSUES_TOKEN=[redacted]', null, '.claude.json', undefined, ['overridden', 'redacted']),
        mcpRow('user-engram', 'engram', 'user', 'stdio', 'engram mcp --tools=agent', 'ENGRAM_DATA_DIR=D:/ws/config/.engram', null, '.claude.json'),
        mcpRow('user-browser', 'browser', 'user', 'stdio', 'npx -y @example/browser-mcp@1.4.0', null, null, '.claude.json'),
        mcpRow('plugin-search', 'docs-tools:search', 'plugin', 'http', 'https://search.example/mcp', null, 'X-Api-Key: [redacted]', 'docs-tools plugin.json', undefined, ['redacted']),
      ] }],
  };

  const permRows = [];
  const pushRules = (kind, list, label, sourceId) => list.forEach((rule) => permRows.push({ id: `${kind}-${permRows.length}`, cells: { kind, rule, source: link(label, sourceId) } }));
  pushRules('deny', projectDeny, 'project', P);
  pushRules('deny', USER_DENY, 'user', U);
  pushRules('ask', USER_ASK, 'user', U);
  pushRules('allow', projectAllow, 'project', P);
  pushRules('allow', USER_ALLOW, 'user', U);
  const highlights = {
    collectorId: 'settings.highlights', view: 'mcp-permissions', title: 'Permissions and highlights',
    blocks: [
      { type: 'kv', id: 'highlights', title: 'Highlights', rows: [
        kv('permissions.defaultMode', 'acceptEdits', 'project (local)', L),
        kv('model', 'opus', 'project (local)', L),
        kv('effortLevel', 'high', 'user', U),
        kv('outputStyle', 'Concise', 'user', U),
        kv('agent', 'orchestrator', 'user', U),
        kv('statusLine', { type: 'command', command: STATUS_CMD }, 'user', U),
        kv('autoMode.environment', AUTO_MODE.environment, 'user', U),
        kv('enabledPlugins', PLUGINS, 'user', U),
        kv('extraKnownMarketplaces', MARKETPLACES, 'user', U),
      ] },
      { type: 'table', id: 'permissions', title: 'Permission rules', description: 'Deny rules from any scope are evaluated before allow rules.',
        columns: [{ key: 'kind', label: 'Kind' }, { key: 'rule', label: 'Rule' }, { key: 'source', label: 'Source' }], rows: permRows },
    ],
  };

  const envRows = [
    rich
      ? kv('CLAUDE_CODE_EFFORT_LEVEL', 'high', 'project', P, { contributions: [contrib('high', 'project', P, 'applied'), contrib('medium', 'user', U, 'overridden')] })
      : kv('CLAUDE_CODE_EFFORT_LEVEL', 'medium', 'user', U),
    ...Object.entries(userEnv).filter(([k]) => k !== 'CLAUDE_CODE_EFFORT_LEVEL')
      .map(([k, v]) => kv(k, v, 'user', U, v === '[redacted]' ? { redacted: true, flags: ['redacted'] } : {})),
    kv('HTTPS_PROXY', null, 'not set in any layer'),
  ];
  const varRow = (nm, proc, user, mach, kind, flags) => ({ id: nm, cells: { name: nm, process: proc, user, machine: mach, kind }, ...(flags ? { flags } : {}) });
  const env = {
    collectorId: 'env.harness', view: 'env', title: 'Environment',
    blocks: [
      { type: 'kv', id: 'settings-env', title: 'Settings env', description: 'Merged per variable name from every settings layer.', rows: envRows },
      { type: 'table', id: 'variables', title: 'Allowlisted variables', description: 'Credential variables show only whether they are set.',
        columns: [{ key: 'name', label: 'Name' }, { key: 'process', label: 'Process' }, { key: 'user', label: 'User' }, { key: 'machine', label: 'Machine' }, { key: 'kind', label: 'Kind' }],
        rows: [
          varRow('CLAUDE_CONFIG_DIR', CFG, CFG, null, 'harness'),
          varRow('WORKSPACE_ROOT', 'D:/ws', 'D:/ws', null, 'harness'),
          varRow('ENGRAM_DATA_DIR', 'D:/ws/config/.engram', 'D:/ws/config/.engram', null, 'harness'),
          varRow('CC_DISABLED_HOOKS', null, null, null, 'harness'),
          varRow('CLAUDE_CODE_USE_POWERSHELL_TOOL', '1', '1', null, 'harness'),
          varRow('CLAUDE_CODE_GIT_BASH_PATH', null, null, 'C:/Program Files/Git/bin/bash.exe', 'harness'),
          varRow('OTEL_EXPORTER_OTLP_HEADERS', '[redacted]', null, null, 'harness', ['redacted']),
          varRow('NO_PROXY', 'localhost,127.0.0.1,.example', null, null, 'harness'),
          varRow('HTTPS_PROXY', null, null, null, 'harness'),
          varRow('ANTHROPIC_API_KEY', null, null, null, 'presence only'),
          varRow('ANTHROPIC_AUTH_TOKEN', '[set]', null, null, 'presence only'),
          varRow('CLAUDE_CODE_OAUTH_TOKEN', '[set]', '[set]', null, 'presence only'),
          varRow('AWS_BEARER_TOKEN_BEDROCK', null, null, null, 'presence only'),
        ] },
    ],
  };

  const taskRow = (nm, state, lastRun, lastResult, script, purpose) => {
    const id = add(`${REPO}/scripts/${script}`, 'other', ps1(purpose));
    return { id: nm, cells: { name: nm, state, lastRun, lastResult, action: link(`pwsh -File ${REPO}/scripts/${script}`, id) }, fileId: id };
  };
  const jobs = {
    collectorId: 'jobs.windows-tasks', view: 'jobs', title: 'Windows scheduled tasks',
    blocks: [{ type: 'table', id: 'tasks', title: 'Scheduled tasks',
      columns: [{ key: 'name', label: 'Task' }, { key: 'state', label: 'State' }, { key: 'lastRun', label: 'Last run' }, { key: 'lastResult', label: 'Last result' }, { key: 'action', label: 'Action' }],
      rows: [
        taskRow('cfg-backup', 'Ready', '2026-10-27T03:00:00Z', '0x0 (success)', 'backup-config.ps1', 'Backs up the config repo to the archive drive.'),
        taskRow('engram-sync', 'Running', '2026-10-27T09:55:00Z', '0x41301 (task is currently running)', 'engram-sync.ps1', 'Syncs memory exports.'),
        taskRow('nightly-lint', 'Disabled', '2026-10-20T02:00:00Z', '0x1 (incorrect function)', 'lint-nightly.ps1', 'Lints hook scripts every night.'),
        taskRow('prune-worktrees', 'Ready', null, '0x41303 (task has not yet run)', 'prune-worktrees.ps1', 'Removes merged worktrees.'),
      ] }],
  };

  const usage = {
    collectorId: 'usage.otel', view: 'usage', title: 'OpenTelemetry usage',
    blocks: [
      { type: 'kv', id: 'exporter', title: 'Exporter', rows: [
        kv('exporter', 'otlp', 'env:OTEL_METRICS_EXPORTER'),
        kv('endpoint', 'http://collector.example:4318', 'user', U),
        kv('protocol', 'http/protobuf', 'default:otlp'),
        kv('headers', '[redacted]', 'user', U, { redacted: true, flags: ['redacted'] }),
        kv('lastExport', '2026-10-27T09:58:00Z', 'usage-export.jsonl', BIG),
      ] },
      { type: 'chart', id: 'tokens-per-day', title: 'Tokens per day', description: 'Last 7 days',
        series: [{ name: 'input', points: [['2026-10-21', 120000], ['2026-10-22', 98000], ['2026-10-23', 143000], ['2026-10-24', 61000], ['2026-10-25', 12000], ['2026-10-26', 154000], ['2026-10-27', 40000]] }] },
    ],
  };

  const collector = (id, title, view, requirements, durationMs, extra = {}) => ({ id, title, version: '1.0.0', view, state: 'active', requirements, durationMs, ...extra });
  const timedOut = dir === TIMEOUT_DIR;
  snap.collectors = [
    ex['overview.machine'],
    collector('overview.config-repo', 'Config repo', 'overview', { tools: ['git'], paths: ['configRepo'], targetDir: false }, 64),
    ex['settings.effective'],
    timedOut ? { ...ex['instructions.chain'], state: 'unavailable', reason: 'Timed out after 20 s', durationMs: 20000 } : ex['instructions.chain'],
    collector('agents.definitions', 'Agent definitions', 'agents', { paths: ['claudeConfigDir'], targetDir: true }, 18, rich ? { notes: ['1 agent overridden by a higher scope'] } : {}),
    ex['agents.company-core'],
    collector('skills.catalog', 'Skills and commands', 'skills', { paths: ['claudeConfigDir'], targetDir: true }, 15),
    ex['hooks.registered'],
    collector('mcp.servers', 'MCP servers', 'mcp-permissions', { targetDir: true }, 7, { notes: ['${VAR} references are shown as written, never expanded.'] }),
    collector('settings.highlights', 'Permissions and highlights', 'mcp-permissions', { paths: ['claudeConfigDir'], targetDir: true }, 6),
    collector('env.harness', 'Environment', 'env', { targetDir: false }, 3),
    collector('jobs.windows-tasks', 'Windows scheduled tasks', 'jobs', { os: ['windows'], tools: ['schtasks'], targetDir: false }, 410, { notes: ['Only tasks whose action references the workspace or config repo are listed.'] }),
    ex['jobs.systemd-user'],
    collector('jobs.crontab', 'crontab', 'jobs', { os: ['linux', 'macos'], tools: ['crontab'], targetDir: false }, 0, { state: 'notPresent', reason: 'Linux and macOS only' }),
    { id: 'usage.otel', title: 'OpenTelemetry usage', version: '0.3.0', view: 'usage', state: 'active', requirements: { targetDir: false }, durationMs: 44 },
  ];
  snap.sections = [machine, configRepo, settings, ...(timedOut ? [] : [instructions]), agents, skills, hooks, mcp, highlights, env, jobs, usage];

  const referenced = new Set();
  JSON.stringify(snap.sections, (k, v) => { if (k === 'fileId') referenced.add(v); return v; });
  for (const id of [...files.keys()]) if (!referenced.has(id)) files.delete(id);
  snap.files = [...files.values()].map((f) => f.ref);
  return { snapshot: snap, files };
}
