import {
  afterNextRender,
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  ElementRef,
  inject,
  Injector,
  signal,
  untracked,
  viewChild
} from '@angular/core';
import { Observable, Subscription } from 'rxjs';
import { ApiClient, classifyError } from './api-client';
import { formatBytes, formatTokens } from './format';
import { DetailContent, FileContent } from './models';
import { RequestState, SnapshotStore } from './snapshot-store';
import { Icon } from './ui/icon';
import { closeModal, openModal } from './ui-state';
import { UrlState } from './url-state';

/** Modal file and detail viewer driven by the `file`, `detail` and `line` query params. */
@Component({
  selector: 'app-file-viewer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './file-viewer.html'
})
export class FileViewer {
  private readonly api = inject(ApiClient);
  private readonly store = inject(SnapshotStore);
  private readonly injector = inject(Injector);
  protected readonly url = inject(UrlState);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');
  private trigger: HTMLElement | null = null;
  private subscription?: Subscription;
  private copyTimer: ReturnType<typeof setTimeout> | undefined;

  protected readonly state = signal<RequestState<FileContent | DetailContent>>({ phase: 'loading' });
  protected readonly wrap = signal(true);
  protected readonly copied = signal('');
  protected readonly detailId = computed(() => (this.url.file() ? undefined : this.url.detail()));
  protected readonly noun = computed(() => (this.detailId() ? 'detail' : 'file'));
  protected readonly ref = computed(() => this.store.file(this.url.file()));
  protected readonly path = computed(() => {
    const state = this.state();
    return state.phase === 'content' && 'path' in state.value ? state.value.path : (this.ref()?.path ?? '');
  });
  protected readonly name = computed(() => {
    const state = this.state();
    if (state.phase === 'content' && 'title' in state.value) {
      return state.value.title;
    }
    return this.path().split('/').pop() || this.url.file() || this.detailId() || 'File';
  });
  protected readonly kind = computed(() => {
    const state = this.state();
    return state.phase === 'content' && 'kind' in state.value ? state.value.kind : this.ref()?.kind;
  });
  protected readonly lines = computed(() => {
    const state = this.state();
    if (state.phase !== 'content') {
      return [];
    }
    const lines = state.value.content.split(/\r?\n/);
    if (lines.length > 1 && lines[lines.length - 1] === '') {
      lines.pop();
    }
    return lines;
  });
  protected readonly size = computed(() => {
    const ref = this.ref();
    return ref ? formatBytes(ref.sizeBytes) : '';
  });
  protected readonly tokens = computed(() => {
    const ref = this.ref();
    return ref ? formatTokens(ref.tokens) : '';
  });

  constructor() {
    afterRenderEffect(() => {
      const open = !!(this.url.file() || this.url.detail());
      untracked(() => (open ? this.show() : this.hide()));
    });
    effect(() => {
      const line = this.url.line();
      if (this.lines().length && line) {
        afterNextRender(() => this.scrollToLine(line), { injector: this.injector });
      }
    });
    inject(DestroyRef).onDestroy(() => {
      this.subscription?.unsubscribe();
      clearTimeout(this.copyTimer);
    });
  }

  protected close(): void {
    this.url.closeFile();
  }

  protected onCancel(event: Event): void {
    event.preventDefault();
    this.close();
  }

  protected retry(): void {
    this.load();
  }

  protected onClosed(): void {
    if (this.url.file() || this.url.detail()) {
      this.close();
    }
  }

  protected copyPath(): void {
    const write = navigator.clipboard?.writeText(this.path()) ?? Promise.reject(new Error('no clipboard'));
    write.then(
      () => this.showCopyFeedback('Copied'),
      () => this.showCopyFeedback("Couldn't copy")
    );
  }

  private showCopyFeedback(text: string): void {
    this.copied.set(text);
    clearTimeout(this.copyTimer);
    this.copyTimer = setTimeout(() => this.copied.set(''), 2000);
  }

  private show(): void {
    const dialog = this.dialog().nativeElement;
    if (!dialog.open) {
      const active = document.activeElement;
      this.trigger = active instanceof HTMLElement ? active : null;
      openModal(dialog);
      this.heading().nativeElement.focus();
    }
    this.load();
  }

  private hide(): void {
    this.subscription?.unsubscribe();
    this.url.fileClosed();
    const dialog = this.dialog().nativeElement;
    if (dialog.open) {
      closeModal(dialog);
      if (this.trigger?.isConnected) {
        this.trigger.focus();
      }
    }
    this.trigger = null;
  }

  private load(): void {
    const fileId = this.url.file();
    const detailId = this.detailId();
    const request: Observable<FileContent | DetailContent> | null = fileId ?this.api.file(fileId, this.url.dir()) : detailId ? this.api.detail(detailId, this.url.dir()) : null;
    if (!request) {
      return;
    }
    this.subscription?.unsubscribe();
    this.state.set({ phase: 'loading' });
    this.copied.set('');
    this.subscription = request.subscribe({
      next: (value) => this.state.set({ phase: 'content', value }),
      error: (error: unknown) => this.state.set({ phase: 'error', kind: classifyError(error) })
    });
  }

  private scrollToLine(line: number): void {
    const element = this.dialog().nativeElement.querySelector(`[data-line="${line}"]`);
    element?.scrollIntoView?.({ block: 'center' });
  }
}
