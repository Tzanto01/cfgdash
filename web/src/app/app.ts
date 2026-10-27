import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  Injector,
  untracked,
  viewChild
} from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AccessState } from './access';
import { FileViewer } from './file-viewer';
import { FolderPicker } from './folder-picker';
import { formatRelative, formatTime } from './format';
import { SnapshotStore } from './snapshot-store';
import { Icon } from './ui/icon';
import { Announcer, Clock, FolderPickerState } from './ui-state';
import { UrlState } from './url-state';

/** Shell: skip link, sidebar or compact header with nav, search bar, content states, dialogs, live region. */
@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, Icon, FileViewer, FolderPicker],
  templateUrl: './app.html',
  host: { '(document:keydown)': 'onKeydown($event)' }
})
export class App {
  protected readonly access = inject(AccessState);
  protected readonly store = inject(SnapshotStore);
  protected readonly url = inject(UrlState);
  protected readonly announcer = inject(Announcer);
  private readonly picker = inject(FolderPickerState);
  private readonly clock = inject(Clock);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly searchInput = viewChild<ElementRef<HTMLInputElement>>('searchInput');
  private lastPath: string | null = null;

  protected readonly activeOptions = {
    paths: 'exact',
    queryParams: 'ignored',
    fragment: 'ignored',
    matrixParams: 'ignored'
  } as const;
  protected readonly snapshot = this.store.snapshot;
  protected readonly isSearch = computed(() => this.url.path() === 'search');
  protected readonly collected = computed(() => {
    const at = this.snapshot()?.machine.collectedAt;
    return at ? `Collected ${formatTime(at)} · ${formatRelative(at, this.clock.now())}` : '';
  });

  constructor() {
    effect(() => {
      if (!this.url.ready()) {
        return;
      }
      const path = this.url.path();
      const fragment = this.url.fragment();
      untracked(() => {
        const changed = this.lastPath !== null && this.lastPath !== path;
        this.lastPath = path;
        afterNextRender(
          () => {
            document
              .querySelector('.nav a[aria-current="page"]')
              ?.scrollIntoView?.({ block: 'nearest', inline: 'nearest' });
            if (changed && !fragment) {
              document.querySelector<HTMLElement>('main h1')?.focus();
            }
          },
          { injector: this.injector }
        );
      });
    });
  }

  protected skipToContent(event: Event): void {
    event.preventDefault();
    const main = document.getElementById('content');
    main?.focus();
    main?.scrollIntoView?.();
  }

  protected onSearch(event: Event): void {
    event.preventDefault();
    const q = this.searchInput()?.nativeElement.value.trim() ?? '';
    void this.router.navigate(['/search'], { queryParams: { dir: this.url.dir(), q: q || null } });
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.key !== '/' || event.ctrlKey || event.metaKey || event.altKey || isTyping(event.target)) {
      return;
    }
    const input =
      this.searchInput()?.nativeElement ?? document.querySelector<HTMLInputElement>('#search-page-input');
    if (input && input.offsetParent !== null) {
      event.preventDefault();
      input.focus();
      input.select();
    } else if (!this.isSearch()) {
      event.preventDefault();
      void this.router.navigate(['/search'], { queryParams: { dir: this.url.dir() } });
    }
  }

  protected retryAccess(): void {
    this.access.problem.set(null);
    this.store.reload();
  }

  protected chooseFolder(): void {
    this.picker.open.set(true);
  }
}

function isTyping(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false;
  }
  return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
}
