import { act, fireEvent, screen } from '@testing-library/react';
import type { CfgdashSettings } from '../lib/models';
import { startApp, type AppHarness, type RequestRecord } from '../test/harness';
import { answerWhenSent } from '../test/lazy';

const HEALTH_EDITS = { status: 'ok', version: '1.0.0', schemaVersion: 1, edits: true };

function makeSettings(extra: Partial<CfgdashSettings> = {}): CfgdashSettings {
  return {
    values: {
      startAtLogin: false,
      theme: 'system',
      defaultDir: null,
      tailnet: false,
      fleet: { enabled: false },
      assistant: { model: 'sonnet', effort: 'medium', maxBudgetUsd: 2 }
    },
    startAtLogin: { configured: false, actual: 'off' },
    paths: { config: '/home/user/.config/cfgdash/config.json', overrides: '/home/user/.config/cfgdash/overrides.json' },
    ...extra
  };
}

async function openGear(settings: CfgdashSettings = makeSettings()): Promise<AppHarness> {
  const h = await startApp('/cfgdash');
  await answerWhenSent(h, '/api/v2/cfgdash-settings', settings);
  return h;
}

function control(label: string): HTMLElement {
  const span = [...document.querySelectorAll('main span[id$="-label"]')].find((s) => s.textContent === label);
  const el = span && document.querySelector<HTMLElement>(`main [aria-labelledby~="${span.id}"]`);
  if (!el) {
    throw new Error(`No control for ${label}`);
  }
  return el;
}

const body = (r: RequestRecord) => JSON.parse(r.body ?? 'null') as unknown;
const alerts = () => [...document.querySelectorAll('main [role="alert"]')].map((a) => a.textContent);

describe('cfgdash settings (gear)', () => {
  it('shows every key, the read-only paths and a start-at-login mismatch between configured and actual', async () => {
    const h = await openGear(makeSettings({ startAtLogin: { configured: true, actual: 'off' } }));

    expect(h.el.querySelector('main h1')?.textContent).toBe('cfgdash settings');
    for (const label of ['Start at login', 'Default folder', 'Publish on the tailnet', 'Theme', 'Answer fleet peers', 'Model and effort', 'Budget per session (USD)']) {
      expect(() => control(label)).not.toThrow();
    }
    expect(h.text()).toContain('Configured: On · Actual: Off');
    expect(h.text()).toContain('Mismatch');
    expect(h.text()).toContain('/home/user/.config/cfgdash/config.json');
    expect([...document.querySelectorAll('main input')].some((i) => (i as HTMLInputElement).value.includes('config.json'))).toBe(false);
  });

  it('shows no mismatch when configured and actual agree', async () => {
    const h = await openGear();

    expect(h.text()).toContain('Configured: Off · Actual: Off');
    expect(h.text()).not.toContain('Mismatch');
  });

  it('saves a switch as a guarded PUT of only that key and shows the answer', async () => {
    const h = await openGear();

    await h.click(control('Answer fleet peers'));
    const put = await h.answer('/api/v2/cfgdash-settings', makeSettings({ values: { ...makeSettings().values, fleet: { enabled: true } } }));

    expect(put.method).toBe('PUT');
    expect(put.headers['x-cfgdash-request']).toBe('1');
    expect(body(put)).toEqual({ fleet: { enabled: true } });
    expect(control('Answer fleet peers').getAttribute('aria-checked')).toBe('true');
  });

  it('shows autostart-failed inline with the reason and keeps the switch off', async () => {
    const h = await openGear();

    await h.click(control('Start at login'));
    await h.answer('/api/v2/cfgdash-settings', { error: 'autostart-failed', reason: 'no user systemd' }, 500);

    expect(alerts()).toEqual(["Couldn't change start at login: no user systemd. Nothing was saved."]);
    expect(control('Start at login').getAttribute('aria-checked')).toBe('false');
  });

  it('shows configError on load and config-unreadable inline on a PUT', async () => {
    const h = await openGear(makeSettings({ configError: 'config.json is not valid JSON' }));
    expect(h.text()).toContain("config.json can't be read: config.json is not valid JSON");

    await h.click(control('Publish on the tailnet'));
    await h.answer('/api/v2/cfgdash-settings', { error: 'config-unreadable' }, 409);

    expect(alerts()).toEqual(["config.json can't be read, so nothing was saved. Fix or remove the file first."]);
  });

  it('saves the default folder from the text field and explains a folder the server rejects', async () => {
    const h = await openGear();

    await act(async () => {
      fireEvent.change(control('Default folder'), { target: { value: '/home/user/missing' } });
    });
    await h.click(control('Default folder').closest('form')!.querySelector('button')!);
    const put = await h.answer('/api/v2/cfgdash-settings', { error: 'invalid-setting', key: 'defaultDir', reason: 'bad-value' }, 400);

    expect(body(put)).toEqual({ defaultDir: '/home/user/missing' });
    expect(alerts()).toEqual(['Not an existing absolute folder.']);
  });

  it('applies and stores a theme saved from the screen', async () => {
    const h = await openGear();

    await act(async () => {
      fireEvent.change(control('Theme'), { target: { value: 'dark' } });
    });
    const put = await h.answer('/api/v2/cfgdash-settings', makeSettings({ values: { ...makeSettings().values, theme: 'dark' } }));

    expect(body(put)).toEqual({ theme: 'dark' });
    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem('cfgdash-theme')).toBe('dark');
  });
});

