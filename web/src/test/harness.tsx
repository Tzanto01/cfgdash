import { act, render } from '@testing-library/react';
import { RouterProvider } from 'react-router';
import { onTestFinished, vi } from 'vitest';
import type { Snapshot } from '../lib/models';
import { createAppRouter } from '../shell/App';
import { makeSnapshot } from './fixtures';

/** One request the app sent through `fetch`. */
export interface RequestRecord {
  url: URL;
  aborted: boolean;
  method: string;
  /** Request headers with lower-cased names. */
  headers: Record<string, string>;
  /** The raw request body, or null. */
  body: string | null;
}

interface Pending extends RequestRecord {
  respond(body: unknown, status: number): void;
}

export interface AppHarness {
  /** `document.body`: the app plus anything portaled (sheets, dialogs, tooltips). */
  el: HTMLElement;
  /** Every request sent so far, in order. */
  requests: RequestRecord[];
  /** Answers the one pending request whose pathname equals `path`; throws unless exactly one matches. */
  answer(path: string, body: unknown, status?: number): Promise<RequestRecord>;
  /** The first pending request matching `predicate`. */
  pending(predicate: (request: RequestRecord) => boolean): RequestRecord | undefined;
  navigate(url: string): Promise<void>;
  /** Pathname, search and hash of the current location. */
  url(): string;
  /** Whitespace-collapsed text of the page. */
  text(): string;
  /** Buttons whose accessible name (`aria-label`, else text) equals `name` or matches the RegExp. */
  buttons(name: RegExp | string): HTMLButtonElement[];
  /** The single button named `name`; throws when there are none or several. */
  button(name: RegExp | string): HTMLButtonElement;
  click(element: Element): Promise<void>;
  /** Flushes pending promise callbacks and React work. */
  settle(): Promise<void>;
}

function matches(text: string, name: RegExp | string): boolean {
  const value = text.replace(/\s+/g, ' ').trim();
  return typeof name === 'string' ? value === name : name.test(value);
}

/**
 * Boots the real router and App at `url`; only `fetch` is replaced. The snapshot request is answered with `snapshot`
 * unless it is null.
 */
export async function startApp(url: string, snapshot: Snapshot | null = makeSnapshot()): Promise<AppHarness> {
  const requests: RequestRecord[] = [];
  const open: Pending[] = [];

  vi.stubGlobal('fetch', (input: RequestInfo | URL, init?: RequestInit) => {
    return new Promise<Response>((resolve, reject) => {
      const record: Pending = {
        url: new URL(String(input), window.location.origin),
        aborted: false,
        method: init?.method ?? 'GET',
        headers: Object.fromEntries(Object.entries((init?.headers ?? {}) as Record<string, string>).map(([k, v]) => [k.toLowerCase(), v])),
        body: typeof init?.body === 'string' ? init.body : null,
        respond: (body, status) => {
          open.splice(open.indexOf(record), 1);
          resolve({ ok: status >= 200 && status < 300, status, json: async () => body } as Response);
        }
      };
      requests.push(record);
      open.push(record);
      init?.signal?.addEventListener('abort', () => {
        record.aborted = true;
        const index = open.indexOf(record);
        if (index >= 0) {
          open.splice(index, 1);
        }
        reject(new DOMException('The request was aborted.', 'AbortError'));
      });
    });
  });

  window.history.replaceState(null, '', url);
  const router = createAppRouter();
  onTestFinished(() => router.dispose());

  const settle = async (): Promise<void> => {
    await act(async () => {
      for (let i = 0; i < 10; i++) {
        await Promise.resolve();
      }
    });
  };

  const harness: AppHarness = {
    el: document.body,
    requests,
    settle,
    answer: async (path, body, status = 200) => {
      await settle();
      const found = open.filter((r) => r.url.pathname === path);
      if (found.length !== 1) {
        throw new Error(`Expected one pending request for ${path}, found ${found.length}`);
      }
      found[0].respond(body, status);
      await settle();
      return found[0];
    },
    pending: (predicate) => open.find(predicate),
    navigate: async (target) => {
      await act(async () => {
        await router.navigate(target);
      });
      await settle();
    },
    url: () => `${window.location.pathname}${window.location.search}${window.location.hash}`,
    text: () => (document.body.textContent ?? '').replace(/\s+/g, ' '),
    buttons: (name) =>
      [...document.body.querySelectorAll('button')].filter((b) => matches(b.getAttribute('aria-label') ?? b.textContent ?? '', name)),
    button: (name) => {
      const found = harness.buttons(name);
      if (found.length !== 1) {
        throw new Error(`Expected one button ${String(name)}, found ${found.length}`);
      }
      return found[0];
    },
    click: async (element) => {
      await act(async () => {
        (element as HTMLElement).click();
      });
      await settle();
    }
  };

  render(<RouterProvider router={router} />);
  await settle();
  if (snapshot) {
    await harness.answer('/api/v1/snapshot', snapshot);
  }
  return harness;
}
