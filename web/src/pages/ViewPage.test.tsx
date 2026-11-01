import { fireEvent } from '@testing-library/react';
import type { KvBlock, Snapshot, TableBlock } from '../lib/models';
import { makeSnapshot } from '../test/fixtures';
import { startApp, type AppHarness } from '../test/harness';

const KV_ID = 'settings.effective/effective/';
const HOOK_ID = 'hooks.registered/hooks/';

const settingsKeys = () => [...document.querySelectorAll(`li[id^="${KV_ID}"]`)].map((li) => li.id.slice(KV_ID.length));
const hookRows = () => [...document.querySelectorAll(`tr[id^="${HOOK_ID}"]`)].map((tr) => tr.id.slice(HOOK_ID.length));
const pressed = (h: AppHarness, name: string) => h.button(name).getAttribute('aria-pressed');

function clone(): Snapshot {
  return JSON.parse(JSON.stringify(makeSnapshot())) as Snapshot;
}

function kvBlock(snapshot: Snapshot): KvBlock {
  return snapshot.sections.find((s) => s.collectorId === 'settings.effective')?.blocks[0] as KvBlock;
}

function hooksBlock(snapshot: Snapshot): TableBlock {
  return snapshot.sections.find((s) => s.collectorId === 'hooks.registered')?.blocks[0] as TableBlock;
}

async function filterBy(h: AppHarness, text: string): Promise<void> {
  const input = document.querySelector<HTMLInputElement>('input[aria-label="Filter rows"]');
  if (!input) {
    throw new Error('No filter input');
  }
  fireEvent.change(input, { target: { value: text } });
  await h.settle();
}

describe('view page', () => {
  it('shows an unavailable collector with its reason', async () => {
    const h = await startApp('/agents');

    expect(h.text()).toContain('agents.company-core: unavailable (pwsh not installed)');
  });

  it('shows sections of unknown view ids under Other, grouped by view id, with a fallback for an unknown block type', async () => {
    const h = await startApp('/other');

    expect(h.el.querySelector('main h1')?.textContent).toBe('Other');
    expect(h.el.querySelector('main h2')?.textContent).toBe('zeta');
    expect(h.text()).toContain('Zeta things');
    expect(h.text()).toContain('This block type (chart) needs a newer cfgdash UI.');
  });

  it('lists collectors that are not on this machine in one line with their reasons', async () => {
    const h = await startApp('/jobs');

    expect(h.text()).toContain('Not on this machine: jobs.systemd-user (Linux only)');
  });

  it('shows collector notes under the section title', async () => {
    const snapshot = makeSnapshot();
    snapshot.collectors.find((c) => c.id === 'hooks.registered')!.notes = ['Only user-level hooks were read.'];
    const h = await startApp('/hooks', snapshot);

    expect(h.text()).toContain('Only user-level hooks were read.');
  });

  it('says there is nothing here for an unknown view and links back to the overview with the folder', async () => {
    const h = await startApp('/nope?dir=D%3A%2Fws%2Fapp');

    expect(h.el.querySelector('main h1')?.textContent).toBe('Nothing here');
    const link = [...h.el.querySelectorAll('main a')].find((a) => a.textContent === 'Go to the overview');
    expect(link?.getAttribute('href')).toBe('/overview?dir=D%3A%2Fws%2Fapp');
  });

  describe('folder row', () => {
    it('shows the folder and "Change folder" on a view with a collector that needs a folder', async () => {
      const h = await startApp('/settings');

      expect(h.el.querySelector('main header')?.textContent).toContain('D:/ws/app');
      expect(h.buttons('Change folder')).toHaveLength(1);
    });

    it('shows no folder row on a view without such a collector', async () => {
      const h = await startApp('/hooks');

      expect(h.buttons('Change folder')).toHaveLength(0);
    });
  });

  describe('toolbar', () => {
    it('offers no filter on a view without kv or table blocks', async () => {
      await startApp('/instructions');

      expect(document.querySelector('input[aria-label="Filter rows"]')).toBeNull();
    });

    it('narrows kv rows by key or value, case-insensitively, and counts them', async () => {
      const h = await startApp('/settings');
      expect(settingsKeys()).toEqual(['model', 'permissions.allow', 'env.TOKEN_HEADER', 'theme']);

      await filterBy(h, 'PERMISSIONS');
      expect(settingsKeys()).toEqual(['permissions.allow']);

      await filterBy(h, 'Opus');
      expect(settingsKeys()).toEqual(['model']);
      expect(h.text()).toContain('1 of 4 rows');

      await filterBy(h, '');
      expect(settingsKeys()).toHaveLength(4);
    });

    it('says so when no row matches the filter', async () => {
      const h = await startApp('/settings');

      await filterBy(h, 'no-such-row');

      expect(settingsKeys()).toEqual([]);
      expect(h.text()).toContain('No rows match the filter.');
      expect(h.text()).toContain('0 of 4 rows');
    });

    it('narrows table rows across the tables of the view, case-insensitively', async () => {
      const h = await startApp('/hooks');

      await filterBy(h, 'ALPHA');

      expect(hookRows()).toEqual(['h-alpha']);
      expect(h.text()).toContain('1 of 58 rows');
    });

    it('never lets the text filter match a redacted value or its contributions', async () => {
      const h = await startApp('/settings');

      await filterBy(h, 'secret-value');
      expect(settingsKeys()).toEqual([]);

      await filterBy(h, '[redacted]');
      expect(settingsKeys()).toEqual([]);

      await filterBy(h, 'TOKEN_HEADER');
      expect(settingsKeys()).toEqual(['env.TOKEN_HEADER']);
    });

    it('keeps rows with an overridden contribution or an overridden flag with the Overridden chip', async () => {
      const snapshot = clone();
      kvBlock(snapshot).rows.push({ key: 'flagged', value: 'x', flags: ['overridden'] });
      const h = await startApp('/settings', snapshot);

      await h.click(h.button('Overridden'));

      expect(pressed(h, 'Overridden')).toBe('true');
      expect(settingsKeys()).toEqual(['model', 'env.TOKEN_HEADER', 'flagged']);
      expect(h.text()).toContain('3 of 5 rows');
    });

    it('keeps only redacted rows with the Redacted chip', async () => {
      const h = await startApp('/settings');

      await h.click(h.button('Redacted'));

      expect(settingsKeys()).toEqual(['env.TOKEN_HEADER']);
    });

    it('keeps only company rows with the Company chip and restores all rows when it is pressed again', async () => {
      const h = await startApp('/settings');

      await h.click(h.button('Company'));
      expect(settingsKeys()).toEqual(['theme']);

      await h.click(h.button('Company'));
      expect(pressed(h, 'Company')).toBe('false');
      expect(pressed(h, 'All')).toBe('true');
      expect(settingsKeys()).toHaveLength(4);
    });

    it('applies the chip to table rows by their flags', async () => {
      const snapshot = clone();
      hooksBlock(snapshot).rows[2].flags = ['company'];
      const h = await startApp('/hooks', snapshot);

      await h.click(h.button('Company'));

      expect(hookRows()).toEqual(['h-bravo']);
    });

    it('combines the text and the chip', async () => {
      const h = await startApp('/settings');

      await h.click(h.button('Overridden'));
      await filterBy(h, 'model');

      expect(settingsKeys()).toEqual(['model']);
    });
  });
});
