import type { CollectorStatus, Snapshot } from '../lib/models';
import { makeFindings, makeSnapshot } from '../test/fixtures';
import { startApp } from '../test/harness';
import { answerWhenSent } from '../test/lazy';

const navLinks = () => [...document.querySelectorAll<HTMLAnchorElement>('nav[aria-label="Views"] a')];
const navLabels = () => navLinks().map((a) => a.querySelector('span')?.textContent?.trim());
const navLink = (label: string) => navLinks().find((a) => a.querySelector('span')?.textContent?.trim() === label);
const groupOf = (label: string) => navLink(label)?.closest('div')?.querySelector('p')?.textContent;

const interactiveNames = () =>
  [...document.querySelectorAll('a, button')].map((e) => (e.getAttribute('aria-label') ?? e.textContent ?? '').replace(/\s+/g, ' ').trim());

function withView(snapshot: Snapshot, view: string, state: CollectorStatus['state'], title: string): Snapshot {
  const collectorId = `${view}.main`;
  snapshot.collectors.push({ id: collectorId, title, version: '1.0.0', view, state, requirements: {}, durationMs: 1 });
  if (state === 'active') {
    snapshot.sections.push({
      collectorId,
      view,
      title,
      blocks: [{ type: 'kv', id: `${view}-kv`, title: `${title} settings`, rows: [{ key: 'calls', value: '3' }] }]
    });
  }
  return snapshot;
}

describe('navigation', () => {
  it('lists nav views in contract order, adds Other for unknown ids and hides notPresent-only views', async () => {
    await startApp('/overview');

    expect(navLabels()).toEqual(['Overview', 'Health', 'Settings', 'Instructions', 'Agents', 'Hooks', 'Other']);
  });

  it('lists Health under Machine', async () => {
    await startApp('/overview');

    expect(groupOf('Health')).toBe('Machine');
  });

  it.each([
    ['t3', 'T3 Code'],
    ['codex', 'Codex'],
    ['opencode', 'OpenCode']
  ])('lists the active %s collector as %s under Harness and renders its sections at its own path', async (view, label) => {
    const h = await startApp('/overview', withView(makeSnapshot(), view, 'active', label));

    expect(groupOf(label)).toBe('Harness');
    await h.navigate(`/${view}`);
    expect(h.el.querySelector('main h1')?.textContent).toBe(label);
    expect(h.text()).toContain(`${label} settings`);
    expect(h.text()).toContain('calls');
  });

  it('orders T3 Code, Codex and OpenCode by the contract, after Hooks and before Other', async () => {
    const snapshot = makeSnapshot();
    withView(snapshot, 'opencode', 'active', 'OpenCode');
    withView(snapshot, 't3', 'active', 'T3 Code');
    withView(snapshot, 'codex', 'active', 'Codex');
    await startApp('/overview', snapshot);

    expect(navLabels()).toEqual(['Overview', 'Health', 'Settings', 'Instructions', 'Agents', 'Hooks', 'T3 Code', 'Codex', 'OpenCode', 'Other']);
  });

  it('hides T3 Code, Codex and OpenCode whose collectors are not on this machine', async () => {
    const snapshot = makeSnapshot();
    withView(snapshot, 't3', 'notPresent', 'T3 Code');
    withView(snapshot, 'codex', 'notPresent', 'Codex');
    withView(snapshot, 'opencode', 'notPresent', 'OpenCode');
    await startApp('/overview', snapshot);

    expect(navLabels()).toEqual(['Overview', 'Health', 'Settings', 'Instructions', 'Agents', 'Hooks', 'Other']);
  });

  it('lists Usage under Activity between Sessions and Memory when its collector is active', async () => {
    const snapshot = makeSnapshot();
    withView(snapshot, 'memory', 'active', 'Memory');
    withView(snapshot, 'usage', 'active', 'Usage');
    withView(snapshot, 'sessions', 'active', 'Sessions');
    await startApp('/overview', snapshot);

    expect(navLabels()).toEqual(['Overview', 'Health', 'Settings', 'Instructions', 'Agents', 'Hooks', 'Sessions', 'Usage', 'Memory', 'Other']);
    expect(groupOf('Usage')).toBe('Activity');
  });

  it('hides Usage when its collector is not on this machine', async () => {
    await startApp('/overview', withView(makeSnapshot(), 'usage', 'notPresent', 'Usage'));

    expect(navLabels()).not.toContain('Usage');
  });

  it.each([
    [{ critical: 1, warning: 2, info: 5 }, '3 findings need attention'],
    [{ critical: 0, warning: 1, info: 0 }, '1 finding needs attention']
  ])('badges the Health entry with the critical and warning findings: %j', async (counts, text) => {
    const h = await startApp('/overview');

    await answerWhenSent(h, '/api/v1/findings', makeFindings({ counts }));

    expect(navLink('Health')?.textContent).toContain(text);
  });

  it('shows no Health badge before the findings load or when only info findings exist', async () => {
    const h = await startApp('/overview');

    expect(navLink('Health')?.textContent?.trim()).toBe('Health');
    await answerWhenSent(h, '/api/v1/findings', makeFindings({ counts: { critical: 0, warning: 0, info: 5 } }));

    expect(navLink('Health')?.textContent?.trim()).toBe('Health');
  });

  it('marks the current view in the nav', async () => {
    await startApp('/hooks');

    const current = [...document.querySelectorAll('nav[aria-label="Views"] a[aria-current="page"]')];

    expect(current.map((a) => a.querySelector('span')?.textContent)).toEqual(['Hooks']);
  });

  it('carries the selected folder in nav links', async () => {
    await startApp('/overview?dir=D%3A%2Fws%2Fapp');

    const hrefs = [...document.querySelectorAll('nav[aria-label="Views"] a')].map((a) => a.getAttribute('href'));

    expect(hrefs).toContain('/settings?dir=D%3A%2Fws%2Fapp');
  });

  it('puts a view id this UI does not know under Other and never in the nav as its own entry', async () => {
    const h = await startApp('/overview', withView(makeSnapshot(), 'omega', 'active', 'Omega'));

    expect(navLabels()).toEqual(['Overview', 'Health', 'Settings', 'Instructions', 'Agents', 'Hooks', 'Other']);
    await h.navigate('/other');
    expect(h.el.querySelector('main h1')?.textContent).toBe('Other');
    expect([...h.el.querySelectorAll('main h2')].map((e) => e.textContent)).toEqual(['zeta', 'omega']);
    expect(h.text()).toContain('calls');
  });

  it.each(['/overview', '/settings', '/other', '/hooks'])('offers a Health link and no Edit link or button on %s', async (path) => {
    const h = await startApp(path);

    expect(interactiveNames().filter((name) => /^edit\b/i.test(name))).toEqual([]);
    expect(h.el.querySelector('nav[aria-label="Views"]')?.textContent).not.toMatch(/edit/i);
    expect(navLink('Health')?.getAttribute('href')).toBe('/health');
  });
});

