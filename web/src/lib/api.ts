import { setAccessProblem } from './access';
import type { DetailContent, DirsResponse, FileContent, SearchResponse, Snapshot } from './models';

/** A non-2xx answer (`status` > 0) or a network failure (`status` 0). */
export class ApiError extends Error {
  readonly status: number;
  readonly code?: string;
  readonly reason?: string;

  constructor(status: number, code?: string, reason?: string) {
    super(`API error ${status}${code ? ` ${code}` : ''}`);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.reason = reason;
  }
}

/** True for an error caused by aborting the request; never shown as an error state. */
export function isAbort(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { name?: unknown }).name === 'AbortError';
}

async function get<T>(path: string, params: URLSearchParams, signal?: AbortSignal): Promise<T> {
  const query = params.toString();
  let response: Response;
  try {
    response = await fetch(query ? `${path}?${query}` : path, {
      credentials: 'same-origin',
      headers: { Accept: 'application/json' },
      signal
    });
  } catch (error) {
    if (isAbort(error) || signal?.aborted) {
      throw error;
    }
    throw new ApiError(0);
  }
  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { error?: unknown; reason?: unknown } | null;
    const code = typeof body?.error === 'string' ? body.error : undefined;
    const reason = typeof body?.reason === 'string' ? body.reason : undefined;
    const error = new ApiError(response.status, code, reason);
    if (!signal?.aborted) {
      noteAccessProblem(error);
    }
    throw error;
  }
  return (await response.json()) as T;
}

function noteAccessProblem(error: ApiError): void {
  if (error.status === 401) {
    setAccessProblem({ kind: 'signed-out' });
  } else if (error.status === 403) {
    setAccessProblem({ kind: 'forbidden', reason: error.reason ?? 'unknown' });
  } else if (error.status === 400 && error.code === 'bad-host') {
    setAccessProblem({ kind: 'bad-host' });
  }
}

function withDir(params: URLSearchParams, dir?: string): URLSearchParams {
  if (dir) {
    params.set('dir', dir);
  }
  return params;
}

/** Typed client for the cfgdash `/api/v1` surface (api-v1.md). All URLs are relative. */
export const api = {
  snapshot(dir: string | undefined, refresh: boolean, signal?: AbortSignal): Promise<Snapshot> {
    const params = withDir(new URLSearchParams(), dir);
    if (refresh) {
      params.set('refresh', 'true');
    }
    return get<Snapshot>('/api/v1/snapshot', params, signal);
  },
  file(id: string, dir?: string, signal?: AbortSignal): Promise<FileContent> {
    return get<FileContent>(`/api/v1/files/${encodeURIComponent(id)}`, withDir(new URLSearchParams(), dir), signal);
  },
  detail(id: string, dir?: string, signal?: AbortSignal): Promise<DetailContent> {
    return get<DetailContent>(`/api/v1/details/${encodeURIComponent(id)}`, withDir(new URLSearchParams(), dir), signal);
  },
  search(q: string, dir?: string, signal?: AbortSignal): Promise<SearchResponse> {
    return get<SearchResponse>('/api/v1/search', withDir(new URLSearchParams({ q }), dir), signal);
  },
  dirs(path?: string, signal?: AbortSignal): Promise<DirsResponse> {
    return get<DirsResponse>('/api/v1/dirs', path ? new URLSearchParams({ path }) : new URLSearchParams(), signal);
  }
};

/** How a failed request is shown: access errors go to the full-page panel, the rest stay local. */
export type ErrorKind = 'access' | 'bad-dir' | 'not-found' | 'unreachable' | 'other';

export function classifyError(error: unknown): ErrorKind {
  if (!(error instanceof ApiError)) {
    return 'other';
  }
  if (error.status === 401 || error.status === 403 || (error.status === 400 && error.code === 'bad-host')) {
    return 'access';
  }
  if (error.status === 400 && error.code === 'bad-dir') {
    return 'bad-dir';
  }
  if (error.status === 404) {
    return 'not-found';
  }
  if (error.status === 0 || error.status >= 500) {
    return 'unreachable';
  }
  return 'other';
}
