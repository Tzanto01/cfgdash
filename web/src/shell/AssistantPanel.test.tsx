import { act, fireEvent, screen } from '@testing-library/react';
import type { AssistantStatus, CfgdashSettings } from '../lib/models';
import { makeFindings, makeSnapshot } from '../test/fixtures';
import { startApp, type AppHarness, type RequestRecord } from '../test/harness';
import { answerWhenSent } from '../test/lazy';

const HEALTH_EDITS = { status: 'ok', version: '1.0.0', schemaVersion: 1, edits: true };
const READY: AssistantStatus = { available: true, claudePath: '/home/user/.local/bin/claude', models: ['sonnet', 'opus', 'haiku'], sessions: [] };

const settings = (): CfgdashSettings => ({
  values: {
    startAtLogin: false,
    theme: 'system',
    defaultDir: null,
    tailnet: false,
    fleet: { enabled: false },
    assistant: { model: 'opus', effort: 'high', maxBudgetUsd: 2 }
  },
  startAtLogin: { configured: false, actual: 'off' },
  paths: { config: '/home/user/.config/cfgdash/config.json', overrides: '/home/user/.config/cfgdash/overrides.json' }
});

class FakeEventSource extends EventTarget {
  static readonly CLOSED = 2;
  static all: FakeEventSource[] = [];
  closed = false;
  readyState = 1;
  constructor(readonly url: string) {
    super();
    FakeEventSource.all.push(this);
  }
  close(): void {
    this.closed = true;
  }
}

const diff = ['--- a/D:/ws/app/.claude/settings.json', '+++ b/D:/ws/app/.claude/settings.json', '@@ -1,1 +1,3 @@', '-{}', '+{', '+  "model": "sonnet"', '+}', ''].join('\n');
const proposal = (id: string) => ({
  proposalId: id,
  expiresAt: '2030-01-01T00:00:00Z',
  files: [{ path: 'D:/ws/app/.claude/settings.json', scope: 'project', sha256Before: null, diff }],
  confirm: `confirm-${id}`,
  rationale: '<b>Pin</b> the model for this folder.'
});

const panel = () => document.querySelector<HTMLElement>('aside[aria-labelledby="assistant-title"]');
const body = (r: RequestRecord) => JSON.parse(r.body ?? 'null') as unknown;
const picker = () => panel()!.querySelector<HTMLButtonElement>('button[aria-haspopup="dialog"]')!;

async function emit(name: string, data: unknown, source = FakeEventSource.all[FakeEventSource.all.length - 1]): Promise<void> {
  await act(async () => {
    source.dispatchEvent(new MessageEvent(name, { data: JSON.stringify(data) }));
  });
}

async function openPanel(status: AssistantStatus = READY, gear: CfgdashSettings = settings(), url = '/settings?dir=D%3A%2Fws%2Fapp'): Promise<AppHarness> {
  FakeEventSource.all = [];
  const h = await startApp(url);
  vi.stubGlobal('EventSource', FakeEventSource);
  await h.answer('/api/v1/health', HEALTH_EDITS);
  await h.answer('/api/v2/cfgdash-settings', settings());
  await h.click(h.button('Assistant'));
  await answerWhenSent(h, '/api/v2/assistant/status', status);
  if (status.available) {
    await h.answer('/api/v2/cfgdash-settings', gear);
  }
  return h;
}

async function type(h: AppHarness, text: string): Promise<void> {
  await act(async () => {
    fireEvent.change(panel()!.querySelector('textarea')!, { target: { value: text } });
  });
  await h.settle();
}

async function startTurn(h: AppHarness, text = 'Why is the model opus?'): Promise<RequestRecord> {
  await type(h, text);
  await h.click(h.button('Send'));
  const start = await h.answer('/api/v2/assistant/sessions', { id: 's1' });
  return start;
}

