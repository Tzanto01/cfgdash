import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  Injector,
  input,
  linkedSignal,
  signal
} from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { catchError, map, of, startWith, switchMap } from 'rxjs';
import { ApiClient, classifyError, ErrorKind } from '../api-client';
import { highlight, itemDomId, normalizeItemId, TextSegment } from '../format';
import { KvBlock, SearchHit, SearchResponse, TableBlock, TreeBlock, TreeNode } from '../models';
import { SnapshotStore } from '../snapshot-store';
import { Icon } from '../ui/icon';
import { Announcer } from '../ui-state';
import { UrlState } from '../url-state';
import { navViewId, VIEW_DEFINITIONS } from '../views';

type SearchState =
  | { phase: 'hint' }
  | { phase: 'loading' }
  | { phase: 'error'; kind: ErrorKind }
  | { phase: 'content'; value: SearchResponse };

interface HitView {
  hit: SearchHit;
  location: string;
  segments: TextSegment[];
  link: string;
  fragment: string;
}

interface HitGroup {
  id: string;
  label: string;
  hits: HitView[];
}

interface FileGroup {
  fileId: string;
  path: string;
  hits: HitView[];
}

const FILE_PREVIEW = 5;

function queryParam(value: unknown): string | undefined {
  const first = Array.isArray(value) ? value[0] : value;
  return typeof first === 'string' ? first : undefined;
}

/** Search page: `q` in the URL, debounced typing, results grouped by view in nav order. */
@Component({
  selector: 'app-search-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, Icon],
  templateUrl: './search-page.html'
})
export class SearchPage {
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly store = inject(SnapshotStore);
  private readonly announcer = inject(Announcer);
  private readonly injector = inject(Injector);
  protected readonly url = inject(UrlState);
  private timer: ReturnType<typeof setTimeout> | undefined;

  readonly q = input<string | undefined, unknown>(undefined, { transform: queryParam });
  private committed: string | undefined;
  protected readonly text = linkedSignal<string | undefined, string>({
    source: this.q,
    computation: (q, previous) => (previous && q === this.committed ? previous.value : (q ?? ''))
  });
  private readonly retryCount = signal(0);
  private readonly query = computed(() => (this.q() ?? '').trim());
  private readonly params = computed(() => ({ q: this.query(), dir: this.url.dir(), retry: this.retryCount() }));

  protected readonly state = toSignal(
    toObservable(this.params).pipe(
      switchMap(({ q, dir }) =>
        q.length < 2
          ? of<SearchState>({ phase: 'hint' })
          : this.api.search(q, dir).pipe(
              map((value): SearchState => ({ phase: 'content', value })),
              catchError((error: unknown) => of<SearchState>({ phase: 'error', kind: classifyError(error) })),
              startWith<SearchState>({ phase: 'loading' })
            )
      )
    ),
    { initialValue: { phase: 'hint' } as SearchState }
  );

  protected readonly response = computed(() => {
    const state = this.state();
    return state.phase === 'content' ? state.value : null;
  });
  protected readonly summary = computed(() => {
    const response = this.response();
    if (!response) {
      return '';
    }
    const n = response.results.length;
    return `${n} ${n === 1 ? 'result' : 'results'} for "${response.query}"`;
  });
  protected readonly groups = computed<HitGroup[]>(() => {
    const response = this.response();
    if (!response) {
      return [];
    }
    return VIEW_DEFINITIONS.map((definition) => ({
      id: definition.id,
      label: definition.label,
      hits: response.results
        .filter((hit) => !(hit.kind === 'file' && hit.fileId) && navViewId(hit.view) === definition.id)
        .map((hit) => this.toView(hit, response.query))
    })).filter((group) => group.hits.length > 0);
  });

