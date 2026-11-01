import { fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { makeSnapshot } from '../test/fixtures';
import { startApp, type AppHarness } from '../test/harness';
import { answerWhenSent, currentParams } from '../test/lazy';

const listing = (path: string, parent: string | null, names: string[]) => ({
  path,
  parent,
  entries: names.map((name) => ({ name, path: `${path}/${name}` })),
  suggestions: []
});

async function openPicker(names: string[] = ['web']): Promise<AppHarness> {
  const h = await startApp('/settings');
  await h.click(h.button('Change folder'));
  const req = await answerWhenSent(h, '/api/v1/dirs', listing('D:/ws/app', 'D:/ws', names));
  expect(req.url.searchParams.get('path')).toBe('D:/ws/app');
  return h;
}

const typeInto = (label: string, value: string) => fireEvent.change(screen.getByLabelText(label), { target: { value } });
const snapshotRequests = (h: AppHarness) => h.requests.filter((r) => r.url.pathname === '/api/v1/snapshot');

describe('folder picker', () => {
  it('browsing into a folder does not change dir, "Use this folder" does', async () => {
    const h = await openPicker();

    await h.click(h.button('web'));
    const req = await h.answer('/api/v1/dirs', listing('D:/ws/app/web', 'D:/ws/app', []));
    expect(req.url.searchParams.get('path')).toBe('D:/ws/app/web');
    expect(currentParams().get('dir')).toBeNull();
    expect(h.text()).toContain('Folders in D:/ws/app/web');

    await h.click(h.button('Use this folder'));
    expect(currentParams().get('dir')).toBe('D:/ws/app/web');
    await h.answer('/api/v1/snapshot', makeSnapshot({ targetDir: 'D:/ws/app/web' }));

    await waitFor(() => expect(document.activeElement).toBe(h.el.querySelector('main h1')));
    expect(document.activeElement?.textContent?.trim()).toBe('Settings');
  });

  it('"Use this folder" opens and uses a typed path that was not browsed yet', async () => {
    const h = await openPicker();
    typeInto('Folder path', 'D:/ws/site');

    await h.click(h.button('Use this folder'));
    const req = await h.answer('/api/v1/dirs', listing('D:/ws/site', 'D:/ws', []));

    expect(req.url.searchParams.get('path')).toBe('D:/ws/site');
    expect(currentParams().get('dir')).toBe('D:/ws/site');
  });

  it('moves focus to the error heading when the chosen folder fails to load', async () => {
    const h = await openPicker();
    await h.click(h.button('web'));
    await h.answer('/api/v1/dirs', listing('D:/ws/app/web', 'D:/ws/app', []));

    await h.click(h.button('Use this folder'));
    await h.answer('/api/v1/snapshot', { error: 'bad-dir' }, 400);

    await waitFor(() => expect(document.activeElement?.textContent?.trim()).toBe("This folder can't be used"));
  });

  it('shows a field error and does not navigate for a typed path the server rejects', async () => {
    const h = await openPicker();
    typeInto('Folder path', 'relative/x');

    await h.click(h.button('Open'));
    const req = await h.answer('/api/v1/dirs', { error: 'bad-dir' }, 400);

    expect(req.url.searchParams.get('path')).toBe('relative/x');
    expect(screen.getByLabelText('Folder path').getAttribute('aria-invalid')).toBe('true');
    expect(h.text()).toContain("That folder doesn't exist or isn't an absolute path.");
    expect(currentParams().get('dir')).toBeNull();
  });

  it('falls back to the start listing when a browsed entry is rejected', async () => {
    const h = await openPicker();

    await h.click(h.button('web'));
    await h.answer('/api/v1/dirs', { error: 'bad-dir' }, 400);
    const fallback = await h.answer('/api/v1/dirs', listing('D:/ws', null, ['app']));

    expect(fallback.url.searchParams.has('path')).toBe(false);
    expect(h.text()).toContain('Folders in D:/ws');
    expect(h.text()).not.toContain("That folder doesn't exist");
  });

  it('offers a filter above 20 entries and narrows the list with it', async () => {
    const names = Array.from({ length: 21 }, (_, i) => `dir-${String(i).padStart(2, '0')}`);
    const h = await openPicker(names);
    const shown = () => h.buttons(/^dir-/).map((b) => b.textContent);

    expect(shown()).toHaveLength(21);
    typeInto('Filter folders', 'DIR-1');

    expect(shown()).toEqual(names.filter((n) => n.startsWith('dir-1')));
  });

  it('offers no filter for 20 entries', async () => {
    const names = Array.from({ length: 20 }, (_, i) => `dir-${String(i).padStart(2, '0')}`);
    await openPicker(names);

    expect(screen.queryByLabelText('Filter folders')).toBeNull();
  });

  const closers: [string, (h: AppHarness) => Promise<unknown>][] = [
    ['Cancel', (h) => h.click(h.button('Cancel'))],
    ['Escape', () => userEvent.setup().keyboard('{Escape}')]
  ];

  it.each(closers)('%s closes the picker without changing dir', async (_name, close) => {
    const h = await openPicker();
    await h.click(h.button('web'));
    await h.answer('/api/v1/dirs', listing('D:/ws/app/web', 'D:/ws/app', []));

    await close(h);

    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull());
    expect(currentParams().get('dir')).toBeNull();
    expect(snapshotRequests(h)).toHaveLength(1);
  });
});
