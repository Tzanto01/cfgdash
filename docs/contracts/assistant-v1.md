# Contract: config assistant v1 (ROADMAP 9)

Owner: department lead. Producer: team `assistant` (core, host, `web/mock`). Consumer: team `web` (side panel, full check). Decided by Nico on 2026-11-02:
- a live Claude Code session;
- write scope limited to the edits-v1 catalog, through proposals only;
- explanations and suggested text for every other file.

This contract needs a `security-reviewer` pass before any code is merged.

## Runtime

- One assistant session per open panel, per target dir. cfgdash starts this process with the target dir as cwd:

  `claude --print --input-format stream-json --output-format stream-json --include-partial-messages --model <model> --effort <effort> --mcp-config <generated> --strict-mcp-config --tools "" --allowedTools "mcp__cfgdash__*" --permission-mode dontAsk --setting-sources user --append-system-prompt <cfgdash assistant prompt>`

  The team verifies the exact flag names against `claude --help` of the installed version. If a flag is missing, it records the fallback in its receipt and never widens tools.
- The process stays alive while the panel is open, and each user message is written to its stdin. If the panel stays closed for 10 minutes, cfgdash ends the process and keeps the `session_id`. Reopening the panel starts `--resume <session_id>` with the same flags.
- Requires `claude` on PATH, logged in. Otherwise `GET /api/v2/assistant/status` answers `{ "available": false, "reason" }`, and the panel shows the reason. cfgdash never handles API keys or OAuth tokens.
- Model and effort come from the panel, with defaults in cfgdash settings ([cfgdash-config-v1](cfgdash-config-v1.md)). The model is one of the CLI's aliases or a full model id, and the effort one of `low`, `medium`, `high`, `xhigh`, `max`.
- At most 2 live sessions per cfgdash instance. A third request gets 429.

## Tools the model gets (cfgdash MCP server, stdio, served by the cfgdash exe)

| Tool | Returns |
| --- | --- |
| `get_snapshot` (`view?`, `collectorId?`) | The redacted snapshot of the session's dir, filtered |
| `get_findings` | health-v1 findings for that dir |
| `get_file` (`fileId`) | The same redacted content as `GET /api/v1/files/{id}`. Never-read rules apply, and only registered file ids are accepted |
| `search` (`q`) | The same results as `/api/v1/search` |
| `propose_change` (`changes` in edits-v1 shape, `rationale`) | Creates an edits-v1 proposal through the same validation and answers `{ proposalId, files[].diff }`. Never applies anything |

- No built-in tools: no Bash, Read, Edit, Write or web tools. Every tool call is logged with its name and arguments, but never its results, to `<cache>/assistant/<session_id>.jsonl`.
- The prompt prefix tells the model that values marked `[redacted]` are unknown to it, and to never ask the user for secrets.

## API (UI listener only; POSTs follow the edits-v1 request rules)

| Method + path | Answer |
| --- | --- |
| `GET /api/v2/assistant/status` | `{ "available", "reason"?, "claudeVersion"?, "models": [aliases], "sessions": [ { "id", "dir", "model", "effort", "live" } ] }` |
| `POST /api/v2/assistant/sessions` | Body `{ "dir", "model"?, "effort"?, "resume"? }`. Answers `{ "id" }` |
| `POST /api/v2/assistant/sessions/{id}/messages` | Body `{ "text", "context": [ { "kind": "row" \| "file" \| "finding", "collectorId"?, "blockId"?, "itemId"?, "fileId"?, "findingId"? } ] }`. cfgdash turns each context item into a redacted text block put in front of the user text. Answers 202 |
| `GET /api/v2/assistant/sessions/{id}/events` | Server-sent events: `delta` (text), `tool` (name and status), `proposal` (`proposalId` with its diffs), `done`, `error`. The UI applies a proposal through the existing `/api/v2/proposals/{id}/apply` with the confirm |
| `POST /api/v2/assistant/sessions/{id}/check` | Runs the full check: a fixed prompt that starts from the findings and asks for ranked suggestions, each with a `propose_change` where the catalog allows one. Its output is the same event stream |
| `DELETE /api/v2/assistant/sessions/{id}` | Ends the process and keeps the id for resume |

All of these answer 404 on the tailnet listener. Proposals made by the assistant carry `"origin": "assistant"` in the audit entry.
