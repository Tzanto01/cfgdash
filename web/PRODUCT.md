# cfgdash UI: product truth

Facts from the repository (`docs/architecture.md`, `docs/contracts/`, `README.md`). Design decisions live in [DESIGN.md](DESIGN.md).

## Product

cfgdash is a self-contained executable that reads the Claude Code harness of the machine it runs on and serves a dashboard on `127.0.0.1` (optionally published to the owner's tailnet). This folder is its UI, embedded into the executable.

## Audience and job

One owner (Nico) and his own devices. He opens cfgdash to answer "what does this machine load for this folder, and why": effective settings and the layer each value comes from, the instruction load chain, agents, skills, hooks, MCP servers and permissions, environment, jobs, sessions and memory. Surface mode: **Operate**. It is a dense lookup tool, used daily, often from the keyboard.

## Primary tasks

1. See what is set and which layer won (managed, local, project, user), with the file that set it.
2. Follow the instruction chain for a folder with sizes and token estimates.
3. Switch the target folder and compare.
4. Find anything: keys, values, labels and file contents (search and the command palette).
5. Open the redacted content of any registered file or on-demand detail.
6. Understand why a source is missing (`notPresent`, `unavailable` with a reason).

## Constraints (verified)

- Read-only UI over `/api/v1` (api-v1, snapshot-v1, security-v1). The guarded edit screen, Usage and Health are later slices and are not in this UI yet.
- Data arrives already redacted. Never print a contribution value of a redacted row.
- Generic rendering: `kv`, `table`, `tree` blocks. New collectors and unknown views must work with no UI code; unknown views show under "Other", unknown block types show a fallback note.
- CSP: `default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'`. No inline scripts, no CDN, no remote fonts, no requests outside the cfgdash origin.
- Access: a per-run token; 401, 403 (tailnet identity) and `400 bad-host` show a full-page access panel. cfgdash stops after 30 idle minutes.
- Cross-platform paths: shown as given (forward slashes).
- Budget: initial JS at most 200 KB gzip. WCAG 2.1 AA. Usable at 375 px. Light and dark themes following the system, with a manual toggle.

## Voice

Plain, short, specific. Sentence case. Say what happened and what to do next ("Open the link printed in your terminal", "This folder can't be used"). No marketing tone, no exclamation marks.
