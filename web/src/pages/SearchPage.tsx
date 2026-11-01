import { Search } from 'lucide-react';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { flushSync } from 'react-dom';
import { Link, useLocation, useNavigate } from 'react-router';
import { Button } from '../components/ui/button';
import { api, classifyError, isAbort, type ErrorKind } from '../lib/api';
import type { SearchHit, SearchResponse } from '../lib/models';
import { mergeSearch, useUrl } from '../lib/url';
import { navViewId, VIEW_DEFINITIONS } from '../lib/views';
import { focusPendingHeading, LoadingPanel } from '../shell/Shell';
import { useAnnounce } from '../state/announcer';
import { useSnapshotStore } from '../state/snapshot';
import { Highlight, hitHref, hitLocation } from './hits';

type SearchState = { phase: 'hint' } | { phase: 'loading' } | { phase: 'error'; kind: ErrorKind } | { phase: 'content'; value: SearchResponse };

interface FileGroup {
  fileId: string;
  path: string;
  hits: SearchHit[];
}

const FILE_PREVIEW = 5;
const HIT = 'touch-row block w-full px-4 py-2 text-left hover:bg-hover';
const HITS = 'divide-y divide-border overflow-hidden rounded-lg border border-border bg-surface';

function summaryOf(response: SearchResponse): string {
  const n = response.results.length;
  return `${n} ${n === 1 ? 'result' : 'results'} for "${response.query}"`;
}

function HitText({ location, snippet, query, extra }: { location: string; snippet: string; query: string; extra?: string }) {
  return (
    <>
      <span className="block text-xs text-muted">
        {location}
        {extra && <span className="sr-only">{extra}</span>}
      </span>
      <span className="mono-value block break-words">
        <Highlight text={snippet} query={query} />
      </span>
    </>
  );
}

function FileHits({ group, query, location, onOpen }: { group: FileGroup; query: string; location(hit: SearchHit): string; onOpen(hit: SearchHit): void }) {
  const [all, setAll] = useState(false);
  const list = useRef<HTMLUListElement>(null);
  const n = group.hits.length;
  const showAll = () => {
    flushSync(() => setAll(true));
    list.current?.querySelectorAll<HTMLElement>('button')[FILE_PREVIEW]?.focus();
  };
  return (
    <section className="space-y-2">
      <h3 className="font-mono text-sm font-semibold break-all">
        {group.path} <span className="font-sans font-normal text-muted tabular">({n} {n === 1 ? 'match' : 'matches'})</span>
      </h3>
      <ul ref={list} className={HITS}>
        {(all ? group.hits : group.hits.slice(0, FILE_PREVIEW)).map((hit, index) => (
          <li key={index}>
            <button type="button" className={HIT} onClick={() => onOpen(hit)}>
              <HitText location={location(hit)} snippet={hit.snippet} query={query} extra={` of ${group.path}`} />
            </button>
          </li>
        ))}
      </ul>
      {!all && n > FILE_PREVIEW && (
        <Button variant="link" onClick={showAll}>
          Show all {n} in this file
        </Button>
      )}
    </section>
  );
}

