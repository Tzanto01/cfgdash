import { act, fireEvent, screen } from '@testing-library/react';
import { FILE_CLAUDE } from '../test/fixtures';
import { startApp, type AppHarness, type RequestRecord } from '../test/harness';
import { answerWhenSent } from '../test/lazy';

const HEALTH_EDITS = { status: 'ok', version: '1.0.0', schemaVersion: 1, edits: true };
const FILE_URL = `/api/v1/files/${FILE_CLAUDE}`;
const PROPOSE_URL = `/api/v2/files/${FILE_CLAUDE}/proposals`;
const SHA = 'a'.repeat(64);
const diff = ['--- a/D:/ws/app/CLAUDE.md', '+++ b/D:/ws/app/CLAUDE.md', '@@ -1,2 +1,3 @@', ' # App', ' Keep changes small.', '+Test first.', ''].join('\n');

const served = (edit: object, content = '# App\r\nKeep changes small.\r\n') => ({
  id: FILE_CLAUDE,
  path: 'D:/ws/app/CLAUDE.md',
  kind: 'instruction',
  contentType: 'markdown',
  content,
  truncated: false,
  edit
});
const proposal = (id: string) => ({ id, expiresAt: '2030-01-01T00:00:00Z', files: [{ path: 'D:/ws/app/CLAUDE.md', scope: 'file', sha256Before: SHA, diff }], confirm: `confirm-${id}` });
const body = (r: RequestRecord) => JSON.parse(r.body ?? 'null') as unknown;
const editor = () => screen.getByRole('textbox', { name: 'Edit CLAUDE.md' }) as HTMLTextAreaElement;

async function openFile(edit: object, edits = true, content?: string): Promise<AppHarness> {
  const h = await startApp(`/instructions?dir=D%3A%2Fws%2Fapp&file=${FILE_CLAUDE}`);
  await h.answer('/api/v1/health', edits ? HEALTH_EDITS : { ...HEALTH_EDITS, edits: false });
  await answerWhenSent(h, FILE_URL, served(edit, content));
  return h;
}

async function typeText(text: string): Promise<void> {
  await act(async () => {
    fireEvent.change(editor(), { target: { value: text } });
  });
}

describe('file edit mode', () => {
  it('proposes the edited text with the file line endings, applies it with its confirm and undoes it through the audit revert', async () => {
    const h = await openFile({ editable: true, sha256: SHA, gitTracked: true });
    await h.click(h.button('Edit'));
    await screen.findByRole('textbox', { name: 'Edit CLAUDE.md' });
    expect(editor().value).toBe('# App\nKeep changes small.\n');
    expect(h.text()).toContain('This file is tracked by git.');

    await typeText('# App\nKeep changes small.\nTest first.\n');
    await h.click(h.button('Review changes'));
    const propose = await h.answer(PROPOSE_URL, proposal('p1'));
    expect(propose.method).toBe('POST');
    expect(propose.headers['x-cfgdash-request']).toBe('1');
    expect(propose.url.searchParams.get('dir')).toBe('D:/ws/app');
    expect(body(propose)).toEqual({ content: '# App\r\nKeep changes small.\r\nTest first.\r\n', sha256Before: SHA });
    expect(h.text()).toContain('Test first.');

    await h.click(h.button('Apply change'));
    const apply = await h.answer('/api/v2/proposals/p1/apply', { applied: true, auditId: 'a1' });
    expect(body(apply)).toEqual({ confirm: 'confirm-p1' });
    expect(h.text()).toContain('Applied. Audit entry a1.');

    await h.click(h.button('Undo change a1'));
    await h.answer('/api/v2/audit/a1/revert', proposal('p2'));
    expect(h.text()).toContain('Review undo of a1');
    await h.click(h.button('Apply undo'));
    const undo = await h.answer('/api/v2/proposals/p2/apply', { applied: true, auditId: 'a2' });
    expect(body(undo)).toEqual({ confirm: 'confirm-p2' });
    expect(h.text()).toContain('Undone. Audit entry a2.');

    await h.click(h.button('Done'));
    expect(h.pending((r) => r.url.pathname === FILE_URL)).toBeDefined();
  });

  it('never sends text that still holds the redaction marker', async () => {
    const h = await openFile({ editable: true, sha256: SHA });
    await h.click(h.button('Edit'));
    await screen.findByRole('textbox', { name: 'Edit CLAUDE.md' });

    await typeText('token: [redacted]\n');
    await h.click(h.button('Review changes'));

    expect(h.requests.some((r) => r.url.pathname === PROPOSE_URL)).toBe(false);
    expect(screen.getByRole('alert').textContent).toContain('[redacted] marker');
  });

  it('says why a file is read-only and offers no Edit', async () => {
    const h = await openFile({ editable: false, reason: 'contains-redacted-values' }, true, '# App\n');

    expect(h.buttons('Edit')).toHaveLength(0);
    expect(h.text()).toContain('Read-only: This file holds values cfgdash hides');
  });

  it('offers no Edit where edits are not available (the tailnet listener)', async () => {
    const h = await openFile({ editable: true, sha256: SHA }, false);

    expect(h.buttons('Edit')).toHaveLength(0);
  });

  it('shows a stale apply in words and reloads the file on request', async () => {
    const h = await openFile({ editable: true, sha256: SHA });
    await h.click(h.button('Edit'));
    await screen.findByRole('textbox', { name: 'Edit CLAUDE.md' });
    await typeText('# App\n');
    await h.click(h.button('Review changes'));
    await h.answer(PROPOSE_URL, proposal('p1'));

    await h.click(h.button('Apply change'));
    await h.answer('/api/v2/proposals/p1/apply', { error: 'stale' }, 409);

    expect(screen.getByRole('alert').textContent).toContain('This file changed since you opened it.');
    await h.click(h.button('Reload the file'));
    expect(h.pending((r) => r.url.pathname === FILE_URL)).toBeDefined();
  });

  it('keeps the sheet open on Escape while an edit is not applied', async () => {
    const h = await openFile({ editable: true, sha256: SHA });
    await h.click(h.button('Edit'));
    await screen.findByRole('textbox', { name: 'Edit CLAUDE.md' });
    await typeText('# Changed\n');

    await act(async () => {
      fireEvent.keyDown(editor(), { key: 'Escape' });
    });

    expect(screen.queryByRole('textbox', { name: 'Edit CLAUDE.md' })).not.toBeNull();
  });
});
