import { startApp } from '../test/harness';

describe('overview', () => {
  it('shows the identity, a count tile per visible view, the collectors and the target folder', async () => {
    const h = await startApp('/overview?dir=D%3A%2Fws%2Fapp');

    expect(h.el.querySelector('main h1')?.textContent).toBe('Overview');
    expect(h.text()).toContain('Test OS 1.0, x64');

    const links = [...h.el.querySelectorAll<HTMLAnchorElement>('main a')].map((a) => a.getAttribute('href'));
    expect(links).toContain('/settings?dir=D%3A%2Fws%2Fapp');

    expect(h.text()).toContain('5 of 7 active');
    expect(h.text()).toContain('pwsh not installed');
    expect(h.text()).toContain('Target folder');
    expect(h.text()).toContain('D:/ws/app');
  });
});
