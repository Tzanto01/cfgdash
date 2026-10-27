import { startApp, AppHarness } from '../../testing/harness';

function kvRow(h: AppHarness, key: string): HTMLElement {
  const row = [...h.el.querySelectorAll('li')].find((li) => li.querySelector('.kv-key')?.textContent?.trim() === key);
  if (!row) {
    throw new Error(`No row ${key}`);
  }
  return row;
}

function rowButton(row: HTMLElement, name: RegExp): HTMLButtonElement {
  const button = [...row.querySelectorAll('button')].find((b) => name.test((b.getAttribute('aria-label') ?? b.textContent ?? '').trim()));
  if (!button) {
    throw new Error(`No button ${name}`);
  }
  return button;
}

describe('settings provenance', () => {
  it('shows the value, its source chip and the overridden contribution without a click', async () => {
    const h = await startApp('/settings');
    const row = kvRow(h, 'model');

    expect(row.textContent).toContain('opus');
    const chip = rowButton(row, /^project \(local\), open /);
    expect(chip.getAttribute('aria-label')).toBe('project (local), open D:/ws/app/.claude/settings.local.json');
    expect(row.textContent?.replace(/\s+/g, ' ')).toContain('Overrides sonnet from user');
    expect(row.querySelector('ol')).toBeNull();
  });

  it('opens the source file when the chip is clicked', async () => {
    const h = await startApp('/settings');
    await h.click(rowButton(kvRow(h, 'model'), /^project \(local\), open /));

    expect(h.router.url).toContain('file=a1b2c3d4e5f60718');
  });

  it('reveals applied, overridden and merged entries with their sources in the layers disclosure', async () => {
    const h = await startApp('/settings');

    const model = kvRow(h, 'model');
    await h.click(rowButton(model, /^2 layers$/));
    const layers = [...model.querySelectorAll('ol > li')].map((li) => li.textContent?.replace(/\s+/g, ' ').trim());
    expect(layers).toHaveLength(2);
    expect(layers[0]).toMatch(/applied.*opus.*project \(local\)/);
    expect(layers[1]).toMatch(/overridden.*sonnet.*user/);

    const permissions = kvRow(h, 'permissions.allow');
    await h.click(rowButton(permissions, /^2 layers$/));
    const merged = [...permissions.querySelectorAll('ol > li')].map((li) => li.textContent?.replace(/\s+/g, ' ').trim());
    expect(merged[0]).toMatch(/merged.*Bash\(npm test \*\).*project/);
    expect(merged[1]).toMatch(/merged.*Read\(\.\/docs\/\*\*\).*user/);
  });

  it('shows the redacted marker and never prints a contribution value of a redacted row', async () => {
    const h = await startApp('/settings');
    const row = kvRow(h, 'env.TOKEN_HEADER');
    await h.click(rowButton(row, /^2 layers$/));

    expect(row.textContent).toContain('[redacted]');
    expect(row.textContent).toMatch(/Overrides/);
    expect(h.el.textContent).not.toContain('secret-value-old');
    expect(h.el.textContent).not.toContain('secret-value-new');
  });

  it('renders flags as badges with their text, including unknown ones', async () => {
    const h = await startApp('/settings');
    const badges = [...kvRow(h, 'theme').querySelectorAll('li')].map((li) => li.textContent?.trim());

    expect(badges).toEqual(['company', 'brand-new-flag']);
    expect([...kvRow(h, 'env.TOKEN_HEADER').querySelectorAll('li.badge')].map((li) => li.textContent?.trim())).toContain(
      'redacted'
    );
  });
});
