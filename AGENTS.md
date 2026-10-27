# cfgdash: rules for agents

Read [docs/architecture.md](docs/architecture.md) and the contracts in [docs/contracts](docs/contracts/) before changing code.

## Workflow

- Nico owns this repo. The department lead merges team branches into `main` and pushes; team threads work on their own `v1-<team>` branch, commit at every checkpoint and push that branch at milestones. Never force-push.
- Contracts in `docs/contracts/` and the abstractions they name (`src/Cfgdash.Core/Abstractions`, `Snapshot`, `SnapshotJson.cs`, `CfgdashCore.cs` signatures) change only through the department lead.
- Build and test: `dotnet build cfgdash.slnx`, `dotnet test cfgdash.slnx`; UI: `cd web && npm ci && npm test && npm run build`. Warnings are errors.

## Product rules

- v1 is read-only. It never writes, edits or deletes configuration, settings, instructions, agents, skills, hooks, jobs or git state. It writes only its own cache dir (and its own binary in `update`).
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
