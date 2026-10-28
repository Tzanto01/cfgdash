import { Snapshot } from './models';
import { buildViews } from './views';

describe('memory view', () => {
  it('lists the engram.memory section under Memory, not Other', () => {
    const snapshot = {
      collectors: [{ id: 'engram.memory', title: 'Engram memory', version: '1.0.0', view: 'memory', state: 'active', requirements: {}, durationMs: 1 }],
      sections: [{ collectorId: 'engram.memory', view: 'memory', title: 'Engram memory', blocks: [] }]
    } as unknown as Snapshot;

    const views = buildViews(snapshot);

    expect(views.find((v) => v.id === 'memory')).toMatchObject({ label: 'Memory', visible: true, sections: snapshot.sections });
    expect(views.find((v) => v.id === 'other')?.visible).toBe(false);
  });
});
