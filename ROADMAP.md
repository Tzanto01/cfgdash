# Roadmap

v1 shows the harness of the machine it runs on, read-only. Next, in order:

1. **Fleet view.** Discover the other machines with `tailscale status --json`, fetch each one's `/api/v1/snapshot` over the tailnet, show them side by side and as a diff keyed by collector, block and row ids. Needs machine-to-machine auth that works between tagged devices (no user identity header there): a whois-verified peer plus a token exchange, decided before building.
2. **Session-audit and transcript insights.** Shipped in v0.2.0 (Sessions view). Read Claude Code transcripts (metadata first), surface loops, missed skills and gates, cost per session; reuse the `session-audit` skill's rubric.
3. **Usage and cost.** Read the existing OpenTelemetry metrics from VictoriaMetrics on the homeserver: tokens, cost and sessions per machine, model and project.
4. **Engram browser.** Shipped in v0.2.0 (Memory view). Metadata first (projects, scopes, topic keys, counts, review dates), content on click, company scope only on machines that have it.
5. **T3 settings.** Show T3 Code providers, projects and per-project settings.
6. **Codex and OpenCode configs.** Collectors for their config files, instructions and skills, same block model.
7. **Guarded edits.** The write path: a proposed change renders as a dry-run diff, applies only after explicit confirmation, and lands in an append-only audit log in the cache dir. Separate `/api/v2` surface, off by default. First candidate: the config auto-pull switch (`configAutoPull` in the config repo's `.claude/machine.json`), which the overview already shows read-only.
8. **One-line installer.** `irm <url>/install.ps1 | iex` on Windows and `curl -fsSL <url>/install.sh | sh` on Linux, verifying SHA256SUMS before installing. Open decision: where the script and binaries are served, since the repo and its releases are private (public release repo or public cfgdash, or tailnet-only from the homeserver).
9. **Write path with AI diagnosis.** Builds on 7: the configured AI providers (for example headless `claude -p`) diagnose harness problems and propose config rewrites, and every proposal goes through the same dry-run diff, explicit confirmation and audit log. Lifts the v1 read-only rule, so it needs its own plan and a security review before any code.
10. **Redesign.** A whole-UI redesign with the `impeccable` skill: more interesting to look at, more comfortable to move around in, more engaging. After the current slices (3, 5, 6, 7) land, so it covers the new views and the settings screen.
