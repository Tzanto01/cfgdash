import { ChangeDetectionStrategy, Component, computed, input, linkedSignal } from '@angular/core';
import { JsonValue } from '../models';
import { Icon } from '../ui/icon';

const LONG_TEXT = 240;
const LIST_PREVIEW = 10;
const JSON_PREVIEW_LINES = 12;

type Shape =
  | { kind: 'redacted' }
  | { kind: 'unset' }
  | { kind: 'text'; text: string; long: boolean }
  | { kind: 'literal'; text: string }
  | { kind: 'list'; items: string[] }
  | { kind: 'json'; lines: string[] };

/** Renders any kv value: text, literals, scalar lists, pretty JSON, "not set" and redacted. */
@Component({
  selector: 'app-json-value',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Icon],
  template: `
    @let s = shape();
    @switch (s.kind) {
      @case ('redacted') {
        <span class="muted redacted"><app-icon name="lock" /><span aria-hidden="true">[redacted]</span><span class="sr-only">value redacted</span></span>
      }
      @case ('unset') {
        <span class="muted">not set</span>
      }
      @case ('text') {
        @if (s.kind === 'text') {
          <span class="value-text" [class.clamped]="s.long && !expanded()">{{ s.text }}</span>
          @if (s.long) {
            <button type="button" class="link-button more" [attr.aria-expanded]="expanded()" (click)="expanded.set(!expanded())">
              {{ expanded() ? 'Show less' : 'Show more' }}
            </button>
          }
        }
      }
      @case ('literal') {
        @if (s.kind === 'literal') {
          <span class="literal">{{ s.text }}</span>
        }
      }
      @case ('list') {
        @if (s.kind === 'list') {
          <ul class="value-list">
            @for (item of expanded() ? s.items : s.items.slice(0, listPreview); track $index) {
              <li>{{ item }}</li>
            }
          </ul>
          @if (s.items.length > listPreview) {
            <button type="button" class="link-button more" [attr.aria-expanded]="expanded()" (click)="expanded.set(!expanded())">
              {{ expanded() ? 'Show fewer' : 'Show all ' + s.items.length }}
            </button>
          }
        }
      }
      @case ('json') {
        @if (s.kind === 'json') {
          <pre class="value-json">{{ (expanded() ? s.lines : s.lines.slice(0, jsonPreview)).join('\n') }}</pre>
          @if (s.lines.length > jsonPreview) {
            <button type="button" class="link-button more" [attr.aria-expanded]="expanded()" (click)="expanded.set(!expanded())">
              {{ expanded() ? 'Show less' : 'Show more' }}
            </button>
          }
        }
      }
    }
  `
})
export class JsonValueView {
  readonly value = input<JsonValue | undefined>();
  readonly redacted = input(false);
  protected readonly listPreview = LIST_PREVIEW;
  protected readonly jsonPreview = JSON_PREVIEW_LINES;
  protected readonly expanded = linkedSignal({ source: this.value, computation: () => false });

  protected readonly shape = computed<Shape>(() => {
    const value = this.value();
    if (this.redacted()) {
      return { kind: 'redacted' };
    }
    if (value === undefined || value === null) {
      return { kind: 'unset' };
    }
    if (typeof value === 'string') {
      return { kind: 'text', text: value, long: value.length > LONG_TEXT };
    }
    if (typeof value === 'number' || typeof value === 'boolean') {
      return { kind: 'literal', text: String(value) };
    }
    if (Array.isArray(value) && value.length === 0) {
      return { kind: 'literal', text: '[]' };
    }
    if (Array.isArray(value) && value.every((item) => item === null || typeof item !== 'object')) {
      return { kind: 'list', items: value.map((item) => (typeof item === 'string' ? item : String(item))) };
    }
    return { kind: 'json', lines: JSON.stringify(value, null, 2).split('\n') };
  });
}