/** Search page: `q` in the URL, debounced typing, results grouped by view in nav order, file contents last. */
export default function SearchPage() {
  const url = useUrl();
  const store = useSnapshotStore();
  const announce = useAnnounce();
  const navigate = useNavigate();
  const location = useLocation();
  const search = useRef(location.search);
  search.current = location.search;
  const committed = useRef<string | undefined | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const q = url.q;
  const dir = url.dir;
  const [text, setText] = useState(q ?? '');
  const [retry, setRetry] = useState(0);
  const [result, setResult] = useState<{ key: string; state: SearchState } | null>(null);

  const query = (q ?? '').trim();
  const key = JSON.stringify([query, dir, retry]);
  const state: SearchState = query.length < 2 ? { phase: 'hint' } : result?.key === key ? result.state : { phase: 'loading' };

  useEffect(() => {
    if (q !== committed.current) {
      setText(q ?? '');
    }
    committed.current = null;
  }, [q]);

  useEffect(() => {
    if (query.length < 2) {
      return;
    }
    const controller = new AbortController();
    api.search(query, dir, controller.signal).then(
      (value) => {
        if (controller.signal.aborted) {
          return;
        }
        setResult({ key, state: { phase: 'content', value } });
        announce(value.results.length ? `${summaryOf(value)}.` : `No results for "${value.query}".`);
      },
      (error: unknown) => {
        if (controller.signal.aborted || isAbort(error)) {
          return;
        }
        setResult({ key, state: { phase: 'error', kind: classifyError(error) } });
        announce('Search failed.');
      }
    );
    return () => controller.abort();
  }, [query, dir, key, announce]);

  useEffect(() => {
    focusPendingHeading();
    return () => clearTimeout(timer.current);
  }, []);

  const commit = (value: string) => {
    committed.current = value || undefined;
    void navigate({ pathname: '/search', search: mergeSearch(search.current, { q: value || undefined }) }, { replace: true });
  };

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    clearTimeout(timer.current);
    commit(text);
  };

  const response = state.phase === 'content' ? state.value : null;
  const locate = (hit: SearchHit) => hitLocation(store.snapshot, hit);
  const groups = response
    ? VIEW_DEFINITIONS.map((definition) => ({
        id: definition.id,
        label: definition.label,
        hits: response.results.filter((hit) => !(hit.kind === 'file' && hit.fileId) && navViewId(hit.view) === definition.id)
      })).filter((group) => group.hits.length > 0)
    : [];
  const fileGroups = new Map<string, FileGroup>();
  for (const hit of response?.results ?? []) {
    if (hit.kind === 'file' && hit.fileId) {
      const group = fileGroups.get(hit.fileId) ?? { fileId: hit.fileId, path: store.file(hit.fileId)?.path ?? hit.fileId, hits: [] };
      group.hits.push(hit);
      fileGroups.set(hit.fileId, group);
    }
  }
  const fileHitCount = [...fileGroups.values()].reduce((sum, group) => sum + group.hits.length, 0);

  return (
    <div className="space-y-6">
      <header>
        <h1 tabIndex={-1} className="text-2xl leading-[30px] font-semibold">
          Search
        </h1>
      </header>

      <form role="search" onSubmit={onSubmit} className="max-w-xl space-y-1">
        <label htmlFor="search-page-input" className="block text-xs font-medium text-muted">
          Search everything
        </label>
        <div className="relative">
          <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted" strokeWidth={1.75} aria-hidden="true" />
          <input
            id="search-page-input"
            type="search"
            autoComplete="off"
            spellCheck={false}
            placeholder="Search settings, files, hooks..."
            value={text}
            onChange={(event) => {
              const value = event.target.value;
              setText(value);
              clearTimeout(timer.current);
              timer.current = setTimeout(() => commit(value), 300);
            }}
            className="touch-target h-9 w-full rounded-md border border-border-strong bg-raised pr-2.5 pl-8 placeholder:text-muted"
          />
        </div>
      </form>

      <div className="space-y-6">
        {state.phase === 'hint' && <p className="text-muted">Search keys, values, labels and file contents. Type at least 2 characters.</p>}
        {state.phase === 'loading' && <LoadingPanel text="Searching..." className="py-6" />}
        {state.phase === 'error' && (
          <div className="space-y-3">
            {state.kind === 'unreachable' ? (
              <p>
                cfgdash isn't answering. It may have stopped; start it again with <code className="font-mono">cfgdash</code>.
              </p>
            ) : (
              <p>The search didn't work.</p>
            )}
            <Button onClick={() => setRetry(retry + 1)}>Try again</Button>
          </div>
        )}
        {response && !response.results.length && <p>No results for "{response.query}".</p>}
        {response && response.results.length > 0 && (
          <>
            <div>
              <p className="tabular">{summaryOf(response)}</p>
              {response.truncated && <p className="text-muted">Showing the first 200; refine the search.</p>}
            </div>
            {groups.map((group) => (
              <section key={group.id} className="space-y-2">
                <h2 className="font-semibold">
                  {group.label} ({group.hits.length})
                </h2>
                <ul className={HITS}>
                  {group.hits.map((hit, index) => (
                    <li key={index}>
                      <Link to={hitHref(hit, dir)} className={HIT}>
                        <HitText location={locate(hit)} snippet={hit.snippet} query={response.query} />
                      </Link>
                    </li>
                  ))}
                </ul>
              </section>
            ))}
            {fileGroups.size > 0 && (
              <section className="space-y-4">
                <h2 className="font-semibold">File contents ({fileHitCount})</h2>
                {[...fileGroups.values()].map((group) => (
                  <FileHits
                    key={`${key}:${group.fileId}`}
                    group={group}
                    query={response.query}
                    location={locate}
                    onOpen={(hit) => url.openFile(group.fileId, hit.line)}
                  />
                ))}
              </section>
            )}
          </>
        )}
      </div>
    </div>
  );
}
