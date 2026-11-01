import { act, fireEvent, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { DirSuggestion, SearchHit } from '../lib/models';
import { FILE_USER } from '../test/fixtures';
import { startApp, type AppHarness } from '../test/harness';
import { answerWhenSent, currentParams } from '../test/lazy';

const DIR_QUERY = '?dir=D%3A%2Fws%2Fapp';
const sibling: DirSuggestion = { label: 'Sibling project', path: 'D:/ws/other' };

const user = () => userEvent.setup({ pointerEventsCheck: 0 });
const palette = () => screen.queryByRole('dialog', { name: 'Search cfgdash' });
const option = (name: RegExp | string) => screen.getByRole('option', { name });
const dirsReply = (suggestions: DirSuggestion[] = []) => ({ path: 'D:/ws/app', parent: 'D:/ws', entries: [], suggestions });

const rowHit: SearchHit = {
  kind: 'row',
  view: 'settings',
  collectorId: 'settings.effective',
  blockId: 'effective',
  itemId: 'model',
  snippet: 'Uses Sonnet here'
};
const fileHit: SearchHit = {
  kind: 'file',
  view: 'settings',
  collectorId: 'settings.effective',
  blockId: 'effective',
  fileId: FILE_USER,
  line: 3,
  snippet: 'sonnet in a file'
};

async function openPalette(url = '/overview', suggestions: DirSuggestion[] = []) {
  const h = await startApp(url);
  await user().keyboard('{Control>}k{/Control}');
  await screen.findByRole('dialog', { name: 'Search cfgdash' });
  await answerWhenSent(h, '/api/v1/dirs', dirsReply(suggestions));
  return h;
}

async function searchFor(h: AppHarness, text: string, results: SearchHit[]) {
  fireEvent.change(screen.getByRole('combobox', { name: 'Search cfgdash' }), { target: { value: text } });
  return answerWhenSent(h, '/api/v1/search', { query: text, results, truncated: false });
}

describe('opening', () => {
  it.each([
    ['Ctrl+K', '{Control>}k{/Control}'],
    ['/', '/']
  ])('%s opens the palette from the page', async (_name, keys) => {
    await startApp('/overview');

    await user().keyboard(keys);

    expect(await screen.findByRole('dialog', { name: 'Search cfgdash' })).toBeTruthy();
  });

  it('ignores "/" while typing in the toolbar filter', async () => {
    await import('./CommandPalette');
    const h = await startApp('/hooks');
    const filter = screen.getByLabelText('Filter rows') as HTMLInputElement;
    await act(async () => filter.focus());

    await user().keyboard('/');
    await h.settle();

    expect(palette()).toBeNull();
    expect(filter.value).toBe('/');
  });

  it('focuses the page input instead on the search page', async () => {
    await startApp('/search');
    const input = await screen.findByLabelText('Search everything');

    await user().keyboard('/');

    expect(document.activeElement).toBe(input);
    expect(palette()).toBeNull();
  });

  it('closes on Escape and returns focus to the button that opened it', async () => {
    const h = await startApp('/overview');
    const trigger = h.button('Search');
    await act(async () => trigger.focus());
    await h.click(trigger);
    await screen.findByRole('dialog', { name: 'Search cfgdash' });

    await user().keyboard('{Escape}');

    await waitFor(() => expect(palette()).toBeNull());
    await waitFor(() => expect(document.activeElement).toBe(trigger));
  });
});

describe('entries', () => {
  it('opens a view and keeps the selected folder', async () => {
    const h = await openPalette(`/overview${DIR_QUERY}`);

    await h.click(option('Hooks'));

    expect(h.url()).toBe(`/hooks${DIR_QUERY}`);
    await waitFor(() => expect(palette()).toBeNull());
  });

  it('offers the folder suggestions from /api/v1/dirs and choosing one sets dir', async () => {
    const h = await openPalette('/overview', [sibling]);

    await h.click(option(/Sibling project/));

    expect(currentParams().get('dir')).toBe('D:/ws/other');
    await waitFor(() => expect(palette()).toBeNull());
  });

  it('offers no Health, Usage or Edit entry', async () => {
    await openPalette();
    const names = screen.getAllByRole('option').map((o) => o.textContent ?? '');

    expect(names.length).toBeGreaterThan(0);
    expect(names.filter((name) => /health|usage|edit/i.test(name))).toEqual([]);
  });
});

describe('search', () => {
  it('requests /api/v1/search with q and dir once typing has paused', async () => {
    const h = await openPalette(`/overview${DIR_QUERY}`);

    const req = await searchFor(h, 'sonnet', [rowHit]);

    expect(req.url.searchParams.get('q')).toBe('sonnet');
    expect(req.url.searchParams.get('dir')).toBe('D:/ws/app');
    expect(option(/Uses\s*Sonnet\s*here/)).toBeTruthy();
  });

  it('opens a row hit at its view with dir and the collector/block/item fragment', async () => {
    const h = await openPalette(`/overview${DIR_QUERY}`);
    await searchFor(h, 'sonnet', [rowHit]);

    await h.click(option(/Uses\s*Sonnet\s*here/));

    expect(h.url()).toBe(`/settings${DIR_QUERY}#settings.effective/effective/model`);
    await waitFor(() => expect(palette()).toBeNull());
  });

  it('opens a file hit in the file sheet at its line', async () => {
    const h = await openPalette();
    await searchFor(h, 'sonnet', [fileHit]);

    await h.click(option(/sonnet\s*in a file/));
    await answerWhenSent(h, `/api/v1/files/${FILE_USER}`, {
      id: FILE_USER,
      path: 'C:/Users/user/.claude/settings.json',
      kind: 'settings',
      contentType: 'text',
      content: 'one\ntwo\nthree\n',
      truncated: false
    });

    expect(currentParams().get('file')).toBe(FILE_USER);
    expect(currentParams().get('line')).toBe('3');
    const lines = [...document.querySelectorAll('ol[aria-label="File content"] li')];
    expect(lines.map((li) => li.classList.contains('tint-accent'))).toEqual([false, false, true]);
  });

  it('"Show all results" opens the search page with the query', async () => {
    const h = await openPalette();
    await searchFor(h, 'sonnet', [rowHit]);

    await h.click(option('Show all results'));

    expect(h.url()).toBe('/search?q=sonnet');
  });

  it('shows only the redacted marker in the preview of a redacted kv row', async () => {
    const h = await openPalette();
    await searchFor(h, 'TOKEN', [{ ...rowHit, itemId: 'env.TOKEN_HEADER', snippet: 'env.TOKEN_HEADER' }]);

    await user().hover(option(/env\.TOKEN_HEADER/));
    const preview = screen.getByRole('complementary', { name: 'Preview' });

    await waitFor(() => expect(preview.textContent).toContain('value redacted'));
    expect(preview.textContent).not.toMatch(/secret-value/);
  });
});
