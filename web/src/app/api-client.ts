import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiError, DetailContent, DirsResponse, FileContent, SearchResponse, Snapshot } from './models';

/** Typed client for the cfgdash `/api/v1` surface (api-v1.md). All URLs are relative. */
@Injectable({ providedIn: 'root' })
export class ApiClient {
  private readonly http = inject(HttpClient);

  snapshot(dir?: string, refresh = false): Observable<Snapshot> {
    let params = withDir(new HttpParams(), dir);
    if (refresh) {
      params = params.set('refresh', 'true');
    }
    return this.http.get<Snapshot>('/api/v1/snapshot', { params });
  }

  file(id: string, dir?: string): Observable<FileContent> {
    return this.http.get<FileContent>(`/api/v1/files/${encodeURIComponent(id)}`, {
      params: withDir(new HttpParams(), dir)
    });
  }

  detail(id: string, dir?: string): Observable<DetailContent> {
    return this.http.get<DetailContent>(`/api/v1/details/${encodeURIComponent(id)}`, {
      params: withDir(new HttpParams(), dir)
    });
  }

  search(q: string, dir?: string): Observable<SearchResponse> {
    return this.http.get<SearchResponse>('/api/v1/search', {
      params: withDir(new HttpParams().set('q', q), dir)
    });
  }

  dirs(path?: string): Observable<DirsResponse> {
    const params = path ? new HttpParams().set('path', path) : new HttpParams();
    return this.http.get<DirsResponse>('/api/v1/dirs', { params });
  }
}

function withDir(params: HttpParams, dir?: string): HttpParams {
  return dir ? params.set('dir', dir) : params;
}

/** How a failed request is shown: access errors go to the full-page panel, the rest stay local. */
export type ErrorKind = 'access' | 'bad-dir' | 'not-found' | 'unreachable' | 'other';

export function classifyError(error: unknown): ErrorKind {
  if (!(error instanceof HttpErrorResponse)) {
    return 'other';
  }
  const code = apiErrorCode(error);
  if (error.status === 401 || error.status === 403 || (error.status === 400 && code === 'bad-host')) {
    return 'access';
  }
  if (error.status === 400 && code === 'bad-dir') {
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

export function apiErrorCode(error: HttpErrorResponse): string | undefined {
  const body = error.error as Partial<ApiError> | null;
  return body && typeof body === 'object' && typeof body.error === 'string' ? body.error : undefined;
}
