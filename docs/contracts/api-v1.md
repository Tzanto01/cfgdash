# Contract: HTTP API v1 and CLI

Owner: department lead. Producer: `host`. Consumer: `web` (and later the fleet view). The `/api/v1` surface is stable: additive optional fields only; anything else goes to `/api/v2`.

## Auth (every `/api/*` route)

A request is authenticated when it carries the per-run token as `Authorization: Bearer <token>` or a valid `cfgdash_session` cookie. Static UI files and `GET /` are public (they contain no data). `GET /?token=<token>` with a valid token sets the cookie and answers `302 Location: /` so the token leaves the address bar. The cookie never holds the raw token: its value is base64url HMAC-SHA256 keyed by the token over `cfgdash_session:ui` or `cfgdash_session:tailnet`, accepted only on the listener that issued it. Failures: `401 {"error":"unauthenticated"}`, `403 {"error":"forbidden","reason":"..."}` (tailnet identity), `400 {"error":"bad-host"}` (Host not allowed). Full rules: [security-v1](security-v1.md).

## Endpoints

| Method + path | Answer |
| --- | --- |
| `GET /api/v1/health` | `{ "status": "ok", "version": "0.1.0", "schemaVersion": 1, "edits": bool }` (`edits` true only on the UI listener, see [edits-v1](edits-v1.md)) |
| `GET /api/v1/snapshot?dir=<abs path>&refresh=true` | the [snapshot](snapshot-v1.md) for `dir` (default: the run's start dir). Cached per dir until `refresh=true`. `dir` must be an existing absolute directory, else `400 {"error":"bad-dir"}` |
| `GET /api/v1/files/{id}?dir=<abs path>` | `{ "id", "path", "kind", "contentType": "markdown" \| "json" \| "text", "content", "truncated": bool }` for a file registered by the snapshot of `dir`. JSON files are re-serialized after redaction; text passes value-pattern redaction. Max 512 KiB, then `truncated: true`. Unknown id: `404 {"error":"not-found"}` |
| `GET /api/v1/details/{detailId}?dir=<abs path>` | `{ "id", "title", "contentType": "markdown" \| "json" \| "text", "content", "truncated": bool }` for a `detailId` emitted by the cached snapshot of `dir` (URL-encoded; form `<collectorId>:<key>`). The owning collector resolves it on demand; the core redacts (JSON re-serialized after redaction, text value-pattern redaction), caps at 512 KiB (`truncated: true`) and times out after 20 s (`503 {"error":"unavailable"}`). Id not in that snapshot or unknown to the collector: `404 {"error":"not-found"}`. Details are not part of search |
| `GET /api/v1/search?q=<text>&dir=<abs path>` | `{ "query", "results": [ { "kind": "row" \| "node" \| "file", "view", "collectorId", "blockId", "itemId"?, "fileId"?, "line"?, "snippet" } ], "truncated": bool }`. Case-insensitive substring over every block value/label in the snapshot and the redacted content of every registered file. Max 200 results. `q` shorter than 2 chars: `400 {"error":"bad-query"}` |
| `GET /api/v1/dirs?path=<abs path>` | `{ "path", "parent", "entries": [ { "name", "path" } ], "suggestions": [ { "label", "path" } ] }`. Directories only, sorted, max 500, hidden dirs included. `suggestions`: start dir, workspace root, home, config repo, project dirs Claude Code knows (keys of `projects` in `.claude.json`, existing ones only). Missing `path`: answers for the start dir |
| `GET /api/v1/findings?dir=<abs path>&refresh=true` | health findings for `dir`, see [health-v1](health-v1.md) |
| `POST /api/v1/shutdown` | `202`, then graceful stop. Bearer header only (cookie rejected) |

Every other method or path under `/api/` answers 404/405. No `/api/v1` endpoint writes, edits or deletes configuration; guarded edits live under `/api/v2` ([edits-v1](edits-v1.md)). Responses carry `Cache-Control: no-store`.

## CLI (`cfgdash`)

```
cfgdash [run] [--port <n>] [--dir <path>] [--tailnet] [--no-open]
cfgdash stop
cfgdash update [--check]
cfgdash export [--dir <path>]
cfgdash doctor [--dir <path>] [--json]
cfgdash --version
```

- `run` (default): starts the server on `127.0.0.1:<port>` (default 7820; `--port 0` picks a free port), prints `http://127.0.0.1:<port>/?token=<token>`, opens the browser only when a desktop session is present (Windows: interactive and not under SSH; Linux/macOS: `DISPLAY` or `WAYLAND_DISPLAY` set and not under SSH). Headless: prints the URL and recommends `--tailnet`. A second `run` while one is alive prints the live URL and exits 0.
- `--tailnet`: see security-v1. Prints `https://<machine>.<tailnet>.ts.net:<port>/?token=<token>` and the allowed login.
- `stop`: reads the run-state file, calls `POST /api/v1/shutdown`, waits up to 10 s, otherwise stops the recorded process (PID + start time + exe path must match) and removes the recorded tailscale serve handler itself. Exit 0 when nothing is running.
- `update`: `gh release download` of the latest release asset for this OS/arch from the cfgdash repo, verifies it against `SHA256SUMS`, replaces the installed binary (Windows: rename the running exe to `.old`, write the new one, delete `.old` on next start; Linux: write next to it and `rename`). Refuses while an instance is running (prints `cfgdash stop`). `--check` only compares versions. Without gh or gh auth: exit 2 with the reason.
- `export`: builds the snapshot and writes `<cache>/exports/<hostname>-<UTC timestamp>.json`, prints the path. Never writes anywhere else.
- `doctor`: see [health-v1](health-v1.md).
- Exit codes: 0 ok, 1 error, 2 prerequisite missing (for example gh, tailscale, HTTPS certs), 3 `doctor` found a critical finding.

## Run state and cache (all under the user cache dir: Windows `%LOCALAPPDATA%\cfgdash`, Linux `$XDG_CACHE_HOME/cfgdash` or `~/.cache/cfgdash`)

- `run.json`: `{ pid, processStart, exePath, port, token, startDir, tailnet: { httpsPort, backendPort, dnsName } | null, startedAt }`. Created with user-only permissions (Linux dir 0700, file 0600), deleted on clean stop.
- `logs/cfgdash-<date>.log`: startup, shutdown, request method + path + status (never query strings, tokens, headers or file contents).
- `exports/`: see `export`.
