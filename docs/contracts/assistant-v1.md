# Contract: config assistant v1 (ROADMAP 9)

Owner: department lead. Producer: team `assistant` (core, host, `web/mock`). Consumer: team `web` (side panel, full check).

Decided by Nico on 2026-11-02:
- a live Claude Code session;
- no transcript and no resume;
- the edits-v1 catalog, narrowed by the assistant policy below, through proposals only;
- explanations and suggested text for every other file.

Revision 2 applies the pre-implementation security review (FIX FIRST: H1–H3, M1–M7, lows). Revision 2.1 (2026-11-02): `--verbose`, no built-in agents, and a one-retry plugin disable, as measured against CLI 2.1.316.

## Runtime

### Process and flags

- One `claude` process per open panel. The cwd is a cfgdash-owned empty dir `<cache>/assistant/work` (0700). The target dir is never the cwd: it is a server-side binding of the session that only the MCP tools use.
- Flags. Pass every value as `--flag=value`, built by cfgdash and never taken from client text:
  - `--print`, `--input-format=stream-json`, `--output-format=stream-json`, `--include-partial-messages`, `--verbose` (required by the CLI for stream-json output)
  - `--no-session-persistence`
  - `--setting-sources=` (empty: no user, project or local settings)
  - `--settings=<generated file>`
  - `--mcp-config=<generated file>`, `--strict-mcp-config`
  - `--tools=` (empty), `--allowedTools=mcp__cfgdash__*`, `--permission-mode=dontAsk`
  - `--disable-slash-commands`
  - `--max-budget-usd=<per-session cap, default 2.00, from cfgdash settings>`
  - `--model=<v>`, `--effort=<v>`
  - `--append-system-prompt=<cfgdash prompt>`
- The generated settings file sets `disableAllHooks: true`, `disableClaudeAiConnectors: true`, `autoMemoryEnabled: false`, `includeGitInstructions: false` and `enableAllProjectMcpServers: false`. It turns off every plugin sync and sets `enabledPlugins` to false for each plugin found in the user's settings.
- The child process env sets `CLAUDE_AGENT_SDK_DISABLE_BUILTIN_AGENTS=1`, so no built-in agents load.
- Built-in plugins: when the init check fails only because of entries in `plugins`, cfgdash ends that process, writes those plugin names as `enabledPlugins: false` into a fresh settings file and starts once more. A second failure, or any extra entry in `tools`, `mcp_servers`, agents, skills or slash commands, fails closed with no retry. The disabled names are cached per CLI version.
- Both generated files live in `<cache>/assistant/` with mode 0600 (a user-only ACL on Windows) and are deleted when the process exits. `--bare` is not used, because it disables OAuth.
- `model` must match `^[A-Za-z0-9][A-Za-z0-9.\-\[\]]{0,99}$`. `effort` is one of `low`, `medium`, `high`, `xhigh`, `max`.

### Fail-closed checks

- cfgdash reads the first stream-json `system`/`init` event. Unless `tools` is exactly the five `mcp__cfgdash__*` names, `mcp_servers` is exactly `[cfgdash]`, and plugins, agents, skills and slash commands are empty, it kills the process and emits `error` ("containment check failed").
- If the installed CLI lacks one of these flags, or the init event cannot be read, `status` answers `available: false` with the reason. There is no fallback that widens anything.
- A canary test runs against a fake `claude` and a synthetic home that holds a user hook, a plugin, a user CLAUDE.md marker, a `.mcp.json` and a connector-style permission. It asserts that none of them reach the session (flags, settings file and init check).

### Finding the CLI

- `claude` is resolved to an absolute path through absolute PATH entries only. On Windows `.cmd` and `.bat` shims are refused (a native `claude.exe` is required). `status` shows the resolved path.
- If the CLI is missing or not logged in, `status` answers `available: false` with the reason. cfgdash never handles API keys or OAuth tokens.

### Turns and limits

- Each stdin line is serialized by cfgdash as `{"type":"user",...}`; client JSON is never forwarded. Inbound `control_request` events from the CLI are ignored.
- One running turn per session; `messages` and `check` answer 409 while one runs.
- Per-turn limits: 10 minutes wall clock (interrupt, then kill), 40 MCP tool calls (the tools then refuse), and a maximum size for each stdout line.
- At most 2 live `claude` processes per cfgdash instance (counted as processes); a third request gets 429.
- An idle panel (no request for 10 minutes) ends its process. There is no resume: the next message starts a new session.
- Every shutdown path kills the whole process tree (a Windows Job object with KILL_ON_JOB_CLOSE, a process group on Linux), autostart included.
- `total_cost_usd` from the result event is shown in the panel.

## MCP server: `cfgdash mcp`

