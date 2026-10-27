import { computed, effect, inject, Injectable, signal, untracked } from '@angular/core';
import { Subscription } from 'rxjs';
import { ApiClient, classifyError, ErrorKind } from './api-client';
import { FileRef, Snapshot } from './models';
import { Announcer } from './ui-state';
import { UrlState } from './url-state';
import { buildViews } from './views';

export type RequestState<T> =
  | { phase: 'loading' }
  | { phase: 'content'; value: T }
  | { phase: 'error'; kind: ErrorKind };

/** Loads the snapshot for the `dir` query param; refresh keeps the current content visible. */
@Injectable({ providedIn: 'root' })
export class SnapshotStore {
  private readonly api = inject(ApiClient);
  private readonly url = inject(UrlState);
  private readonly announcer = inject(Announcer);
  private readonly request = signal<RequestState<Snapshot>>({ phase: 'loading' });
  private subscription?: Subscription;
  private loadedDir: string | undefined | null = null;

  readonly state = this.request.asReadonly();
  readonly refreshing = signal(false);
  /** True when the last refresh failed and the previous content is still shown. */
  readonly refreshFailed = signal(false);
  /** Increments after each load caused by a folder change settles, with content or an error. */
  readonly dirLoads = signal(0);
  readonly snapshot = computed(() => {
    const state = this.request();
    return state.phase === 'content' ? state.value : null;
  });
  readonly views = computed(() => {
    const snapshot = this.snapshot();
    return snapshot ? buildViews(snapshot) : [];
  });
  readonly navViews = computed(() => this.views().filter((view) => view.visible));
  readonly files = computed(() => new Map<string, FileRef>((this.snapshot()?.files ?? []).map((f) => [f.id, f])));

  constructor() {
    effect(() => {
      if (!this.url.ready()) {
        return;
      }
      const dir = this.url.dir();
      untracked(() => {
        if (dir !== this.loadedDir) {
          const changed = this.loadedDir !== null;
          this.loadedDir = dir;
          this.load(dir, false, changed);
        }
      });
    });
  }

  file(id: string | undefined): FileRef | undefined {
    return id ? this.files().get(id) : undefined;
  }

  refresh(): void {
    if (this.refreshing() || this.request().phase === 'loading') {
      return;
    }
    if (this.request().phase === 'content') {
      this.refreshing.set(true);
    }
    this.load(this.url.dir(), true, false);
  }

  reload(): void {
    this.load(this.url.dir(), false, false);
  }

  private load(dir: string | undefined, refresh: boolean, dirChanged: boolean): void {
    this.subscription?.unsubscribe();
    this.refreshFailed.set(false);
    if (!refresh) {
      this.refreshing.set(false);
    }
    if (!this.refreshing()) {
      this.request.set({ phase: 'loading' });
    }
    this.subscription = this.api.snapshot(dir, refresh).subscribe({
      next: (value) => {
        this.request.set({ phase: 'content', value });
        this.refreshing.set(false);
        if (dirChanged) {
          this.dirLoads.update((n) => n + 1);
        }
        this.announcer.announce(
          refresh
            ? 'Snapshot refreshed.'
            : dirChanged
              ? `Folder changed to ${value.context.targetDir}. Snapshot loaded.`
              : 'Snapshot loaded.'
        );
      },
      error: (error: unknown) => {
        const kind = classifyError(error);
        const keepContent = this.refreshing() && kind !== 'access';
        this.refreshing.set(false);
        if (keepContent) {
          this.refreshFailed.set(true);
          this.announcer.announce("Couldn't refresh. Showing the previous snapshot.");
          return;
        }
        this.request.set({ phase: 'error', kind });
        if (dirChanged) {
          this.dirLoads.update((n) => n + 1);
        }
        this.announcer.announce(
          kind === 'bad-dir'
            ? "This folder can't be used."
            : kind === 'unreachable'
              ? "Couldn't load the snapshot: cfgdash isn't answering."
              : "Couldn't load the snapshot."
        );
      }
    });
  }
}
