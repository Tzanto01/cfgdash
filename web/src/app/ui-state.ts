import { DestroyRef, inject, Injectable, signal } from '@angular/core';

/** The single polite live region's text. */
@Injectable({ providedIn: 'root' })
export class Announcer {
  readonly message = signal('');
  private timer: ReturnType<typeof setTimeout> | undefined;

  announce(text: string): void {
    clearTimeout(this.timer);
    this.message.set('');
    this.timer = setTimeout(() => this.message.set(text), 100);
  }
}

/** A coarse clock for relative times ("2 min ago"). */
@Injectable({ providedIn: 'root' })
export class Clock {
  readonly now = signal(Date.now());

  constructor() {
    const id = setInterval(() => this.now.set(Date.now()), 30_000);
    inject(DestroyRef).onDestroy(() => clearInterval(id));
  }
}

/** Open state of the folder picker dialog. */
@Injectable({ providedIn: 'root' })
export class FolderPickerState {
  readonly open = signal(false);
}

/** `showModal()` where supported (all target browsers), else the plain `open` attribute. */
export function openModal(dialog: HTMLDialogElement): void {
  if (typeof dialog.showModal === 'function') {
    dialog.showModal();
  } else {
    dialog.setAttribute('open', '');
  }
}

export function closeModal(dialog: HTMLDialogElement): void {
  if (typeof dialog.close === 'function') {
    dialog.close();
  } else {
    dialog.removeAttribute('open');
  }
}
