import type { JsonValue } from './models';

const numberFormat = new Intl.NumberFormat('en-US');
const timeFormat = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });

export function formatCount(value: number): string {
  return numberFormat.format(value);
}

/** 18400 -> "18.0 KB" (1024-based). */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  const units = ['KB', 'MB', 'GB'];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(1)} ${units[unit]}`;
}

export function formatTokens(tokens: number): string {
  return `about ${numberFormat.format(tokens)} tokens`;
}

export function formatTime(iso: string): string {
  const date = new Date(iso);
  return Number.isNaN(date.getTime()) ? iso : timeFormat.format(date);
}

export function formatRelative(iso: string, now: number): string {
  const time = new Date(iso).getTime();
  if (Number.isNaN(time)) {
    return '';
  }
  const minutes = Math.floor((now - time) / 60_000);
  if (minutes < 1) {
    return 'just now';
  }
  if (minutes < 60) {
    return `${minutes} min ago`;
  }
  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours} h ago`;
  }
  return `${Math.floor(hours / 24)} d ago`;
}

/** One-line rendering of a JSON value for compact places (overridden lines, snippets). */
export function inlineValue(value: JsonValue | undefined): string {
  if (value === undefined || value === null) {
    return 'not set';
  }
  const text = typeof value === 'string' ? value : JSON.stringify(value);
  return text.length > 120 ? `${text.slice(0, 119)}…` : text;
}

/** DOM id of a block item: `<collectorId>/<blockId>/<itemId>` with whitespace replaced by `_`. */
export function itemDomId(collectorId: string, blockId: string, itemId: string): string {
  return `${collectorId}/${blockId}/${normalizeItemId(itemId)}`;
}

export function normalizeItemId(itemId: string): string {
  return itemId.replace(/\s/g, '_');
}

export interface TextSegment {
  text: string;
  match: boolean;
}

/** Splits text into segments where every case-insensitive occurrence of `query` is marked. */
export function highlight(text: string, query: string): TextSegment[] {
  if (!query) {
    return [{ text, match: false }];
  }
  const pattern = new RegExp(`(${query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')})`, 'giu');
  return text
    .split(pattern)
    .map((part, index) => ({ text: part, match: index % 2 === 1 }))
    .filter((segment) => segment.text.length > 0);
}
