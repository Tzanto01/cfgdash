import { act, fireEvent } from '@testing-library/react';
import type { EditScope, JsonValue, Proposal, ProposalFile, SettingRow, SettingsResponse } from '../lib/models';
import { makeSnapshot } from '../test/fixtures';
import { startApp, type AppHarness, type RequestRecord } from '../test/harness';
import { answerWhenSent } from '../test/lazy';

const DIR = 'D:/ws/app';
const EDIT_URL = '/edit?dir=D%3A%2Fws%2Fapp';
const MINUS = '\u2212';

interface RowOptions {
  scopes?: EditScope[];
  options?: string[];
  editable?: boolean;
  reason?: string;
  effective?: JsonValue | null;
}

function row(key: string, label: string, type: string, values: Partial<Record<EditScope, JsonValue | null>>, extra: RowOptions = {}): SettingRow {
  const scopes = extra.scopes ?? ['user', 'project', 'local'];
  const first = scopes.find((s) => values[s] !== undefined && values[s] !== null);
  return {
    key,
    label,
    description: `Describes ${label}.`,
    type,
    scopes,
    values,
    effective: extra.effective !== undefined ? extra.effective : first ? (values[first] ?? null) : null,
    effectiveSource: first ?? null,
    editable: extra.editable ?? true,
    ...(extra.options ? { options: extra.options } : {}),
    ...(extra.reason ? { reason: extra.reason } : {})
  };
}

function makeSettings(model = 'sonnet'): SettingsResponse {
  return {
    categories: [
      {
        id: 'general',
        label: 'General',
        settings: [
          row('model', 'Model', 'string', { user: model }),
          row('alwaysThinkingEnabled', 'Always think', 'bool', { user: true })
        ]
      },
      {
        id: 'permissions',
        label: 'Permissions',
        settings: [
          row('permissions.defaultMode', 'Default permission mode', 'enum', { user: 'default' }, { options: ['default', 'acceptEdits', 'plan'] }),
          row('permissions.allow', 'Allow rules', 'stringList', { user: ['Bash(ls)'] })
        ]
      },
      {
        id: 'env',
        label: 'Environment',
        settings: [
          row('env.EXAMPLE_API_TOKEN', 'EXAMPLE_API_TOKEN', 'string', { user: '[redacted]' }, { editable: false, reason: 'secret' }),
          row('env.EXAMPLE_HOOK', 'EXAMPLE_HOOK', 'string', { user: 'echo hi' }, { editable: false, reason: 'unsafe-env' }),
          row('env.EXAMPLE_PLAIN', 'EXAMPLE_PLAIN', 'string', { user: '[redacted]' })
        ]
      },
      {
        id: 'config',
        label: 'Config repo',
        settings: [row('configAutoPull', 'Config auto-pull', 'bool', { machine: true }, { scopes: ['machine'] })]
      },
      {
        id: 'other',
        label: 'Other',
        settings: [row('customTheme', 'customTheme', 'string', { user: 'dusk' }, { editable: false, reason: 'not-in-catalog' })]
      }
    ],
    files: [
      { scope: 'user', path: 'C:/Users/user/.claude/settings.json', exists: true, gitTracked: false },
      { scope: 'project', path: 'D:/ws/app/.claude/settings.json', exists: true, gitTracked: true },
      { scope: 'local', path: 'D:/ws/app/.claude/settings.local.json', exists: false, gitTracked: false },
      { scope: 'machine', path: 'D:/ws/config/.claude/machine.json', exists: true, gitTracked: false }
    ]
  };
}

const userDiff = [
  '--- a/C:/Users/user/.claude/settings.json',
  '+++ b/C:/Users/user/.claude/settings.json',
  '@@ -1,4 +1,4 @@',
  ' {',
  '-  "model": "sonnet",',
  '+  "model": "opus",',
  '   "alwaysThinkingEnabled": true',
  ' }',
  ''
].join('\n');

const newFileDiff = [
  '--- /dev/null',
  '+++ b/D:/ws/app/.claude/settings.json',
  '@@ -0,0 +1,3 @@',
  '+{',
  '+  "model": "opus"',
  '+}',
  ''
].join('\n');

const reformatDiff = [
  '# cfgdash: file will be reformatted',
  '--- a/D:/ws/app/.claude/settings.local.json',
  '+++ b/D:/ws/app/.claude/settings.local.json',
  '@@ -1,1 +1,3 @@',
  '-{"model":"x"}',
  '+{',
  '+  "model": "opus"',
  '+}',
  ''
].join('\n');

