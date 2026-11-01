import type { Snapshot } from '../lib/models';
import { makeSnapshot } from '../test/fixtures';
import { startApp } from '../test/harness';

const navLabels = () =>
  [...document.querySelectorAll('nav[aria-label="Views"] a')].map((a) => a.querySelector('span')?.textContent?.trim());

const interactiveNames = () =>
  [...document.querySelectorAll('a, button')].map((e) => (e.getAttribute('aria-label') ?? e.textContent ?? '').replace(/\s+/g, ' ').trim());

function withUsage(): Snapshot {
  const snapshot = makeSnapshot();
  snapshot.collectors.push({ id: 'usage.local', title: 'usage.local', version: '1.0.0', view: 'usage', state: 'active', requirements: {}, durationMs: 1 });
  snapshot.sections = [
    ...snapshot.sections,
    {
      collectorId: 'usage.local',
      view: 'usage',
      title: 'Usage',
      blocks: [{ type: 'kv', id: 'usage', title: 'Usage', rows: [{ key: 'calls', value: '3' }] }]
    }
  ];
  return snapshot;
}

describe('navigation', () => {
  it('lists nav views in contract order, adds Other for unknown ids and hides notPresent-only views', async () => {
    await startApp('/overview');

    expect(navLabels()).toEqual(['Overview', 'Settings', 'Instructions', 'Agents', 'Hooks', 'Other']);
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
    const h = await startApp('/overview', withUsage());

    expect(navLabels()).toEqual(['Overview', 'Settings', 'Instructions', 'Agents', 'Hooks', 'Other']);
    await h.navigate('/other');
    expect(h.el.querySelector('main h1')?.textContent).toBe('Other');
    expect([...h.el.querySelectorAll('main h2')].map((e) => e.textContent)).toEqual(['zeta', 'usage']);
    expect(h.text()).toContain('calls');
  });

  it.each(['/overview', '/settings', '/other', '/hooks'])('offers no Health, Usage or Edit link or button on %s', async (path) => {
    const h = await startApp(path, withUsage());

    expect(interactiveNames().filter((name) => /^(health|usage|edit)\b/i.test(name))).toEqual([]);
    expect(h.el.querySelector('nav[aria-label="Views"]')?.textContent).not.toMatch(/health|usage|edit/i);
  });

  it('shows no Health tile on the overview', async () => {
    const h = await startApp('/overview');

    expect(h.text()).not.toMatch(/health/i);
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

    expect(h.requests).toHaveLength(1);
    expect(h.requests[0].url.searchParams.get('dir')).toBe('D:/ws/app');
  });
});

describe('error panels', () => {
  it('says cfgdash is not answering on a server error and reloads on Try again', async () => {
    const h = await startApp('/overview', null);
    await h.answer('/api/v1/snapshot', {}, 500);

    expect(h.el.querySelector('main h1')?.textContent).toBe("cfgdash isn't answering");
    expect(h.text()).toContain('start it again with cfgdash');

    await h.click(h.button('Try again'));
    expect(h.requests).toHaveLength(2);
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
