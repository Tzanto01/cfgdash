import { makeSnapshot } from '../testing/fixtures';
import { AppHarness, startApp } from '../testing/harness';

const listing = (path: string, parent: string | null, names: string[]) => ({
  path,
  parent,
  entries: names.map((name) => ({ name, path: `${path}/${name}` })),
  suggestions: []
});

async function openPicker(): Promise<AppHarness> {
  const h = await startApp('/settings');
  await h.click(h.button('Change folder'));
  const req = await h.answer('/api/v1/dirs', listing('D:/ws/app', 'D:/ws', ['web']));
  expect(req.request.params.get('path')).toBe('D:/ws/app');
  return h;
}

describe('folder picker', () => {
  it('browsing into a folder does not change dir, "Use this folder" does', async () => {
    const h = await openPicker();

    await h.click(h.button('web'));
    const req = await h.answer('/api/v1/dirs', listing('D:/ws/app/web', 'D:/ws/app', []));
    expect(req.request.params.get('path')).toBe('D:/ws/app/web');
    expect(h.router.parseUrl(h.router.url).queryParams['dir']).toBeUndefined();
    expect(h.text()).toContain('Folders in D:/ws/app/web');

    await h.click(h.button('Use this folder'));
    expect(h.router.parseUrl(h.router.url).queryParams['dir']).toBe('D:/ws/app/web');
    await h.answer('/api/v1/snapshot', makeSnapshot({ targetDir: 'D:/ws/app/web' }));
    expect(document.activeElement?.textContent?.trim()).toBe('Settings');
  });

  it('"Use this folder" opens and uses a typed path that was not browsed yet', async () => {
    const h = await openPicker();
    const input = h.el.querySelector<HTMLInputElement>('#folder-path') as HTMLInputElement;
    input.value = 'D:/ws/site';
    input.dispatchEvent(new Event('input'));
    await h.settle();

    await h.click(h.button('Use this folder'));
    const req = await h.answer('/api/v1/dirs', listing('D:/ws/site', 'D:/ws', []));

    expect(req.request.params.get('path')).toBe('D:/ws/site');
    expect(h.router.parseUrl(h.router.url).queryParams['dir']).toBe('D:/ws/site');
  });

  it('moves focus to the error heading when the chosen folder fails to load', async () => {
    const h = await openPicker();
    await h.click(h.button('web'));
    await h.answer('/api/v1/dirs', listing('D:/ws/app/web', 'D:/ws/app', []));

    await h.click(h.button('Use this folder'));
    await h.answer('/api/v1/snapshot', { error: 'bad-dir' }, 400);

    expect(document.activeElement?.textContent?.trim()).toBe("This folder can't be used");
  });

  it('shows a field error and does not navigate for a typed path the server rejects', async () => {
    const h = await openPicker();
    const input = h.el.querySelector<HTMLInputElement>('#folder-path') as HTMLInputElement;
    input.value = 'relative/x';
    input.dispatchEvent(new Event('input'));
    await h.settle();

    await h.click(h.button('Open'));
    const req = await h.answer('/api/v1/dirs', { error: 'bad-dir' }, 400);

    expect(req.request.params.get('path')).toBe('relative/x');
    expect(h.el.querySelector('#folder-path-error')?.textContent).toContain("That folder doesn't exist or isn't an absolute path.");
    expect(h.router.parseUrl(h.router.url).queryParams['dir']).toBeUndefined();
  });
});
