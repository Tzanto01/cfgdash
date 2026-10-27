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
import { Subscription } from 'rxjs';
import { ApiClient, classifyError } from './api-client';
import { formatBytes, formatTokens } from './format';
import { FileContent } from './models';
import { RequestState, SnapshotStore } from './snapshot-store';
import { Icon } from './ui/icon';
import { closeModal, openModal } from './ui-state';
import { UrlState } from './url-state';

/** Modal file viewer driven by the `file` and `line` query params. */
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

  protected readonly state = signal<RequestState<FileContent>>({ phase: 'loading' });
  protected readonly wrap = signal(true);
  protected readonly copied = signal(false);
  protected readonly ref = computed(() => this.store.file(this.url.file()));
  protected readonly path = computed(() => {
    const state = this.state();
    return state.phase === 'content' ? state.value.path : (this.ref()?.path ?? '');
  });
  protected readonly name = computed(() => this.path().split('/').pop() || this.url.file() || 'File');
  protected readonly kind = computed(() => {
    const state = this.state();
    return state.phase === 'content' ? state.value.kind : this.ref()?.kind;
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
      const fileId = this.url.file();
      untracked(() => (fileId ? this.show(fileId) : this.hide()));
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
    const fileId = this.url.file();
    if (fileId) {
      this.load(fileId);
    }
  }

  protected copyPath(): void {
    const path = this.path();
    void navigator.clipboard?.writeText(path).then(() => {
      this.copied.set(true);
      clearTimeout(this.copyTimer);
      this.copyTimer = setTimeout(() => this.copied.set(false), 2000);
    });
  }

  private show(fileId: string): void {
    const dialog = this.dialog().nativeElement;
    if (!dialog.open) {
      const active = document.activeElement;
      this.trigger = active instanceof HTMLElement ? active : null;
      openModal(dialog);
      this.heading().nativeElement.focus();
    }
    this.load(fileId);
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

  private load(fileId: string): void {
    this.subscription?.unsubscribe();
    this.state.set({ phase: 'loading' });
    this.copied.set(false);
    this.subscription = this.api.file(fileId, this.url.dir()).subscribe({
      next: (value) => this.state.set({ phase: 'content', value }),
      error: (error: unknown) => this.state.set({ phase: 'error', kind: classifyError(error) })
    });
  }

  private scrollToLine(line: number): void {
    const element = this.dialog().nativeElement.querySelector(`[data-line="${line}"]`);
    element?.scrollIntoView?.({ block: 'center' });
  }
}
