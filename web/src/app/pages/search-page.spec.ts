import { FILE_USER } from '../../testing/fixtures';
import { SearchHit } from '../models';
import { startApp } from '../../testing/harness';

const hit = (over: Partial<SearchHit>): SearchHit => ({
  kind: 'row',
  view: 'settings',
  collectorId: 'settings.effective',
  blockId: 'effective',
  snippet: 'x',
  ...over
});

const results: SearchHit[] = [
  hit({ view: 'hooks', collectorId: 'hooks.registered', blockId: 'hooks', itemId: 'h-alpha', snippet: 'runs Sonnet hook' }),
  hit({ kind: 'file', view: 'settings', fileId: FILE_USER, line: 3, snippet: '"model": "sonnet"' }),
  hit({ view: 'zeta', collectorId: 'zeta.things', blockId: 'c1', snippet: 'a sonnet chart' }),
  hit({ itemId: 'model', snippet: 'Uses Sonnet here' }),
  hit({ kind: 'file', view: 'settings', fileId: FILE_USER, line: 9, snippet: 'sonnet again' }),
  hit({ kind: 'file', view: 'settings', line: 4, snippet: 'sonnet in a file without id' }),
  hit({ kind: 'node', view: 'instructions', collectorId: 'instructions.chain', blockId: 'chain', itemId: 'n2.1', snippet: 'İİ sonnet node' })
];

describe('search page', () => {
  async function open() {
    const h = await startApp('/search?q=sonnet');
    const req = await h.answer('/api/v1/search', { query: 'sonnet', results, truncated: false });
    return { h, req };
  }

  it('requests the query and groups results by view in nav order with file contents last', async () => {
    const { h, req } = await open();

    expect(req.request.params.get('q')).toBe('sonnet');
    const headings = [...h.el.querySelectorAll('.search-results h2')].map((n) => n.textContent?.trim());
    expect(headings).toEqual(['Settings (2)', 'Instructions (1)', 'Hooks (1)', 'Other (1)', 'File contents (2)']);
  });

  it('uses the first q when the URL repeats it', async () => {
    const h = await startApp('/search?q=sonnet&q=opus');
    const req = await h.answer('/api/v1/search', { query: 'sonnet', results, truncated: false });

    expect(req.request.params.get('q')).toBe('sonnet');
    expect(h.text()).toContain('7 results for "sonnet"');
  });

  it('previews 5 hits per file and moves focus to the first revealed hit on "Show all"', async () => {
    const h = await startApp('/search?q=sonnet');
    const many = [1, 2, 3, 4, 5, 6, 7].map((line) => hit({ kind: 'file', fileId: FILE_USER, line, snippet: `sonnet ${line}` }));
    await h.answer('/api/v1/search', { query: 'sonnet', results: many, truncated: false });
    const fileHits = () => [...h.el.querySelectorAll<HTMLButtonElement>('.file-hits button.hit')];

    expect(fileHits().length).toBe(5);
    await h.click(h.button('Show all 7 in this file'));

    expect(fileHits().length).toBe(7);
    expect(document.activeElement).toBe(fileHits()[5]);
  });

  it('groups file hits per file under its path', async () => {
    const { h } = await open();
    const fileHeading = h.el.querySelector('.search-results h3');

    expect(fileHeading?.textContent?.replace(/\s+/g, ' ').trim()).toBe('C:/Users/user/.claude/settings.json (2 matches)');
  });

  it('highlights the query with mark elements, case-insensitively', async () => {
    const { h } = await open();
    const marks = [...h.el.querySelectorAll('.search-results mark')].map((m) => m.textContent);

    expect(marks).toContain('Sonnet');
    expect(marks.every((m) => m?.toLowerCase() === 'sonnet')).toBe(true);
  });

  it('links a row hit to its view with the collector/block/item fragment', async () => {
    const { h } = await open();
    const link = [...h.el.querySelectorAll<HTMLAnchorElement>('.search-results a.hit')].find((a) =>
      a.textContent?.includes('Uses Sonnet here')
    );

    expect(link?.getAttribute('href')).toBe('/settings#settings.effective/effective/model');
  });
});