describe('theme from config.json', () => {
  it('replaces the stored browser theme with the server value after load', async () => {
    localStorage.setItem('cfgdash-theme', 'light');
    const h = await startApp('/overview');
    expect(document.documentElement.getAttribute('data-theme')).toBe('light');

    await h.answer('/api/v1/health', HEALTH_EDITS);
    await h.answer('/api/v2/cfgdash-settings', makeSettings({ values: { ...makeSettings().values, theme: 'dark' } }));

    expect(document.documentElement.getAttribute('data-theme')).toBe('dark');
    expect(localStorage.getItem('cfgdash-theme')).toBe('dark');
  });

  it('keeps the stored theme when config.json is unreadable', async () => {
    localStorage.setItem('cfgdash-theme', 'light');
    const h = await startApp('/overview');

    await h.answer('/api/v1/health', HEALTH_EDITS);
    await h.answer('/api/v2/cfgdash-settings', makeSettings({ configError: 'bad JSON' }));

    expect(document.documentElement.getAttribute('data-theme')).toBe('light');
  });

  it('saves the header toggle choice to config.json where edits are available', async () => {
    const h = await startApp('/overview');
    await h.answer('/api/v1/health', HEALTH_EDITS);
    await h.answer('/api/v2/cfgdash-settings', makeSettings());

    await h.click(h.button('Theme: system. Switch to light'));
    const put = h.pending((r) => r.method === 'PUT');

    expect(put && body(put)).toEqual({ theme: 'light' });
    expect(put?.headers['x-cfgdash-request']).toBe('1');
  });

  it('keeps the header toggle browser-only without edits', async () => {
    const h = await startApp('/overview');
    await h.answer('/api/v1/health', { ...HEALTH_EDITS, edits: false });

    await h.click(h.button('Theme: system. Switch to light'));

    expect(h.requests.some((r) => r.url.pathname === '/api/v2/cfgdash-settings')).toBe(false);
  });

  it('sets the default effort in the model picker as a PUT of only the effort, with the models from the assistant status', async () => {
    const h = await openGear();
    await h.answer('/api/v2/assistant/status', { available: true, models: ['sonnet', 'opus'], sessions: [] });

    await h.click(control('Model and effort'));
    expect(screen.getAllByRole('radio').map((r) => r.closest('label')?.textContent)).toEqual(['Claude', 'sonnet', 'opus', 'Low', 'MediumDefault', 'High', 'Extra high', 'Max']);
    await h.click(screen.getByRole('radio', { name: 'High' }));
    const put = await h.answer('/api/v2/cfgdash-settings', makeSettings({ values: { ...makeSettings().values, assistant: { model: 'sonnet', effort: 'high', maxBudgetUsd: 2 } } }));

    expect(put.method).toBe('PUT');
    expect(body(put)).toEqual({ assistant: { effort: 'high' } });
    expect(control('Model and effort').textContent).toBe('sonnet · High');
  });
});
