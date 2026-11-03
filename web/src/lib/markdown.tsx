import { memo, useEffect, useRef, useState, type ReactNode } from 'react';

/** The label on every code block of model output (assistant-v1 UI rules). */
export const SUGGESTED_LABEL = 'model output derived from your files; review before pasting';

type MdBlock =
  | { type: 'code'; text: string }
  | { type: 'heading'; text: string }
  | { type: 'list'; ordered: boolean; start: number; items: string[] }
  | { type: 'p'; text: string };

const FENCE = /^\s*(`{3,}(?=[^`]*$)|~{3,})/;
const HEADING = /^\s{0,3}#{1,6}\s+(.*)$/;
const ITEM = /^\s*([-*+]|\d{1,9}[.)])\s+(.*)$/;
const INLINE = /(`[^`\n]{1,2000}`)|(!\[([^\]\n]{0,500})\]\(([^)\n]{0,2048})\))|(\[([^\]\n]{1,500})\]\(([^)\n]{0,2048})\))|(\*\*([^*\n]{1,500})\*\*)|(<(https?:\/\/[^>\s]{1,2048})>)/g;
const HIDDEN = /[\u00AD\u061C\u200B-\u200F\u202A-\u202E\u2060-\u2064\u2066-\u2069\uFEFF]/g;

/** `text` with bidi and zero-width characters shown as U+FFFD, so what is displayed is what is written. */
export const showHidden = (text: string) => text.replace(HIDDEN, '\uFFFD');

/** A URL for display: normalized (punycode host) when it parses, else as written. */
function shownUrl(raw: string): string {
  const url = raw.trim().split(/\s/)[0] ?? '';
  try {
    return new URL(url).href;
  } catch {
    return url;
  }
}

function parse(source: string): MdBlock[] {
  const lines = source.replace(HIDDEN, '\uFFFD').replace(/\r\n?/g, '\n').split('\n');
  const blocks: MdBlock[] = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    const fence = FENCE.exec(line);
    if (fence) {
      const body: string[] = [];
      i++;
      while (i < lines.length && !(lines[i].trim().startsWith(fence[1]) && /^[`~]+$/.test(lines[i].trim()))) {
        body.push(lines[i]);
        i++;
      }
      i++;
      blocks.push({ type: 'code', text: body.join('\n') });
      continue;
    }
    if (!line.trim()) {
      i++;
      continue;
    }
    const heading = HEADING.exec(line);
    if (heading) {
      blocks.push({ type: 'heading', text: heading[1] });
      i++;
      continue;
    }
    const item = ITEM.exec(line);
    if (item) {
      const ordered = /\d/.test(item[1]);
      const items: string[] = [];
      while (i < lines.length && lines[i].trim()) {
        const next = ITEM.exec(lines[i]);
        if (next && /\d/.test(next[1]) === ordered) {
          items.push(next[2]);
        } else if (next || FENCE.test(lines[i]) || HEADING.test(lines[i])) {
          break;
        } else {
          items[items.length - 1] += ` ${lines[i].trim()}`;
        }
        i++;
      }
      blocks.push({ type: 'list', ordered, start: ordered ? Number.parseInt(item[1], 10) : 1, items });
      continue;
    }
    const text: string[] = [];
    while (i < lines.length && lines[i].trim() && !FENCE.test(lines[i]) && !HEADING.test(lines[i]) && !ITEM.test(lines[i])) {
      text.push(lines[i].trim());
      i++;
    }
    blocks.push({ type: 'p', text: text.join(' ') });
  }
  return blocks;
}

function inline(text: string): ReactNode[] {
  const out: ReactNode[] = [];
  let last = 0;
  for (const m of text.matchAll(INLINE)) {
    const at = m.index ?? 0;
    if (at > last) {
      out.push(text.slice(last, at));
    }
    const key = out.length;
    if (m[1]) {
      out.push(
        <code key={key} className="rounded bg-hover px-1 font-mono text-[12.5px]">
          {m[1].slice(1, -1)}
        </code>
      );
    } else if (m[2]) {
      out.push(`[image: ${m[3] || 'no description'}]`);
    } else if (m[5]) {
      const url = shownUrl(m[7]);
      out.push(m[6] === url || m[6] === m[7].trim() ? url : `${m[6]} (${url})`);
    } else if (m[8]) {
      out.push(<strong key={key}>{m[9]}</strong>);
    } else {
      out.push(shownUrl(m[11]));
    }
    last = at + m[0].length;
  }
  if (last < text.length) {
    out.push(text.slice(last));
  }
  return out;
}

/** A code block of model output with Copy (the block text only) and a Wrap toggle, wrapped by default. */
function CodeBlock({ text }: { text: string }) {
  const [wrap, setWrap] = useState(true);
  const [copied, setCopied] = useState('');
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);
  const feedback = (value: string) => {
    setCopied(value);
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setCopied(''), 2000);
  };
  const copy = () => {
    Promise.resolve()
      .then(() => navigator.clipboard.writeText(text))
      .then(
        () => feedback('Copied'),
        () => feedback("Couldn't copy")
      );
  };
  const tool = 'touch-target h-6 rounded px-1.5 text-xs font-medium text-text hover:bg-hover';
  return (
    <figure className="rounded-md border border-border bg-raised">
      <figcaption className="flex flex-wrap items-center gap-x-2 border-b border-border px-2 py-1">
        <span className="min-w-0 flex-1 text-xs text-warn">{SUGGESTED_LABEL}</span>
        <span role="status" className="text-xs text-muted">
          {copied}
        </span>
        <button type="button" aria-pressed={wrap} onClick={() => setWrap(!wrap)} className={tool}>
          Wrap
        </button>
        <button type="button" onClick={copy} className={tool}>
          Copy
        </button>
      </figcaption>
      <pre
        tabIndex={wrap ? undefined : 0}
        aria-label={wrap ? undefined : 'Code, scrolls sideways'}
        className={wrap ? 'p-2 font-mono text-[12.5px] leading-[18px] whitespace-pre-wrap wrap-anywhere' : 'overflow-x-auto p-2 font-mono text-[12.5px] leading-[18px]'}
      >
        <code>{text}</code>
      </pre>
    </figure>
  );
}

/**
 * Model text as markdown, built from React text nodes only: raw HTML stays literal text, images become their alt text,
 * links show their full URL as text, bidi and zero-width characters become U+FFFD, and code blocks carry
 * {@link SUGGESTED_LABEL}.
 */
export const Markdown = memo(function Markdown({ text }: { text: string }) {
  return (
    <div className="space-y-2 break-words">
      {parse(text).map((block, index) => {
        switch (block.type) {
          case 'code':
            return <CodeBlock key={index} text={block.text} />;
          case 'heading':
            return (
              <p key={index} className="font-semibold">
                {inline(block.text)}
              </p>
            );
          case 'list': {
            const List = block.ordered ? 'ol' : 'ul';
            return (
              <List key={index} start={block.ordered && block.start !== 1 ? block.start : undefined} className={block.ordered ? 'list-decimal space-y-1 pl-5' : 'list-disc space-y-1 pl-5'}>
                {block.items.map((item, i) => (
                  <li key={i}>{inline(item)}</li>
                ))}
              </List>
            );
          }
          default:
            return <p key={index}>{inline(block.text)}</p>;
        }
      })}
    </div>
  );
});