describe('assistant panel', () => {
  it('opens only where edits are available', async () => {
    const h = await startApp('/overview');
    await h.answer('/api/v1/health', { ...HEALTH_EDITS, edits: false });

    expect(h.buttons('Assistant')).toHaveLength(0);
  });

  it('shows the available: false reason instead of the input, and Escape closes it back to the toggle', async () => {
    const h = await openPanel({ available: false, reason: 'claude not found on PATH', models: [], sessions: [] });

    expect(panel()?.textContent).toContain('claude not found on PATH');
    expect(panel()?.querySelector('textarea')).toBeNull();
    expect(h.buttons('Full check')).toHaveLength(0);

    await act(async () => {
      fireEvent.keyDown(panel()!, { key: 'Escape' });
    });
    expect(panel()).toBeNull();
    expect(document.activeElement?.getAttribute('aria-label')).toBe('Assistant');
  });

  it('starts a session with the defaults from cfgdash settings, sends row context as ids and streams the answer', async () => {
    const h = await openPanel();
    expect(picker().textContent).toBe('opus · High');

    await h.click(h.button('Add Effective settings / model to assistant context'));
    expect(panel()?.textContent).toContain('Context 1 of 8');

    const start = await startTurn(h);
    expect(start.method).toBe('POST');
    expect(start.headers['x-cfgdash-request']).toBe('1');
    expect(body(start)).toMatchObject({ model: 'opus', effort: 'high' });
    expect(FakeEventSource.all.map((s) => s.url)).toEqual(['/api/v2/assistant/sessions/s1/events']);

    const message = await h.answer('/api/v2/assistant/sessions/s1/messages', null, 202);
    expect(message.headers['x-cfgdash-request']).toBe('1');
    expect(body(message)).toEqual({ text: 'Why is the model opus?', context: [{ kind: 'row', collectorId: 'settings.effective', blockId: 'effective', itemId: 'model' }] });
    expect(panel()?.textContent).not.toContain('Context 1 of 8');

    await emit('delta', { text: 'The user scope sets ' });
    await emit('tool', { name: 'get_snapshot', status: 'started' });
    await emit('tool', { name: 'get_snapshot', status: 'done' });
    await emit('delta', { text: '`model` to opus. See [docs](https://example.com/settings).' });
    await emit('cost', { costUsd: 0.0123 });
    await emit('done', {});

    const text = panel()!.textContent ?? '';
    expect(text).toContain('Assistant · model output derived from your files; review before pasting');
    expect(text).toContain('The user scope sets');
    expect(text).toContain('model to opus. See docs (https://example.com/settings).');
    expect(text).toContain('get_snapshot: done');
    expect(text).not.toContain('get_snapshot: started');
    expect(text).toContain('Cost $0.0123');
    expect(text).not.toContain('The assistant is working');
    expect(panel()!.querySelector('a, img')).toBeNull();
  });

  it('gives every proposal its own review and Apply with that proposal confirm, under the assistant banner, rationale as plain text', async () => {
    const h = await openPanel();
    await startTurn(h);
    await h.answer('/api/v2/assistant/sessions/s1/messages', null, 202);

    await emit('proposal', proposal('p1'));
    await emit('proposal', proposal('p2'));

    const cards = [...panel()!.querySelectorAll('section[aria-label="Assistant proposal"]')];
    expect(cards).toHaveLength(2);
    expect(cards[0].textContent).toContain('Proposed by the assistant from your config files');
    expect(cards[0].textContent).toContain('<b>Pin</b> the model for this folder.');
    expect(cards[0].querySelector('b')).toBeNull();
    expect(cards[0].textContent).toContain('"model": "sonnet"');
    expect(h.buttons('Apply this proposal')).toHaveLength(2);

    await h.click(h.buttons('Apply this proposal')[0]);
    const apply = await h.answer('/api/v2/proposals/p1/apply', { applied: true, auditId: 'a9' });

    expect(apply.headers['x-cfgdash-request']).toBe('1');
    expect(body(apply)).toEqual({ confirm: 'confirm-p1' });
    expect(cards[0].textContent).toContain('Applied. Audit entry a9.');
    expect(h.buttons('Apply this proposal')).toHaveLength(1);
    expect(h.requests.some((r) => r.url.pathname === '/api/v2/proposals/p2/apply')).toBe(false);
  });

  it('flags a proposal for a file tracked by git', async () => {
    const h = await openPanel();
    await h.answer('/api/v2/settings', { categories: [], files: [{ scope: 'project', path: 'D:/ws/app/.claude/settings.json', exists: true, gitTracked: true }] });
    await startTurn(h);
    await h.answer('/api/v2/assistant/sessions/s1/messages', null, 202);

    await emit('proposal', proposal('p1'));

    expect(panel()?.querySelector('section[aria-label="Assistant proposal"]')?.textContent).toContain('tracked by git');
  });

  it('reads the tracked files again for a new folder while the panel stays open', async () => {
    const h = await openPanel();
    await h.answer('/api/v2/settings', { categories: [], files: [] });

    await h.navigate('/settings?dir=D%3A%2Fws%2Fother');
    await h.answer('/api/v1/snapshot', makeSnapshot({ targetDir: 'D:/ws/other' }));

    expect(panel()).not.toBeNull();
    expect(h.pending((r) => r.url.pathname === '/api/v2/settings')?.url.searchParams.get('dir')).toBe('D:/ws/other');
  });

  it('stops the working state and says so when the event stream is lost', async () => {
    const h = await openPanel();
    await startTurn(h);
    await h.answer('/api/v2/assistant/sessions/s1/messages', null, 202);
    expect(panel()?.textContent).toContain('The assistant is working');

    const source = FakeEventSource.all[0];
    source.readyState = FakeEventSource.CLOSED;
    await act(async () => {
      source.dispatchEvent(new Event('error'));
    });

    expect(panel()?.textContent).not.toContain('The assistant is working');
    expect(panel()?.textContent).toContain('Lost the connection to the assistant.');
    expect(h.button('Full check').disabled).toBe(false);
  });

  it('falls back to the first model when cfgdash settings give none', async () => {
    const h = await openPanel(READY, { ...settings(), configError: 'bad JSON' });
    expect(picker().textContent).toBe('sonnet · Medium');

    const start = await startTurn(h);

    expect(body(start)).toMatchObject({ model: 'sonnet', effort: 'medium' });
  });

  it('keeps at most 8 context chips and drops them when the folder changes', async () => {
    const h = await openPanel(READY, settings(), '/hooks?dir=D%3A%2Fws%2Fapp');
    const adds = h.buttons(/^Add Many rows \/ .* to assistant context$/);
    expect(adds.length).toBeGreaterThan(8);

    for (const button of adds.slice(0, 9)) {
      await h.click(button);
    }
    expect(panel()?.querySelectorAll('ul[aria-label="Context"] li')).toHaveLength(8);
    expect(panel()?.textContent).toContain('Context 8 of 8');

    await h.navigate('/hooks?dir=D%3A%2Fws%2Fother');
    await h.answer('/api/v1/snapshot', makeSnapshot({ targetDir: 'D:/ws/other' }));

    expect(panel()?.querySelector('ul[aria-label="Context"]')).toBeNull();
  });

  it('shows a stale apply inline on its proposal', async () => {
    const h = await openPanel();
    await startTurn(h);
    await h.answer('/api/v2/assistant/sessions/s1/messages', null, 202);
    await emit('proposal', proposal('p1'));

    await h.click(h.button('Apply this proposal'));
    await h.answer('/api/v2/proposals/p1/apply', { error: 'stale' }, 409);

    expect(panel()?.querySelector('[role="alert"]')?.textContent).toBe('The files changed since this proposal. Ask the assistant again.');
  });

  it.each([
    ['429 on start', '/api/v2/assistant/sessions', 429, { error: 'too-many-sessions' }, 'Too many assistant sessions are running (at most 2). End another one first.'],
    ['409 on a message', '/api/v2/assistant/sessions/s1/messages', 409, { error: 'turn-running' }, 'The assistant is still answering. Wait for it to finish.'],
    ['400 bad-context', '/api/v2/assistant/sessions/s1/messages', 400, { error: 'bad-context' }, 'Some context items are not available to the assistant. Remove them and send again.']
  ])('shows %s inline', async (_name, path, status, response, words) => {
    const h = await openPanel();
    await type(h, 'Hello');
    await h.click(h.button('Send'));
    if (path.endsWith('/messages')) {
      await h.answer('/api/v2/assistant/sessions', { id: 's1' });
    }

    await h.answer(path, response, status);

    expect(panel()?.querySelector('[role="alert"]')?.textContent).toBe(words);
    expect([...panel()!.querySelectorAll('p')].some((p) => p.textContent === 'Hello')).toBe(false);
  });

  it('runs the full check and ends the session with a guarded DELETE', async () => {
    const h = await openPanel();

    await h.click(h.button('Full check'));
    await h.answer('/api/v2/assistant/sessions', { id: 's1' });
    const check = await h.answer('/api/v2/assistant/sessions/s1/check', null, 202);
    expect(check.method).toBe('POST');
    await emit('error', { error: 'process-exited', message: 'the assistant process ended unexpectedly' });
    expect(panel()?.textContent).toContain('the assistant process ended unexpectedly');

    await h.click(h.button('End session'));
    const end = await h.answer('/api/v2/assistant/sessions/s1', null, 204);

    expect(end.method).toBe('DELETE');
    expect(end.headers['x-cfgdash-request']).toBe('1');
    expect(FakeEventSource.all[0].closed).toBe(true);
  });

  it('ends the live session when the page unloads, so it does not hold one of the two process slots', async () => {
    const h = await openPanel();
    await startTurn(h);

    await act(async () => {
      window.dispatchEvent(new Event('pagehide'));
    });

    const end = h.pending((r) => r.method === 'DELETE' && r.url.pathname === '/api/v2/assistant/sessions/s1');
    expect(end?.headers['x-cfgdash-request']).toBe('1');
  });

  it('resizes from its handle with the arrow keys between 320 px and the window limit, and remembers the width', async () => {
    const h = await openPanel();
    const handle = () => screen.getByRole('separator', { name: 'Resize assistant panel' });
    const key = async (k: string) => {
      await act(async () => {
        fireEvent.keyDown(handle(), { key: k });
      });
    };
    expect(handle().getAttribute('aria-valuenow')).toBe('400');

    await key('End');
    expect(handle().getAttribute('aria-valuenow')).toBe(String(Math.min(Math.floor(window.innerWidth * 0.6), window.innerWidth - 480)));
    await key('Home');
    await key('ArrowRight');
    expect(handle().getAttribute('aria-valuenow')).toBe('320');
    await key('ArrowLeft');
    expect(panel()!.style.getPropertyValue('--assistant-width')).toBe('336px');

    await h.click(h.button('Close assistant'));
    await h.click(h.button('Assistant'));
    expect(handle().getAttribute('aria-valuenow')).toBe('336');
  });

  it('picks the model and effort in the popover, labels the default effort and starts the session with them', async () => {
    const h = await openPanel();
    await h.click(picker());
    const popover = screen.getByRole('dialog', { name: 'Model and effort' });
    expect(document.activeElement).toBe(screen.getByRole('searchbox', { name: 'Search models' }));
    expect(screen.getByRole('radio', { name: /^High/ }).closest('label')?.textContent).toContain('Default');

    await act(async () => {
      fireEvent.change(screen.getByRole('searchbox', { name: 'Search models' }), { target: { value: 'hai' } });
    });
    expect(screen.getAllByRole('radio', { name: /^(sonnet|opus|haiku)/ }).map((r) => r.closest('label')?.textContent)).toEqual(['haiku']);
    await h.click(screen.getByRole('radio', { name: 'haiku' }));
    await h.click(screen.getByRole('radio', { name: 'Low' }));
    expect(picker().textContent).toBe('haiku · Low');

    await act(async () => {
      fireEvent.keyDown(popover, { key: 'Escape' });
    });
    expect(screen.queryByRole('dialog', { name: 'Model and effort' })).toBeNull();
    expect(panel()).not.toBeNull();
    expect(document.activeElement).toBe(picker());

    const start = await startTurn(h);
    expect(body(start)).toMatchObject({ model: 'haiku', effort: 'low' });
  });

  it('accepts a full model id typed into the search', async () => {
    const h = await openPanel();
    await h.click(picker());
    await act(async () => {
      fireEvent.change(screen.getByRole('searchbox', { name: 'Search models' }), { target: { value: 'claude-sonnet-9-9' } });
    });

    await h.click(h.button('Use model id claude-sonnet-9-9'));

    expect(picker().textContent).toBe('claude-sonnet-9-9 · High');
  });

  it('runs Diagnose all from Health as a check limited to the visible findings', async () => {
    FakeEventSource.all = [];
    const h = await startApp('/health?dir=D%3A%2Fws%2Fapp');
    vi.stubGlobal('EventSource', FakeEventSource);
    await h.answer('/api/v1/health', HEALTH_EDITS);
    await h.answer('/api/v2/cfgdash-settings', settings());
    await answerWhenSent(h, '/api/v1/findings', makeFindings());
    await screen.findByRole('table', { name: 'Findings' });
    await h.click(h.button('Warning'));

    await h.click(h.button('Diagnose all'));
    await answerWhenSent(h, '/api/v2/assistant/status', READY);
    await h.answer('/api/v2/cfgdash-settings', settings());
    const start = await h.answer('/api/v2/assistant/sessions', { id: 's1' });
    const check = await h.answer('/api/v2/assistant/sessions/s1/check', null, 202);

    expect(body(start)).toMatchObject({ model: 'opus', effort: 'high' });
    expect(body(check)).toEqual({ findingIds: ['overridden:settings.effective:effective:model'] });
    expect(panel()?.textContent).toContain('Diagnose 1 finding');
  });

  it('drops a Diagnose request when the panel closes before it ran, so reopening starts no check', async () => {
    FakeEventSource.all = [];
    const h = await startApp('/health?dir=D%3A%2Fws%2Fapp');
    vi.stubGlobal('EventSource', FakeEventSource);
    await h.answer('/api/v1/health', HEALTH_EDITS);
    await h.answer('/api/v2/cfgdash-settings', settings());
    await answerWhenSent(h, '/api/v1/findings', makeFindings());
    await screen.findByRole('table', { name: 'Findings' });

    await h.click(h.button('Diagnose all'));
    await answerWhenSent(h, '/api/v2/assistant/status', READY);
    await h.click(h.button('Close assistant'));
    await h.click(h.button('Assistant'));
    await answerWhenSent(h, '/api/v2/assistant/status', READY);
    await answerWhenSent(h, '/api/v2/cfgdash-settings', settings());

    expect(h.requests.some((r) => r.url.pathname === '/api/v2/assistant/sessions')).toBe(false);
    expect(h.button('Full check').disabled).toBe(false);
  });

  it('has no resize handle below 768 px, where it covers the screen', async () => {
    vi.stubGlobal('matchMedia', (query: string) => ({ matches: query === '(max-width: 767px)', media: query, addEventListener: () => undefined, removeEventListener: () => undefined }));
    await openPanel();

    expect(panel()).not.toBeNull();
    expect(screen.queryByRole('separator')).toBeNull();
  });
});
