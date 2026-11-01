import { makeFindings, makeSnapshot } from '../test/fixtures';
import { startApp, type RequestRecord } from '../test/harness';
import { answerWhenSent } from '../test/lazy';

const DIR_QUERY = '?dir=D%3A%2Fws%2Fapp';
const isFindings = (r: RequestRecord) => r.url.pathname === '/api/v1/findings';

const healthTile = () => [...document.querySelectorAll('section')].find((s) => s.querySelector('h2')?.textContent === 'Health')?.textContent ?? '';

describe('findings store', () => {
  it('requests /api/v1/findings with the snapshot dir only after the snapshot has loaded', async () => {
    const h = await startApp(`/overview${DIR_QUERY}`, null);
    await h.settle();

    expect(h.requests.filter(isFindings)).toHaveLength(0);
    await h.answer('/api/v1/snapshot', makeSnapshot());
    const request = await answerWhenSent(h, '/api/v1/findings', makeFindings());

    expect(request.url.searchParams.get('dir')).toBe('D:/ws/app');
    expect(h.requests.filter(isFindings)).toHaveLength(1);
  });

  it('requests the findings again after a refresh has loaded the new snapshot and shows the new counts', async () => {
    const h = await startApp(`/overview${DIR_QUERY}`);
    await answerWhenSent(h, '/api/v1/findings', makeFindings({ counts: { critical: 1, warning: 0, info: 0 } }));
    expect(healthTile()).toContain('1 critical');

    await h.click(h.button('Refresh'));
    expect(h.requests.filter(isFindings)).toHaveLength(1);
    await h.answer('/api/v1/snapshot', makeSnapshot());
    await answerWhenSent(h, '/api/v1/findings', makeFindings({ counts: { critical: 4, warning: 0, info: 0 } }));

    expect(h.requests.filter(isFindings)).toHaveLength(2);
    expect(healthTile()).toContain('4 critical');
    expect(healthTile()).not.toContain('1 critical');
  });

  it('abandons a pending findings request when a refreshed snapshot arrives', async () => {
    const h = await startApp(`/overview${DIR_QUERY}`);
    await h.settle();
    const first = h.pending(isFindings);

    await h.click(h.button('Refresh'));
    await h.answer('/api/v1/snapshot', makeSnapshot());
    await answerWhenSent(h, '/api/v1/findings', makeFindings({ counts: { critical: 3, warning: 0, info: 0 } }));

    expect(first?.aborted).toBe(true);
    expect(healthTile()).toContain('3 critical');
  });

  it('drops the findings of the old folder and requests the new folder with its own dir', async () => {
    const h = await startApp(`/overview${DIR_QUERY}`);
    await answerWhenSent(h, '/api/v1/findings', makeFindings({ counts: { critical: 7, warning: 0, info: 0 } }));
    expect(healthTile()).toContain('7 critical');

    await h.navigate('/overview?dir=D%3A%2Fws%2Fother');
    await h.answer('/api/v1/snapshot', makeSnapshot({ targetDir: 'D:/ws/other' }));

    const request = h.pending(isFindings);
    expect(request?.url.searchParams.get('dir')).toBe('D:/ws/other');
    expect(healthTile()).not.toContain('7 critical');
    expect(healthTile()).toContain('Checking...');

    await h.answer('/api/v1/findings', makeFindings({ dir: 'D:/ws/other', counts: { critical: 2, warning: 0, info: 0 } }));

    expect(healthTile()).toContain('2 critical');
  });
});
