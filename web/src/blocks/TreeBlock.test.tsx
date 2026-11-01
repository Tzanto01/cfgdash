import { FILE_CLAUDE } from '../test/fixtures';
import { startApp } from '../test/harness';

const node = (id: string) => document.getElementById(`instructions.chain/chain/${id}`);
const nodeText = (id: string) => node(id)?.textContent?.replace(/\s+/g, ' ') ?? '';

describe('tree block', () => {
  it('toggles children with the disclosure and aria-expanded', async () => {
    const h = await startApp('/instructions');
    const toggle = h.button('Children of project/CLAUDE.md');

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(node('n2.1')).not.toBeNull();

    await h.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(node('n2.1')).toBeNull();

    await h.click(toggle);
    expect(node('n2.1')).not.toBeNull();
  });

  it('shows the summary with files, size and tokens', async () => {
    const h = await startApp('/instructions');

    expect(h.text()).toContain('4 files · 18.0 KB · about 4,600 tokens (estimate)');
  });

  it('marks lazy and referenced nodes as not counted and shows the error text', async () => {
    await startApp('/instructions');

    expect(nodeText('n4')).toContain('not counted');
    expect(nodeText('n2.2')).toContain('not counted');
    expect(nodeText('n2.1')).not.toContain('not counted');
    expect(nodeText('n2.3')).toContain('file is missing');
  });

  it('opens the file of a node that has a fileId', async () => {
    const h = await startApp('/instructions');

    await h.click(h.button('project/CLAUDE.md'));

    expect(h.url()).toContain(`file=${FILE_CLAUDE}`);
  });

  it('keeps a nested node collapsed without a jump target', async () => {
    await startApp('/instructions');

    expect(node('n2.4')).not.toBeNull();
    expect(node('n2.4.1')).toBeNull();
  });

  it('expands collapsed ancestors when the URL fragment points at a nested node', async () => {
    await startApp('/instructions#instructions.chain/chain/n2.4.1');

    expect(node('n2.4.1')).not.toBeNull();
  });
});