function makeProposal(files: ProposalFile[]): Proposal {
  return { id: 'p1', expiresAt: '2030-01-01T00:00:00.000Z', files, confirm: 'confirm-token-1' };
}

const userFile = (): ProposalFile => ({ path: 'C:/Users/user/.claude/settings.json', scope: 'user', sha256Before: 'a'.repeat(64), diff: userDiff });

async function openEditor(settings = makeSettings(), url = EDIT_URL): Promise<AppHarness> {
  const h = await startApp(url, makeSnapshot({ targetDir: 'D:/ws/resolved' }));
  await answerWhenSent(h, '/api/v2/settings', settings);
  return h;
}

function field(label: string): HTMLElement {
  const span = [...document.querySelectorAll('span[id$="-label"]')].find((s) => s.textContent === label);
  const el = span && document.querySelector<HTMLElement>(`[aria-labelledby="${span.id}"]`);
  if (!el) {
    throw new Error(`No control for ${label}`);
  }
  return el;
}

const controls = () => [...document.querySelectorAll('input[aria-labelledby], textarea[aria-labelledby], select[aria-labelledby], button[role="switch"]')];
const radios = () => [...document.querySelectorAll('[role="radio"]')].map((r) => r.textContent);
const pills = () => [...document.querySelectorAll('span')].filter((s) => s.textContent === 'Changed');
const alertText = () => (document.querySelector('[role="alert"]')?.textContent ?? '').replace(/\s+/g, ' ');
const posts = (h: AppHarness) => h.requests.filter((r) => r.method === 'POST');
const body = (r: RequestRecord) => JSON.parse(r.body ?? 'null') as unknown;
const staged = (h: AppHarness) => /\d+ changes? staged/.exec(h.text())?.[0] ?? null;

async function type(h: AppHarness, el: HTMLElement, value: string): Promise<void> {
  await act(async () => {
    fireEvent.change(el, { target: { value } });
  });
  await h.settle();
}

async function toReview(h: AppHarness, files: ProposalFile[] = [userFile()]): Promise<void> {
  await type(h, field('Model'), 'opus');
  await h.click(h.button('Review changes'));
  await h.answer('/api/v2/proposals', makeProposal(files));
}

describe('Settings header link', () => {
  const links = () => [...document.querySelectorAll<HTMLAnchorElement>('a[href^="/edit"]')];

  it('links to /edit with the folder once health says edits are on', async () => {
    const h = await startApp('/overview?dir=D%3A%2Fws%2Fapp');
    expect(links()).toHaveLength(0);

    await h.answer('/api/v1/health', { status: 'ok', version: '1.0.0', schemaVersion: 1, edits: true });

    expect(links().length).toBeGreaterThan(0);
    expect(links().map((a) => a.getAttribute('href'))).toEqual(links().map(() => EDIT_URL));
    expect(links().some((a) => a.textContent === 'Settings' && a.getAttribute('aria-label') === 'Edit settings')).toBe(true);
  });

  it.each([
    ['edits is false', { status: 'ok', version: '1.0.0', schemaVersion: 1, edits: false }, 200],
    ['edits is missing', { status: 'ok', version: '1.0.0', schemaVersion: 1 }, 200],
    ['health fails', { error: 'internal' }, 500]
  ])('has no link when %s', async (_name, health, status) => {
    const h = await startApp('/overview?dir=D%3A%2Fws%2Fapp');

    await h.answer('/api/v1/health', health, status);

    expect(links()).toHaveLength(0);
  });
});

