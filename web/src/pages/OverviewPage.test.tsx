import { makeSnapshot } from '../test/fixtures';
import { startApp } from '../test/harness';

const SELECTED = 'D%3A%2Fws%2Fapp';

function contextRow(name: string): HTMLElement {
  const section = [...document.querySelectorAll('section')].find((s) => s.querySelector('h2')?.textContent === 'Context paths');
  const row = [...(section?.querySelectorAll('li') ?? [])].find((li) => li.querySelector('span')?.textContent === name);
  if (!row) {
    throw new Error(`No context row ${name}`);
  }
  return row as HTMLElement;
}

const tile = (label: string) =>
  [...document.querySelectorAll<HTMLAnchorElement>('main a')].find((a) => a.textContent?.startsWith(label));

describe('overview', () => {
  it('titles the page Overview and keeps the hostname and platform in the Identity tile', async () => {
    const h = await startApp('/overview');
    const identity = [...h.el.querySelectorAll('section')].find((s) => s.querySelector('h2')?.textContent === 'Identity');

    expect(h.el.querySelector('main h1')?.textContent).toBe('Overview');
    expect(identity?.textContent).toContain('devbox');
    expect(identity?.textContent).toContain('Test OS 1.0, x64');
  });

  it('links a count tile for each visible view except the overview, carrying the folder, and none for hidden views', async () => {
    await startApp(`/overview?dir=${SELECTED}`);

    expect(tile('Settings')?.getAttribute('href')).toBe(`/settings?dir=${SELECTED}`);
    expect(tile('Hooks')?.getAttribute('href')).toBe(`/hooks?dir=${SELECTED}`);
    expect(tile('Other')?.getAttribute('href')).toBe(`/other?dir=${SELECTED}`);
    expect(tile('Jobs')).toBeUndefined();
    expect(tile('Overview')).toBeUndefined();
  });

  it('counts the items of each view in its tile and flags unavailable sources', async () => {
    await startApp('/overview');

    expect(tile('Settings')?.textContent).toMatch(/^Settings4$/);
    expect(tile('Hooks')?.textContent).toMatch(/^Hooks58$/);
    expect(tile('Agents')?.textContent).toMatch(/1 unavailable/);
  });

  it('counts active collectors and lists the others with their state and reason', async () => {
    const h = await startApp('/overview');

    expect(h.text()).toContain('5 of 7 active');
    expect(h.text()).toContain('pwsh not installed');
    expect(h.text()).toContain('Linux only');
    expect(h.text()).toContain('not on this machine');
  });

  describe('context paths', () => {
    it('shows the target folder with its source and no missing badge', async () => {
      await startApp('/overview');
      const row = contextRow('Target folder');

      expect(row.textContent).toContain('D:/ws/app');
      expect(row.textContent).toContain('selected folder');
      expect(row.textContent).not.toContain('missing');
    });

    it('shows each path with its source and a "missing" badge only for a path that does not exist', async () => {
      const snapshot = makeSnapshot();
      snapshot.context.paths = [
        { key: 'userHome', value: 'C:/Users/user/.claude', source: 'home directory', exists: true },
        { key: 'managedSettings', value: 'C:/Managed/claude', source: 'OS default', exists: false },
        { key: 'projectRoot', value: null, source: null, exists: true }
      ];
      await startApp('/overview', snapshot);

      expect(contextRow('userHome').textContent).toContain('C:/Users/user/.claude');
      expect(contextRow('userHome').textContent).toContain('home directory');
      expect(contextRow('userHome').textContent).not.toContain('missing');
      expect(contextRow('managedSettings').textContent).toContain('C:/Managed/claude');
      expect(contextRow('managedSettings').textContent).toContain('OS default');
      expect(contextRow('managedSettings').textContent).toContain('missing');
      expect(contextRow('projectRoot').textContent).toContain('not set');
      expect(contextRow('projectRoot').textContent).not.toContain('missing');
    });
  });
});
