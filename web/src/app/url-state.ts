import { Location } from '@angular/common';
import { computed, inject, Injectable } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Params, Router } from '@angular/router';
import { filter, map } from 'rxjs';

interface UrlSnapshot {
  path: string;
  query: Params;
  fragment: string | null;
}

/** Signals over the URL state (`dir`, `file`, `line`, `q`, fragment) and the file viewer navigation. */
@Injectable({ providedIn: 'root' })
export class UrlState {
  private readonly router = inject(Router);
  private readonly location = inject(Location);
  private fileOpenedInApp = false;

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((event) => event instanceof NavigationEnd),
      map((): UrlSnapshot | null => this.read())
    ),
    { initialValue: this.router.navigated ? this.read() : null }
  );

  /** False until the first navigation has finished. */
  readonly ready = computed(() => this.url() !== null);
  readonly path = computed(() => this.url()?.path ?? '');
  readonly dir = computed(() => stringParam(this.url()?.query['dir']));
  readonly file = computed(() => stringParam(this.url()?.query['file']));
  readonly line = computed(() => {
    const value = Number(stringParam(this.url()?.query['line']));
    return Number.isInteger(value) && value > 0 ? value : undefined;
  });
  readonly fragment = computed(() => this.url()?.fragment ?? null);
  /** Query params every internal link carries. */
  readonly linkParams = computed<Params>(() => ({ dir: this.dir() }));

  openFile(fileId: string, line?: number): void {
    this.fileOpenedInApp = true;
    void this.router.navigate([], {
      queryParams: { file: fileId, line: line ?? null },
      queryParamsHandling: 'merge',
      preserveFragment: true
    });
  }

  closeFile(): void {
    if (this.fileOpenedInApp) {
      this.fileOpenedInApp = false;
      this.location.back();
      return;
    }
    void this.router.navigate([], {
      queryParams: { file: null, line: null },
      queryParamsHandling: 'merge',
      preserveFragment: true,
      replaceUrl: true
    });
  }

  /** Called when the file param disappears by other means (nav link, browser back). */
  fileClosed(): void {
    this.fileOpenedInApp = false;
  }

  private read(): UrlSnapshot {
    const tree = this.router.parseUrl(this.router.url);
    const segments = tree.root.children['primary']?.segments ?? [];
    return {
      path: segments.map((segment) => segment.path).join('/'),
      query: tree.queryParams,
      fragment: tree.fragment
    };
  }
}

function stringParam(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}
