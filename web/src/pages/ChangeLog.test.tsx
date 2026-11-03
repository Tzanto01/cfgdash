import { render } from '@testing-library/react';
import { ChangeLog } from './ChangeLog';

describe('change log', () => {
  it('lists a file edit by its path and a settings change by key and scope, each with Undo', () => {
    const { container } = render(
      <ChangeLog
        entries={[
          {
            auditId: 'a2',
            at: '2030-01-01T10:00:00Z',
            listener: 'ui',
            files: [{ path: 'D:/ws/app/CLAUDE.md', scope: 'file', sha256Before: 'a', sha256After: 'b' }],
            changes: [{ file: 'd4e5f60718293041', action: 'edit' }]
          },
          {
            auditId: 'a1',
            at: '2030-01-01T09:00:00Z',
            listener: 'ui',
            files: [{ path: 'D:/ws/app/.claude/settings.json', scope: 'project', sha256Before: null, sha256After: 'c' }],
            changes: [{ key: 'model', scope: 'project', action: 'set' }]
          }
        ]}
        failed={false}
        busyId={null}
        error={null}
        onUndo={() => undefined}
        onRetry={() => undefined}
      />
    );

    expect([...container.querySelectorAll('li li')].map((li) => li.textContent)).toEqual(['edit D:/ws/app/CLAUDE.md', 'set model (project)']);
    expect([...container.querySelectorAll('button')].map((b) => b.textContent)).toEqual(['Undo change a2', 'Undo change a1']);
  });
});
