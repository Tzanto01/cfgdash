import { startApp } from '../test/harness';

const root = document.documentElement;

describe('theme toggle', () => {
  it('cycles system, light, dark and back, setting and clearing data-theme on the document', async () => {
    const h = await startApp('/overview');
    expect(root.hasAttribute('data-theme')).toBe(false);

    await h.click(h.button('Theme: system. Switch to light'));
    expect(root.getAttribute('data-theme')).toBe('light');

    await h.click(h.button('Theme: light. Switch to dark'));
    expect(root.getAttribute('data-theme')).toBe('dark');

    await h.click(h.button('Theme: dark. Switch to system'));
    expect(root.hasAttribute('data-theme')).toBe(false);
    expect(h.buttons('Theme: system. Switch to light')).toHaveLength(1);
  });

  it('persists light and dark under cfgdash-theme and forgets the choice for system', async () => {
    const h = await startApp('/overview');

    await h.click(h.button('Theme: system. Switch to light'));
    expect(localStorage.getItem('cfgdash-theme')).toBe('light');

    await h.click(h.button('Theme: light. Switch to dark'));
    expect(localStorage.getItem('cfgdash-theme')).toBe('dark');

    await h.click(h.button('Theme: dark. Switch to system'));
    expect(localStorage.getItem('cfgdash-theme')).toBeNull();
  });

  it('starts in the stored theme', async () => {
    localStorage.setItem('cfgdash-theme', 'dark');

    const h = await startApp('/overview');

    expect(root.getAttribute('data-theme')).toBe('dark');
    expect(h.buttons('Theme: dark. Switch to system')).toHaveLength(1);
  });

  it('treats an unknown stored value as system', async () => {
    localStorage.setItem('cfgdash-theme', 'blue');

    const h = await startApp('/overview');

    expect(root.hasAttribute('data-theme')).toBe(false);
    expect(h.buttons('Theme: system. Switch to light')).toHaveLength(1);
  });
});
