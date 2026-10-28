import { FILE_USER } from '../testing/fixtures';
import { AppHarness, startApp } from '../testing/harness';

const filePath = `/api/v1/files/${FILE_USER}`;
const content = (extra: object = {}) => ({
  id: FILE_USER,
  path: 'C:/Users/user/.claude/settings.json',
  kind: 'settings',
  contentType: 'text',
  content: 'line one\nline two\nline three\n',
  truncated: false,
  ...extra
});

describe('file viewer', () => {
  it('requests the file with the current dir and renders one list item per line', async () => {
    const h = await startApp(`/overview?dir=D%3A%2Fws%2Fapp&file=${FILE_USER}`);
    const req = await h.answer(filePath, content());

    expect(req.request.params.get('dir')).toBe('D:/ws/app');
    const lines = [...h.el.querySelectorAll('ol[aria-label="File content"] li')].map((li) => li.textContent);
    expect(lines).toEqual(['line one', 'line two', 'line three']);
    expect(h.text()).not.toContain('the rest of the file is cut off');
  });

  it('sends no dir when the URL has none and shows the truncated notice', async () => {
    const h = await startApp(`/overview?file=${FILE_USER}`);
    const req = await h.answer(filePath, content({ truncated: true }));

    expect(req.request.params.has('dir')).toBe(false);
    expect(h.text()).toContain('Showing the first 512 KB; the rest of the file is cut off.');
  });

  it('ignores the "/" search shortcut while the viewer is open', async () => {
    const h = await startApp(`/overview?file=${FILE_USER}`);
    await h.answer(filePath, content());

    document.dispatchEvent(new KeyboardEvent('keydown', { key: '/' }));
    await h.settle();

    expect(h.router.url).toBe(`/overview?file=${FILE_USER}`);
  });

  it('explains a 404', async () => {
    const h = await startApp(`/overview?file=${FILE_USER}`);
    await h.answer(filePath, { error: 'not-found' }, 404);

    expect(h.text()).toContain("This file isn't available for the selected folder.");
  });
});

describe('detail viewer', () => {
  const detailPath = (key: string) => `/api/v1/details/${encodeURIComponent(`hooks.registered:${key}`)}`;
  const detail = (key: string, title: string, text: string) => ({
    id: `hooks.registered:${key}`,
    title,
    contentType: 'text',
    content: text,
    truncated: false
  });
  const contentLines = (h: AppHarness) =>
    [...h.el.querySelectorAll('ol[aria-label="Detail content"] li')].map((li) => li.textContent);

  it('opens a row detail with the current dir and shows its title and content', async () => {
    const h = await startApp('/hooks?dir=D%3A%2Fws%2Fapp');
    await h.click(h.button('alpha.ps1'));
    const req = await h.answer(detailPath('alpha'), detail('alpha', 'Alpha hook', 'first\nsecond'));

    expect(req.request.url).toBe('/api/v1/details/hooks.registered%3Aalpha');
    expect(req.request.params.get('dir')).toBe('D:/ws/app');
    expect(h.el.querySelector('#file-viewer-title')?.textContent?.trim()).toBe('Alpha hook');
    expect(contentLines(h)).toEqual(['first', 'second']);
  });

  it('replaces earlier detail content with the error on a 404', async () => {
    const h = await startApp('/hooks?detail=hooks.registered%3Aalpha');
    await h.answer(detailPath('alpha'), detail('alpha', 'Alpha hook', 'old content'));
    await h.navigate('/hooks?detail=hooks.registered%3Agone');
    await h.answer(detailPath('gone'), { error: 'not-found' }, 404);

    expect(h.text()).toContain("This detail isn't available for the selected folder.");
    expect(h.text()).not.toContain('Alpha hook');
    expect(contentLines(h)).toEqual([]);
  });
});