describe('redirects', () => {
  it.each([
    ['/', '/overview'],
    ['/?dir=D%3A%2Fws%2Fapp', '/overview?dir=D%3A%2Fws%2Fapp'],
    ['/a/b?dir=D%3A%2Fws%2Fapp', '/overview?dir=D%3A%2Fws%2Fapp'],
    ['/a/b?q=sonnet', '/overview?q=sonnet']
  ])('sends %s to %s', async (from, to) => {
    const h = await startApp(from);

    expect(h.url()).toBe(to);
    expect(h.el.querySelector('main h1')?.textContent).toBe('Overview');
  });

  it('loads the snapshot of the folder in the query once, across the redirect', async () => {
    const h = await startApp('/a/b?dir=D%3A%2Fws%2Fapp');

    const snapshots = h.requests.filter((r) => r.url.pathname === '/api/v1/snapshot');
    expect(snapshots).toHaveLength(1);
    expect(snapshots[0].url.searchParams.get('dir')).toBe('D:/ws/app');
  });
});

describe('error panels', () => {
  it('says cfgdash is not answering on a server error and reloads on Try again', async () => {
    const h = await startApp('/overview', null);
    await h.answer('/api/v1/snapshot', {}, 500);

    expect(h.el.querySelector('main h1')?.textContent).toBe("cfgdash isn't answering");
    expect(h.text()).toContain('start it again with cfgdash');

    await h.click(h.button('Try again'));
    expect(h.requests.filter((r) => r.url.pathname === '/api/v1/snapshot')).toHaveLength(2);
    await h.answer('/api/v1/snapshot', makeSnapshot());

    expect(h.el.querySelector('main h1')?.textContent).toBe('Overview');
  });

  it('says cfgdash is not answering when the network request fails', async () => {
    const h = await startApp('/overview');
    vi.stubGlobal('fetch', () => Promise.reject(new TypeError('Failed to fetch')));

    await h.navigate('/overview?dir=D%3A%2Fws%2Fother');
    await h.settle();

    expect(h.el.querySelector('main h1')?.textContent).toBe("cfgdash isn't answering");
  });

  it('shows the generic panel for any other failed answer and reloads on Try again', async () => {
    const h = await startApp('/overview', null);
    await h.answer('/api/v1/snapshot', {}, 422);

    expect(h.el.querySelector('main h1')?.textContent).toBe("Couldn't load the snapshot");
    expect(h.text()).not.toContain("isn't answering");

    await h.click(h.button('Try again'));
    await h.answer('/api/v1/snapshot', makeSnapshot());

    expect(h.el.querySelector('main h1')?.textContent).toBe('Overview');
  });

  it('hides the shell navigation while the snapshot cannot be loaded', async () => {
    await startApp('/overview', null).then((h) => h.answer('/api/v1/snapshot', {}, 500));

    expect(document.querySelector('nav[aria-label="Views"]')).toBeNull();
  });
});
