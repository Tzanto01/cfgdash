# Contract: Claude Code loading and merge rules v1

Owner: department lead. Implemented by `core` (`SettingsLayers`, instruction chain, hooks, MCP). Source: official docs at code.claude.com/docs/en (settings, settings-reference, managed-settings, managed-mcp, memory, hooks, mcp, sub-agents, skills, output-styles, permissions, env-vars, claude-directory), read 2026-10-27 against CLI 2.1.311. Where the docs are silent the rule says so and the UI labels it "not documented". Re-check a rule against its page before changing it.

## Settings layers for a target dir (highest precedence first)

1. **Managed.** Sources ranked: server-managed cache `<config dir>/remote-settings.json`, then Windows `HKLM\SOFTWARE\Policies\ClaudeCode` value `Settings` (macOS plist domain `com.anthropic.claudecode`), then the files (`managed-settings.json` merged first, then `managed-settings.d/*.json` alphabetically) in `C:\Program Files\ClaudeCode\`, `/etc/claude-code/`, `/Library/Application Support/ClaudeCode/`, then `HKCU\SOFTWARE\Policies\ClaudeCode` value `Settings`. First wins: the highest-ranked source that delivers at least one key is used and the rest are shown as `ignored`. `C:\ProgramData\ClaudeCode\managed-settings.json` is not read (show it as ignored if present). Registry values are read by exact key and value name.
2. **Command line.** Not observable; shown as a note.
3. **Project local.** `.claude/settings.local.json` at the git repository root containing the target dir (the target dir itself when it is not in a repo).
4. **Project shared.** `.claude/settings.json` in the target dir only. No parent walk.
5. **User.** `<config dir>/settings.json`.

The same physical file can appear in two layers (for example the config repo, where the project `.claude/settings.json` is the user file). Show both rows, mark the second `duplicate`, merge it once.

Settings files are strict JSON: a comment or trailing comma is a parse error. A layer that fails to parse is shown with `error` and contributes nothing.

## Merge

- Scalars and any key not covered below: the highest layer that sets the key wins; lower layers are `overridden`.
- Arrays: concatenated across layers (highest first), duplicates removed, each element keeps its source (`merged`). Exceptions replaced whole by the highest layer: `fallbackModel`, `modelPicker`; a managed `availableModels` is used as-is.
- `env`: merged per variable name, each name follows settings precedence.
- `hooks`: entries from every layer combine; an identical handler defined in more than one file runs once (show the copies as `duplicate`).
- Other objects (`permissions`, `enabledPlugins`, `extraKnownMarketplaces`, `modelSettings`, `autoMode`, ...): general deep merge is not documented. Merge per leaf key with the scalar and array rules above and label the section "object merge: not documented".
- Ignored outside allowed scopes, shown with a note: `autoMode` (user or managed only), `allowManagedHooksOnly`, `allowManagedMcpServersOnly`, `allowManagedPermissionRulesOnly`, `strictPluginOnlyCustomization` (managed only), `permissions.defaultMode` values `auto` and `bypassPermissions` from project or local files.
- Trust gate: `permissions.allow`, `permissions.additionalDirectories`, `extraKnownMarketplaces` and most `env` values from project files apply only after the folder is trusted. Show the note; trust state itself is not evaluated in v1.
- Permission evaluation (not merge): deny rules from any scope are evaluated before allow rules. The permissions table shows every rule with its source.

## Instruction files for a target dir (load order)

1. Managed `CLAUDE.md` (`C:\Program Files\ClaudeCode\CLAUDE.md`, `/etc/claude-code/CLAUDE.md`, `/Library/Application Support/ClaudeCode/CLAUDE.md`).
2. User `<config dir>/CLAUDE.md`, then user rules `<config dir>/rules/**/*.md`.
3. For each directory from the filesystem root down to the target dir: `CLAUDE.md` or `.claude/CLAUDE.md`, then `CLAUDE.local.md`.
4. Project rules `.claude/rules/**/*.md` in the target dir: without `paths` frontmatter loaded at launch; with `paths` shown `conditional` with its globs.
5. `AGENTS.md` in the target dir loads natively only when no `CLAUDE.md` exists in the target dir or any ancestor (v2.1.277+). `AGENTS.override.md`, `AGENTS.local.md` and `.agents/` are never read natively.
6. CLAUDE.md files in subdirectories below the target dir load lazily when Claude touches files there: list them `lazy` at the end, not counted in the total.
7. Main-thread agent (`agent` setting, resolved to its definition file) and output style (`outputStyle`, custom file or built-in) are system-prompt inputs: list them last, counted.

Dedupe by real path (the config repo's `.claude/CLAUDE.md` is also the user file).

`@import` resolution: `@path` outside code spans and fenced blocks; relative to the containing file; `~/` expands to the home dir; max depth 4 hops from the importing root file (deeper shown `error: depth`); cycles shown once with `error: cycle`; missing targets `error: missing`. Imports outside the project root are `external` (Claude asks for approval the first time). Imports are children of the importing node and count toward the total.

Referenced files: a Markdown link or plain path to an `AGENTS.md` / `AGENTS.override.md` (or any `.md`) that is not an `@import` is a child with badge `referenced` (a route the agent may read, not loaded); size and tokens shown, not counted.

Token estimate: `ceil(chars / 4)`, labelled as an estimate.

## Agents, skills, commands, output styles

- Agents by priority: managed `.claude/agents/`, `--agents` flag (not observable), project `.claude/agents/`, user `<config dir>/agents/`, plugin `agents/`. Same name: the higher location wins, the rest `overridden`. Frontmatter: `name`, `description` required; show `model`, `tools`, `disallowedTools`, `hooks`, `permissionMode`, `effort`, `isolation`, `skills`, `mcpServers` when present. YAML frontmatter with multi-line values.
- Skills: managed, user `<config dir>/skills/<name>/SKILL.md`, project `.claude/skills/` from the target dir up to the repo root, plugin `skills/`. Priority enterprise > personal > project. Show the link target when the skill dir is a junction or symlink.
- Commands: `.claude/commands/**/*.md` (user and project). Output styles: `<config dir>/output-styles`, `.claude/output-styles`, managed; built-ins `Proactive`, `Concise`, `Explanatory`, `Learning` listed as built-in.

## Hooks

Sources: every settings layer, plugin `hooks/hooks.json` for enabled plugins, skill frontmatter `hooks`, agent frontmatter `hooks` (plugin agents ignore theirs). Shape `hooks.<Event>[] = { matcher?, hooks: [{ type, command | url | ..., timeout?, if? }] }`; handler types `command`, `http`, `mcp_tool`, `prompt`, `agent`. Unknown events are shown, never dropped. Matcher display: empty / `*` = all.

Script resolution for `command` handlers (mirrors `config/ci/parse-gate.ps1`): take the `-File "<path>"` argument, else the first argument ending in `.ps1`, `.sh`, `.py`, `.js`, `.mjs`, `.cjs`; expand `$CLAUDE_CONFIG_DIR`, `${CLAUDE_CONFIG_DIR}`, `%CLAUDE_CONFIG_DIR%` to the resolved config dir, `$CLAUDE_PROJECT_DIR` / `${CLAUDE_PROJECT_DIR}` to the target dir, `${CLAUDE_PLUGIN_ROOT}` to the plugin dir, a leading `~` to home; normalize separators. Not found: `missing-script`. Purpose: the script's first header comment paragraph (`.ps1`: the first `<# ... #>` block after `#Requires` lines, else leading `#` lines; `.sh`/`.py`: leading `#` lines after the shebang), max 300 chars.

## MCP servers

Scopes, highest first: local (`projects[<target dir>].mcpServers` in `.claude.json`), project (`.mcp.json` at the project root), user (top-level `mcpServers` in `.claude.json`), plugin (`.mcp.json` or `plugin.json` `mcpServers`). `.claude.json` lives in the config dir when `CLAUDE_CONFIG_DIR` is set (observed; not documented), else `~/.claude.json`. Same name: the whole entry from the higher scope is used, fields never merged; lower entries `overridden`. `managed-mcp.json` (same managed dirs as above) replaces all other servers when present. Show `${VAR}` references as written; never expand them. Redact `env` and `headers` values per security-v1.
