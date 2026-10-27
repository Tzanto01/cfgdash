import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type IconName =
  | 'mark'
  | 'search'
  | 'refresh'
  | 'folder'
  | 'folder-up'
  | 'file'
  | 'chevron-right'
  | 'close'
  | 'copy'
  | 'lock'
  | 'sort'
  | 'sort-up'
  | 'sort-down'
  | 'open';

/** Decorative 24x24 stroke icon; the control carrying it provides the accessible name. */
@Component({
  selector: 'app-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'icon', 'aria-hidden': 'true' },
  template: `
    <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.75" stroke-linecap="round" stroke-linejoin="round" focusable="false">
      @switch (name()) {
        @case ('mark') {
          <path d="M12 3 3 7.5l9 4.5 9-4.5z" />
          <path d="m3 12 9 4.5 9-4.5" />
          <path d="m3 16.5 9 4.5 9-4.5" />
        }
        @case ('search') {
          <circle cx="11" cy="11" r="6.5" />
          <path d="m16 16 4.5 4.5" />
        }
        @case ('refresh') {
          <path d="M4.5 12a7.5 7.5 0 0 1 13-5.1L20 9.5" />
          <path d="M20 4.5v5h-5" />
          <path d="M19.5 12a7.5 7.5 0 0 1-13 5.1L4 14.5" />
          <path d="M4 19.5v-5h5" />
        }
        @case ('folder') {
          <path d="M3 6.5A1.5 1.5 0 0 1 4.5 5h4l2 2.5h9A1.5 1.5 0 0 1 21 9v9.5a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5z" />
        }
        @case ('folder-up') {
          <path d="M3 6.5A1.5 1.5 0 0 1 4.5 5h4l2 2.5h9A1.5 1.5 0 0 1 21 9v9.5a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 18.5z" />
          <path d="M12 17.5v-6" />
          <path d="m9.5 14 2.5-2.5 2.5 2.5" />
        }
        @case ('file') {
          <path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z" />
          <path d="M14 3v5h5" />
        }
        @case ('chevron-right') {
          <path d="m9 6 6 6-6 6" />
        }
        @case ('close') {
          <path d="M6 6l12 12M18 6 6 18" />
        }
        @case ('copy') {
          <rect x="9" y="9" width="11" height="11" rx="2" />
          <path d="M5 15V6a2 2 0 0 1 2-2h8" />
        }
        @case ('lock') {
          <rect x="5" y="11" width="14" height="10" rx="2" />
          <path d="M8 11V8a4 4 0 0 1 8 0v3" />
        }
        @case ('sort') {
          <path d="m8 9 4-4 4 4" />
          <path d="m8 15 4 4 4-4" />
        }
        @case ('sort-up') {
          <path d="m7 14 5-5 5 5" />
        }
        @case ('sort-down') {
          <path d="m7 10 5 5 5-5" />
        }
        @case ('open') {
          <path d="M14 4h6v6" />
          <path d="m20 4-9 9" />
          <path d="M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5" />
        }
      }
    </svg>
  `
})
export class Icon {
  readonly name = input.required<IconName>();
}
