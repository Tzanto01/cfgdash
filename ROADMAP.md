# Roadmap

v1 shows the harness of the machine it runs on, read-only. Next, in order:

1. **Fleet view.** Discover the other machines with `tailscale status --json`, fetch each one's `/api/v1/snapshot` over the tailnet, show them side by side and as a diff keyed by collector, block and row ids. Needs machine-to-machine auth that works between tagged devices (no user identity header there): a whois-verified peer plus a token exchange, decided before building.
2. **Session-audit and transcript insights.** Read Claude Code transcripts (metadata first), surface loops, missed skills and gates, cost per session; reuse the `session-audit` skill's rubric.
3. **Usage and cost.** Read the existing OpenTelemetry metrics from VictoriaMetrics on the homeserver: tokens, cost and sessions per machine, model and project.
4. **Engram browser.** Metadata first (projects, scopes, topic keys, counts, review dates), content on click, company scope only on machines that have it.
5. **T3 settings.** Show T3 Code providers, projects and per-project settings.
6. **Codex and OpenCode configs.** Collectors for their config files, instructions and skills, same block model.
7. **Guarded edits.** The write path: a proposed change renders as a dry-run diff, applies only after explicit confirmation, and lands in an append-only audit log in the cache dir. Separate `/api/v2` surface, off by default.
