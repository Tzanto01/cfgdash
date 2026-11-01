import { screen, waitFor, within } from '@testing-library/react';
import type { FindingsResponse } from '../lib/models';
import { FILE_HOOK, makeFindings } from '../test/fixtures';
import { startApp, type AppHarness } from '../test/harness';
import { answerWhenSent, currentParams } from '../test/lazy';

const DIR_QUERY = '?dir=D%3A%2Fws%2Fapp';
const isFindings = (r: { url: URL }) => r.url.pathname === '/api/v1/findings';

async function openHealth(response: FindingsResponse = makeFindings()): Promise<AppHarness> {
  const h = await startApp(`/health${DIR_QUERY}`);
  await answerWhenSent(h, '/api/v1/findings', response);
  await screen.findByRole('table', { name: 'Findings' });
  return h;
}

const rows = () => [...document.querySelectorAll('table tbody tr')].map((tr) => [...tr.querySelectorAll('td')].map((td) => td.textContent?.trim()));
const mainText = () => document.querySelector('main')?.textContent ?? '';
const sheet = () => screen.queryByRole('dialog');

async function openSheet(h: AppHarness, title: string): Promise<HTMLElement> {
  await h.click(h.button(title));
  return screen.findByRole('dialog', { name: title });
}

describe('health page', () => {
  it('lists each finding with its severity word, title and where it is', async () => {
    await openHealth();

    expect(rows()).toEqual([
      ['Critical', 'Hook script not found', 'Hooks › Registered hooks › alpha.ps1'],
      ['Warning', 'Setting overridden', 'Effective settings › Effective settings › model'],
      ['Info', 'Collector unavailable', 'agents.company-core']
    ]);
  });

  it('filters by severity with pressed chips, says how many are shown and clears the filter on a second click', async () => {
    const h = await openHealth();

    await h.click(h.button('Warning'));

    expect(h.button('Warning').getAttribute('aria-pressed')).toBe('true');
    expect(h.button('All').getAttribute('aria-pressed')).toBe('false');
    expect(rows().map((r) => r[1])).toEqual(['Setting overridden']);
    expect(mainText()).toContain('1 of 3 findings');

    await h.click(h.button('Warning'));

    expect(h.button('All').getAttribute('aria-pressed')).toBe('true');
    expect(rows()).toHaveLength(3);
    expect(mainText()).not.toContain('1 of 3 findings');
  });

  it('says no findings match when the chosen severity has none', async () => {
    const h = await openHealth(makeFindings({ counts: { critical: 1, warning: 0, info: 0 }, findings: makeFindings().findings.slice(0, 1) }));

    await h.click(h.button('Info'));

    expect(rows()).toEqual([]);
    expect(h.text()).toContain('No findings match the filter.');
  });

  it('opens a finding in a dialog with its detail, rule and location', async () => {
    const h = await openHealth();

    const dialog = await openSheet(h, 'Hook script not found');

    expect(dialog.textContent).toContain('alpha.ps1 does not exist.');
    expect(dialog.textContent).toContain('missing-script');
    expect(dialog.textContent).toContain('Critical');
    expect(dialog.textContent).toContain('Hooks › Registered hooks › alpha.ps1');
  });

  it.each([
    ['Hook script not found', 'Show in Hooks', `/hooks${DIR_QUERY}#hooks.registered/hooks/h-alpha`],
    ['Setting overridden', 'Show in Settings', `/settings${DIR_QUERY}#settings.effective/effective/model`],
    ['Collector unavailable', 'Show in Agents', `/agents${DIR_QUERY}`]
  ])('"%s" links to its evidence: %s goes to %s, and navigating closes the sheet', async (title, linkName, href) => {
    const h = await openHealth();
    const dialog = await openSheet(h, title);
    const link = within(dialog).getByRole('link', { name: linkName });

    expect(link.getAttribute('href')).toBe(href);
    await h.click(link);

    expect(h.url()).toBe(href);
    await waitFor(() => expect(sheet()).toBeNull());
  });

  it('opens the evidence file from the sheet: sets file in the URL and closes the finding', async () => {
    const h = await openHealth();
    await openSheet(h, 'Hook script not found');

    await h.click(h.button('Open file'));

    expect(currentParams().get('file')).toBe(FILE_HOOK);
    expect(window.location.pathname).toBe('/health');
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Hook script not found' })).toBeNull());
  });

  it('offers no Open file button for evidence without a file', async () => {
    const h = await openHealth();
    const dialog = await openSheet(h, 'Setting overridden');

    expect(within(dialog).queryByRole('button', { name: 'Open file' })).toBeNull();
  });

  it('says the findings could not load and requests them again on Try again', async () => {
    const h = await startApp(`/health${DIR_QUERY}`);
    await answerWhenSent(h, '/api/v1/findings', {}, 500);
    await screen.findByText("Couldn't load findings");

    await h.click(h.button('Try again'));
    expect(h.requests.filter(isFindings)).toHaveLength(2);
    await h.answer('/api/v1/findings', makeFindings());

    expect(await screen.findByRole('table', { name: 'Findings' })).toBeTruthy();
    expect(h.text()).not.toContain("Couldn't load findings");
  });

  it('says nothing needs attention when there are no findings', async () => {
    const h = await startApp(`/health${DIR_QUERY}`);
    await answerWhenSent(h, '/api/v1/findings', makeFindings({ counts: { critical: 0, warning: 0, info: 0 }, findings: [] }));

    expect(await screen.findByText('No findings. Nothing in this snapshot needs attention.')).toBeTruthy();
    expect(screen.queryByRole('table', { name: 'Findings' })).toBeNull();
  });
});
