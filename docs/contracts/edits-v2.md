# Contract: file edits v2 (`/api/v2/files`)

Owner: department lead. Producer: team `edits` (core, host, `web/mock`). Consumers: team `web` (the file viewer's Edit mode) and team `assistant` (`propose_file_edit`). Decided by Nico on 2026-11-03: text config files are editable in the file viewer, by Nico and as assistant proposals, each through a diff he confirms, audited and undoable. The assistant never applies a file edit itself.

Everything in [edits-v1](edits-v1.md) still holds: UI listener only, the request rules, the proposal, confirm, apply, audit, backup and revert machinery. This contract adds one target kind.

## Editable files

A registered file (api-v1 `/api/v1/files`, so never-read rules already apply) is editable when all of these hold:

- **Kind:**
  - an instruction file the chain loads (`CLAUDE.md`, `CLAUDE.local.md`, `AGENTS.md`, `AGENTS.override.md`, and any `@import` target ending in `.md`);
  - an agent definition (`*.md` in an agents dir);
  - a skill file (`SKILL.md` and other `*.md` in a skill dir);
  - a command (`*.md` in a commands dir);
  - a Claude Code settings file (`settings.json`, `settings.local.json`) in the user, project or local scope.
- **Never editable:**
  - hook scripts and any non-`.md` file except those settings files;
  - `.mcp.json`, `.claude.json`, managed settings, other apps' files (T3, Codex, OpenCode);
  - credential and never-read files;
  - company-layer files;
  - files outside a known harness root (the config dir, the target dir and its ancestors up to the workspace root, the config repo).
- **Size and text:** at most 256 KiB, valid UTF-8, no NUL bytes.
- **No redaction:** the redacted text equals the raw text. A file the redactor would change is read-only with reason `contains-redacted-values`, because the UI only ever holds the redacted text.

A symlink is resolved first, and the real path must pass the same rules. The write goes to the real path. When the real path is tracked by git (the index check), the file entry carries `gitTracked: true` and the UI says the repo is now dirty. cfgdash never commits.

## Endpoints (UI listener only, edits-v1 request rules; body max 320 KiB for these two)

| Method + path | Answer |
| --- | --- |
| `GET /api/v1/files/{fileId}?dir=` | Adds `"edit": { "editable": bool, "reason"?, "sha256"?, "gitTracked"? }` to the existing answer. `sha256` (of the raw content) is present only when editable, because a hash of a file with redacted values would let someone confirm a guessed secret offline. `gitTracked` uses the same index check as `/api/v2/settings`. An editable settings JSON is served as its raw text (editable only when both the text and JSON redaction change nothing) |
| `POST /api/v2/files/{fileId}/proposals?dir=<abs path>` | `dir` is required (file ids resolve through that dir's snapshot). Body `{ "content", "sha256Before" }`. Dry run: rejects with `400 {"error":"invalid-change","reason"}` when the file is not editable, the content fails the size/UTF-8 rules, a settings file's new content fails the strict JSON parse, or the content contains the `[redacted]` marker; `409 stale` when the current sha256 differs. Otherwise answers the edits-v1 proposal shape (one file, full-file unified diff, `confirm`). Apply, audit (`changes: [ { "file": "<fileId>", "action": "edit" } ]`) and revert are edits-v1's |

Line endings and a trailing newline are kept exactly as the content sends them; the UI keeps the file's existing line ending style.

## Assistant

- A sixth MCP tool, `propose_file_edit` (`fileId`, `content`, `rationale`). It is allowed only for files in the assistant view (allowlist and company filter of [assistant-v1](assistant-v1.md)) that are editable here. It answers `{ proposalId, files[].diff }`, never `confirm`, and counts toward the quota of 8 open proposals.
- A file edit from the assistant is always a proposal Nico confirms, even when `assistant.applyEnabled` is on.
- Content the redactor would change is rejected (`would-hide`).
