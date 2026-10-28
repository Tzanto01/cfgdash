# Contract: collector v1

Owner: department lead. The abstractions below ship in the initial commit (`src/Cfgdash.Core/Abstractions`); `core` implements collectors and services against them, `host` consumes `SnapshotBuilder`. Signature changes only through the department lead.

## Abstractions (C#, `Cfgdash.Core`)

The code in the initial commit is the contract. Signature changes go through the department lead:

- `src/Cfgdash.Core/Abstractions/Collectors.cs`: `OsFamily`, `CollectorState`, `PathKeys`, `Requirements`, `CollectorManifest`, `ICollector`, `CollectorResult`, `CollectContext`.
- `src/Cfgdash.Core/Abstractions/Services.cs`: `ISnapshotSource` (+ `SnapshotResult`), `IFileCatalog` / `IFileRegistry` (+ `RegisteredFile`), `IFileContentReader` (+ `FileContent`), `ISnapshotSearch` (+ `SearchHit`, `SearchResponse`), `IRedactor`, `IEnvironmentReader`, `IProcessRunner` (+ `ProcessResult`), `MachineEnvironment`, `TailnetStatus`.
- `src/Cfgdash.Core/Snapshot/SnapshotModel.cs`: the snapshot records, one to one with [snapshot-v1](snapshot-v1.md).
- `src/Cfgdash.Core/SnapshotJson.cs`: the only serializer options (camelCase, nulls omitted, enums camelCase).
- `src/Cfgdash.Core/CfgdashCore.cs`: `CfgdashCore.CreateAsync(version, ct)` returns `CoreServices` (environment, env reader, process runner, snapshot source, file reader, search). It is the host's only entry into the core. The initial commit ships placeholder internals (empty snapshot) so the host builds from day one; `core` replaces them with `MachineEnvironmentDiscovery`, `SnapshotBuilder`, `FileRegistry`, `FileContentReader`, `SnapshotSearch`, `Redactor` and `CollectorCatalog`, keeping the signatures.
- `src/Cfgdash.Core/Environment/`: `HarnessEnvAllowlist`, `AllowlistEnvironmentReader`, `ProcessRunner` (shipped working; `core` owns them).
- `src/Cfgdash.Core/CfgdashPaths.cs`: cache dir, config dir, overrides file.
## Runner rules (`SnapshotBuilder : ISnapshotSource`)

1. Evaluate requirements in this order, first failure wins: OS (`notPresent`, "Windows only"), paths (`notPresent`, "no <key> on this machine"), tools (`unavailable`, "<tool> not installed"). Missing things are a state, never an exception.
2. Run every remaining collector concurrently with a 20 s timeout each. Timeout or exception -> `unavailable` with a one-line reason (exception type + message, no stack, no file contents). One collector never breaks the snapshot.
3. `CollectorResult.UnavailableReason` set -> `unavailable`. Otherwise `active` and its blocks become one section.
4. All string values pass through the `Redactor` before they enter a block (collectors call it; the builder runs a final defensive pass over kv values and table cells).
5. `collectors[]` lists every registered collector in catalog order, active or not.
6. Details: a collector that emits `detailId` values implements `IDetailProvider`. Every `detailId` starts with the emitting collector's id and a colon; the key matches `[A-Za-z0-9._-]{1,128}`, is never a path, and the provider validates it against its own data. The builder drops invalid ids and returns the rest in `SnapshotResult.DetailIds`; the host opens only those, and `IDetailReader` resolves them through the owning collector, with the same redaction, a 512 KiB cap and a 20 s timeout.

## Registration

`CollectorCatalog.All` (owned by `core`) returns the built-in collectors in display order. Adding a source = one class implementing `ICollector` + one line in the catalog. No reflection scanning.

## MachineEnvironment (owned by `core`)

Discovered once per run, then per target dir where needed:

- `CLAUDE_CONFIG_DIR` (process env, then Windows User/Machine scope by exact name), else `~/.claude`.
- `configRepo`: the parent of the config dir when it contains `.claude/hooks/lib/agent-core.ps1`, else `<WORKSPACE_ROOT>/config` when that contains it, else absent.
- `workspaceRoot`: `WORKSPACE_ROOT` (process, then Windows User/Machine scope) when it exists, else the config repo's parent, else absent. Mirrors `Resolve-WorkspacePath` in `config/.claude/hooks/lib/agent-core.ps1`.
- `engramDataDir`: `ENGRAM_DATA_DIR` by the same rule, else absent.
- `companyLayer`: `CompanyPathPrefixes` from `<configRepo>/.claude/hooks/company-paths.psd1` (plain string literals in the `@(...)` list), non-rooted entries resolved under the workspace root; present only when one resolved directory exists.
- Optional override file `<user config dir>/cfgdash/overrides.json` (Windows `%APPDATA%\cfgdash`, Linux `~/.config/cfgdash`): may set any path key and `allowedLogins`; never required. Values from it carry source `override-file`.
- Tool probe: PATH search plus known install dirs (Windows `C:\Program Files\Tailscale\tailscale.exe`, `C:\Program Files\PowerShell\7\pwsh.exe`).
- Environment variables are read only through one allowlist-enforcing reader (exact names, see security-v1). No enumeration anywhere.