  /** File-content hits, one group per file in first-hit order. */
  protected readonly fileGroups = computed<FileGroup[]>(() => {
    const response = this.response();
    const groups = new Map<string, FileGroup>();
    for (const hit of response?.results ?? []) {
      if (hit.kind !== 'file' || !hit.fileId) {
        continue;
      }
      let group = groups.get(hit.fileId);
      if (!group) {
        group = { fileId: hit.fileId, path: this.store.file(hit.fileId)?.path ?? hit.fileId, hits: [] };
        groups.set(hit.fileId, group);
      }
      group.hits.push(this.toView(hit, response?.query ?? ''));
    }
    return [...groups.values()];
  });
  protected readonly fileHitCount = computed(() => this.fileGroups().reduce((sum, g) => sum + g.hits.length, 0));
  protected readonly expandedFiles = linkedSignal({ source: this.response, computation: () => new Set<string>() });
  protected readonly filePreview = FILE_PREVIEW;

  constructor() {
    effect(() => {
      const state = this.state();
      if (state.phase === 'content') {
        this.announcer.announce(
          state.value.results.length ? `${this.summary()}.` : `No results for "${state.value.query}".`
        );
      } else if (state.phase === 'error') {
        this.announcer.announce("Search failed.");
      }
    });
    inject(DestroyRef).onDestroy(() => clearTimeout(this.timer));
  }

  protected onInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.text.set(value);
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.commit(value), 300);
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    clearTimeout(this.timer);
    this.commit(this.text());
  }

  protected showAllInFile(fileId: string, list: HTMLElement): void {
    this.expandedFiles.set(new Set(this.expandedFiles()).add(fileId));
    afterNextRender(() => list.querySelectorAll<HTMLElement>('.hit')[FILE_PREVIEW]?.focus(), { injector: this.injector });
  }

  protected retry(): void {
    this.retryCount.update((n) => n + 1);
  }

  private commit(value: string): void {
    this.committed = value || undefined;
    void this.router.navigate([], {
      queryParams: { q: value || null },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }

  private toView(hit: SearchHit, query: string): HitView {
    return {
      hit,
      location: this.location(hit),
      segments: highlight(hit.snippet.trim(), query),
      link: `/${navViewId(hit.view)}`,
      fragment: hit.itemId ? itemDomId(hit.collectorId, hit.blockId, hit.itemId) : `${hit.collectorId}/${hit.blockId}`
    };
  }

  private location(hit: SearchHit): string {
    if (hit.kind === 'file') {
      return hit.line ? `line ${hit.line}` : 'File';
    }
    const section = this.store.snapshot()?.sections.find((s) => s.collectorId === hit.collectorId);
    const block = section?.blocks.find((b) => b.id === hit.blockId);
    const parts = [section?.title ?? hit.collectorId, block?.title ?? hit.blockId];
    const label = block && hit.itemId ? itemLabel(block, normalizeItemId(hit.itemId)) : undefined;
    if (label ?? hit.itemId) {
      parts.push(label ?? hit.itemId ?? '');
    }
    return parts.join(' › ');
  }
}

function itemLabel(block: { type: string }, itemId: string): string | undefined {
  if (block.type === 'kv') {
    return (block as KvBlock).rows.find((row) => normalizeItemId(row.key) === itemId)?.key;
  }
  if (block.type === 'table') {
    const table = block as TableBlock;
    const row = table.rows.find((r) => normalizeItemId(r.id) === itemId);
    const first = row && table.columns[0] ? row.cells[table.columns[0].key] : undefined;
    if (first === null || first === undefined) {
      return row?.id;
    }
    return typeof first === 'object' ? first.text : String(first);
  }
  if (block.type === 'tree') {
    return findNode((block as TreeBlock).nodes, itemId)?.label;
  }
  return undefined;
}

function findNode(nodes: TreeNode[], id: string): TreeNode | undefined {
  for (const node of nodes) {
    if (normalizeItemId(node.id) === id) {
      return node;
    }
    const found = findNode(node.children ?? [], id);
    if (found) {
      return found;
    }
  }
  return undefined;
}
