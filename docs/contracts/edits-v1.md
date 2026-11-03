# Contract: guarded edits v1 (`/api/v2`)

Owner: department lead. Producer: team `edits` (core, host), which also adds the `/api/v2` routes to `web/mock` so the UI can be built against them. Consumer: team `web`, which builds the settings screen in the React UI ([redesign plan](../plans/redesign.md)); team `edits` adds no Angular code. Decided by Nico on 2026-10-28: cfgdash is no longer read-only; edits cover the config auto-pull switch and Claude Code `settings.json` keys; they are applied from localhost only; they are reached through a Settings button in the dashboard that opens a settings screen in the style of T3 Code's settings (category nav, grouped cards, one row per setting with a toggle, select or text control, restore default).

## Scope

Writable targets, nothing else:

| Target | File | Notes |
| --- | --- | --- |
| Claude Code settings, scope `user` | `<claudeConfigDir>/settings.json` | Layer 5 of load-and-merge-v1 |
| Claude Code settings, scope `project` | `<target dir>/.claude/settings.json` | Layer 4; created on first write |
| Claude Code settings, scope `local` | `.claude/settings.local.json` at the layer-3 location for the target dir | Layer 3; created on first write |
| Config auto-pull | `<configRepo>/.claude/machine.json` key `configAutoPull` (bool) | Gitignored in the config repo; created on first write. `CC_CONFIG_AUTOPULL` still wins and the UI says so |

Never written: managed settings (files, registry, remote cache), `.claude.json`, credential files (security-v1 never-read list), hook-script and job files, instruction, agent, skill and command files except through [edits-v2](edits-v2.md), cfgdash's own `overrides.json`, git state. cfgdash never commits; when a written file is tracked by git the UI flags that the repo is now dirty.

## Settings catalog

Edits address catalog entries, never free-form paths. Each entry: `key` (dot path, e.g. `permissions.defaultMode`, `env.<NAME>`, `enabledPlugins.<id>`), `category`, `label`, `description`, `type` (`bool` | `enum` | `string` | `number` | `stringList`), `options` for enums, allowed scopes, and the doc page it came from. The catalog covers the documented Claude Code settings keys (code.claude.com/docs/en/settings), read at build time; keys outside the catalog are shown read-only. Not editable in v1: `hooks`, `mcpServers`, managed-only keys, `apiKeyHelper`, `awsAuthRefresh`, `awsCredentialExport`, `otelHeadersHelper`, and any key or `env` name the redactor treats as secret (shown, never editable).

## Endpoints (UI listener only)

All `/api/v2/*` routes exist only on the `127.0.0.1` UI listener. On the tailnet listener they answer 404 and `GET /api/v1/health` reports `"edits": false`; on the UI listener `"edits": true`.

| Method + path | Answer |
| --- | --- |
| `GET /api/v2/settings?dir=<abs path>` | `{ "categories": [ { "id", "label", "settings": [ { "key", "label", "description", "type", "options"?, "scopes": ["user","project","local"], "values": { "<scope>": value \| null }, "effective": value \| null, "effectiveSource", "editable": bool, "reason"? } ] } ], "files": [ { "scope", "path", "exists", "gitTracked": bool } ] }`. Values pass through the redactor. Includes a `config` category with `configAutoPull` (scope `machine`) and the env override note |
| `POST /api/v2/proposals` | Body `{ "dir", "changes": [ { "key", "scope", "value" } \| { "key", "scope", "unset": true } ] }`. Dry run, nothing written. Validates each change against the catalog (type, options, scope, editable). Answers `{ "id", "expiresAt", "files": [ { "path", "scope", "sha256Before" \| null, "diff" } ], "confirm" }` where `diff` is a unified diff of the redacted before and after text and `confirm` is the SHA-256 over the ordered file paths, `sha256Before` values and full after-contents. Errors: `400 {"error":"invalid-change","key","reason"}` |
| `POST /api/v2/proposals/{id}/apply` | Body `{ "confirm": "<value from the proposal>" }`. Single use, expires 10 minutes after creation. Re-reads every file: when its SHA-256 differs from `sha256Before` (or it appeared or vanished) answers `409 {"error":"stale"}` and writes nothing. Otherwise writes all files (each atomically: temp file in the same dir, flush, replace), appends the audit entry and answers `{ "applied": true, "auditId" }`. Wrong or missing confirm: `400 {"error":"confirm-mismatch"}`; unknown or expired id: `404` |
| `POST /api/v2/audit/{auditId}/revert` | Body `{}`. Dry run like `/proposals`: builds a proposal whose after-content is that apply's backup of each file (a file the apply created is deleted). Answers the proposal shape; apply it with the normal `/apply` and confirm. Unknown id or no backup: 404. If any file's current SHA-256 differs from that apply's `sha256After`: `409 {"error":"stale"}`, nothing proposed. The audit entry of the revert names `revertOf` |
| `GET /api/v2/audit?limit=50` | `{ "entries": [ { "auditId", "at", "listener", "kind"?, "origin"?, "sessionId"?, "revertOf"?, "files": [ { "path", "scope", "sha256Before", "sha256After" } ], "changes": [ { "key", "scope", "action" } ] } ] }` newest first. `kind` is `cfgdash-settings` on settings lines; `origin` and `sessionId` mark assistant proposals ([assistant-v1](assistant-v1.md)); `revertOf` names the reverted `auditId` |

Request rules for every `POST /api/v2/*`: authenticated as in api-v1; `Origin` must equal the UI listener origin (`http://127.0.0.1:<port>` or `http://localhost:<port>`); header `X-Cfgdash-Request: 1` required; JSON body max 64 KiB. Anything else `403 {"error":"forbidden"}`.

## Writing

- JSON is parsed with the same strict rules as load-and-merge-v1; a target file that fails to parse is not edited (`400 invalid-change`, reason `parse-error`). Writes keep key order, use 2-space indentation and a trailing newline; unset removes the key and then any object it leaves empty.
- Before writing, the previous content of each file is copied to `<cache dir>/edits/backups/<auditId>/`. The audit log `<cache dir>/edits/audit.jsonl` is append-only, one JSON object per line, values redacted; cfgdash never rewrites or truncates it.
- After an apply the host drops its cached snapshots so the next snapshot shows the new state.

## UI

Editing lives in the Settings view (Nico, 2026-11-02): an "Edit" toggle on that view (only when health says `edits: true`) turns it into the editor; the header gear is cfgdash's own settings ([cfgdash-config-v1](cfgdash-config-v1.md)), and `/edit` redirects to the Settings view in edit mode. The editor has a category nav with search, cards of rows, a scope switch (User / Project / Local, plus Machine for auto-pull) that shows where the effective value comes from, "Restore default" per row (unset in that scope). Changes are staged; "Review changes" shows the proposal diffs; "Apply" sends the confirm. Stale, mismatch and validation errors are shown inline and keep the staged changes.
