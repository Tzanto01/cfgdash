import { makeSnapshot } from '../test/fixtures';
import { startApp } from '../test/harness';

describe('access problems', () => {
  it('asks to open the printed link on 401 and focuses the heading', async () => {
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

  it('replaces the whole shell with the panel, with no navigation', async () => {
    const h = await startApp('/overview', null);
    await h.answer('/api/v1/snapshot', { error: 'unauthenticated' }, 401);

    expect(h.el.querySelector('nav[aria-label="Views"]')).toBeNull();
    expect(h.buttons('Refresh')).toHaveLength(0);
  });

  it('loads the snapshot again on Try again and leaves the panel when it succeeds', async () => {
    const h = await startApp('/overview', null);
    await h.answer('/api/v1/snapshot', { error: 'unauthenticated' }, 401);

    await h.click(h.button('Try again'));
    await h.answer('/api/v1/snapshot', makeSnapshot());

    expect(h.requests.filter((r) => r.url.pathname === '/api/v1/snapshot')).toHaveLength(2);
    expect(h.el.querySelector('main h1')?.textContent).toBe('Overview');
  });
});
