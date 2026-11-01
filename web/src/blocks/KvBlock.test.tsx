import { FILE_LOCAL } from '../test/fixtures';
import { startApp } from '../test/harness';

const row = (key: string) => document.getElementById(`settings.effective/effective/${key}`) as HTMLElement;
const lines = (element: HTMLElement) => [...element.querySelectorAll('ol > li')].map((li) => li.textContent?.replace(/\s+/g, ' ').trim());
const lineages = (element: HTMLElement) => [...element.querySelectorAll('[role="img"]')];

describe('kv block', () => {
  it('shows the value, the source chip and the overridden contribution without a click', async () => {
    const h = await startApp('/settings');
    const model = row('model');

    expect(model.textContent).toContain('opus');
    expect(model.textContent?.replace(/\s+/g, ' ')).toContain('Overrides sonnet from user');
    expect(model.querySelector('ol')).toBeNull();
    expect(h.button('model').getAttribute('aria-expanded')).toBe('false');
    expect(h.button('project (local), open D:/ws/app/.claude/settings.local.json')).toBeDefined();
  });

  it('opens the source file when the chip is clicked', async () => {
    const h = await startApp('/settings');

    await h.click(h.button('project (local), open D:/ws/app/.claude/settings.local.json'));

    expect(h.url()).toContain(`file=${FILE_LOCAL}`);
  });

  it('expands the key into applied and overridden entries with their sources, and collapses again', async () => {
    const h = await startApp('/settings');
    const key = h.button('model');

    await h.click(key);
    expect(key.getAttribute('aria-expanded')).toBe('true');
    const entries = lines(row('model'));
    expect(entries).toHaveLength(2);
    expect(entries[0]).toMatch(/applied.*opus.*project \(local\)/);
    expect(entries[1]).toMatch(/overridden.*sonnet.*user/);

    await h.click(key);
    expect(key.getAttribute('aria-expanded')).toBe('false');
    expect(row('model').querySelector('ol')).toBeNull();
  });

  it('lists merged entries of a list value with their sources', async () => {
    const h = await startApp('/settings');

    await h.click(h.button('permissions.allow'));
    const entries = lines(row('permissions.allow'));

    expect(entries).toHaveLength(2);
    expect(entries[0]).toMatch(/merged.*Bash\(npm test \*\).*project/);
    expect(entries[1]).toMatch(/merged.*Read\(\.\/docs\/\*\*\).*user/);
  });

  it('gives a row without contributions no expand button', async () => {
    const h = await startApp('/settings');

    expect(h.buttons('theme')).toHaveLength(0);
    expect(row('theme').textContent).toContain('theme');
  });

  it('renders flags as badges with their text, including unknown ones', async () => {
    await startApp('/settings');

    expect([...row('theme').querySelectorAll('ul > li')].map((li) => li.textContent?.trim())).toEqual(['company', 'brand-new-flag']);
    expect([...row('env.TOKEN_HEADER').querySelectorAll('ul > li')].map((li) => li.textContent?.trim())).toEqual(['redacted']);
  });

  describe('lineage', () => {
    it('labels the strip with the applied and overridden sources', async () => {
      await startApp('/settings');

      expect(lineages(row('model'))[0].getAttribute('aria-label')).toBe('Effective from project (local); overridden: user');
    });

    it('labels merged sources as merged', async () => {
      await startApp('/settings');

      expect(lineages(row('permissions.allow'))[0].getAttribute('aria-label')).toBe('Merged from project, user');
    });

    it('adds the route with the same label when the row is expanded', async () => {
      const h = await startApp('/settings');
      expect(lineages(row('model'))).toHaveLength(1);

      await h.click(h.button('model'));

      expect(lineages(row('model')).map((l) => l.getAttribute('aria-label'))).toEqual([
        'Effective from project (local); overridden: user',
        'Effective from project (local); overridden: user'
      ]);
    });

    it('shows the values only in the tooltip of a row that is not redacted', async () => {
      await startApp('/settings');

      expect(lineages(row('model'))[0].getAttribute('title')).toBe('project (local): opus\nuser: sonnet');
    });
  });

  describe('redaction', () => {
    it('shows the marker and counts the entries, but never prints a contribution value of a redacted row', async () => {
      const h = await startApp('/settings');
      const redacted = row('env.TOKEN_HEADER');
      expect(redacted.textContent).toContain('[redacted]');

      await h.click(h.button('env.TOKEN_HEADER'));

      expect(lines(redacted)).toHaveLength(2);
      expect(lines(redacted)[0]).toMatch(/applied.*\[redacted\]/);
      expect(lines(redacted)[1]).toMatch(/overridden.*\[redacted\]/);
      expect(redacted.textContent).toMatch(/Overrides/);
    });

    it('keeps contribution values out of the whole page, including titles and labels, collapsed or expanded', async () => {
      const h = await startApp('/settings');
      expect(h.el.innerHTML).not.toContain('secret-value');

      await h.click(h.button('env.TOKEN_HEADER'));

      expect(h.el.innerHTML).not.toContain('secret-value');
    });

    it('gives the lineage of a redacted row a label without values and no tooltip', async () => {
      const h = await startApp('/settings');
      await h.click(h.button('env.TOKEN_HEADER'));

      const strips = lineages(row('env.TOKEN_HEADER'));

      expect(strips).toHaveLength(2);
      for (const strip of strips) {
        expect(strip.getAttribute('aria-label')).toBe('Effective from project (local); overridden: user');
        expect(strip.hasAttribute('title')).toBe(false);
      }
    });
  });
});
