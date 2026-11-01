import { useEffect, useState } from 'react';
import { Button } from '../components/ui/button';
import { findingLocation } from '../lib/findings';
import { formatCount } from '../lib/format';
import type { Severity } from '../lib/models';
import { cn } from '../lib/utils';
import { VIEW_DEFINITIONS } from '../lib/views';
import { focusPendingHeading, LoadingPanel } from '../shell/Shell';
import { useAnnounce } from '../state/announcer';
import { useFindings } from '../state/findings';
import { useSnapshotStore } from '../state/snapshot';
import { FindingSheet } from './FindingSheet';
import { SEVERITIES, SeverityBar, SeverityLabel } from './SeverityBar';

type Chip = Severity | 'all';

const CHIPS: readonly { id: Chip; label: string }[] = [{ id: 'all', label: 'All' }, ...SEVERITIES];
const DESCRIPTION = VIEW_DEFINITIONS.find((v) => v.id === 'health')?.description;
const HEAD = 'font-sans text-xs font-medium text-muted';

/** Health page: severity bar, severity filter and the findings table; a finding opens in the side sheet. */
export default function HealthPage() {
  const health = useFindings();
  const store = useSnapshotStore();
  const announce = useAnnounce();
  const [chip, setChip] = useState<Chip>('all');
  const [selectedId, setSelectedId] = useState<string | null>(null);

  const response = health.request.phase === 'error' ? null : health.findings;
  const all = response?.findings ?? [];
  const shown = chip === 'all' ? all : all.filter((f) => f.severity === chip);
  const filtering = chip !== 'all';
  const selected = selectedId === null ? undefined : all.find((f) => f.id === selectedId);
  if (selectedId !== null && !selected) {
    setSelectedId(null);
  }

  useEffect(() => {
    focusPendingHeading();
  }, []);

  useEffect(() => {
    if (filtering) {
      announce(`${shown.length} of ${all.length} findings`);
    }
  }, [announce, filtering, shown.length, all.length]);

  return (
    <>
      <header className="mb-4">
        <div className="flex flex-wrap items-baseline gap-x-3">
          <h1 tabIndex={-1} className="text-2xl leading-[30px] font-semibold">
            Health
          </h1>
          {response && (
            <span className="text-xs font-medium text-muted tabular">
              {formatCount(all.length)} {all.length === 1 ? 'finding' : 'findings'}
            </span>
          )}
        </div>
        {DESCRIPTION && <p className="mt-1 text-muted">{DESCRIPTION}</p>}
      </header>

      {health.request.phase === 'error' ? (
        <div className="space-y-3">
          <p>Couldn't load findings</p>
          <Button onClick={health.reload}>Try again</Button>
        </div>
      ) : !response ? (
        <LoadingPanel text="Checking this harness..." />
      ) : !all.length ? (
        <p>No findings. Nothing in this snapshot needs attention.</p>
      ) : (
        <>
          <SeverityBar counts={response.counts} className="mb-4 max-w-xl" />

          <div className="mb-4 flex flex-wrap items-center gap-2">
            <div role="group" aria-label="Show findings" className="flex flex-wrap gap-1">
              {CHIPS.map((c) => {
                const pressed = chip === c.id;
                return (
                  <button
                    key={c.id}
                    type="button"
                    aria-pressed={pressed}
                    onClick={() => setChip(pressed ? 'all' : c.id)}
                    className={cn(
                      'touch-target h-7 rounded-md border px-2.5 text-sm transition-colors duration-100',
                      pressed ? 'tint-accent border-accent text-accent' : 'border-border bg-surface hover:bg-hover'
                    )}
                  >
                    {c.label}
                  </button>
                );
              })}
            </div>
            {filtering && (
              <p className="text-xs text-muted tabular">
                {shown.length} of {all.length} findings
              </p>
            )}
          </div>

          <section className="card overflow-hidden rounded-lg border border-border bg-surface">
            <table className="data-table stack w-full border-collapse">
              <caption className="sr-only">Findings</caption>
              <thead>
                <tr className="border-b border-border">
                  <th scope="col" className={HEAD}>
                    Severity
                  </th>
                  <th scope="col" className={HEAD}>
                    Title
                  </th>
                  <th scope="col" className={HEAD}>
                    Where
                  </th>
                </tr>
              </thead>
              <tbody>
                {shown.map((finding) => (
                  <tr key={finding.id} className="border-b border-border transition-colors duration-100 last:border-b-0 hover:bg-hover">
                    <td data-label="Severity" className="whitespace-nowrap">
                      <SeverityLabel severity={finding.severity} />
                    </td>
                    <td data-label="Title">
                      <button
                        type="button"
                        aria-haspopup="dialog"
                        className="touch-target text-left text-accent underline-offset-2 hover:underline"
                        onClick={() => setSelectedId(finding.id)}
                      >
                        {finding.title}
                      </button>
                    </td>
                    <td data-label="Where" className="mono-value wrap-anywhere text-muted">
                      {findingLocation(store.snapshot, finding)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            {!shown.length && <p className="px-4 py-2 text-muted">No findings match the filter.</p>}
          </section>
        </>
      )}

      {selected && <FindingSheet finding={selected} onClose={() => setSelectedId(null)} />}
    </>
  );
}
