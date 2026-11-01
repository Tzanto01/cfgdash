import { FILE_HOOK } from '../test/fixtures';
import { startApp } from '../test/harness';

const firstRowId = () => document.querySelector('table tbody tr')?.id;

describe('table block', () => {
  it('cycles the sort ascending, descending and back to the original order', async () => {
    const h = await startApp('/hooks');
    const sort = h.button('Script');
    const header = sort.closest('th') as HTMLElement;

    await h.click(sort);
    expect(header.getAttribute('aria-sort')).toBe('ascending');
    expect(firstRowId()).toBe('hooks.registered/hooks/h-alpha');

    await h.click(sort);
    expect(header.getAttribute('aria-sort')).toBe('descending');
    expect(firstRowId()).toBe('hooks.registered/hooks/h-charlie');

    await h.click(sort);
    expect(header.hasAttribute('aria-sort')).toBe(false);
    expect(firstRowId()).toBe('hooks.registered/hooks/h-charlie');
  });

  it('opens the row file from the first cell', async () => {
    const h = await startApp('/hooks');
    await h.click(h.button('charlie.ps1'));

    expect(h.url()).toContain(`file=${FILE_HOOK}`);
  });
});
