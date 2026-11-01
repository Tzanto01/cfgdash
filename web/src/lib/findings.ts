import { hitHref, hitLocation } from '../pages/hits';
import type { Finding, SearchHit, Snapshot } from './models';
import { linkTo } from './url';
import { navViewId } from './views';

function asHit(finding: Finding): SearchHit | null {
  const { collectorId, blockId, itemId, key } = finding.evidence;
  if (!blockId) {
    return null;
  }
  const item = itemId || key;
  return { kind: 'row', view: finding.view, collectorId, blockId, snippet: '', ...(item ? { itemId: item } : {}) };
}

/** The view link of a finding's evidence (with its row or block fragment when the evidence names a block), carrying `dir`. */
export function findingHref(finding: Finding, dir: string | undefined): string {
  const hit = asHit(finding);
  return hit ? hitHref(hit, dir) : linkTo(`/${navViewId(finding.view)}`, dir);
}

/** "Section › Block › Item" for block evidence, else the collector's title (or its id). */
export function findingLocation(snapshot: Snapshot | null, finding: Finding): string {
  const hit = asHit(finding);
  if (hit) {
    return hitLocation(snapshot, hit);
  }
  const { collectorId } = finding.evidence;
  return snapshot?.collectors.find((c) => c.id === collectorId)?.title || collectorId;
}