- A subcommand of the cfgdash exe that speaks MCP over stdio. It never listens on a port, never reads `run.json`, never runs `update`, and exits when its link fails.
- Its link back to the running instance is a per-session capability: 32 random bytes, delivered through the `env` of the generated MCP config file (never on a command line) and revoked when the session ends.
- The capability is accepted as a bearer token (cookies rejected) on internal routes under `/api/v2/assistant/internal/*`, on the UI listener only (404 on the tailnet). Those routes expose exactly the five tools below, for the session's bound dir. Fleet peer identity never authorizes any `/api/v2` route.

| Tool | Returns |
| --- | --- |
| `get_snapshot` (`view?`, `collectorId?`) | The redacted snapshot of the bound dir, filtered to the assistant collector allowlist (below) |
| `get_findings` | health-v1 findings for the bound dir |
| `get_file` (`fileId`) | The redacted file content, as `/api/v1/files`, capped at 64 KiB for the model. Registered ids only; never-read rules apply |
| `search` (`q`) | `/api/v1/search` results, filtered by the same allowlist |
| `propose_change` (`changes`, `rationale`) | A proposal for the bound dir (a `dir` argument is ignored), checked against edits-v1 validation and the assistant policy. Answers `{ proposalId, files[].diff }`, never `confirm`. Assistant proposals have their own quota of 8 open proposals |

- **Collector allowlist:** every collector except `engram.memory`, `sessions.transcripts` and `usage.metrics`. Items flagged `company` are dropped unless the bound dir is under the company layer.
- **Tool-call log:** `<cache>/assistant/<cfgdash-session-id>.jsonl` (0600) records tool name, redacted arguments capped at 2 KiB, and timing. Results are never logged.

## Assistant policy (on top of edits-v1)

- Every catalog key gets an `assistant` policy, recorded in `SettingsCatalogData`:
  - `free`: any valid value
  - `tighten-only`: only the stated safer direction, for example narrowing `permissions.allow`, adding to `permissions.deny`, or `sandbox.*` toward stricter
  - `deny`: the default for any key without a policy
- Always `deny` for the assistant:
  - all `env.*`
  - `permissions.defaultMode`, `skipDangerousModePermissionPrompt`
  - `disableAllHooks`, `enableAllProjectMcpServers`, `claudeMdExcludes`, `language`
  - `forceLogin*`, `autoMode.*`
  - widening `permissions.allow`
  - sandbox loosening
- User scope is allowed only for keys whose policy names it.
- A denied change answers the model with `{ "denied": key, "reason": "assistant-policy" }`, so it explains and suggests text instead.
- **For every origin:** a value containing the `[redacted]` marker is rejected (`redacted-value`).
- **For the assistant only:** a value that the redactor would change is rejected (`would-hide`).
- **UI rules:**
  - One review per assistant proposal, with no batch apply.
  - A banner says "Proposed by the assistant from your config files".
  - `rationale` and suggested text render as plain text.
  - Suggested text for files outside the catalog is labelled "model output derived from your files; review before pasting". This is an accepted residual: Nico pastes it by hand.

## Public API (UI listener only; every non-GET follows the edits-v1 request rules)

| Method + path | Answer |
| --- | --- |
| `GET /api/v2/assistant/status` | `{ "available", "reason"?, "claudePath"?, "claudeVersion"?, "models": [aliases], "sessions": [ { "id", "dir", "model", "effort", "live", "costUsd" } ] }` |
| `POST /api/v2/assistant/sessions` | Body `{ "dir", "model"?, "effort"? }`. Answers `{ "id" }` (a cfgdash session id; the claude session UUID is never exposed) |
| `POST /api/v2/assistant/sessions/{id}/messages` | Body `{ "text", "context": [ { "kind": "row" \| "file" \| "finding", ...ids } ] }`, at most 8 items. Context goes to the model as references (ids and titles) inside a nonce-fenced block that the system prompt declares untrusted data, never as content. The model fetches content with the tools. Answers 202, or 409 while a turn runs |
| `GET /api/v2/assistant/sessions/{id}/events` | Server-sent events: `delta`, `tool` (name and status), `proposal` (`proposalId` and diffs), `cost`, `done` and `error`. `delta` text passes through the redactor value rule at line boundaries. This GET never starts a process |
| `POST /api/v2/assistant/sessions/{id}/check` | The full check: a fixed prompt that starts from the findings and asks for ranked suggestions, using `propose_change` where the policy allows. Same events |
| `DELETE /api/v2/assistant/sessions/{id}` | Ends the process tree |

- All routes answer 404 on the tailnet listener.
- The UI renders model text as markdown with raw HTML off and no images. Links show as text with the full URL.
- Audit entries of applied assistant proposals carry `"origin": "assistant"` and the cfgdash session id.

## Write exceptions

The CLI runs with `--no-session-persistence`, so it writes no transcript. Claude Code may still update its own bookkeeping in `~/.claude.json` (startup counters and similar). That file is Claude Code's own file, not a cfgdash write; it is recorded here as an accepted residual (Nico, 2026-11-02, by choosing the live-session runtime). Everything cfgdash itself writes for the assistant stays under `<cache>/assistant/`.
