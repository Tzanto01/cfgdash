# Contract: cfgdash settings and start at login v1

Owner: department lead. Producer: team `platform` (core, host, `web/mock`). Consumer: team `web` (the gear screen). Decided by Nico on 2026-11-02: the gear opens cfgdash's own settings, and start at login is opt-in.

## Config file

`config.json` in cfgdash's config dir (Windows `%APPDATA%\cfgdash`, Linux `$XDG_CONFIG_HOME/cfgdash` or `~/.config/cfgdash`, next to `overrides.json`). Only cfgdash writes it: atomic replace, 2-space JSON, unknown keys kept. A missing file means defaults. `overrides.json` stays hand-edited and is never written.

| Key | Type | Default | Meaning |
| --- | --- | --- | --- |
| `startAtLogin` | bool | false | Mirrors the OS entry below. The entry is the truth; on read, a mismatch is shown, never silently fixed |
| `theme` | `system` \| `light` \| `dark` | `system` | Replaces the browser-only toggle; the UI still applies it before first render |
| `defaultDir` | string \| null | null | Start folder when `--dir` is not given (absolute, must exist) |
| `tailnet` | bool | false | Publish on the tailnet at start (same as `--tailnet`) |
| `fleet.enabled` | bool | false | This machine answers fleet peers ([fleet-v1](fleet-v1.md)) |
| `assistant.model` | string | `sonnet` | Default model alias for the assistant ([assistant-v1](assistant-v1.md)) |
| `assistant.effort` | `low` \| `medium` \| `high` \| `xhigh` \| `max` | `medium` | Default effort |
| `assistant.maxBudgetUsd` | number 0.1 to 50 | 2.0 | Per-session cost cap (`--max-budget-usd`) |

CLI flags win over the file for one run.

## Start at login

- `cfgdash autostart on|off|status`. Exit codes follow api-v1; `status` prints `on`, `off` or `mismatch`.
- Windows: the HKCU `Software\Microsoft\Windows\CurrentVersion\Run` value `cfgdash`, set to `"<exe>" run --autostart`. No admin, no scheduled task.
- Linux: `$XDG_CONFIG_HOME/systemd/user/cfgdash.service` (default `~/.config/systemd/user/`, where `systemd --user` looks) (`ExecStart=<exe> run --autostart`, `Restart=on-failure`, `RestartSec=10`, `WantedBy=default.target`, and `Environment=PATH=<the PATH of the process that ran autostart on>` with `%` escaped as `%%`, so tools in user PATH dirs such as `~/.local/bin` stay visible), then `systemctl --user enable --now` (or `disable --now --no-block`, since the stopped instance may be the one serving the request, and delete the file). Without a user systemd it fails with exit 2 and the reason.
- `run --autostart` means `--no-open`, no idle shutdown, the snapshot for the start dir built at start, localhost only unless `tailnet` is true. With no `defaultDir`, the start dir is the user's home dir.
- `cfgdash` or `cfgdash run` while an instance is alive opens the browser on the live URL in a desktop session (the same rule as today), otherwise it prints the URL.
- These two OS entries are the only files outside the cache and config dirs cfgdash writes for this feature. They need a line in `AGENTS.md`'s write exceptions; the department lead adds it with Nico's authorization.

## API (UI listener only, same request rules as edits-v1 POSTs)

| Method + path | Answer |
| --- | --- |
| `GET /api/v2/cfgdash-settings` | `{ "values": { ...keys above }, "startAtLogin": { "configured": bool, "actual": "on" \| "off" \| "unsupported", "detail"? }, "paths": { "config", "overrides" }, "configError"? }`. `configError` is set when `config.json` exists but cannot be parsed (then `values` are the defaults and a PUT answers `409 {"error":"config-unreadable"}` without touching the file or the OS entry) |
| `PUT /api/v2/cfgdash-settings` | Body: a partial `values` object (only the keys being changed; nested keys as in GET, for example `{ "assistant": { "effort": "high" } }`). Validates types, writes the config file, and for `startAtLogin` changes the OS entry first (if that fails, `500 {"error":"autostart-failed","reason"}` and the file is unchanged). Answers the GET shape. No diff or confirm step: these are cfgdash's own settings, not harness config. An audit line goes to `<cache>/edits/audit.jsonl` with `kind: "cfgdash-settings"` |

On the tailnet listener both answer 404.
