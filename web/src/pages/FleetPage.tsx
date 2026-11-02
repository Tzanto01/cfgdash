import { useEffect, useId, useState } from 'react';
import { Button } from '../components/ui/button';
import { api, ApiError, isAbort } from '../lib/api';
import { formatDateTime } from '../lib/format';
import type { CompareChange, CompareResponse, ExportsResponse, FleetResponse, JsonValue, PeerState } from '../lib/models';
import { cn } from '../lib/utils';
import { VIEW_DEFINITIONS } from '../lib/views';
import { focusPendingHeading, LoadingPanel } from '../shell/Shell';
import { useAnnounce } from '../state/announcer';
import { useSnapshotStore } from '../state/snapshot';

const DESCRIPTION = VIEW_DEFINITIONS.find((v) => v.id === 'fleet')?.description ?? '';
const REFUSED = 'port 7820 not reachable on the tailnet';
const STATE: Record<PeerState, { word: string; dot: string }> = {
  ok: { word: 'ok', dot: 'bg-ok' },
  absent: { word: 'absent', dot: 'bg-border-strong' },
  refused: { word: 'refused', dot: 'bg-warn' },
  error: { word: 'error', dot: 'bg-danger' }
};
const ORDER: Record<CompareChange, number> = { changed: 0, onlyLeft: 1, onlyRight: 2 };
const SELECT = 'touch-target h-8 min-w-0 rounded-md border border-border-strong bg-raised px-2 text-sm';

interface Loaded<T> {
  data?: T;
  error?: unknown;
}

function useLoad<T>(load: (signal: AbortSignal) => Promise<T>, reloads: number): Loaded<T> | null {
  const [loaded, setLoaded] = useState<{ reloads: number; value: Loaded<T> } | null>(null);
  useEffect(() => {
    const controller = new AbortController();
    load(controller.signal).then(
      (data) => setLoaded({ reloads, value: { data } }),
      (error: unknown) => {
        if (!isAbort(error) && !controller.signal.aborted) {
          setLoaded({ reloads, value: { error } });
        }
      }
    );
    return () => controller.abort();
  }, [load, reloads]);
  return loaded && loaded.reloads === reloads ? loaded.value : null;
}

const loadFleet = (signal: AbortSignal) => api.fleet(signal);
const loadExports = (signal: AbortSignal) => api.exports(signal);

function compareFailure(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 502 && error.code === 'peer-unavailable') {
      return `That machine can't be compared${error.reason ? `: ${error.reason}` : ''}.`;
    }
    if (error.status === 404) {
      return 'That export or machine is no longer there. Pick another one.';
    }
    if (error.status === 422) {
      return "That export can't be read.";
    }
    if (error.status === 400 && error.code === 'bad-dir') {
      return "This folder can't be used.";
    }
  }
  return "Couldn't compare.";
}

function valueText(value: JsonValue | undefined): string {
  return value === undefined ? '' : typeof value === 'string' ? value : JSON.stringify(value);
}

function StateChip({ state }: { state: PeerState }) {
  const { word, dot } = STATE[state] ?? { word: state, dot: 'bg-border-strong' };
  return (
    <span className="inline-flex items-center gap-1.5">
      <span className={cn('size-2 shrink-0 rounded-full', dot)} aria-hidden="true" />
      {word}
    </span>
  );
}

