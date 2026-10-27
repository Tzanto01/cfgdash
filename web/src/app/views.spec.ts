import { startApp } from '../testing/harness';

describe('views', () => {
  it('lists nav views in contract order, adds Other for unknown ids and hides notPresent-only views', async () => {
    const h = await startApp('/overview');
    const labels = [...h.el.querySelectorAll('nav[aria-label="Views"] .nav-label')].map((n) => n.textContent?.trim());

    expect(labels).toEqual(['Overview', 'Settings', 'Instructions', 'Agents', 'Hooks', 'Other']);
    expect(labels).not.toContain('Jobs');
  });

  it('sends an unknown deep path to the overview', async () => {
    const h = await startApp('/a/b');

    expect(h.router.url).toBe('/overview');
    expect(h.el.querySelector('h1')?.textContent?.trim()).toBe('devbox');
  });

  it('shows an unavailable collector with its reason', async () => {
    const h = await startApp('/agents');

    expect(h.text()).toContain('agents.company-core: unavailable (pwsh not installed)');
  });

  it('shows sections of unknown view ids under Other and a fallback for an unknown block type', async () => {
    const h = await startApp('/other');

    expect(h.el.querySelector('h1')?.textContent?.trim()).toBe('Other');
    expect(h.el.querySelector('h2')?.textContent?.trim()).toBe('zeta');
    expect(h.text()).toContain('Zeta things');
    expect(h.text()).toContain('This block type (chart) needs a newer cfgdash UI.');
  });
});
