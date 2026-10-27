import { FILE_CLAUDE } from '../../testing/fixtures';
import { AppHarness, startApp } from '../../testing/harness';

function nodeRow(h: AppHarness, label: string): HTMLElement {
  const el = [...h.el.querySelectorAll('.node-label')].find((n) => n.textContent?.trim() === label);
  const row = el?.closest('.node-row');
  if (!row) {
    throw new Error(`No node ${label}`);
  }
  return row as HTMLElement;
}

const hasNode = (h: AppHarness, label: string) =>
  [...h.el.querySelectorAll('.node-label')].some((n) => n.textContent?.trim() === label);

describe('tree block', () => {
  it('toggles children with the disclosure and aria-expanded', async () => {
    const h = await startApp('/instructions');
    const toggle = h.button('Children of project/CLAUDE.md');

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(hasNode(h, '@AGENTS.md')).toBe(true);

    await h.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(hasNode(h, '@AGENTS.md')).toBe(false);

    await h.click(toggle);
    expect(hasNode(h, '@AGENTS.md')).toBe(true);
  });

  it('shows the summary with files, size and tokens', async () => {
    const h = await startApp('/instructions');

    expect(h.text()).toContain('4 files · 18.0 KB · about 4,600 tokens (estimate)');
  });

  it('marks lazy and referenced nodes as not counted and shows the error text', async () => {
    const h = await startApp('/instructions');

    expect(nodeRow(h, 'web/CLAUDE.md').textContent).toContain('not counted');
    expect(nodeRow(h, 'docs/override.md').textContent).toContain('not counted');
    expect(nodeRow(h, '@AGENTS.md').textContent).not.toContain('not counted');
    expect(nodeRow(h, '@docs/missing.md').textContent).toContain('file is missing');
  });

  it('opens the file of a node that has a fileId', async () => {
    const h = await startApp('/instructions');
    await h.click(nodeRow(h, 'project/CLAUDE.md').querySelector('button.node-label') as HTMLElement);

    expect(h.router.url).toContain(`file=${FILE_CLAUDE}`);
  });

  it('keeps a nested node collapsed without a jump target', async () => {
    const h = await startApp('/instructions');

    expect(hasNode(h, 'docs/index.md')).toBe(true);
    expect(hasNode(h, 'deep/nested.md')).toBe(false);
  });

  it('expands collapsed ancestors when the URL fragment points at a nested node', async () => {
    const h = await startApp('/instructions#instructions.chain/chain/n2.4.1');

    expect(hasNode(h, 'deep/nested.md')).toBe(true);
  });
});