function Diff({ diff }: { diff: CompareResponse }) {
  const { left, right, counts } = diff;
  const changeWord: Record<CompareChange, string> = { changed: 'Changed', onlyLeft: `Only on ${left.machine}`, onlyRight: `Only on ${right.machine}` };
  const collectors = diff.collectors
    .filter((c) => c.rows.length > 0)
    .map((c) => ({ ...c, rows: [...c.rows].sort((a, b) => ORDER[a.change] - ORDER[b.change]) }))
    .sort((a, b) => Number(b.rows[0].change === 'changed') - Number(a.rows[0].change === 'changed'));
  return (
    <div className="space-y-4">
      <p className="text-muted">
        <span className="font-medium text-text">{left.machine}</span> (collected {formatDateTime(left.collectedAt)}) compared with{' '}
        <span className="font-medium text-text">{right.machine}</span> (collected {formatDateTime(right.collectedAt)})
      </p>
      <p className="tabular">
        {counts.changed} changed · {counts.onlyLeft} only on {left.machine} · {counts.onlyRight} only on {right.machine}
      </p>
      {collectors.length === 0 && <p className="text-muted">No differences.</p>}
      {collectors.map((collector) => (
        <section key={collector.id} aria-label={collector.id} className="card rounded-lg border border-border bg-surface">
          <h3 className="flex min-h-10 flex-wrap items-center gap-x-3 border-b border-border px-4 py-2 font-mono text-sm font-semibold">
            {collector.id}
            {collector.state.left !== collector.state.right && (
              <span className="font-sans text-xs font-normal text-muted">
                {left.machine}: {collector.state.left} · {right.machine}: {collector.state.right}
              </span>
            )}
          </h3>
          <ul>
            {collector.rows.map((row) => (
              <li key={`${row.blockId}\n${row.itemId}`} className="space-y-1 border-b border-border px-4 py-2 last:border-b-0">
                <div className="flex flex-wrap items-baseline gap-x-3">
                  <span className="mono-value wrap-anywhere">
                    {row.blockId} › {row.itemId}
                  </span>
                  <span className={cn('text-xs font-medium', row.change === 'changed' ? 'text-warn' : 'text-muted')}>{changeWord[row.change]}</span>
                </div>
                {row.change !== 'onlyRight' && (
                  <p className="grid grid-cols-[minmax(0,8rem)_minmax(0,1fr)] gap-2 text-xs">
                    <span className="truncate text-muted">{left.machine}</span>
                    <span className="mono-value wrap-anywhere">{valueText(row.left)}</span>
                  </p>
                )}
                {row.change !== 'onlyLeft' && (
                  <p className="grid grid-cols-[minmax(0,8rem)_minmax(0,1fr)] gap-2 text-xs">
                    <span className="truncate text-muted">{right.machine}</span>
                    <span className="mono-value wrap-anywhere">{valueText(row.right)}</span>
                  </p>
                )}
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}

/** Fleet view (fleet-v1): tailnet peers with their cfgdash state, and a compare with a peer or an earlier export. */
export default function FleetPage() {
  const store = useSnapshotStore();
  const announce = useAnnounce();
  const id = useId();
  const dir = store.snapshot?.context.targetDir;
  const [reloads, setReloads] = useState(0);
  const fleet = useLoad<FleetResponse>(loadFleet, reloads);
  const exports = useLoad<ExportsResponse>(loadExports, reloads);
  const [against, setAgainst] = useState('');
  const [result, setResult] = useState<{ dir?: string; diff?: CompareResponse; error?: string } | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    focusPendingHeading();
  }, []);

  const okPeers = fleet?.data?.peers.filter((p) => p.cfgdash === 'ok') ?? [];
  const exportList = exports?.data?.exports ?? [];
  const options = [...exportList.map((e) => `export:${e.id}`), ...okPeers.map((p) => `peer:${p.dnsName}`)];
  const selected = options.includes(against) ? against : (options[0] ?? '');
  const shown = result && result.dir === dir ? result : null;

  const compare = async (target: string) => {
    if (busy || !target) {
      return;
    }
    setAgainst(target);
    setBusy(true);
    try {
      const diff = await api.compare(dir, target);
      setResult({ dir, diff });
      announce(`${diff.counts.changed + diff.counts.onlyLeft + diff.counts.onlyRight} differences`);
    } catch (error) {
      setResult({ dir, error: compareFailure(error) });
    } finally {
      setBusy(false);
    }
  };

  const peers = () => {
    if (!fleet) {
      return <LoadingPanel text="Looking for machines on the tailnet..." />;
    }
    if (!fleet.data) {
      return (
        <div className="space-y-3 px-4 py-3">
          <p>Couldn't load the machines on the tailnet.</p>
          <Button onClick={() => setReloads((n) => n + 1)}>Try again</Button>
        </div>
      );
    }
    const { self, peers: list } = fleet.data;
    if (list.length === 0) {
      return (
        <p className="px-4 py-3 text-muted">
          {self.dnsName
            ? 'No other machines on your tailnet are online.'
            : "This machine isn't on a tailnet (Tailscale is missing or logged out), so there are no peers. You can still compare with an earlier export."}
        </p>
      );
    }
    return (
      <>
        <ul>
          {list.map((peer) => (
            <li
              key={peer.dnsName}
              className="grid gap-x-4 gap-y-1 border-b border-border px-4 py-2 last:border-b-0 md:grid-cols-[minmax(0,1.4fr)_6rem_minmax(0,1.6fr)_auto] md:items-center"
            >
              <div className="min-w-0">
                <span className="font-medium">{peer.name}</span>
                <span className="mono-value block truncate text-muted">{peer.dnsName}</span>
              </div>
              <div className="text-sm">
                <StateChip state={peer.cfgdash} />
              </div>
              <div className="min-w-0 text-sm text-muted">
                {peer.os}
                {peer.version && ` · cfgdash ${peer.version}`}
                {peer.cfgdash !== 'ok' && <span className="block text-text">{peer.reason || (peer.cfgdash === 'refused' ? REFUSED : '')}</span>}
              </div>
              <div>
                {peer.cfgdash === 'ok' && (
                  <Button size="sm" disabled={busy} onClick={() => void compare(`peer:${peer.dnsName}`)}>
                    Compare<span className="sr-only"> with {peer.name}</span>
                  </Button>
                )}
              </div>
            </li>
          ))}
        </ul>
        {list.some((p) => p.cfgdash === 'refused') && (
          <p className="border-t border-border px-4 py-3 text-sm text-muted">
            Refused: the machine runs cfgdash, but the tailnet does not let this machine reach port 7820 on it. Add an ACL grant for tcp:7820
            between your devices, and turn on "Answer fleet peers" in that machine's cfgdash settings.
          </p>
        )}
        {list.some((p) => p.cfgdash === 'absent') && (
          <p className="border-t border-border px-4 py-3 text-sm text-muted">
            Absent: cfgdash isn't running on that machine, or isn't published on the tailnet there.
          </p>
        )}
      </>
    );
  };

  return (
    <>
      <header className="mb-4">
        <h1 tabIndex={-1} className="text-2xl leading-[30px] font-semibold">
          Fleet
        </h1>
        <p className="mt-1 text-muted">{DESCRIPTION}</p>
        {fleet?.data && (
          <p className="mt-1 text-sm text-muted">
            This machine: <span className="font-medium text-text">{fleet.data.self.name}</span>
            {fleet.data.self.dnsName && <span className="mono-value"> ({fleet.data.self.dnsName})</span>}
          </p>
        )}
      </header>

      <div className="space-y-4">
        <section aria-labelledby={`${id}-peers`} className="card rounded-lg border border-border bg-surface">
          <h2 id={`${id}-peers`} className="flex h-10 items-center border-b border-border px-4 font-semibold">
            Machines on the tailnet
          </h2>
          {peers()}
        </section>

        <section aria-labelledby={`${id}-compare`} className="space-y-4">
          <h2 id={`${id}-compare`} className="font-semibold">
            Compare
          </h2>
          <form
            className="flex flex-wrap items-end gap-2"
            onSubmit={(event) => {
              event.preventDefault();
              void compare(selected);
            }}
          >
            <label className="flex min-w-0 flex-col gap-1">
              <span className="text-xs font-medium text-muted">Compare this folder with</span>
              <select value={selected} onChange={(event) => setAgainst(event.target.value)} className={SELECT} disabled={!selected}>
                {!selected && <option value="">Nothing to compare with yet</option>}
                {exportList.length > 0 && (
                  <optgroup label="Exports">
                    {exportList.map((e) => (
                      <option key={e.id} value={`export:${e.id}`}>
                        {e.machine} · {formatDateTime(e.collectedAt)} · {e.id}
                      </option>
                    ))}
                  </optgroup>
                )}
                {okPeers.length > 0 && (
                  <optgroup label="Machines">
                    {okPeers.map((p) => (
                      <option key={p.dnsName} value={`peer:${p.dnsName}`}>
                        {p.name} ({p.dnsName})
                      </option>
                    ))}
                  </optgroup>
                )}
              </select>
            </label>
            <Button type="submit" variant="primary" disabled={!selected} aria-busy={busy}>
              {busy ? 'Comparing...' : 'Compare'}
            </Button>
          </form>
          {exports?.data && exportList.length === 0 && (
            <p className="text-sm text-muted">
              No exports yet. Run <code className="font-mono">cfgdash export</code> to save one, then compare against it later.
            </p>
          )}
          {exports?.error !== undefined && <p className="text-sm text-muted">Couldn't list the exports.</p>}
          {shown?.error && (
            <p role="alert" className="tint-danger rounded-lg border border-danger px-4 py-3">
              {shown.error}
            </p>
          )}
          {shown?.diff && <Diff diff={shown.diff} />}
        </section>
      </div>
    </>
  );
}
