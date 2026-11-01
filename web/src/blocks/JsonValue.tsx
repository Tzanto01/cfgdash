import { EyeOff } from 'lucide-react';
import { useState } from 'react';
import type { JsonValue as Value } from '../lib/models';

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

function shapeOf(value: Value | undefined, redacted: boolean): Shape {
  if (redacted) {
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
}

const MORE = 'touch-target text-xs font-sans text-accent hover:underline';

/** Renders any kv value: text, literals, scalar lists, pretty JSON, "not set" and redacted. */
export function JsonValue({ value, redacted = false }: { value?: Value; redacted?: boolean }) {
  const [expandedFor, setExpandedFor] = useState<{ value: Value | undefined } | null>(null);
  const expanded = expandedFor !== null && expandedFor.value === value;
  const toggle = () => setExpandedFor(expanded ? null : { value });
  const shape = shapeOf(value, redacted);

  switch (shape.kind) {
    case 'redacted':
      return (
        <span className="inline-flex items-center gap-1 text-muted">
          <EyeOff className="size-3.5" strokeWidth={1.75} aria-hidden="true" />
          <span aria-hidden="true">[redacted]</span>
          <span className="sr-only">value redacted</span>
        </span>
      );
    case 'unset':
      return <span className="font-sans text-muted">not set</span>;
    case 'literal':
      return <span>{shape.text}</span>;
    case 'text':
      return (
        <>
          <span className={expanded || !shape.long ? 'break-words whitespace-pre-wrap' : 'line-clamp-3 break-words'}>{shape.text}</span>
          {shape.long && (
            <button type="button" className={MORE} aria-expanded={expanded} onClick={toggle}>
              {expanded ? 'Show less' : 'Show more'}
            </button>
          )}
        </>
      );
    case 'list':
      return (
        <>
          <ul>
            {(expanded ? shape.items : shape.items.slice(0, LIST_PREVIEW)).map((item, index) => (
              <li key={index} className="break-words">
                {item}
              </li>
            ))}
          </ul>
          {shape.items.length > LIST_PREVIEW && (
            <button type="button" className={MORE} aria-expanded={expanded} onClick={toggle}>
              {expanded ? 'Show fewer' : `Show all ${shape.items.length}`}
            </button>
          )}
        </>
      );
    case 'json':
      return (
        <>
          <pre className="overflow-x-auto font-mono whitespace-pre">
            {(expanded ? shape.lines : shape.lines.slice(0, JSON_PREVIEW_LINES)).join('\n')}
          </pre>
          {shape.lines.length > JSON_PREVIEW_LINES && (
            <button type="button" className={MORE} aria-expanded={expanded} onClick={toggle}>
              {expanded ? 'Show less' : 'Show more'}
            </button>
          )}
        </>
      );
  }
}
