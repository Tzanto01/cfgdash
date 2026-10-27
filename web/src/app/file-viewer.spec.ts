import { FILE_USER } from '../testing/fixtures';
import { startApp } from '../testing/harness';

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
