# Architecture

cfgdash is one self-contained executable per OS (`win-x64`, `linux-x64`) that reads the Claude Code harness of the machine it runs on and serves a read-only dashboard on `127.0.0.1`, optionally published to the owner's tailnet.

```
src/Cfgdash.Core    collectors, settings merge, redaction, file registry, search, environment discovery
src/Cfgdash.Host    the `cfgdash` exe: CLI, Kestrel, security, /api/v1, tailscale serve, update, export, embedded UI
web/                Angular UI, built into the host as embedded resources
tests/              xUnit (Core, Host); the UI has its own Vitest tests in web/
docs/contracts/     the versioned contracts every part builds against
```

## Seams for extension

- **New data source** = one class implementing `ICollector` plus one line in `CollectorCatalog`. It declares its requirements (OS, tools, paths, target dir) and emits generic blocks (`kv`, `table`, `tree`). The runner turns unmet requirements into `notPresent` or `unavailable` with a reason, so a missing tool is a normal state. The UI renders every block type generically, so a new source normally needs no UI code.
- **New view** = a new `view` id on a collector; the UI lists unknown views under "Other" until it gets a nav entry.
- **Fleet view** (ROADMAP 1): every snapshot carries machine identity, `/api/v1/snapshot` is stable and `cfgdash export` writes snapshots to the cache dir. A fleet page discovers peers with `tailscale status --json`, fetches their snapshots over the tailnet and diffs them by collector, block and row ids.
- **Guarded write path** (ROADMAP 7, [edits-v1](contracts/edits-v1.md)): a separate `/api/v2` surface on the UI listener only. A change to a catalog setting is proposed as data, rendered as a dry-run diff, applied only after an explicit confirmation that names the exact diff, and recorded in an append-only audit log in the cache dir. `/api/v1` stays read-only; its only non-GET endpoint is the bearer-only shutdown.

## Data flow

1. `CfgdashCore.CreateAsync` discovers the machine once (paths with their sources, tools, tailnet status, company layer, overrides).
2. `ISnapshotSource.BuildAsync(dir)` runs every collector for the target dir and returns the snapshot plus the files it registered.
3. The host caches one snapshot per dir, serves it, serves registered files through the redacting reader, and runs search over both.
4. The UI loads the snapshot, groups sections by view and renders blocks with the generic components.

See [contracts](contracts/) for the exact shapes and rules.
