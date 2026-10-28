# Contract: security v1

Owner: department lead. Every team implements its side; `security-reviewer` checks the whole before the first release. Nothing here may be weakened for brevity.

## Read-only

- The only writes to configuration are the guarded edits in [edits-v1](edits-v1.md) (catalog targets, UI listener only, dry-run diff, explicit confirm, audit log). Apart from them cfgdash never writes, edits, deletes or moves any configuration, settings, instruction, agent, skill, hook or job file, and never changes scheduled tasks, systemd units, crontab or git state. Git is called read-only (`status`, `rev-parse`, `log`) with `--no-optional-locks`.
- cfgdash writes only inside its own cache dir (run state, logs, exports) and, for `update`, its own install location. Accepted exceptions (Nico, 2026-10-28): opening `engram.db` with `Mode=ReadOnly` may create the empty SQLite `-shm`/`-wal` side files next to it (they exist anyway while Engram runs; `engram.db` is never written), and the single-file exe unpacks its native SQLite library into the .NET bundle extraction dir (`%TEMP%\.net\cfgdash`, `$TMPDIR/.net/cfgdash`).
- External processes run without a shell, arguments as a list, with timeouts, inheriting no extra environment.
- The future write path (dry-run diff, explicit confirmation, audit log) lives behind its own `/api/v2` surface and is out of v1. `docs/architecture.md` names the seam.

## Network

- Kestrel binds `127.0.0.1` only: the UI port and, with `--tailnet`, one extra ephemeral loopback port that only `tailscale serve` targets. Never `0.0.0.0`, `::`, a LAN address or a tailnet address.
- Per-run token: 32 bytes from a CSPRNG, base64url. Compared in constant time. Lives only in memory, the run-state file and the printed URL.
- Cookie `cfgdash_session`: `HttpOnly`, `SameSite=Strict`, `Path=/`, `Secure` on the tailnet listener; its value is a per-listener HMAC of the token (never the token itself), valid only on the listener that issued it.
- Host allowlist (anti DNS-rebinding), checked before auth: UI listener `127.0.0.1:<port>` and `localhost:<port>`; tailnet listener the machine's tailnet DNS name (with and without the HTTPS port) and whatever exact Host value `tailscale serve` is verified to forward. Anything else `400`.
- `POST /api/v1/shutdown` accepts the bearer header only.
- Headers on every response: `Content-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`; API responses `Cache-Control: no-store`.
- Idle shutdown after 30 minutes without an authenticated request; Ctrl+C and `cfgdash stop` shut down gracefully. Every shutdown path removes the tailscale serve handler cfgdash added and deletes `run.json`.

## Tailnet (`--tailnet`)

