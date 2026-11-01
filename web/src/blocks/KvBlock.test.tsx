import { FILE_LOCAL } from '../test/fixtures';
import { startApp } from '../test/harness';

const row = (key: string) => document.getElementById(`settings.effective/effective/${key}`) as HTMLElement;

describe('kv block', () => {
  it('shows the value, the source chip and the overridden value without expanding', async () => {
    const h = await startApp('/settings');
    const model = row('model');

    expect(model.textContent).toContain('opus');
    expect(model.textContent?.replace(/\s+/g, ' ')).toContain('Overrides sonnet from user');
    expect(model.querySelector('ol')).toBeNull();

    await h.click(h.button('project (local), open D:/ws/app/.claude/settings.local.json'));
    expect(h.url()).toContain(`file=${FILE_LOCAL}`);
  });

  it('expands a row into its contributions and never prints the values of a redacted row', async () => {
    const h = await startApp('/settings');

    const key = h.button('model');
    await h.click(key);
    expect(key.getAttribute('aria-expanded')).toBe('true');
    const lines = [...row('model').querySelectorAll('ol > li')].map((li) => li.textContent?.replace(/\s+/g, ' '));
    expect(lines[0]).toMatch(/applied.*opus.*project \(local\)/);
    expect(lines[1]).toMatch(/overridden.*sonnet.*user/);

    await h.click(h.button('env.TOKEN_HEADER'));
    const redacted = row('env.TOKEN_HEADER');
    expect(redacted.querySelectorAll('ol > li')).toHaveLength(2);
    expect(redacted.innerHTML).not.toContain('secret-value');
    expect(h.el.innerHTML).not.toContain('secret-value');
  });
});
