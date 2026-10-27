import { afterRenderEffect, ChangeDetectionStrategy, Component, computed, ElementRef, inject, signal, untracked, viewChild } from '@angular/core';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { ApiClient, classifyError, ErrorKind } from './api-client';
import { DirsResponse } from './models';
import { SnapshotStore } from './snapshot-store';
import { Icon } from './ui/icon';
import { Announcer, closeModal, FolderPickerState, openModal } from './ui-state';

/** Modal folder picker; browsing never changes `dir` until "Use this folder". */
@Component({
  selector: 'app-folder-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  templateUrl: './folder-picker.html'
})
export class FolderPicker {
  private readonly api = inject(ApiClient);
  private readonly router = inject(Router);
  private readonly store = inject(SnapshotStore);
  private readonly announcer = inject(Announcer);
  protected readonly picker = inject(FolderPickerState);
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
  private readonly heading = viewChild.required<ElementRef<HTMLElement>>('heading');
  private trigger: HTMLElement | null = null;
  private subscription?: Subscription;

  protected readonly typed = signal('');
  protected readonly fieldError = signal('');
  protected readonly filter = signal('');
  protected readonly loading = signal(false);
  protected readonly error = signal<ErrorKind | null>(null);
  protected readonly current = signal<DirsResponse | null>(null);
  private lastRequest: { path?: string; fromField: boolean } = { fromField: false };

  protected readonly entries = computed(() => {
    const entries = this.current()?.entries ?? [];
    const filter = this.filter().trim().toLowerCase();
    return filter ? entries.filter((e) => e.name.toLowerCase().includes(filter)) : entries;
  });

  constructor() {
    afterRenderEffect(() => {
      const open = this.picker.open();
      untracked(() => (open ? this.show() : this.hide()));
    });
  }

  protected onTyped(event: Event): void {
    this.typed.set((event.target as HTMLInputElement).value);
    this.fieldError.set('');
  }

  protected onFilter(event: Event): void {
    this.filter.set((event.target as HTMLInputElement).value);
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    const path = this.typed().trim();
    if (!path) {
      this.fieldError.set("That folder doesn't exist or isn't an absolute path.");
      return;
    }
    this.browse(path, true);
  }

  protected browse(path: string | undefined, fromField = false, thenUse = false): void {
    this.subscription?.unsubscribe();
    this.lastRequest = { path, fromField };
    this.loading.set(true);
    this.error.set(null);
    this.subscription = this.api.dirs(path).subscribe({
      next: (value) => {
        this.current.set(value);
        this.typed.set(value.path);
        this.filter.set('');
        this.fieldError.set('');
        this.loading.set(false);
        if (thenUse) {
          this.choose(value.path);
        }
      },
      error: (error: unknown) => {
        this.loading.set(false);
        const kind = classifyError(error);
        if (kind === 'bad-dir' && fromField) {
          this.fieldError.set("That folder doesn't exist or isn't an absolute path.");
        } else if (kind === 'bad-dir' && path) {
          this.browse(undefined);
        } else {
          this.error.set(kind);
        }
      }
    });
  }

  protected retry(): void {
    this.browse(this.lastRequest.path, this.lastRequest.fromField);
  }

  protected cancel(): void {
    this.picker.open.set(false);
  }

  protected onCancel(event: Event): void {
    event.preventDefault();
    this.cancel();
  }

  protected use(): void {
    const typed = this.typed().trim();
    if (typed && typed !== this.current()?.path) {
      this.browse(typed, true, true);
      return;
    }
    this.choose(this.current()?.path);
  }

  protected onClosed(): void {
    if (this.picker.open()) {
      this.picker.open.set(false);
    }
  }

  private choose(path: string | undefined): void {
    if (!path) {
      return;
    }
    this.picker.open.set(false);
    if (path !== this.store.snapshot()?.context.targetDir) {
      void this.router.navigate([], {
        queryParams: { dir: path, file: null, line: null },
        queryParamsHandling: 'merge'
      });
      this.announcer.announce(`Folder changed to ${path}.`);
    }
  }

  protected isHidden(name: string): boolean {
    return name.startsWith('.');
  }

  private show(): void {
    const dialog = this.dialog().nativeElement;
    if (dialog.open) {
      return;
    }
    const active = document.activeElement;
    this.trigger = active instanceof HTMLElement ? active : null;
    const start = this.store.snapshot()?.context.targetDir;
    this.typed.set(start ?? '');
    this.fieldError.set('');
    this.filter.set('');
    this.current.set(null);
    openModal(dialog);
    this.heading().nativeElement.focus();
    this.browse(start);
  }

  private hide(): void {
    this.subscription?.unsubscribe();
    const dialog = this.dialog().nativeElement;
    if (dialog.open) {
      closeModal(dialog);
      if (this.trigger?.isConnected) {
        this.trigger.focus();
      }
    }
    this.trigger = null;
  }
}
