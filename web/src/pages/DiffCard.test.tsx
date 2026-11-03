import { render } from '@testing-library/react';
import { DiffCard } from './DiffCard';

describe('diff card', () => {
  it('shows bidi and zero-width characters in diff lines as U+FFFD, so the review shows what will be written', () => {
    const diff = ['--- a/D:/ws/app/CLAUDE.md', '+++ b/D:/ws/app/CLAUDE.md', '@@ -1,1 +1,2 @@', ' # App', '+Be kind.\u202Eesu\u200Bdo', ''].join('\n');
    const { container } = render(<DiffCard file={{ path: 'D:/ws/app/CLAUDE.md', scope: 'project', sha256Before: 'a', diff }} scopeLabel="File" gitTracked={false} />);

    expect(container.textContent).toContain('Be kind.\uFFFDesu\uFFFDdo');
    expect(container.textContent).not.toMatch(/[\u202E\u200B]/);
  });
});
