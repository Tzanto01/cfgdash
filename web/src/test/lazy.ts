import { waitFor } from '@testing-library/react';
import type { AppHarness, RequestRecord } from './harness';

/** Like `AppHarness.answer`, but first waits for the request: the sheets, dialogs and search page are lazy-loaded. */
export async function answerWhenSent(h: AppHarness, path: string, body: unknown, status = 200): Promise<RequestRecord> {
  await waitFor(() => {
    if (!h.pending((request) => request.url.pathname === path)) {
      throw new Error(`${path} was not requested yet`);
    }
  });
  return h.answer(path, body, status);
}

/** The query parameters of the current location. */
export function currentParams(): URLSearchParams {
  return new URLSearchParams(window.location.search);
}
