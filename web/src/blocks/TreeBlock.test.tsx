import { startApp } from '../test/harness';

const hasNode = (label: string) => [...document.querySelectorAll('.node-label')].some((n) => n.textContent?.trim() === label);

describe('tree block', () => {
  it('shows the summary and toggles children with the disclosure', async () => {
    const h = await startApp('/instructions');
    expect(h.text()).toContain('4 files · 18.0 KB · about 4,600 tokens (estimate)');

    const toggle = h.button('Children of project/CLAUDE.md');
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(hasNode('@AGENTS.md')).toBe(true);
    expect(hasNode('deep/nested.md')).toBe(false);

    await h.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(hasNode('@AGENTS.md')).toBe(false);
  });
});