describe('Settings editor', () => {
  describe('loading and navigation', () => {
    it('reads /api/v2/settings for the dir and shows the nav, the first category and the User, Project and Local scopes', async () => {
      const h = await startApp(EDIT_URL, makeSnapshot({ targetDir: 'D:/ws/resolved' }));
      const request = await answerWhenSent(h, '/api/v2/settings', makeSettings());

      expect(request.url.searchParams.get('dir')).toBe(DIR);
      expect(h.el.querySelector('main h1')?.textContent).toBe('Edit settings');
      const nav = [...document.querySelectorAll('nav[aria-label="Setting categories"] button')].map((b) => b.textContent);
      expect(nav).toEqual(['General', 'Permissions', 'Environment', 'Config repo', 'Other']);
      expect(h.text()).toContain('Always think');
      expect(h.text()).not.toContain('Allow rules');
      expect(radios()).toEqual(['User', 'Project', 'Local']);
    });

    it('shows only Machine for the Config repo category', async () => {
      const h = await openEditor();

      await h.click(h.button('Config repo'));

      expect(radios()).toEqual(['Machine']);
      expect(h.text()).toContain('Writes to D:/ws/config/.claude/machine.json');
    });

    it('names the file of the selected scope', async () => {
      const h = await openEditor();
      expect(h.text()).toContain('Writes to C:/Users/user/.claude/settings.json');

      await h.click(document.querySelectorAll<HTMLElement>('[role="radio"]')[1]);
      expect(h.text()).toContain('Writes to D:/ws/app/.claude/settings.json');
      expect(h.text()).toContain('Tracked by git');

      await h.click(document.querySelectorAll<HTMLElement>('[role="radio"]')[2]);
      expect(h.text()).toContain('Writes to D:/ws/app/.claude/settings.local.json (not created yet)');
      expect(h.text()).not.toContain('Tracked by git');
    });

    it('searches across categories and says when nothing matches', async () => {
      const h = await openEditor();
      const search = document.querySelector<HTMLElement>('input[aria-label="Search settings"]')!;

      await type(h, search, 'allow');
      expect(h.text()).toContain('Allow rules');
      expect(h.text()).not.toContain('Always think');

      await type(h, search, 'zzz-no-such-setting');
      expect(h.text()).toContain('No settings match');
    });
  });

  describe('read-only rows', () => {
    it('renders no control for secret, unsafe-env and not-in-catalog rows and says why', async () => {
      const h = await openEditor();

      await h.click(h.button('Environment'));
      expect(controls()).toHaveLength(0);
      expect(h.text()).toContain('Secret: shown redacted, never editable');
      expect(h.text()).toContain('Can run code when set: not editable here');

      await h.click(h.button('Other'));
      expect(controls()).toHaveLength(0);
      expect(h.text()).toContain('Not in the settings catalog: read-only');
    });

    it('shows a redacted value as the marker and never puts it into an input', async () => {
      const h = await openEditor();

      await h.click(h.button('Environment'));

      expect(h.text()).toContain('[redacted]');
      expect(h.text()).toContain('value redacted');
      const values = [...document.querySelectorAll<HTMLInputElement>('input, textarea, select')].map((e) => e.value);
      expect(values.some((v) => v.includes('[redacted]'))).toBe(false);
    });

  });

  describe('staging', () => {
    const toggle = (h: AppHarness, el: HTMLElement) => h.click(el);
    const cases: [string, string, string, (h: AppHarness) => Promise<void>, (h: AppHarness) => Promise<void>][] = [
      ['text', 'General', 'Model', (h) => type(h, field('Model'), 'opus'), (h) => type(h, field('Model'), 'sonnet')],
      ['bool', 'General', 'Always think', (h) => toggle(h, field('Always think')), (h) => toggle(h, field('Always think'))],
      [
        'enum',
        'Permissions',
        'Default permission mode',
        (h) => type(h, field('Default permission mode'), 'plan'),
        (h) => type(h, field('Default permission mode'), 'default')
      ],
      [
        'stringList',
        'Permissions',
        'Allow rules',
        (h) => type(h, field('Allow rules'), 'Bash(ls)\nRead(D:/ws/**)'),
        (h) => type(h, field('Allow rules'), 'Bash(ls)')
      ]
    ];

    it.each(cases)('stages a %s change and drops it when set back to the original', async (_type, category, _label, change, revert) => {
      const h = await openEditor();
      await h.click(h.button(category));

      await change(h);
      expect(staged(h)).toBe('1 change staged');
      expect(pills()).toHaveLength(1);

      await revert(h);
      expect(staged(h)).toBeNull();
      expect(pills()).toHaveLength(0);
    });

    it('counts changes across categories', async () => {
      const h = await openEditor();
      await type(h, field('Model'), 'opus');
      await h.click(h.button('Permissions'));
      await type(h, field('Default permission mode'), 'plan');

      expect(staged(h)).toBe('2 changes staged');
    });

    it('stages an unset with Restore default and unstages it on a second press', async () => {
      const h = await openEditor();

      await h.click(h.button('Restore default for Model'));
      expect(staged(h)).toBe('1 change staged');
      expect(pills()).toHaveLength(1);

      await h.click(h.button('Restore default for Model'));
      expect(staged(h)).toBeNull();
    });

    it('clears every staged change on Discard', async () => {
      const h = await openEditor();
      await type(h, field('Model'), 'opus');
      await h.click(h.button('Restore default for Always think'));
      expect(staged(h)).toBe('2 changes staged');

      await h.click(h.button('Discard'));

      expect(staged(h)).toBeNull();
      expect(pills()).toHaveLength(0);
      expect((field('Model') as HTMLInputElement).value).toBe('sonnet');
    });
  });

  describe('review', () => {
    it('sends one guarded POST with the snapshot folder and the staged changes', async () => {
      const h = await openEditor();
      await type(h, field('Model'), 'opus');
      await h.click(h.button('Restore default for Always think'));

      await h.click(h.button('Review changes'));

      const sent = posts(h);
      expect(sent).toHaveLength(1);
      expect(sent[0].url.pathname).toBe('/api/v2/proposals');
      expect(sent[0].headers['x-cfgdash-request']).toBe('1');
      expect(sent[0].headers['content-type']).toBe('application/json');
      expect(body(sent[0])).toEqual({
        dir: 'D:/ws/resolved',
        changes: [
          { key: 'model', scope: 'user', value: 'opus' },
          { key: 'alwaysThinkingEnabled', scope: 'user', unset: true }
        ]
      });
    });

    it('drops a review whose staged changes were edited while it was pending, so Apply never covers a stale set', async () => {
      const h = await openEditor();
      await type(h, field('Model'), 'opus');
      await h.click(h.button('Review changes'));

      await h.click(h.button('Restore default for Always think'));
      await h.answer('/api/v2/proposals', makeProposal([userFile()]));

      expect(h.buttons(/^Apply/)).toHaveLength(0);
      expect(staged(h)).toBe('2 changes staged');
    });

    it('shows a diff card per file with path, scope, dry run and counts', async () => {
      const h = await openEditor();

      await toReview(h);

      expect(h.text()).toContain('Review changes');
      const card = document.querySelector('section[aria-label="Changes to C:/Users/user/.claude/settings.json"]')!;
      const text = (card.textContent ?? '').replace(/\s+/g, ' ');
      expect(text).toContain('User');
      expect(text).toContain('dry run');
      expect(text).toContain(`+1 ${MINUS}1`);
      expect(text).toContain('"model": "sonnet"');
      expect(text).toContain('"model": "opus"');
      expect(text).not.toContain('new file');
      expect(text).not.toContain('tracked by git');
      expect(text).not.toContain('will reformat');
    });

    it('flags a new file, a git-tracked file and a reformatted file', async () => {
      const h = await openEditor();

      await toReview(h, [
        userFile(),
        { path: 'D:/ws/app/.claude/settings.json', scope: 'project', sha256Before: null, diff: newFileDiff },
        { path: 'D:/ws/app/.claude/settings.local.json', scope: 'local', sha256Before: 'b'.repeat(64), diff: reformatDiff }
      ]);

      const card = (path: string) => (document.querySelector(`section[aria-label="Changes to ${path}"]`)?.textContent ?? '').replace(/\s+/g, ' ');
      const project = card('D:/ws/app/.claude/settings.json');
      expect(project).toContain('Project');
      expect(project).toContain('new file');
      expect(project).toContain('tracked by git');
      expect(project).toContain(`+3 ${MINUS}0`);
      const local = card('D:/ws/app/.claude/settings.local.json');
      expect(local).toContain('Local');
      expect(local).toContain('will reformat this file');
      expect(local).not.toContain('new file');
      expect(local).not.toContain('tracked by git');
      expect(card('C:/Users/user/.claude/settings.json')).not.toContain('tracked by git');
    });

    it('shows the reason on the row and a summary alert on invalid-change, keeping the staged changes', async () => {
      const h = await openEditor();
      await type(h, field('Model'), 'opus');
      await h.click(h.button('Review changes'));

      await h.answer('/api/v2/proposals', { error: 'invalid-change', key: 'model', reason: 'bad-type' }, 400);

      expect(alertText()).toContain("Some changes can't be reviewed.");
      expect(alertText()).toContain('Model: Wrong type.');
      expect([...document.querySelectorAll('p')].some((p) => p.textContent === 'Wrong type')).toBe(true);
      expect(staged(h)).toBe('1 change staged');
      expect(h.text()).not.toContain('Dry run: nothing is written');
    });
  });

  describe('apply', () => {
    it('sends the confirm, then reports the audit entry, clears the staged changes, reloads settings and refreshes the snapshot', async () => {
      const h = await openEditor();
      await toReview(h);

      await h.click(h.button('Apply 1 change'));
      const request = posts(h)[1];
      expect(request.url.pathname).toBe('/api/v2/proposals/p1/apply');
      expect(request.headers['x-cfgdash-request']).toBe('1');
      expect(request.headers['content-type']).toBe('application/json');
      expect(body(request)).toEqual({ confirm: 'confirm-token-1' });

      await h.answer('/api/v2/proposals/p1/apply', { applied: true, auditId: '20300101T000000Z-ab12cd34' });

      expect(h.text()).toContain('Applied. Audit entry 20300101T000000Z-ab12cd34.');
      expect(staged(h)).toBeNull();
      expect(
        h.requests.some((r) => r.url.pathname === '/api/v1/snapshot' && r.url.searchParams.get('refresh') === 'true')
      ).toBe(true);
      await h.answer('/api/v2/settings', makeSettings('opus'));
      expect((field('Model') as HTMLInputElement).value).toBe('opus');
    });

    it.each([
      ['409 stale', 409, { error: 'stale' }, 'The files changed since the review'],
      ['400 confirm-mismatch', 400, { error: 'confirm-mismatch' }, 'The review no longer matches'],
      ['404 expired', 404, { error: 'not-found' }, 'The review expired'],
      ['403 forbidden', 403, { error: 'forbidden' }, 'cfgdash refused the change'],
      [
        '500 write-failed with files written',
        500,
        { error: 'write-failed', written: ['C:/Users/user/.claude/settings.json'], auditId: 'audit-9' },
        'Some files were written; the audit entry audit-9 records it.'
      ],
      ['500 write-failed with nothing written', 500, { error: 'write-failed', written: [] }, 'Nothing was written.'],
      ['500 other', 500, { error: 'internal' }, "Couldn't apply the changes."]
    ])('shows an alert and keeps the staged changes on %s', async (_name, status, response, words) => {
      const h = await openEditor();
      await toReview(h);
      await h.click(h.button('Apply 1 change'));

      await h.answer('/api/v2/proposals/p1/apply', response, status);

      expect(alertText()).toContain(words);
      expect(staged(h)).toBe('1 change staged');
      expect(h.text()).not.toContain('Applied.');
      expect(h.el.querySelector('main h1')?.textContent).toBe('Edit settings');
      expect(h.text()).not.toContain("This device or account isn't allowed");
    });
  });

  describe('signed out', () => {
    it('shows the access panel on 401 while reviewing', async () => {
      const h = await openEditor();
      await type(h, field('Model'), 'opus');
      await h.click(h.button('Review changes'));

      await h.answer('/api/v2/proposals', { error: 'unauthenticated' }, 401);

      expect(h.el.querySelector('h1')?.textContent).toContain('Open the link printed in your terminal');
    });

    it('shows the access panel on 401 while applying', async () => {
      const h = await openEditor();
      await toReview(h);
      await h.click(h.button('Apply 1 change'));

      await h.answer('/api/v2/proposals/p1/apply', { error: 'unauthenticated' }, 401);

      expect(h.el.querySelector('h1')?.textContent).toContain('Open the link printed in your terminal');
    });
  });

  describe('folder change', () => {
    it('drops the staged changes when dir changes', async () => {
      const h = await openEditor();
      await type(h, field('Model'), 'opus');
      expect(staged(h)).toBe('1 change staged');

      await h.navigate('/edit?dir=D%3A%2Fws%2Fother');
      await h.answer('/api/v1/snapshot', makeSnapshot({ targetDir: 'D:/ws/other' }));
      const request = await answerWhenSent(h, '/api/v2/settings', makeSettings());

      expect(request.url.searchParams.get('dir')).toBe('D:/ws/other');
      expect(staged(h)).toBeNull();
      expect(pills()).toHaveLength(0);
      expect((field('Model') as HTMLInputElement).value).toBe('sonnet');
    });
  });
});
