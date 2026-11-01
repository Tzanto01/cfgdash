import { makeSnapshot } from '../test/fixtures';
import { startApp, type AppHarness, type RequestRecord } from '../test/harness';

const isRefresh = (r: RequestRecord) => r.url.searchParams.get('refresh') === 'true';
const mainHeading = () => document.querySelector('main h1');

async function startRefresh(h: AppHarness): Promise<RequestRecord> {
  await h.click(h.button('Refresh'));
  const refresh = h.pending(isRefresh);
  if (!refresh) {
    throw new Error('No refresh request');
  }
  return refresh;
}

describe('snapshot store', () => {
  it('shows the loading state, not the old snapshot, when the folder changes during a refresh', async () => {
    const h = await startApp('/overview');
    const refresh = await startRefresh(h);

    await h.navigate('/overview?dir=D%3A%2Fws%2Fother');

    expect(refresh.aborted).toBe(true);
    expect(h.text()).toContain("Reading this machine's harness");
    expect(h.text()).not.toContain('devbox');
    expect(h.text()).not.toContain("Couldn't");

    const request = await h.answer('/api/v1/snapshot', makeSnapshot({ hostname: 'otherbox', targetDir: 'D:/ws/other' }));
    expect(request.url.searchParams.get('dir')).toBe('D:/ws/other');
    expect(h.text()).toContain('otherbox');
  });

  it('links "Back to the start folder" without dir but with the search query', async () => {
    const h = await startApp('/search?q=sonnet&dir=D%3A%2Fgone', null);
    await h.answer('/api/v1/snapshot', { error: 'bad-dir' }, 400);
    const link = [...h.el.querySelectorAll('a')].find((a) => a.textContent?.trim() === 'Back to the start folder');

    expect(mainHeading()?.textContent).toBe("This folder can't be used");
    expect(link?.getAttribute('href')).toBe('/search?q=sonnet');
  });

  it('keeps the content and says "Couldn\'t refresh" when a refresh fails, and Try again focuses the heading', async () => {
    const h = await startApp('/overview');
    await startRefresh(h);
    await h.answer('/api/v1/snapshot', {}, 500);

    expect(h.text()).toContain("Couldn't refresh");
    expect(h.text()).toContain('devbox');
    expect(h.text()).not.toContain("cfgdash isn't answering");

    await h.click(h.button('Try again'));

    expect(document.activeElement).toBe(mainHeading());
    expect(h.pending(isRefresh)).toBeDefined();
  });

  it('keeps the content and the refresh button usable while a refresh is running', async () => {
    const h = await startApp('/overview');
    await startRefresh(h);

    expect(h.text()).toContain('devbox');
    expect(h.button('Refreshing').getAttribute('aria-busy')).toBe('true');

    await h.answer('/api/v1/snapshot', makeSnapshot({ hostname: 'devbox2' }));

    expect(h.text()).toContain('devbox2');
    expect(h.button('Refresh').getAttribute('aria-busy')).toBe('false');
  });

  it('does not treat an aborted request as an error', async () => {
    const h = await startApp('/overview');

    await h.navigate('/overview?dir=D%3A%2Fws%2Fa');
    await h.navigate('/overview?dir=D%3A%2Fws%2Fb');

    expect(h.requests[1].aborted).toBe(true);
    expect(h.requests[2].aborted).toBe(false);
    expect(h.text()).not.toContain("Couldn't");
    expect(h.text()).not.toContain("isn't answering");

    const request = await h.answer('/api/v1/snapshot', makeSnapshot({ hostname: 'boxb', targetDir: 'D:/ws/b' }));
    expect(request.url.searchParams.get('dir')).toBe('D:/ws/b');
    expect(h.text()).toContain('boxb');
    expect(h.text()).not.toContain("Couldn't");
  });
});