## Built-in collectors (v1)

| Id | View | Requirements | Content |
| --- | --- | --- | --- |
| `overview.machine` | overview | none | kv: hostname, OS, the five context paths with sources, Claude Code version (`claude --version`, row omitted with a note when claude is absent) |
| `overview.config-repo` | overview | tools git, paths configRepo | kv: branch, upstream, ahead/behind, config auto-pull on/off with its source (`CC_CONFIG_AUTOPULL`, `.claude/machine.json` or default), last fetch (`.git/FETCH_HEAD` time), dirty count, last commit (hash, date, subject); table: dirty files |
| `settings.effective` | settings | paths claudeConfigDir, targetDir | kv with provenance over every layer that applies to the target dir; table of the layer files in precedence order |
| `instructions.chain` | instructions | targetDir | tree: ordered instruction files with @imports resolved as children, referenced AGENTS.md / AGENTS.override.md as `referenced` children, main-thread agent and output style prompts; summary totals |
| `agents.definitions` | agents | paths claudeConfigDir, targetDir | table: name, scope, model, tools, frontmatter hooks, description, company-core marker; row opens the file |
| `agents.company-core` | agents | tools pwsh, paths configRepo + companyLayer | tree: per company agent type the exact text `Get-AgentComposedContext` returns (state Ready / Missing / Empty / Invalid) |
| `skills.catalog` | skills | paths claudeConfigDir, targetDir | tables: skills, slash commands, output styles (name, description, scope, path, link target when exposed through a junction or symlink) |
| `hooks.registered` | hooks | paths claudeConfigDir, targetDir | table: event, matcher, command, resolved script, registering file, purpose (script header), `missing-script` flag |
| `mcp.servers` | mcp-permissions | targetDir | table: name, scope, transport, command/url, redacted env and headers, source file |
| `settings.highlights` | mcp-permissions | paths claudeConfigDir, targetDir | kv/table: permissions allow/ask/deny with source per rule, defaultMode, autoMode environment strings, model, effortLevel, modelSettings, statusLine, enabledPlugins, extraKnownMarketplaces, outputStyle, agent |
| `env.harness` | env | none | kv: merged settings `env` blocks with provenance; table: allowlisted variables with process value and (Windows) User/Machine values |
| `jobs.windows-tasks` | jobs | os windows, tools schtasks | table: harness scheduled tasks (action references the workspace or config repo), state, last run, last result (hex + meaning) |
| `jobs.systemd-user` | jobs | os linux, tools systemctl | table: user services and timers, active state, last trigger, result |
| `jobs.crontab` | jobs | os linux + macos, tools crontab | table: user crontab entries (redacted), managed-block marker, log file last write when the line redirects to one |
| `engram.memory` | memory | paths engramDataDir | `<engramDataDir>/engram.db` opened read-only (`Mode=ReadOnly`, no pooling, parameterized queries, schema checked via `pragma_table_info`; missing optional columns are omitted, missing core columns -> unavailable "unsupported engram schema"); soft-deleted rows excluded. kv: db size, last write, user_version, counts of observations, sessions, prompts; tables: projects (observations, sessions, last activity, counts per scope), newest 500 observations (id, updated, created, project, scope, type, title, topic key, review after) with `detailId` `engram.memory:<id>` opening metadata and content as markdown, topic keys with counts. Projects starting with `company` appear (flag `company`) only with a company layer, otherwise they are excluded everywhere and a note counts them |

Collectors that need the shared settings merge use one `SettingsLayers` service (owned by `core`) so all views agree.

## Tests the `core` team owns (value bar applies)

Settings layering with provenance, instruction chain and @import resolution (order, cycles, depth limit, code spans ignored, missing targets, referenced AGENTS.md), injected agent core through a fake `agent-core.ps1` in a synthetic config repo (real pwsh), hook resolution (`$CLAUDE_CONFIG_DIR` forms, missing script flag, header purpose), redaction (keys, headers, env, value patterns, file content, never-read files), collector states (OS, tool, path, exception, timeout). Synthetic fixtures only.
