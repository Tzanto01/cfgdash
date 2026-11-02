# Contract: usage and cost v1 (ROADMAP 3)

Owner: department lead. Producer: team `usage` (core, `web/mock`). Consumer: the generic UI blocks now, team `web`'s Usage view later. Decided by Nico on 2026-10-28: read the existing Claude Code OpenTelemetry metrics from VictoriaMetrics.

## Source

- Endpoint: the origin (scheme, host, port) of `OTEL_EXPORTER_OTLP_METRICS_ENDPOINT`, else of `OTEL_EXPORTER_OTLP_ENDPOINT`, taken from the `env` of the managed and user settings layers only (project and local layers are ignored, because a cloned repo must not choose where cfgdash sends requests), else from the process environment through `AllowlistEnvironmentReader` (both names are already allowlisted). Queries go to that origin's Prometheus API (`/api/v1/query`, `/api/v1/query_range`, `/api/v1/labels`, `/api/v1/series`). Optional override in `overrides.json`: `"usage": { "metricsUrl": "http://host:8428" }`.
- Requests: `GET` only, plain `HttpClient`, 5 s per request, at most 10 requests per snapshot, no redirects followed to another origin, no credentials and no `OTEL_EXPORTER_OTLP_*HEADERS` sent. The URL path and query never contain user data beyond metric and label names.
- Metric names are discovered at runtime (`claude_code` token, cost, session and active-time metrics, with or without the exporter's unit and `_total` suffixes). Never hardcode one exact name list without that fallback.

## Collector `usage.metrics`

View `usage`. Requirements: none (the endpoint check is the state rule). State:

| Situation | State, reason |
| --- | --- |
| No endpoint configured | `notPresent`, "no OpenTelemetry metrics endpoint configured" |
| Endpoint unreachable, timeout, non-2xx, or 401/403 | `unavailable`, one line ("metrics store at <host:port> unreachable", "metrics store needs auth (not supported)") |
| Store answers but holds no `claude_code` metrics | `active` with the totals kv only and a note |

Blocks (plain block ids as in snapshot-v1, stable; a block whose labels do not exist in the store is omitted):

| Block id | Kind | Content |
| --- | --- | --- |
| `totals` | kv | `cost-today`, `cost-7d`, `cost-30d` (USD, 2 decimals), `tok-30d`, `sess-30d` (key names avoid the redactor's secret words), `active-time-30d` (when present), `source` (host:port of the store), `range` |
| `by-day` | table | last 30 days, newest first: `date`, `cost`, `input`, `output`, `cache-read`, `cache-creation`, `sessions` |
| `by-model` | table | 30 days: `model`, `cost`, `input`, `output`, `cache-read`, `cache-creation` |
| `by-machine` | table | 30 days per `host.name` or `service.instance.id` label when present: `machine`, `cost`, `tokens`, `sessions` |
| `by-project` | table | 30 days per a project-like label when present (`project`, `repo`, or a `OTEL_RESOURCE_ATTRIBUTES` key the store exposes): `project`, `cost`, `tokens`, `sessions` |

Values pass through the redactor like every block. Account, organization, user and session id labels are never shown. Days use the machine's local time zone; the kv `range` names it.
