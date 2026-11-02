# Contract: fleet v1 (ROADMAP 1)

Owner: department lead. Producer: team `fleet` (core, host, `web/mock`). Consumer: team `web` (Fleet view). Decided by Nico on 2026-11-02: cfgdash over the tailnet, export compare first, no T3 fork.

## Phase A: compare with an earlier export (no network)

- `GET /api/v1/exports` lists `<cache>/exports/*.json` written by `cfgdash export`: `{ "exports": [ { "id": "<file name without .json>", "machine", "collectedAt", "dir" } ] }`, newest first. Ids match `^[A-Za-z0-9._-]+$`, and only files directly in that folder count.
- `GET /api/v1/compare?dir=<abs>&against=export:<id>` computes the current snapshot of `dir` against that export (see Diff below).
- `cfgdash export` stays as it is. Exports are already redacted snapshots.

## Phase B: peers on the tailnet

- **Peer discovery:** `GET /api/v1/fleet` returns `{ "self", "peers": [ { "name", "dnsName", "os", "online", "cfgdash": "ok" \| "absent" \| "refused" \| "error", "version"?, "reason"? } ] }`. It is built from `tailscale status --json` (peers that are online and owned by the same user or tagged in the same tailnet), then `GET https://<peer>:<port>/api/v1/health` with a 3 s timeout. The port is the peer's tailnet HTTPS port, 7820 by default. Results are cached for 60 s.
- **Peer requests:** a fleet request to a peer is `GET /api/v1/snapshot`, `/api/v1/findings`, or `/api/v1/health` over HTTPS on the tailnet listener. It carries no token and no cookie, only the header `X-Cfgdash-Fleet: 1`.
- **Peer auth on the receiving side:** a request with that header on the tailnet listener is allowed only if all of these hold:
  - `fleet.enabled` is true there ([cfgdash-config-v1](cfgdash-config-v1.md));
  - it is a GET of one of those three paths;
  - `tailscale whois` of the forwarded source address returns a node owned by the allowed login, or a tagged node of the same tailnet. These are the same identity rules as security-v1 option B, minus the token.
  Anything else gets 403. The UI listener ignores the header. Peers never get `/api/v1/files`, `/details`, `/search`, `/dirs`, or any `/api/v2` route.
- **Compare with a peer:** `GET /api/v1/compare?dir=<abs>&against=peer:<dnsName>` fetches the peer's snapshot for its start dir. `dir` applies only to the local side. Machines differ by design, so paths are compared by their context-path key (`workspaceRoot`, `claudeConfigDir`, ...), not as raw strings.
- **Nico's part:** an ACL grant that allows `tcp:7820` between his devices. Until it exists, peers show `refused` with the reason "port 7820 not reachable on the tailnet".

## Diff

`{ "left": { "machine", "collectedAt", "dir" }, "right": {...}, "counts": { "changed", "onlyLeft", "onlyRight" }, "collectors": [ { "id", "state": { "left", "right" }, "rows": [ { "blockId", "itemId", "change": "changed" \| "onlyLeft" \| "onlyRight", "left"?, "right"? } ] } ] }`.

- Rows are keyed by collector id, block id and item id (kv key or table row id), compared on redacted values.
- Collectors with no changes are omitted.
- Values arrive already redacted, so a secret never appears on either side.
