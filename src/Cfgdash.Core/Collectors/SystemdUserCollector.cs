using System.Text.Json.Nodes;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary><c>jobs.systemd-user</c>: services and timers defined in the user's systemd unit dir, state from one read-only <c>systemctl --user show</c>.</summary>
internal sealed class SystemdUserCollector(IEnvironmentReader environmentReader) : ICollector
{
    private static readonly TimeSpan ShowTimeout = TimeSpan.FromSeconds(10);
    private const string Properties = "--property=Id,Description,LoadState,ActiveState,SubState,Result,UnitFileState,ExecMainStatus,ExecMainExitTimestamp,LastTriggerUSec,NextElapseUSecRealtime,Triggers";

    public CollectorManifest Manifest { get; } = new(
        "jobs.systemd-user", "systemd user units", "1.0.0", "jobs",
        new Requirements(Os: [OsFamily.Linux], Tools: ["systemctl"]));

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var configHome = environmentReader.Get("XDG_CONFIG_HOME");
        var dir = Path.Combine(string.IsNullOrEmpty(configHome) ? Path.Combine(context.Environment.HomeDir, ".config") : configHome, "systemd", "user");
        var files = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir)
                .Where(f => f.EndsWith(".service", StringComparison.Ordinal) || f.EndsWith(".timer", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];
        var redactor = context.Redactor;
        if (files.Count == 0)
        {
            return new CollectorResult([Table([])], Notes: [redactor.RedactText($"no user units in {PathUtil.ToJson(dir)}")]);
        }

        var units = files.Select(f => Path.GetFileName(f)).ToList();
        var show = await context.Processes.RunAsync(
            context.Environment.Tools["systemctl"],
            ["--user", "show", "--no-pager", Properties, "--", .. units],
            ShowTimeout,
            cancellationToken);
        if (show.TimedOut || show.ExitCode != 0)
        {
            var firstLine = show.TimedOut ? "timed out" : show.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit code {show.ExitCode}";
            return CollectorResult.Unavailable(redactor.RedactText("systemd user manager not reachable: " + firstLine));
        }

        var properties = ParseShow(show.StandardOutput);
        var rows = new List<TableRow>();
        for (var i = 0; i < files.Count; i++)
        {
            var unit = units[i];
            var p = properties.GetValueOrDefault(unit) ?? new Dictionary<string, string>();
            string? Get(string key) => p.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
            JsonNode? Text(string? value) => value is null ? null : redactor.RedactText(value);
            JsonNode? Time(string? value) => value is null or "n/a" ? null : redactor.RedactText(value);

            var service = unit.EndsWith(".service", StringComparison.Ordinal);
            var result = Get("Result");
            var status = Get("ExecMainStatus");
            var cells = new Dictionary<string, JsonNode?>
            {
                ["unit"] = Text(unit),
                ["kind"] = service ? "service" : "timer",
                ["description"] = Text(Get("Description")),
                ["active"] = Text(Get("ActiveState") is { } active ? Get("SubState") is { } sub ? $"{active}/{sub}" : active : null),
                ["result"] = Text(service && result is not null && status is not null ?$"{result} (exit {status})" : result),
                ["lastRun"] = Time(service ? Get("ExecMainExitTimestamp") : Get("LastTriggerUSec")),
                ["nextRun"] = service ? null : Time(Get("NextElapseUSecRealtime")),
                ["triggers"] = Text(Get("Triggers")),
                ["enabled"] = Text(Get("UnitFileState")),
            };
            IReadOnlyList<string>? flags = Get("ActiveState") == "failed" || (result is not null && result != "success") ? ["error"] : null;
            rows.Add(new TableRow(unit, cells, flags, context.Files.Register(files[i], "other")?.Id));
        }

        return new CollectorResult([Table(rows)]);
    }

    private static TableBlock Table(IReadOnlyList<TableRow> rows) => new(
        "units",
        "systemd user units",
        [
            new TableColumn("unit", "Unit"), new TableColumn("kind", "Kind"), new TableColumn("description", "Description"),
            new TableColumn("active", "Active"), new TableColumn("result", "Result"), new TableColumn("lastRun", "Last run"),
            new TableColumn("nextRun", "Next run"), new TableColumn("triggers", "Triggers"), new TableColumn("enabled", "Enabled"),
        ],
        rows);

    /// <summary>Parses <c>systemctl show</c> output: <c>Key=Value</c> lines, one blank-line separated block per unit, keyed by <c>Id</c>.</summary>
    internal static Dictionary<string, Dictionary<string, string>> ParseShow(string output)
    {
        var units = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var current = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n').Append(string.Empty))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (current.TryGetValue("Id", out var id))
                {
                    units[id] = current;
                }

                current = new Dictionary<string, string>(StringComparer.Ordinal);
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq > 0)
            {
                current[line[..eq]] = line[(eq + 1)..];
            }
        }

        return units;
    }
}
