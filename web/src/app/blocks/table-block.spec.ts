import { FILE_HOOK } from '../../testing/fixtures';
import { AppHarness, startApp } from '../../testing/harness';

function table(h: AppHarness, caption: string): HTMLTableElement {
  const found = [...h.el.querySelectorAll('table')].find((t) => t.querySelector('caption')?.textContent?.trim() === caption);
  if (!found) {
    throw new Error(`No table ${caption}`);
  }
  return found;
}

const firstCells = (t: HTMLTableElement) =>
  [...t.querySelectorAll('tbody tr')].map((tr) => tr.querySelector('td')?.textContent?.trim());

describe('table block', () => {
  it('sorts ascending, descending, then back to the original order and reports aria-sort', async () => {
    const h = await startApp('/hooks');
    const t = table(h, 'Registered hooks');
    const header = t.querySelector('th') as HTMLElement;
    const sortButton = header.querySelector('button') as HTMLButtonElement;

    expect(firstCells(t)).toEqual(['charlie.ps1', 'alpha.ps1', 'bravo.ps1']);
    expect(header.getAttribute('aria-sort')).toBeNull();

    await h.click(sortButton);
    expect(firstCells(t)).toEqual(['alpha.ps1', 'bravo.ps1', 'charlie.ps1']);
    expect(header.getAttribute('aria-sort')).toBe('ascending');

    await h.click(sortButton);
    expect(firstCells(t)).toEqual(['charlie.ps1', 'bravo.ps1', 'alpha.ps1']);
    expect(header.getAttribute('aria-sort')).toBe('descending');

    await h.click(sortButton);
    expect(firstCells(t)).toEqual(['charlie.ps1', 'alpha.ps1', 'bravo.ps1']);
    expect(header.getAttribute('aria-sort')).toBeNull();
  });

  it('opens the row file from the first cell and renders a second link to the same file as text', async () => {
    const h = await startApp('/hooks');
    const row = table(h, 'Registered hooks').querySelector('tbody tr') as HTMLElement;
    const cells = row.querySelectorAll('td');

    expect(cells[1].querySelector('button')).toBeNull();
    expect(cells[1].textContent?.trim()).toBe('user settings');

    await h.click(cells[0].querySelector('button') as HTMLElement);
    expect(h.router.url).toContain(`file=${FILE_HOOK}`);
  });

  it('previews 50 rows and shows all after "Show all N rows"', async () => {
    const h = await startApp('/hooks');
    const t = table(h, 'Many rows');

    expect(t.querySelectorAll('tbody tr')).toHaveLength(50);
    await h.click(h.button('Show all 55 rows'));
    expect(t.querySelectorAll('tbody tr')).toHaveLength(55);
    expect(h.buttons(/^Show all/)).toHaveLength(0);
  });
});
