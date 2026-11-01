# Contract: health findings v1 (ROADMAP 12)

Owner: department lead. Producer: team `health` (core, host, `web/mock`). Consumer: team `web` (Health view, nav badge, Overview tile) and later the fleet view (ROADMAP 1) and AI diagnosis (ROADMAP 9).

Findings are deterministic rules over one snapshot of one target dir. No AI, no new collector, no file reads beyond what the snapshot already holds. A rule that needs a fact the snapshot lacks gets it as a row flag from the owning collector (as `missing-command` below), never by reading on its own.

## Finding

```jsonc
{
  "id": "missing-script:hooks.registered:hooks:SessionStart-0-0", // stable: <rule>:<collectorId>:<blockId>:<itemId or kv key>
  "rule": "missing-script",
  "severity": "critical",           // critical | warning | info
  "title": "Hook script not found",  // short, no paths
  "detail": "SessionStart runs D:/ws/config/.claude/hooks/start.ps1, which does not exist.", // redacted text
  "evidence": { "collectorId": "hooks.registered", "blockId": "hooks", "itemId": "SessionStart-0-0", "fileId": "0f1e2d3c4b5a6978" }, // itemId, key, fileId optional
  "view": "hooks"                    // the snapshot section view the evidence lives in
}
```

Findings are sorted by severity (critical, warning, info), then `id`. Ids stay stable across refreshes of an unchanged harness.

## Rules v1

| Rule | Source in the snapshot | Severity |
| --- | --- | --- |
| `missing-script` | any row or kv entry flagged `missing-script` | critical |
| `missing-command` | any row flagged `missing-command` (new flag on applied stdio MCP servers: a bare command name not found on `PATH` through `ToolProbe`, or a path (absolute, or relative resolved against the target dir) that does not exist. Not flagged: commands containing `${`, network paths (never probed), overridden definitions) | warning |
| `import-error` | instruction chain nodes flagged `error` (missing target, cycle, depth limit) | warning |
| `error` | any other row, node or kv entry flagged `error` | warning |
| `duplicate` | rows flagged `duplicate` (same-name agents, skills or commands where one silently loses) | warning |
| `collector-error` | collector `state` `unavailable` whose reason is a timeout, an exception or a failed command run | warning |
| `collector-unavailable` | any other `unavailable` collector (missing tool or missing input) | info |
| `overridden` | settings entries flagged `overridden` (layering is normal; shown so the Health view can explain it) | info |

`notPresent` collectors are never findings. Company rows produce findings only where the snapshot already shows them (same rule as the snapshot). Adding a rule later is additive; changing a rule's id or severity is a contract change.

## Surfaces

| Surface | Answer |
| --- | --- |
| `GET /api/v1/findings?dir=<abs path>&refresh=true` | `{ "dir", "collectedAt", "counts": { "critical", "warning", "info" }, "findings": [Finding] }` computed from the same cached snapshot as `/api/v1/snapshot` for `dir` (same `dir` rules, same `refresh`, same auth, both listeners). Additive to api-v1 |
| `cfgdash doctor [--dir <path>] [--json]` | Builds the snapshot, prints findings grouped by severity (text) or the endpoint's JSON (`--json`). Exit codes: 0 no critical finding, 3 at least one critical finding, 1 error, 2 prerequisite missing. Writes nothing, starts no server |
| Core | `CfgdashCore` gains one public method returning the findings for a snapshot; `HealthRules` lives in `src/Cfgdash.Core/Health/` |

`snapshot-v1` known flags gain `missing-command`. The snapshot schema is otherwise unchanged; findings are never embedded in it.

## UI (team `web`)

Health nav entry under *Machine* with a badge for critical plus warning counts (hidden at zero), an Overview tile with the three counts, and the Health view from direction D: severity bar above a ruled table (severity, title, where), detail and evidence link in the side sheet, filter by severity.
