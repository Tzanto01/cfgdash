using System.Globalization;
using System.Text.Json.Nodes;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary><c>jobs.windows-tasks</c>: scheduled tasks whose exec actions reference the harness (read-only COM walk).</summary>
internal sealed class WindowsTasksCollector(Func<IReadOnlyList<ScheduledTaskInfo>>? source = null) : ICollector
{
    private static readonly string[] StateNames = ["Unknown", "Disabled", "Queued", "Ready", "Running"];

    private static readonly Dictionary<uint, string> ResultMeanings = new()
    {
        [0x0] = "success",
        [0x1] = "incorrect function",
        [0x2] = "file not found",
        [0xA] = "environment incorrect",
        [0x41300] = "ready",
        [0x41301] = "running",
        [0x41302] = "disabled",
        [0x41303] = "has not run yet",
        [0x41304] = "no more runs scheduled",
        [0x41306] = "terminated by user",
        [0x8004131F] = "an instance is already running",
        [0x800710E0] = "request refused",
        [0xC000013A] = "terminated (Ctrl+C or close)",
        [0xC0000142] = "DLL initialization failed",
    };

    public CollectorManifest Manifest { get; } = new(
        "jobs.windows-tasks", "Scheduled tasks", "1.0.0", "jobs",
        new Requirements(Os: [OsFamily.Windows], Tools: ["schtasks"]));

    public Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        IReadOnlyList<ScheduledTaskInfo> tasks;
        var denied = 0;
        try
        {
            if (source is not null)
            {
                tasks = source();
            }
            else if (OperatingSystem.IsWindows())
            {
                tasks = ScheduledTaskReader.Read(out denied);
            }
            else
            {
                return Task.FromResult(CollectorResult.Unavailable("Task Scheduler not reachable: Windows only"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(CollectorResult.Unavailable(context.Redactor.RedactText("Task Scheduler not reachable: " + ex.Message.ReplaceLineEndings(" ").Trim())));
        }

        var needles = new[] { context.Environment.GetPath(PathKeys.WorkspaceRoot), context.Environment.GetPath(PathKeys.ConfigRepo), "WORKSPACE_ROOT", "CLAUDE_CONFIG_DIR" }
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => Normalize(n!))
            .ToList();

        var redactor = context.Redactor;
        var rows = new List<TableRow>();
        foreach (var task in tasks.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase))
        {
            var action = task.Actions.FirstOrDefault(a =>
            {
                var text = Normalize(a.Path + "\n" + a.Arguments + "\n" + a.WorkingDirectory);
                return needles.Any(n => text.Contains(n, StringComparison.Ordinal));
            });
            if (action is null)
            {
                continue;
            }

            var result = unchecked((uint)task.LastTaskResult);
            var cells = new Dictionary<string, JsonNode?>
            {
                ["task"] = redactor.RedactText(task.Path),
                ["state"] = task.State >= 0 && task.State < StateNames.Length ? StateNames[task.State] : StateNames[0],
                ["enabled"] = task.Enabled,
                ["lastRun"] = Time(task.LastRunTime),
                ["lastResult"] = string.Create(CultureInfo.InvariantCulture, $"0x{result:X8}: {ResultMeanings.GetValueOrDefault(result, "see Task Scheduler result codes")}"),
                ["nextRun"] = Time(task.NextRunTime),
                ["action"] = redactor.RedactText(string.IsNullOrEmpty(action.Arguments) ? $"\"{action.Path}\"" : $"\"{action.Path}\" {action.Arguments}"),
            };
            IReadOnlyList<string>? flags = result != 0 && result is < 0x41300 or > 0x41307 ? ["error"] : null;
            rows.Add(new TableRow(task.Path, cells, flags));
        }

        IReadOnlyList<string>? notes = denied > 0 ? [$"{denied} task folder(s) skipped (access denied)"] : null;
        return Task.FromResult(new CollectorResult(
            [
                new TableBlock(
                    "tasks",
                    "Scheduled tasks",
                    [
                        new TableColumn("task", "Task"), new TableColumn("state", "State"), new TableColumn("enabled", "Enabled"),
                        new TableColumn("lastRun", "Last run"), new TableColumn("lastResult", "Last result"), new TableColumn("nextRun", "Next run"),
                        new TableColumn("action", "Action"),
                    ],
                    rows,
                    "Tasks with an exec action whose path, arguments or working directory reference the workspace root, the config repo, WORKSPACE_ROOT or CLAUDE_CONFIG_DIR"),
            ],
            Notes: notes));
    }

    private static string Normalize(string value) => value.Replace('\\', '/').ToUpperInvariant();

    private static JsonNode? Time(DateTimeOffset value) => value.Year < 2000 ? null : IsoTime.Format(value);
}
