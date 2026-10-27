import { makeSnapshot } from '../testing/fixtures';
import { startApp } from '../testing/harness';

const isRefresh = (r: { params: { get(k: string): string | null } }) => r.params.get('refresh') === 'true';

describe('access problems', () => {
  it('asks to open the printed link on 401', async () => {
    const h = await startApp('/overview', null);
    await h.answer('/api/v1/snapshot', { error: 'unauthenticated' }, 401);

    expect(h.el.querySelector('h1')?.textContent).toContain('Open the link printed in your terminal');
    expect(document.activeElement).toBe(h.el.querySelector('h1'));
  });

  it('shows the forbidden heading with the reason on 403', async () => {
    const h = await startApp('/overview', null);
    await h.answer('/api/v1/snapshot', { error: 'forbidden', reason: 'not-owner' }, 403);

    expect(h.el.querySelector('h1')?.textContent).toContain("This device or account isn't allowed");
    expect(h.text()).toContain('Reason: not-owner');
  });

  it('shows the address message on 400 bad-host', async () => {
    const h = await startApp('/overview', null);
    await h.answer('/api/v1/snapshot', { error: 'bad-host' }, 400);

    expect(h.el.querySelector('h1')?.textContent).toContain('Use the address cfgdash printed');
  });
});

describe('snapshot store', () => {
  it('shows the loading state, not the old snapshot, when the folder changes during a refresh', async () => {
    const h = await startApp('/overview');
    await h.click(h.buttons('Refresh')[0]);
    const refresh = h.http.expectOne((r) => isRefresh(r));

    await h.navigate('/overview?dir=D%3A%2Fws%2Fother');

    expect(refresh.cancelled).toBe(true);
    expect(h.text()).toContain("Reading this machine's harness");
    expect(h.text()).not.toContain('devbox');

    const req = await h.answer('/api/v1/snapshot', makeSnapshot({ hostname: 'otherbox', targetDir: 'D:/ws/other' }));
    expect(req.request.params.get('dir')).toBe('D:/ws/other');
    expect(h.text()).toContain('otherbox');
  });

  it('links "Back to the start folder" without dir but with the search query', async () => {
    const h = await startApp('/search?q=sonnet&dir=D%3A%2Fgone', null);
    await h.answer('/api/v1/snapshot', { error: 'bad-dir' }, 400);
    const link = [...h.el.querySelectorAll('a')].find((a) => a.textContent?.trim() === 'Back to the start folder');

    expect(link?.getAttribute('href')).toBe('/search?q=sonnet');
  });

  it('keeps the content and says "Couldn\'t refresh" when a refresh fails', async () => {
    const h = await startApp('/overview');
    await h.click(h.buttons('Refresh')[0]);
    const refresh = h.http.expectOne((r) => isRefresh(r));
    refresh.flush({}, { status: 500, statusText: 'Server Error' });
    await h.settle();

    expect(h.text()).toContain("Couldn't refresh");
    expect(h.text()).toContain('devbox');
    expect(h.text()).not.toContain("cfgdash isn't answering");

    await h.click(h.button('Try again'));
    expect(document.activeElement?.textContent?.trim()).toBe('devbox');
  });
});
