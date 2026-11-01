import type { Snapshot } from './models';
import { buildViews } from './views';
import { makeSnapshot } from '../test/fixtures';

describe('buildViews', () => {
  it('lists sessions.transcripts under its own Sessions view, not Other', () => {
    const snapshot = makeSnapshot();
    snapshot.collectors.push({
      id: 'sessions.transcripts',
      title: 'Sessions',
      version: '1.0.0',
      view: 'sessions',
      state: 'active',
      requirements: {},
      durationMs: 1
    });
    snapshot.sections = [...snapshot.sections, { collectorId: 'sessions.transcripts', view: 'sessions', title: 'Sessions', blocks: [] }];

    const views = buildViews(snapshot);

    expect(views.find((v) => v.id === 'sessions')).toMatchObject({
      label: 'Sessions',
      visible: true,
      sections: [{ collectorId: 'sessions.transcripts' }]
    });
    expect(views.find((v) => v.id === 'other')?.sections.some((s) => s.view === 'sessions')).toBe(false);
  });

  it('lists the engram.memory section under Memory, not Other', () => {
    const snapshot = {
      collectors: [
        { id: 'engram.memory', title: 'Engram memory', version: '1.0.0', view: 'memory', state: 'active', requirements: {}, durationMs: 1 }
      ],
      sections: [{ collectorId: 'engram.memory', view: 'memory', title: 'Engram memory', blocks: [] }]
    } as unknown as Snapshot;

    const views = buildViews(snapshot);

    expect(views.find((v) => v.id === 'memory')).toMatchObject({ label: 'Memory', visible: true, sections: snapshot.sections });
    expect(views.find((v) => v.id === 'other')?.visible).toBe(false);
  });
});
