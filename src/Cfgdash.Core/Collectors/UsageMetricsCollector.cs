using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary>
/// <c>usage.metrics</c>: Claude Code cost, tokens and sessions from the OpenTelemetry metrics in a Prometheus-API store
/// (VictoriaMetrics), see docs/contracts/usage-v1.md. Days use <see cref="TimeProvider.LocalTimeZone"/>.
/// Every query is evaluated at one instant a minute back: VictoriaMetrics shifts and caches instant queries near now
/// (<c>-search.latencyOffset</c>), so whole hours come from one range query and the partial hour from one instant query at that instant.
/// </summary>
internal sealed partial class UsageMetricsCollector(
    SettingsLayers settings,
    IEnvironmentReader environmentReader,
    string overridesFile,
    HttpMessageHandler? handler = null,
    TimeProvider? time = null) : ICollector
{
    private const string MetricsEndpoint = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT";
    private const string OtlpEndpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string Temporality = "OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE";
    private const int Days = 30;
    private const int Hour = 3600;
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(1);

    private static readonly HttpMessageHandler SharedHandler = new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Credentials = null,
        PreAuthenticate = false,
    };

    private static readonly string[] TokenTypes = ["input", "output", "cache-read", "cache-creation"];
    private static readonly HashSet<string> SuffixTokens = new(["total", "usd", "tokens", "seconds", "s", "count"], StringComparer.Ordinal);
    private static readonly string[] MachineLabels = ["host_name", "service_instance_id"];
    private static readonly HashSet<string> IdTokens = new(["account", "org", "organization", "user", "session", "email", "uuid"], StringComparer.Ordinal);

    private readonly TimeProvider clock = time ?? TimeProvider.System;

    public CollectorManifest Manifest { get; } = new("usage.metrics", "Usage and cost", "1.0.0", "usage", new Requirements());

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var stack = await settings.LoadAsync(context, cancellationToken);
        var endpoint = ResolveEndpoint(stack);
        if (endpoint is null)
        {
            return CollectorResult.NotPresent("no OpenTelemetry metrics endpoint configured");
        }

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
        {
            return new CollectorResult([], "the OpenTelemetry metrics endpoint is not an http(s) URL");
        }

        var client = new PrometheusClient(handler ?? SharedHandler, new Uri($"{uri.Scheme}://{uri.Authority}/"));
        var delta = !string.Equals(SettingText(stack, Temporality)?.Trim(), "cumulative", StringComparison.OrdinalIgnoreCase);
        try
        {
            return await CollectAsync(client, delta, context.Redactor, cancellationToken);
        }
        catch (MetricsStoreException e)
        {
            return new CollectorResult([], e.Message);
        }
    }

    private async Task<CollectorResult> CollectAsync(PrometheusClient client, bool delta, IRedactor redactor, CancellationToken ct)
    {
        var zone = clock.LocalTimeZone;
        var at = clock.GetUtcNow() - Settle;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);
        var firstDay = today.AddDays(1 - Days);
        var start = LocalMidnight(firstDay, zone).ToUnixTimeSeconds();
        var end = at.ToUnixTimeSeconds();
        var lastHour = start + (Hour * ((end - start) / Hour));
        var window = $"[{end - start}s]";
        var notes = new List<string> { delta ? "Counters read as delta temporality (Claude Code default)" : "Counters read as cumulative temporality" };
        var range = $"{firstDay:yyyy-MM-dd} to {today:yyyy-MM-dd} ({ZoneName(zone)})";
        List<KvRow> totals = [new("source", client.Source, new SourceRef("metrics store")), new("range", range, null)];

        var names = (await client.QueryAsync("count by (__name__) (last_over_time({__name__=~\"claude_code[._].+\"}" + window + "))", end, ct))
            .Select(s => s.Labels.GetValueOrDefault("__name__")).OfType<string>().Where(n => NameRegex().IsMatch(n)).ToList();
        var cost = Family(names, "claude_code_cost_usage");
        var tokens = Family(names, "claude_code_token_usage");
        var sessions = Family(names, "claude_code_session_count");
        var active = Family(names, "claude_code_active_time");
        string?[] families = [cost, tokens, sessions, active];
        if (families.All(f => f is null))
        {
            notes.Add("The metrics store holds no claude_code metrics");
            return new CollectorResult([new KvBlock("totals", "Totals", totals)], Notes: notes);
        }

        var labels = (await client.LabelsAsync(families.OfType<string>(), start, end, ct)).Where(l => LabelRegex().IsMatch(l)).ToList();
        var model = labels.FirstOrDefault(l => Normalize(l) == "model");
        var type = labels.FirstOrDefault(l => Normalize(l) == "type");
        var machine = MachineLabels.Select(m => labels.FirstOrDefault(l => Normalize(l) == m)).FirstOrDefault(l => l is not null);
        var project = labels.Where(IsProjectLabel).OrderBy(l => Normalize(l) switch { "project" => 0, "repo" => 1, _ => 2 }).ThenBy(l => l, StringComparer.Ordinal).FirstOrDefault();

        string Rate(string selector, string w) => (delta ? "sum_over_time(" : "increase(") + selector + w + ")";
        string Group(string expr, string target, string? label) =>
            label is null ? expr : $"label_replace({expr}, \"{target}\", \"$1\", \"{label}\", \"(.*)\")";

        string CostBy(string w) => $"sum by (g) ({Group(Rate(cost!, w), "g", model)})";
        string TokensBy(string w) => $"sum by (g, t) ({Group(Group(Rate(tokens!, w), "g", model), "t", type)})";
        string SessionsBy(string w) => $"sum({Rate(sessions!, w)})";
        var costHours = cost is null ? [] : (await client.QueryRangeAsync(CostBy("[1h]"), start + Hour, lastHour, Hour, ct)).ToList();
        var tokenHours = tokens is null ? [] : (await client.QueryRangeAsync(TokensBy("[1h]"), start + Hour, lastHour, Hour, ct)).ToList();
        var sessionHours = sessions is null ? [] : (await client.QueryRangeAsync(SessionsBy("[1h]"), start + Hour, lastHour, Hour, ct)).ToList();
        if (end > lastHour)
        {
            var tail = $"[{end - lastHour}s]";
            var parts = new[] { (cost, "cost", (Func<string, string>)CostBy), (tokens, "tokens", TokensBy), (sessions, "sessions", SessionsBy) }
                .Where(p => p.Item1 is not null)
                .Select(p => $"label_replace({p.Item3(tail)}, \"m\", \"{p.Item2}\", \"m\", \"(.*)\")");
            foreach (var s in await client.QueryAsync(string.Join(" or ", parts), end, ct))
            {
                var labelsOnly = s.Labels.Where(l => l.Key != "m").ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
                var series = new PromSeries(labelsOnly, [.. s.Samples.Select(x => ((double)(lastHour + Hour), x.Value))]);
                (s.Labels.GetValueOrDefault("m") switch { "cost" => costHours, "tokens" => tokenHours, _ => sessionHours }).Add(series);
            }
        }

        double? activeSeconds = active is null ? null : (await client.QueryAsync($"sum({Rate(active, window)})", end, ct)).SelectMany(s => s.Samples).Sum(s => s.Value);

        DateOnly DayOf(double t) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds((long)t - Hour), zone).DateTime);
        var byDay = new Dictionary<DateOnly, double[]>();
        double[] Day(double t) => byDay.TryGetValue(DayOf(t), out var row) ? row : byDay[DayOf(t)] = new double[6];
        foreach (var (t, v) in costHours.SelectMany(s => s.Samples))
        {
            Day(t)[0] += v;
        }

        foreach (var series in tokenHours)
        {
            var column = TokenColumn(series.Labels.GetValueOrDefault("t"));
            foreach (var (t, v) in column < 0 ? [] : series.Samples)
            {
                Day(t)[1 + column] += v;
            }
        }

        foreach (var (t, v) in sessionHours.SelectMany(s => s.Samples))
        {
            Day(t)[5] += v;
        }

        double DaySum(int column, int days) => byDay.Where(d => d.Key > today.AddDays(-days)).Sum(d => d.Value[column]);
        double? CostTotal(int days) => cost is null ? null : Math.Round(DaySum(0, days), 2);
        totals.InsertRange(0,
        [
            new KvRow("cost-today", CostTotal(1), null),
            new KvRow("cost-7d", CostTotal(7), null),
            new KvRow("cost-30d", CostTotal(Days), null),
            new KvRow("tok-30d", tokens is null ? null : (long)Math.Round(Enumerable.Range(1, 4).Sum(c => DaySum(c, Days))), null),
            new KvRow("sess-30d", sessions is null ? null : (long)Math.Round(DaySum(5, Days)), null),
        ]);
        if (activeSeconds is { } seconds)
        {
            totals.Insert(5, new KvRow("active-time-30d", string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:0.0} h"), null));
        }

        var blocks = new List<Block> { new KvBlock("totals", "Totals", totals) };
        blocks.Add(new TableBlock("by-day", "By day",
            [new("date", "Date"), new("cost", "Cost (USD)"), new("input", "Input"), new("output", "Output"), new("cache-read", "Cache read"), new("cache-creation", "Cache creation"), new("sessions", "Sessions")],
            [.. Enumerable.Range(0, Days).Select(i => today.AddDays(-i)).Select(day =>
            {
                var v = byDay.GetValueOrDefault(day) ?? new double[6];
                var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return new TableRow(key, new Dictionary<string, JsonNode?>
                {
                    ["date"] = key,
                    ["cost"] = cost is null ? null : Math.Round(v[0], 2),
                    ["input"] = tokens is null ? null : (long)Math.Round(v[1]),
                    ["output"] = tokens is null ? null : (long)Math.Round(v[2]),
                    ["cache-read"] = tokens is null ? null : (long)Math.Round(v[3]),
                    ["cache-creation"] = tokens is null ? null : (long)Math.Round(v[4]),
                    ["sessions"] = sessions is null ? null : (long)Math.Round(v[5]),
                });
            })],
            $"Newest first, days in {ZoneName(zone)}"));

        if (model is not null)
        {
            var rows = new Dictionary<string, double[]>(StringComparer.Ordinal);
            double[] Row(string? name) => rows.TryGetValue(name ?? "", out var r) ? r : rows[name ?? ""] = new double[5];
            foreach (var s in costHours)
            {
                Row(s.Labels.GetValueOrDefault("g"))[0] += s.Samples.Sum(x => x.Value);
            }

            foreach (var s in tokenHours)
            {
                var column = TokenColumn(s.Labels.GetValueOrDefault("t"));
                if (column >= 0)
                {
                    Row(s.Labels.GetValueOrDefault("g"))[1 + column] += s.Samples.Sum(x => x.Value);
                }
            }

            var modelIds = new HashSet<string>(StringComparer.Ordinal);
            blocks.Add(new TableBlock("by-model", "By model",
                [new("model", "Model"), new("cost", "Cost (USD)"), new("input", "Input"), new("output", "Output"), new("cache-read", "Cache read"), new("cache-creation", "Cache creation")],
                [.. rows.OrderByDescending(r => r.Value[0]).ThenBy(r => r.Key, StringComparer.Ordinal).Select(r => LabeledRow(redactor, r.Key, "model", modelIds, new Dictionary<string, JsonNode?>
                {
                    ["cost"] = cost is null ? null : Math.Round(r.Value[0], 2),
                    ["input"] = tokens is null ? null : (long)Math.Round(r.Value[1]),
                    ["output"] = tokens is null ? null : (long)Math.Round(r.Value[2]),
                    ["cache-read"] = tokens is null ? null : (long)Math.Round(r.Value[3]),
                    ["cache-creation"] = tokens is null ? null : (long)Math.Round(r.Value[4]),
                }))],
                "Last 30 days"));
        }

        async Task GroupTable(string? label, string id, string title, string key, string keyLabel)
        {
            if (label is null)
            {
                return;
            }

            var parts = new[] { (cost, "cost"), (tokens, "tokens"), (sessions, "sessions") }
                .Where(p => p.Item1 is not null)
                .Select(p => $"label_replace(sum by (g) ({Group(Rate(p.Item1!, window), "g", label)}), \"m\", \"{p.Item2}\", \"m\", \"(.*)\")");
            var result = await client.QueryAsync(string.Join(" or ", parts), end, ct);
            var rows = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
            foreach (var s in result)
            {
                var row = rows.TryGetValue(s.Labels.GetValueOrDefault("g") ?? "", out var r) ? r : rows[s.Labels.GetValueOrDefault("g") ?? ""] = [];
                row[s.Labels.GetValueOrDefault("m") ?? ""] = s.Samples.Sum(x => x.Value);
            }

            var ids = new HashSet<string>(StringComparer.Ordinal);
            blocks.Add(new TableBlock(id, title,
                [new(key, keyLabel), new("cost", "Cost (USD)"), new("tokens", "Tokens"), new("sessions", "Sessions")],
                [.. rows.OrderByDescending(r => r.Value.GetValueOrDefault("cost")).ThenBy(r => r.Key, StringComparer.Ordinal).Select(r => LabeledRow(redactor, r.Key, key, ids, new Dictionary<string, JsonNode?>
                {
                    ["cost"] = cost is null ? null : Math.Round(r.Value.GetValueOrDefault("cost"), 2),
                    ["tokens"] = tokens is null ? null : (long)Math.Round(r.Value.GetValueOrDefault("tokens")),
                    ["sessions"] = sessions is null ? null : (long)Math.Round(r.Value.GetValueOrDefault("sessions")),
                }))],
                $"Last 30 days per {label}"));
        }

        await GroupTable(machine, "by-machine", "By machine", "machine", "Machine");
        await GroupTable(project, "by-project", "By project", "project", "Project");
        return new CollectorResult(blocks, Notes: notes);
    }

    /// <summary>A row for one store label value: the redacted value is both the key cell and the row id (made unique), never the raw value.</summary>
    private static TableRow LabeledRow(IRedactor redactor, string value, string column, HashSet<string> ids, Dictionary<string, JsonNode?> cells)
    {
        var shown = redactor.RedactText(value.Length == 0 ? "(none)" : value);
        var id = shown;
        for (var n = 2; !ids.Add(id); n++)
        {
            id = $"{shown} ({n})";
        }

        cells[column] = shown;
        return new TableRow(id, cells);
    }

    private string? ResolveEndpoint(SettingsStack stack)
    {
        if (HarnessFiles.TryReadStrictJson(overridesFile, out var root, out _)
            && root?["usage"]?["metricsUrl"] is JsonValue value && value.TryGetValue<string>(out var url) && url.Length > 0)
        {
            return url;
        }

        string? Effective(string name) => SettingText(stack, name) is { Length: > 0 } s ? s : environmentReader.Get(name);
        return Effective(MetricsEndpoint) ?? Effective(OtlpEndpoint);
    }

    /// <summary>The <c>env</c> value from the managed and user layers only; a project or local layer never chooses the store.</summary>
    private static string? SettingText(SettingsStack stack, string name) =>
        stack.Contributing
            .Where(l => l.Scope is "managed" or "user" || l.ExtraScopes.Contains("user"))
            .Select(l => l.Root?["env"] is JsonObject env && env[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .FirstOrDefault(s => s is not null);

    /// <summary>All discovered names of one metric: the stem with dots or underscores plus only unit and <c>_total</c> suffixes.</summary>
    private static string? Family(IEnumerable<string> names, string stem)
    {
        var matches = names.Where(n =>
        {
            var normal = Normalize(n);
            return normal.StartsWith(stem, StringComparison.Ordinal)
                && normal[stem.Length..].Split('_', StringSplitOptions.RemoveEmptyEntries).All(SuffixTokens.Contains)
                && (normal.Length == stem.Length || normal[stem.Length] == '_');
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return matches.Count == 0 ? null : "{__name__=~\"" + string.Join('|', matches.Select(m => m.Replace(".", "\\\\.", StringComparison.Ordinal))) + "\"}";
    }

    private static bool IsProjectLabel(string label)
    {
        var parts = Normalize(label).Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => p is "project" or "repo" or "repository") && !parts.Any(IdTokens.Contains);
    }

    private static int TokenColumn(string? type) => Normalize(type ?? "").Replace("_", "", StringComparison.Ordinal) switch
    {
        "input" => 0,
        "output" => 1,
        "cacheread" => 2,
        "cachecreation" => 3,
        _ => -1,
    };

    private static string Normalize(string name) => name.Replace('.', '_').ToLowerInvariant();

    private static DateTimeOffset LocalMidnight(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(30);
        }

        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static string ZoneName(TimeZoneInfo zone) =>
        zone.HasIanaId || !TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? zone.Id : iana;

    [GeneratedRegex("^[A-Za-z_:][A-Za-z0-9_.:]*$")]
    private static partial Regex NameRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.]*$")]
    private static partial Regex LabelRegex();
}
