# cfgdash

A read-only dashboard of the Claude Code harness on the machine it runs on: which settings are set where, which instructions load where, agents, skills, hooks, MCP servers, permissions, environment and scheduled jobs. One command on Windows and Linux; it shows only what the machine actually has.

## Use

```
cfgdash                 # serve on http://127.0.0.1:7820 and open the browser (prints the URL when headless)
cfgdash --tailnet       # also publish on https://<machine>.<tailnet>.ts.net:7820 (tailnet only)
cfgdash --dir <path>    # start the "where" views in another directory
cfgdash stop            # stop the running instance and remove its tailscale serve handler
cfgdash update          # install the latest release (needs gh)
cfgdash export          # write a snapshot JSON to the cache dir
```

Every run has a fresh token in the printed URL. The server listens on `127.0.0.1` only and stops after 30 idle minutes.

## Install

Releases ship `cfgdash-win-x64.zip`, `cfgdash-linux-x64.tar.gz` and `SHA256SUMS`. The workspace bootstrap installs the right build (`bootstrap/install-cfgdash.ps1` to `%LOCALAPPDATA%\Programs\cfgdash`, `bootstrap/install-cfgdash.sh` to `~/.local/bin`); after that, `cfgdash update`.

## Develop

```
dotnet build cfgdash.slnx
dotnet test cfgdash.slnx
cd web && npm ci && npm test && npm run build
```

Design: [architecture](docs/architecture.md), [contracts](docs/contracts/), [roadmap](ROADMAP.md).
