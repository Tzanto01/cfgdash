import { fireEvent, screen, waitFor } from '@testing-library/react';
import type { SearchHit } from '../lib/models';
import { FILE_USER } from '../test/fixtures';
import { startApp, type AppHarness } from '../test/harness';
import { answerWhenSent } from '../test/lazy';

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

const answerSearch = (h: AppHarness, body: object, status = 200) => answerWhenSent(h, '/api/v1/search', body, status);
const searchRequests = (h: AppHarness) => h.requests.filter((r) => r.url.pathname === '/api/v1/search');
const headings = (level: 'h2' | 'h3') => [...document.querySelectorAll(`main ${level}`)].map((n) => n.textContent?.replace(/\s+/g, ' ').trim());

async function open(url = '/search?q=sonnet') {
  const h = await startApp(url);
  const req = await answerSearch(h, { query: 'sonnet', results, truncated: false });
  return { h, req };
}

describe('search page', () => {
  it('requests the query and groups results by view in nav order with file contents last', async () => {
    const { req } = await open();

    expect(req.url.searchParams.get('q')).toBe('sonnet');
    expect(headings('h2')).toEqual(['Settings (2)', 'Instructions (1)', 'Hooks (1)', 'Other (1)', 'File contents (2)']);
  });

  it('uses the first q when the URL repeats it', async () => {
    const { h, req } = await open('/search?q=sonnet&q=opus');

    expect(req.url.searchParams.get('q')).toBe('sonnet');
    expect(h.text()).toContain('7 results for "sonnet"');
  });

  it('previews 5 hits per file and moves focus to the first revealed hit on "Show all"', async () => {
    const h = await startApp('/search?q=sonnet');
    const many = [1, 2, 3, 4, 5, 6, 7].map((line) => hit({ kind: 'file', fileId: FILE_USER, line, snippet: `sonnet ${line}` }));
    await answerSearch(h, { query: 'sonnet', results: many, truncated: false });
    const fileHits = () => h.buttons(/^line \d+ of /);

    expect(fileHits()).toHaveLength(5);
    await h.click(h.button('Show all 7 in this file'));

    expect(fileHits()).toHaveLength(7);
    expect(document.activeElement).toBe(fileHits()[5]);
  });

  it('groups file hits per file under its path with the match count', async () => {
    await open();

    expect(headings('h3')).toEqual(['C:/Users/user/.claude/settings.json (2 matches)']);
  });

  it('highlights the query with mark elements, case-insensitively', async () => {
    await open();
    const marks = [...document.querySelectorAll('main mark')].map((m) => m.textContent);

    expect(marks).toContain('Sonnet');
    expect(marks.every((m) => m?.toLowerCase() === 'sonnet')).toBe(true);
  });

  it('links a row hit to its view with the collector/block/item fragment', async () => {
    await open();
    const link = [...document.querySelectorAll<HTMLAnchorElement>('main a')].find((a) => a.textContent?.includes('Uses Sonnet here'));

    expect(link?.getAttribute('href')).toBe('/settings#settings.effective/effective/model');
  });

  it('carries the selected folder in a row hit link and in the request', async () => {
    const { req } = await open('/search?q=sonnet&dir=D%3A%2Fws%2Fapp');
    const link = [...document.querySelectorAll<HTMLAnchorElement>('main a')].find((a) => a.textContent?.includes('Uses Sonnet here'));

    expect(req.url.searchParams.get('dir')).toBe('D:/ws/app');
    expect(link?.getAttribute('href')).toBe('/settings?dir=D%3A%2Fws%2Fapp#settings.effective/effective/model');
  });

  it('asks for at least 2 characters and sends no request below that', async () => {
    const h = await startApp('/search?q=a');

    expect(await screen.findByText(/Type at least 2 characters/)).toBeTruthy();
    expect(searchRequests(h)).toHaveLength(0);
  });

  it('searches after typing once the debounce has passed and puts q in the URL', async () => {
    const h = await startApp('/search');
    fireEvent.change(await screen.findByLabelText('Search everything'), { target: { value: 'sonnet' } });

    const req = await answerSearch(h, { query: 'sonnet', results: [], truncated: false });

    expect(req.url.searchParams.get('q')).toBe('sonnet');
    expect(h.url()).toBe('/search?q=sonnet');
  });

  it('says the search failed and repeats the request on Try again', async () => {
    const h = await startApp('/search?q=sonnet');
    await answerSearch(h, {}, 422);

    expect(await screen.findByText("The search didn't work.")).toBeTruthy();
    await h.click(h.button('Try again'));
    await answerSearch(h, { query: 'sonnet', results: [hit({ itemId: 'model', snippet: 'Uses Sonnet here' })], truncated: false });

    expect(searchRequests(h)).toHaveLength(2);
    expect(h.text()).toContain('1 result for "sonnet"');
    expect(h.text()).not.toContain("The search didn't work.");
  });

  it('says when nothing matched', async () => {
    const h = await startApp('/search?q=zzz');
    await answerSearch(h, { query: 'zzz', results: [], truncated: false });

    expect(h.text()).toContain('No results for "zzz".');
  });

  it('tells the user when the results were cut at 200', async () => {
    const h = await startApp('/search?q=sonnet');
    await answerSearch(h, { query: 'sonnet', results: [hit({ snippet: 'sonnet' })], truncated: true });

    expect(h.text()).toContain('Showing the first 200; refine the search.');
  });

  it('does not show the truncated note for a complete result', async () => {
    const { h } = await open();

    await waitFor(() => expect(h.text()).toContain('7 results for "sonnet"'));
    expect(h.text()).not.toContain('Showing the first 200');
  });
});
