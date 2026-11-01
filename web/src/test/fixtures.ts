import type { Block, CollectorStatus, FileRef, Section, Snapshot, TableRow } from '../lib/models';

export const FILE_USER = '0f1e2d3c4b5a6978';
export const FILE_LOCAL = 'a1b2c3d4e5f60718';
export const FILE_HOOK = '1829304152637485';
export const FILE_CLAUDE = 'd4e5f60718293041';

function collector(id: string, view: string, state: CollectorStatus['state'], reason?: string, targetDir = false): CollectorStatus {
  return { id, title: id, version: '1.0.0', view, state, reason, requirements: { targetDir }, durationMs: 1 };
}

function file(id: string, path: string, kind: string): FileRef {
  return { id, path, kind, sizeBytes: 100, tokens: 25, company: false };
}

const manyRows: TableRow[] = Array.from({ length: 55 }, (_, i) => ({
  id: `r${i}`,
  cells: { name: `item-${String(i).padStart(2, '0')}` }
}));

const sections: Section[] = [
  {
    collectorId: 'settings.effective',
    view: 'settings',
    title: 'Effective settings',
    blocks: [
      {
        type: 'kv',
        id: 'effective',
        title: 'Effective settings',
        rows: [
          {
            key: 'model',
            value: 'opus',
            source: { label: 'project (local)', fileId: FILE_LOCAL },
            contributions: [
              { value: 'opus', source: { label: 'project (local)', fileId: FILE_LOCAL }, status: 'applied' },
              { value: 'sonnet', source: { label: 'user', fileId: FILE_USER }, status: 'overridden' }
            ]
          },
          {
            key: 'permissions.allow',
            value: ['Bash(npm test *)'],
            source: { label: 'merged' },
            contributions: [
              { value: ['Bash(npm test *)'], source: { label: 'project' }, status: 'merged' },
              { value: ['Read(./docs/**)'], source: { label: 'user' }, status: 'merged' }
            ]
          },
          {
            key: 'env.TOKEN_HEADER',
            value: '[redacted]',
            redacted: true,
            flags: ['redacted'],
            source: { label: 'project (local)' },
            contributions: [
              { value: 'secret-value-new', source: { label: 'project (local)' }, status: 'applied' },
              { value: 'secret-value-old', source: { label: 'user' }, status: 'overridden' }
            ]
          },
          { key: 'theme', value: 'dark', flags: ['company', 'brand-new-flag'] }
        ]
      }
    ]
  },
  {
    collectorId: 'instructions.chain',
    view: 'instructions',
    title: 'Instruction chain',
    blocks: [
      {
        type: 'tree',
        id: 'chain',
        title: 'Instruction chain',
        summary: { files: 4, sizeBytes: 18400, tokens: 4600 },
        nodes: [
          {
            id: 'n2',
            label: 'project/CLAUDE.md',
            fileId: FILE_CLAUDE,
            badges: ['project'],
            children: [
              { id: 'n2.1', label: '@AGENTS.md', badges: ['import'], children: [] },
              { id: 'n2.2', label: 'docs/override.md', badges: ['referenced'], children: [] },
              { id: 'n2.3', label: '@docs/missing.md', badges: ['error'], meta: { error: 'file is missing' }, children: [] },
              {
                id: 'n2.4',
                label: 'docs/index.md',
                children: [{ id: 'n2.4.1', label: 'deep/nested.md', children: [] }]
              }
            ]
          },
          { id: 'n4', label: 'web/CLAUDE.md', badges: ['lazy'], children: [] }
        ]
      }
    ]
  },
  {
    collectorId: 'hooks.registered',
    view: 'hooks',
    title: 'Hooks',
    blocks: [
      {
        type: 'table',
        id: 'hooks',
        title: 'Registered hooks',
        columns: [
          { key: 'script', label: 'Script' },
          { key: 'source', label: 'Registered in' }
        ],
        rows: [
          {
            id: 'h-charlie',
            cells: {
              script: { text: 'charlie.ps1', fileId: FILE_HOOK },
              source: { text: 'user settings', fileId: FILE_HOOK }
            },
            fileId: FILE_HOOK
          },
          { id: 'h-alpha', cells: { script: 'alpha.ps1', source: 'project settings' }, detailId: 'hooks.registered:alpha' },
          { id: 'h-bravo', cells: { script: 'bravo.ps1', source: 'user settings' } }
        ]
      },
      { type: 'table', id: 'many', title: 'Many rows', columns: [{ key: 'name', label: 'Name' }], rows: manyRows }
    ]
  },
  {
    collectorId: 'zeta.things',
    view: 'zeta',
    title: 'Zeta things',
    blocks: [{ type: 'chart', id: 'c1', title: 'A chart' } as Block]
  },
  {
    collectorId: 'overview.machine',
    view: 'overview',
    title: 'Machine',
    blocks: [{ type: 'kv', id: 'machine', title: 'Machine', rows: [{ key: 'hostname', value: 'devbox' }] }]
  }
];

/** A synthetic snapshot; views listed in the contract order differ from the order of the collectors on purpose. */
export function makeSnapshot(overrides: { hostname?: string; targetDir?: string } = {}): Snapshot {
  return {
    schemaVersion: 1,
    machine: {
      hostname: overrides.hostname ?? 'devbox',
      os: { family: 'windows', description: 'Test OS 1.0', arch: 'x64' },
      collectedAt: '2026-10-27T10:00:00Z',
      cfgdashVersion: '0.1.0'
    },
    context: { targetDir: overrides.targetDir ?? 'D:/ws/app', paths: [] },
    collectors: [
      collector('jobs.systemd-user', 'jobs', 'notPresent', 'Linux only'),
      collector('hooks.registered', 'hooks', 'active'),
      collector('agents.company-core', 'agents', 'unavailable', 'pwsh not installed'),
      collector('zeta.things', 'zeta', 'active'),
      collector('instructions.chain', 'instructions', 'active'),
      collector('settings.effective', 'settings', 'active', undefined, true),
      collector('overview.machine', 'overview', 'active')
    ],
    sections,
    files: [
      file(FILE_USER, 'C:/Users/user/.claude/settings.json', 'settings'),
      file(FILE_LOCAL, 'D:/ws/app/.claude/settings.local.json', 'settings'),
      file(FILE_HOOK, 'D:/ws/config/hooks/charlie.ps1', 'hookScript'),
      file(FILE_CLAUDE, 'D:/ws/app/CLAUDE.md', 'instruction')
    ]
  };
}
