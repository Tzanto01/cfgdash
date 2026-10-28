# Contract: snapshot v1

Owner: department lead. Producer: `core` (C# records in `src/Cfgdash.Core/Snapshot`). Consumers: `host` (serves it), `web` (renders it), the future fleet view (fetches and diffs it). Changes only through the department lead; additive optional fields are a minor change, anything else bumps `schemaVersion`.

JSON is camelCase, UTF-8, `null` fields omitted. Paths use forward slashes in JSON on every OS (`D:/ws/config`), the UI shows them as given.

## Top level

```jsonc
{
  "schemaVersion": 1,
  "machine": Machine,
  "context": Context,
  "collectors": [CollectorStatus],
  "sections": [Section],
  "files": [FileRef]
}
```

## Machine (identity, required for the fleet view)

```jsonc
{
  "hostname": "pc-01",
  "tailnet": { "dnsName": "pc-01.example.ts.net", "tailnetName": "example.github", "tags": ["tag:pc"] }, // omitted when tailscale is absent or stopped
  "os": { "family": "windows", "description": "Microsoft Windows 10.0.26200", "arch": "x64" },     // family: windows | linux | macos
  "collectedAt": "2026-10-27T10:00:00Z",
  "cfgdashVersion": "0.1.0"
}
```

## Context

```jsonc
{
  "targetDir": "D:/ws/proj",
  "paths": [
    { "key": "workspaceRoot",   "value": "D:/ws",              "source": "env:WORKSPACE_ROOT", "exists": true },
    { "key": "claudeConfigDir", "value": "D:/ws/config/.claude","source": "env:CLAUDE_CONFIG_DIR", "exists": true },
    { "key": "engramDataDir",   "value": null,                 "source": null, "exists": false },
    { "key": "configRepo",      "value": "D:/ws/config",       "source": "derived:claudeConfigDir-parent", "exists": true },
    { "key": "companyLayer",    "value": "D:/ws/company",      "source": "file:company-paths.psd1", "exists": true }
  ]
}
```

`source` vocabulary: `env:<NAME>` (process), `env-user:<NAME>` / `env-machine:<NAME>` (Windows registry scope, exact name), `default:<rule>`, `derived:<rule>`, `file:<name>`, `override-file`.

## CollectorStatus (one per registered collector, every run)

```jsonc
{
  "id": "agents.company-core", "title": "Company agent cores", "version": "1.0.0", "view": "agents",
  "state": "unavailable",                       // active | notPresent | unavailable
  "reason": "pwsh not installed",               // required unless active
  "requirements": { "os": ["windows", "linux"], "tools": ["pwsh"], "paths": ["configRepo", "companyLayer"], "targetDir": false },
  "durationMs": 840,
  "notes": ["2 agent types have no core.md"]   // optional, partial-data notes
}
```

## Section (one per active collector)

```jsonc
{ "collectorId": "hooks.registered", "view": "hooks", "title": "Hooks", "blocks": [Block] }
```

`view` ids (navigation order): `overview`, `settings`, `instructions`, `agents`, `skills`, `hooks`, `mcp-permissions`, `env`, `jobs`, `sessions`, `memory`. A collector picks one. Unknown views render under "Other" in the UI.

## Blocks (the only shapes the UI renders)

Every block has `type`, a section-unique `id`, `title`, optional `description`.

### kv: key/value with provenance

```jsonc
{
  "type": "kv", "id": "effective", "title": "Effective settings",
  "rows": [
    {
      "key": "permissions.defaultMode",
      "value": "acceptEdits",                    // any JSON value
      "source": { "label": "project (local)", "fileId": "a1b2c3d4e5f60718" },
      "redacted": false,
      "flags": [],
      "contributions": [                         // every layer that set this key, highest precedence first
        { "value": "acceptEdits", "source": { "label": "project (local)", "fileId": "a1b2c3d4e5f60718" }, "status": "applied" },
        { "value": "default",     "source": { "label": "user", "fileId": "0f1e2d3c4b5a6978" },            "status": "overridden" }
      ]
    }
  ]
}
```

`status`: `applied` (this value is effective), `overridden` (lost to a higher layer), `merged` (array/object contribution that is part of the effective value). A redacted value is the string `"[redacted]"` with `redacted: true`.

### table

```jsonc
{
  "type": "table", "id": "hooks", "title": "Registered hooks",
  "columns": [ { "key": "event", "label": "Event" }, { "key": "script", "label": "Script" } ],
  "rows": [
    { "id": "PreToolUse-0-1", "cells": { "event": "PreToolUse", "script": { "text": "hooks/guard.ps1", "fileId": "9a8b7c6d5e4f3021" } },
      "flags": ["missing-script"], "fileId": "0f1e2d3c4b5a6978" }
  ]
}
```

A cell is a string, number, boolean, null, or `{ "text": string, "fileId"?: string }`. Row `fileId` opens the row's main file. Row `detailId` (`<collectorId>:<key>`) opens on-demand content through `GET /api/v1/details/{detailId}` instead; a row has at most one of the two. Tree nodes take `detailId` the same way. `flags` (row or kv) render as badges; known flags: `missing-script`, `network-path`, `overridden`, `company`, `redacted`, `link`, `lazy`, `referenced`, `conditional`, `duplicate`, `error`. Unknown flags render as neutral badges.

### tree

```jsonc
{
  "type": "tree", "id": "chain", "title": "Load order",
  "summary": { "files": 6, "sizeBytes": 41000, "tokens": 10250 },
  "nodes": [
    { "id": "n1", "label": "~/.claude/CLAUDE.md", "fileId": "0f1e2d3c4b5a6978", "badges": ["user", "loaded"],
      "meta": { "sizeBytes": 6400, "tokens": 1600, "order": 1 },
      "children": [ { "id": "n1.1", "label": "@rules/style.md", "fileId": "...", "badges": ["import"], "meta": { "tokens": 200 }, "children": [] } ] }
  ]
}
```

`badges` use the same vocabulary as `flags` plus free labels.

## FileRef

```jsonc
{ "id": "0f1e2d3c4b5a6978", "path": "D:/ws/config/.claude/CLAUDE.md", "kind": "instruction", "sizeBytes": 6400, "tokens": 1600, "company": false }
```

`id` = first 16 hex chars of SHA-256 over the normalized absolute path (case-folded on Windows), stable across runs. `kind`: `instruction`, `settings`, `agent`, `skill`, `command`, `outputStyle`, `hookScript`, `mcp`, `other`. `tokens` = ceil(chars / 4), an estimate. Only registered files can be opened through the API.
