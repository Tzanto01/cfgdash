using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary><c>jobs.crontab</c>: the user's crontab lines (redacted) with managed blocks and the last write of redirected log files. Logs are never read.</summary>
internal sealed partial class CrontabCollector : ICollector
{
    private static readonly TimeSpan CrontabTimeout = TimeSpan.FromSeconds(5);

    public CollectorManifest Manifest { get; } = new(
        "jobs.crontab", "Crontab", "1.0.0", "jobs",
        new Requirements(Os: [OsFamily.Linux, OsFamily.Macos], Tools: ["crontab"]));

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var redactor = context.Redactor;
        var list = await context.Processes.RunAsync(context.Environment.Tools["crontab"], ["-l"], CrontabTimeout, cancellationToken);
        if (!list.TimedOut && list.ExitCode != 0 && list.StandardError.Contains("no crontab", StringComparison.OrdinalIgnoreCase))
        {
            return new CollectorResult([Table([])], Notes: ["no crontab for this user"]);
        }

        if (list.TimedOut || list.ExitCode != 0)
        {
            var firstLine = list.TimedOut ? "timed out" : list.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit code {list.ExitCode}";
            return CollectorResult.Unavailable(redactor.RedactText("crontab -l failed: " + firstLine));
        }

        var rows = new List<TableRow>();
        string? block = null;
        var lines = list.StandardOutput.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                if (BeginRegex().Match(line) is { Success: true } begin)
                {
                    block = begin.Groups[1].Value.Trim();
                }
                else if (EndRegex().IsMatch(line))
                {
                    block = null;
                }

                continue;
            }

            string kind;
            string? schedule = null;
            string command;
            string? logFile = null;
            string? logLastWrite = null;
            if (EnvRegex().Match(line) is { Success: true } env)
            {
                kind = "env";
                var name = env.Groups[1].Value;
                command = name + "=" + (redactor.IsSecretName(name) ? Redactor.Marker : redactor.RedactText(env.Groups[2].Value));
            }
            else
            {
                var fields = line.Split((char[]?)null, line.StartsWith('@') ? 2 : 6, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields.Length == (line.StartsWith('@') ? 2 : 6))
                {
                    kind = "job";
                    schedule = string.Join(' ', fields[..^1]);
                    command = redactor.RedactText(fields[^1]);
                    logFile = LogTarget(fields[^1], context.Environment.HomeDir);
                    if (logFile is not null && Path.IsPathRooted(logFile) && File.Exists(logFile))
                    {
                        logLastWrite = IsoTime.Format(new DateTimeOffset(File.GetLastWriteTime(logFile)));
                    }
                }
                else
                {
                    kind = "other";
                    command = redactor.RedactText(line);
                }
            }

            var number = i + 1;
            rows.Add(new TableRow($"line-{number}", new Dictionary<string, JsonNode?>
            {
                ["line"] = number,
                ["kind"] = kind,
                ["schedule"] = schedule,
                ["command"] = command,
                ["block"] = block is null ? null : redactor.RedactText(block),
                ["logFile"] = logFile is null ? null : redactor.RedactText(logFile),
                ["logLastWrite"] = logLastWrite,
            }));
        }

        return new CollectorResult([Table(rows)]);
    }

    private static TableBlock Table(IReadOnlyList<TableRow> rows) => new(
        "crontab",
        "Crontab",
        [
            new TableColumn("line", "Line"), new TableColumn("kind", "Kind"), new TableColumn("schedule", "Schedule"),
            new TableColumn("command", "Command"), new TableColumn("block", "Managed block"), new TableColumn("logFile", "Log file"),
            new TableColumn("logLastWrite", "Log last write"),
        ],
        rows);

    /// <summary>The first <c>&gt;</c> / <c>&gt;&gt;</c> target that is a file (not <c>/dev/null</c> or <c>&amp;N</c>), home-expanded; JSON path form when rooted.</summary>
    internal static string? LogTarget(string command, string homeDir)
    {
        foreach (Match match in RedirectRegex().Matches(command))
        {
            var target = match.Groups[1].Value.Trim('"', '\'');
            if (target.Length == 0 || target.StartsWith('&') || target == "/dev/null")
            {
                continue;
            }

            foreach (var home in new[] { "${HOME}", "$HOME" })
            {
                if (target == home || target.StartsWith(home + "/", StringComparison.Ordinal))
                {
                    target = homeDir + target[home.Length..];
                    break;
                }
            }

            target = PathUtil.ExpandHome(target, homeDir);
            return Path.IsPathRooted(target) ? PathUtil.ToJson(target) : target;
        }

        return null;
    }

    [GeneratedRegex(@"^#\s*BEGIN\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BeginRegex();

    [GeneratedRegex(@"^#\s*END\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndRegex();

    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvRegex();

    [GeneratedRegex(@">>?\s*([^\s;|<>]+)", RegexOptions.CultureInvariant)]
    private static partial Regex RedirectRegex();
}
