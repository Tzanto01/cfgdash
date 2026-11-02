import { setAccessProblem } from './access';
import type {
  ApplyResult,
  AssistantStatus,
  AuditResponse,
  CfgdashPatch,
  CfgdashSettings,
  CompareResponse,
  ContextRef,
  DetailContent,
  DirsResponse,
  ExportsResponse,
  FileContent,
  FleetResponse,
  FindingsResponse,
  HealthInfo,
  Proposal,
  SearchResponse,
  SettingChange,
  SettingsResponse,
  Snapshot
} from './models';

/** A non-2xx answer (`status` > 0) or a network failure (`status` 0). */
export class ApiError extends Error {
  readonly status: number;
  readonly code?: string;
  readonly reason?: string;
  /** The rest of the error body (for example `key`, `auditId`, `written` of `/api/v2`). */
  readonly body?: Record<string, unknown>;

  constructor(status: number, code?: string, reason?: string, body?: Record<string, unknown>) {
    super(`API error ${status}${code ? ` ${code}` : ''}`);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.reason = reason;
    this.body = body;
  }
}

/** True for an error caused by aborting the request; never shown as an error state. */
export function isAbort(error: unknown): boolean {
  return typeof error === 'object' && error !== null && (error as { name?: unknown }).name === 'AbortError';
}

function errorFrom(status: number, body: Record<string, unknown> | null): ApiError {
  const code = body?.['error'];
  const reason = body?.['reason'];
  return new ApiError(status, typeof code === 'string' ? code : undefined, typeof reason === 'string' ? reason : undefined, body ?? undefined);
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
    const error = errorFrom(response.status, await response.json().catch(() => null));
    if (!signal?.aborted) {
      noteAccessProblem(error);
    }
    throw error;
  }
  return (await response.json()) as T;
}

/**
 * A guarded `/api/v2` POST, PUT or DELETE (edits-v1): JSON body and `X-Cfgdash-Request: 1`. Only 401 goes to the access panel; every
 * other failure (including 403 from the origin and header checks) is thrown for the caller to show inline.
 */
async function post<T>(path: string, body: unknown, method: 'POST' | 'PUT' | 'DELETE' = 'POST'): Promise<T> {
  let response: Response;
  try {
    response = await fetch(path, {
      method,
      credentials: 'same-origin',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json', 'X-Cfgdash-Request': '1' },
      body: JSON.stringify(body)
    });
  } catch {
    throw new ApiError(0);
  }
  const json = (await response.json().catch(() => null)) as Record<string, unknown> | null;
  if (!response.ok) {
    const error = errorFrom(response.status, json);
    if (error.status === 401) {
      setAccessProblem({ kind: 'signed-out' });
    }
    throw error;
  }
  return json as T;
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
  findings(dir: string | undefined, signal?: AbortSignal): Promise<FindingsResponse> {
    return get<FindingsResponse>('/api/v1/findings', withDir(new URLSearchParams(), dir), signal);
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
  health(signal?: AbortSignal): Promise<HealthInfo> {
    return get<HealthInfo>('/api/v1/health', new URLSearchParams(), signal);
  },
  settings(dir: string | undefined, signal?: AbortSignal): Promise<SettingsResponse> {
    return get<SettingsResponse>('/api/v2/settings', withDir(new URLSearchParams(), dir), signal);
  },
  propose(dir: string, changes: SettingChange[]): Promise<Proposal> {
    return post<Proposal>('/api/v2/proposals', { dir, changes });
  },
  apply(id: string, confirm: string): Promise<ApplyResult> {
    return post<ApplyResult>(`/api/v2/proposals/${encodeURIComponent(id)}/apply`, { confirm });
  },
  audit(signal?: AbortSignal): Promise<AuditResponse> {
    return get<AuditResponse>('/api/v2/audit', new URLSearchParams({ limit: '50' }), signal);
  },
  /** Dry run of an undo (edits-v1): a proposal that restores that apply's backups. */
  revert(auditId: string): Promise<Proposal> {
    return post<Proposal>(`/api/v2/audit/${encodeURIComponent(auditId)}/revert`, {});
  },
  cfgdashSettings(signal?: AbortSignal): Promise<CfgdashSettings> {
    return get<CfgdashSettings>('/api/v2/cfgdash-settings', new URLSearchParams(), signal);
  },
  putCfgdashSettings(values: CfgdashPatch): Promise<CfgdashSettings> {
    return post<CfgdashSettings>('/api/v2/cfgdash-settings', values, 'PUT');
  },
  fleet(signal?: AbortSignal): Promise<FleetResponse> {
    return get<FleetResponse>('/api/v1/fleet', new URLSearchParams(), signal);
  },
  exports(signal?: AbortSignal): Promise<ExportsResponse> {
    return get<ExportsResponse>('/api/v1/exports', new URLSearchParams(), signal);
  },
  compare(dir: string | undefined, against: string, signal?: AbortSignal): Promise<CompareResponse> {
    return get<CompareResponse>('/api/v1/compare', withDir(new URLSearchParams({ against }), dir), signal);
  },
  assistantStatus(signal?: AbortSignal): Promise<AssistantStatus> {
    return get<AssistantStatus>('/api/v2/assistant/status', new URLSearchParams(), signal);
  },
  assistantStart(dir: string, model: string, effort: string): Promise<{ id: string }> {
    return post<{ id: string }>('/api/v2/assistant/sessions', { dir, model, effort });
  },
  assistantMessage(id: string, text: string, context: ContextRef[]): Promise<unknown> {
    return post(`/api/v2/assistant/sessions/${encodeURIComponent(id)}/messages`, { text, context });
  },
  assistantCheck(id: string): Promise<unknown> {
    return post(`/api/v2/assistant/sessions/${encodeURIComponent(id)}/check`, {});
  },
  assistantEnd(id: string): Promise<unknown> {
    return post(`/api/v2/assistant/sessions/${encodeURIComponent(id)}`, {}, 'DELETE');
  },
  /** The URL of a session's server-sent events. */
  assistantEventsUrl(id: string): string {
    return `/api/v2/assistant/sessions/${encodeURIComponent(id)}/events`;
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