- Preconditions, each a clear exit-2 message when missing: tailscale installed and running, MagicDNS on, HTTPS certificates enabled for the tailnet (`CertDomains` non-empty), the chosen HTTPS port free in `tailscale serve status --json`.
- Publish with `tailscale serve --bg --https=<port> http://127.0.0.1:<backendPort>` on a dedicated port (default = the UI port). Never `funnel`, never `tailscale serve reset`, never touch existing handlers (the PC already serves 443).
- Undo with the exact `off` form for that one handler on every shutdown path, then confirm via `serve status --json` that it is gone.
- Every request on the tailnet listener needs the token AND one identity (Nico's Gate 1 ruling, option B):
  - **User**: `Tailscale-User-Login` equals (case-insensitive) an allowed login. Allowed logins, first match wins: `allowedLogins` in the override file; the self node's user when the node is untagged; the login implied by a personal tailnet name (`<name>.github` -> `<name>@github`). tailscaled strips client copies of these headers.
  - **Own tagged device**: no user header, and `tailscale whois --json <X-Forwarded-For>` says the source node is tagged, its DNS name ends with this tailnet's MagicDNS suffix (excludes shared-in nodes), and its tags intersect `allowedTags` when the override file sets it. `X-Forwarded-For` is trusted only on the tailnet listener and only after verifying on the installed tailscale version that serve overwrites it; cache whois answers per IP for 60 s.
  - Otherwise `403 {"error":"forbidden","reason":"tailnet-identity"}`.
- No allowed login derivable and no tagged-device path: refuse `--tailnet` with exit 2 and the override-file path. Startup prints the allowed login and "tagged devices of <tailnet> allowed".

## Secrets and redaction (`Redactor`, owned by `core`, its own test file)

- Never open: `.credentials.json`, `*.pem`, `*.key`, `*.pfx`, `*.p12`, `*.kdbx`, `id_rsa*`, `id_ecdsa*`, `id_ed25519*`, `.env`, `.env.*`, `.netrc`, `.git-credentials`, anything under `.ssh/`, `.gnupg/` (the full list is `FileRegistry`'s `NeverRead*` arrays). `FileRegistry.Register` refuses them, so the files and search endpoints can never serve them. `.claude.json` is parsed for MCP servers and project keys only and is never registered.
- Never probe or open a path taken from file content (`@imports`, references, hook scripts) when it is a Windows UNC or device path (`\\server\share`, `\\?\`, `\\.\`): a probe can hand the user's NTLM credentials to a remote host. Imports show `network path not read`, hook rows the `network-path` flag. On Linux and macOS, `/proc/`, `/sys/` and `/dev/` are never read.
- Key rule: a JSON property, env var or header name is secret when one of its camelCase/snake/kebab word parts is `token`, `secret`, `password`, `passwd`, `pwd`, `pass`, `auth`, `authorization`, `credential`, `credentials`, `cookie`, `cookies`, `key`, `apikey`, `headers`, `session`, or the whole name contains `apikey`, `accesskey`, `privatekey`. Its value (any JSON type) becomes `"[redacted]"`.
- Every value of an MCP `headers` object and of `OTEL_EXPORTER_OTLP_*HEADERS` is redacted regardless of key.
- Value rule on every remaining string (settings values, args, commands, cron lines, file content served to the UI): the high-confidence patterns from `config/.claude/hooks/secret-patterns.ps1` (`$CCSecretHighConfidence`), the password-literal pattern with its placeholder exemption, `Bearer <x>`, and CLI flags `--(token|api-key|key|secret|password)[= ]<x>`; the matched secret part becomes `[redacted]`.
- Over-redaction is acceptable; under-redaction is a defect.

## Environment variables

Read only by exact name from this allowlist (process scope; on Windows also User and Machine scope for the same names). No prefix scans, no enumeration:

Exactly the names in `src/Cfgdash.Core/Environment/HarnessEnvAllowlist.cs`, read only through `AllowlistEnvironmentReader` (unknown names throw): `Harness` names are shown with their value (redactor still applies), `PresenceOnly` credential names are reported as set or unset and their value never leaves the reader, `Runtime` names are internal probes and never displayed. Adding a name is a contract change.

## Company data

- Files under a company prefix carry `company: true`. They are shown only by the instance on that machine, over localhost or the owner's tailnet.
- Snapshots, caches, logs and exports go only to the user cache dir; nothing goes to a git-tracked path. Logs never contain file content.
- Committed fixtures and examples are synthetic: no real company names, paths, hosts, IPs, ticket ids or people. A fixture-safety test enforces it by pattern, never by listing real values: every IPv4 is loopback or a TEST-NET range, every host is `*.example` / `example.ts.net` / `localhost`, user paths use the user name `user`, ticket-like ids match a small synthetic allowlist. The department lead additionally greps each push locally for known real markers without committing them.

## Accepted residuals (v1, from the release security review)

- **UI cookie and other loopback ports.** Browsers scope cookies by host, not port, so `cfgdash_session` for `127.0.0.1` also reaches any other service on `127.0.0.1`. A hostile local listener that a hostile page steers the browser to could capture and replay it on the UI listener. Needs a process on this machine the owner does not control. Fix path if it ever matters: drop the UI cookie for a `#token=` fragment kept in origin-scoped storage and sent as `Authorization: Bearer`.
- **Stale serve handler after a hard kill or reboot.** `tailscale serve --bg` persists; if cfgdash dies without its shutdown path, the handler keeps pointing at the old loopback backend port until the next `cfgdash run` or `cfgdash stop` removes it. Run `cfgdash stop` after a crash or reboot during a `--tailnet` session.
- **Tagged devices without `allowedTags`.** With no `allowedTags` set, every tagged device of the owner's tailnet passes the identity step; the token is then the only barrier.
- **Symlinks to UNC targets inside a target dir** are still followed by existence checks. Git creates such links only with `core.symlinks=true`, which is off by default on Windows.
