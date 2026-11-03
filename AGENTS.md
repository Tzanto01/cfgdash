# cfgdash: rules for agents

Read [docs/architecture.md](docs/architecture.md) and the contracts in [docs/contracts](docs/contracts/) before changing code.

## Workflow

- Nico owns this repo. The department lead merges team branches into `main` and pushes; team threads work on their own `v1-<team>` branch, commit at every checkpoint and push that branch at milestones. Never force-push.
- Contracts in `docs/contracts/` and the abstractions they name (`src/Cfgdash.Core/Abstractions`, `Snapshot`, `SnapshotJson.cs`, `CfgdashCore.cs` signatures) change only through the department lead.
- Build and test: `dotnet build cfgdash.slnx`, `dotnet test cfgdash.slnx`; UI: `cd web && npm ci && npm test && npm run build`. Warnings are errors.

## Product rules

- Read-only except the guarded edit path in [docs/contracts/edits-v1.md](docs/contracts/edits-v1.md) (Nico, 2026-10-28): only its catalog targets, only from localhost, only after a dry-run diff and an explicit confirm, always audited. Everything else is never written, edited or deleted: instructions, agents, skills, hooks, jobs, credentials, git state. Outside edits it writes only its own cache dir (and its own binary in `update`). Two exceptions Nico accepted on 2026-10-28: SQLite may create the empty `-shm`/`-wal` side files beside `engram.db` when it opens the database read-only, and the single-file exe unpacks its native SQLite library under the .NET extraction dir (`%TEMP%\.net` / `$TMPDIR/.net`). `engram.db` itself is never written. Accepted on 2026-11-02 ([cfgdash-config-v1](docs/contracts/cfgdash-config-v1.md)): cfgdash writes its own `config.json` in its config dir, and when start at login is turned on it writes exactly one OS entry (Windows: the HKCU `...\CurrentVersion\Run` value `cfgdash`; Linux: `~/.config/systemd/user/cfgdash.service` plus `systemctl --user enable`), and removes it when turned off. Other apps' files are never written, including SQLite side files: other apps' databases are opened with `immutable=1`. Accepted on 2026-11-03 ([edits-v2](docs/contracts/edits-v2.md), [assistant-v1](docs/contracts/assistant-v1.md) rev 3): text config files (instruction files, agents, skills, commands, Claude Code settings JSON) are editable through the guarded path after a dry-run diff and explicit confirm, audited and undoable; hook scripts and everything else stay never-written. With `assistant.applyEnabled` on, the assistant may apply settings-catalog changes without a confirm, audited and undoable; it never applies file edits.
- Never open credential files (`.credentials.json`, keys, `.env`, `.netrc`, `.git-credentials`, `.ssh/`, `.gnupg/`). `.claude.json` is parsed for MCP servers and project keys only and never served.
- Read environment variables only through `AllowlistEnvironmentReader`. Never enumerate them.
- Cross-platform: no hardcoded drive letters or Windows-only paths; accept both separators; JSON paths use forward slashes. Detect capabilities, never hostnames.
- Redact before anything leaves the core. Over-redaction is fine, under-redaction is a defect.

## Tests

- Every test passes the test value bar: it protects a real requirement, asserts observable behavior, and fails on a plausible bug. Fast, deterministic, isolated.
- Synthetic data only: no real company names, paths, hosts, IPs, ticket ids or people. Hosts are `*.example` or `example.ts.net`, IPs are loopback or TEST-NET, user paths use the user name `user`.
- Build fixture trees in code inside temp dirs at test time. Do not commit files named `CLAUDE*.md`, `AGENTS*.md` or `SKILL.md`, or anything under a `.claude/` or `.agents/` folder: the harness treats them as agent-instruction files and blocks subagents from writing them.

## Code style

- Smallest change that does the job; reuse existing helpers; no speculative abstraction; no drive-by edits.
- No explanatory inline comments. XML doc comments on public members and contract notes are fine.
