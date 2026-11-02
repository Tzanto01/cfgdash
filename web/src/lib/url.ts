import { useLocation, useNavigate } from 'react-router';

/** The URL state every page reads; the first value of a repeated parameter wins. */
export interface UrlParams {
  dir?: string;
  file?: string;
  detail?: string;
  line?: number;
  q?: string;
  /** `edit=1`: the Settings view in edit mode (edits-v1). */
  edit: boolean;
}

function param(params: URLSearchParams, key: string): string | undefined {
  return params.get(key) || undefined;
}

export function readParams(search: string): UrlParams {
  const params = new URLSearchParams(search);
  const line = Number(param(params, 'line'));
  return {
    dir: param(params, 'dir'),
    file: param(params, 'file'),
    detail: param(params, 'detail'),
    line: Number.isInteger(line) && line > 0 ? line : undefined,
    q: param(params, 'q'),
    edit: params.get('edit') === '1'
  };
}

/** `search` with `changes` applied: a string sets the parameter, `undefined` removes it. Returns `?...` or `''`. */
export function mergeSearch(search: string, changes: Record<string, string | undefined>): string {
  const params = new URLSearchParams(search);
  for (const [key, value] of Object.entries(changes)) {
    if (value === undefined) {
      params.delete(key);
    } else {
      params.set(key, value);
    }
  }
  const text = params.toString();
  return text ? `?${text}` : '';
}

/** An internal link to `path` that carries `dir`. */
export function linkTo(path: string, dir: string | undefined): string {
  return dir ? `${path}?${new URLSearchParams({ dir })}` : path;
}

/** The decoded URL fragment without `#`, or `''`. */
export function readFragment(hash: string): string {
  const raw = hash.startsWith('#') ? hash.slice(1) : hash;
  try {
    return decodeURIComponent(raw);
  } catch {
    return raw;
  }
}

interface InAppState {
  fileInApp?: boolean;
}

/** URL state plus the file and detail viewer navigation. */
export function useUrl() {
  const location = useLocation();
  const navigate = useNavigate();
  const params = readParams(location.search);

  const update = (changes: Record<string, string | undefined>, replace: boolean, state?: InAppState) =>
    navigate(
      { pathname: location.pathname, search: mergeSearch(location.search, changes), hash: location.hash },
      { replace, state }
    );

  return {
    ...params,
    path: location.pathname,
    fragment: readFragment(location.hash),
    /** An internal link to `path` that carries the current `dir`. */
    link: (path: string) => linkTo(path, params.dir),
    openFile: (fileId: string, line?: number) =>
      update({ file: fileId, line: line ? String(line) : undefined, detail: undefined }, false, { fileInApp: true }),
    openDetail: (detailId: string) =>
      update({ detail: detailId, file: undefined, line: undefined }, false, { fileInApp: true }),
    /** Goes back when the viewer was opened in the app, else replaces the URL without the viewer params. */
    closeFile: () => {
      if ((location.state as InAppState | null)?.fileInApp) {
        void navigate(-1);
      } else {
        void update({ file: undefined, line: undefined, detail: undefined }, true);
      }
    }
  };
}
