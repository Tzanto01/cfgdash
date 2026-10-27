import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { apiErrorCode } from './api-client';

export type AccessProblem =
  | { kind: 'signed-out' }
  | { kind: 'forbidden'; reason: string }
  | { kind: 'bad-host' };

/** App-level access state; while set, the app shows only the access panel. */
@Injectable({ providedIn: 'root' })
export class AccessState {
  readonly problem = signal<AccessProblem | null>(null);
}

/** Classifies 401, 403 and `400 bad-host` answers into {@link AccessState}. */
export const accessInterceptor: HttpInterceptorFn = (req, next) => {
  const access = inject(AccessState);
  return next(req).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse) {
        if (error.status === 401) {
          access.problem.set({ kind: 'signed-out' });
        } else if (error.status === 403) {
          const body = error.error as { reason?: unknown } | null;
          const reason = body && typeof body.reason === 'string' ? body.reason : 'unknown';
          access.problem.set({ kind: 'forbidden', reason });
        } else if (error.status === 400 && apiErrorCode(error) === 'bad-host') {
          access.problem.set({ kind: 'bad-host' });
        }
      }
      return throwError(() => error);
    })
  );
};
