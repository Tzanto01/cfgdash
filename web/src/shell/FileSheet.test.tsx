import { act, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { FILE_HOOK, FILE_USER } from '../test/fixtures';
import { startApp } from '../test/harness';
import { answerWhenSent, currentParams } from '../test/lazy';

const filePath = (id: string) => `/api/v1/files/${id}`;
const content = (extra: object = {}) => ({
  id: FILE_USER,
  path: 'C:/Users/user/.claude/settings.json',
  kind: 'settings',
  contentType: 'text',
  content: 'line one\nline two\nline three\n',
  truncated: false,
  ...extra
});
const fileLines = () => [...document.querySelectorAll('ol[aria-label="File content"] li')];

describe('file sheet', () => {
  it('requests the file with the current dir and renders one list item per line', async () => {
    const h = await startApp(`/overview?dir=D%3A%2Fws%2Fapp&file=${FILE_USER}`);
    const req = await answerWhenSent(h, filePath(FILE_USER), content());

    expect(req.url.searchParams.get('dir')).toBe('D:/ws/app');
    expect(fileLines().map((li) => li.textContent)).toEqual(['line one', 'line two', 'line three']);
    expect(h.text()).not.toContain('the rest of the file is cut off');
  });

  it('sends no dir when the URL has none and shows the truncated notice', async () => {
    const h = await startApp(`/overview?file=${FILE_USER}`);
    const req = await answerWhenSent(h, filePath(FILE_USER), content({ truncated: true }));

    expect(req.url.searchParams.has('dir')).toBe(false);
    expect(h.text()).toContain('Showing the first 512 KB; the rest of the file is cut off.');
  });

  it('ignores the "/" search shortcut while the sheet is open', async () => {
    await import('./CommandPalette');
    const h = await startApp(`/overview?file=${FILE_USER}`);
    await answerWhenSent(h, filePath(FILE_USER), content());

    await userEvent.setup().keyboard('/');
    await h.settle();

    expect(h.url()).toBe(`/overview?file=${FILE_USER}`);
    expect(screen.queryByRole('dialog', { name: 'Search cfgdash' })).toBeNull();
  });

  it('explains a 404', async () => {
    const h = await startApp(`/overview?file=${FILE_USER}`);
    await answerWhenSent(h, filePath(FILE_USER), { error: 'not-found' }, 404);

    expect(h.text()).toContain("This file isn't available for the selected folder.");
  });

  it('highlights the line named by a deep link and no other', async () => {
    const h = await startApp(`/hooks?file=${FILE_HOOK}&line=2`);
    await answerWhenSent(h, filePath(FILE_HOOK), content({ id: FILE_HOOK }));

    expect(fileLines().map((li) => li.classList.contains('tint-accent'))).toEqual([false, true, false]);
  });

  it('closes on Escape, drops file and line from a deep-linked URL', async () => {
    const h = await startApp(`/hooks?file=${FILE_HOOK}&line=2`);
    await answerWhenSent(h, filePath(FILE_HOOK), content({ id: FILE_HOOK }));

    await userEvent.setup().keyboard('{Escape}');

    await waitFor(() => expect(h.url()).toBe('/hooks'));
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('closes on Escape back to the previous URL and returns focus to the row that opened it', async () => {
    const h = await startApp('/hooks');
    const trigger = h.button('charlie.ps1');
    await act(async () => trigger.focus());
    await h.click(trigger);
    await answerWhenSent(h, filePath(FILE_HOOK), content({ id: FILE_HOOK }));
    expect(currentParams().get('file')).toBe(FILE_HOOK);

    await userEvent.setup().keyboard('{Escape}');

    await waitFor(() => expect(h.url()).toBe('/hooks'));
    await waitFor(() => expect(document.activeElement).toBe(trigger));
  });
});

describe('detail sheet', () => {
  const detailPath = (key: string) => `/api/v1/details/${encodeURIComponent(`hooks.registered:${key}`)}`;
  const detail = (key: string, title: string, text: string) => ({
    id: `hooks.registered:${key}`,
    title,
    contentType: 'text',
    content: text,
    truncated: false
  });
  const detailLines = () => [...document.querySelectorAll('ol[aria-label="Detail content"] li')].map((li) => li.textContent);

  it('opens a row detail with the current dir and shows its title and content', async () => {
    const h = await startApp('/hooks?dir=D%3A%2Fws%2Fapp');
    await h.click(h.button('alpha.ps1'));
    const req = await answerWhenSent(h, detailPath('alpha'), detail('alpha', 'Alpha hook', 'first\nsecond'));

    expect(req.url.pathname).toBe('/api/v1/details/hooks.registered%3Aalpha');
    expect(req.url.searchParams.get('dir')).toBe('D:/ws/app');
    expect(document.querySelector('#file-viewer-title')?.textContent?.trim()).toBe('Alpha hook');
    expect(detailLines()).toEqual(['first', 'second']);
  });

  it('replaces earlier detail content with the error on a 404', async () => {
    const h = await startApp('/hooks?detail=hooks.registered%3Aalpha');
    await answerWhenSent(h, detailPath('alpha'), detail('alpha', 'Alpha hook', 'old content'));
    await h.navigate('/hooks?detail=hooks.registered%3Agone');
    await h.answer(detailPath('gone'), { error: 'not-found' }, 404);

    expect(h.text()).toContain("This detail isn't available for the selected folder.");
    expect(h.text()).not.toContain('Alpha hook');
    expect(detailLines()).toEqual([]);
  });
});
