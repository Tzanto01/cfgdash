using System.Runtime.Versioning;

namespace Cfgdash.Core.Collectors;

/// <summary>An exec action of a scheduled task.</summary>
internal sealed record ScheduledTaskAction(string Path, string? Arguments, string? WorkingDirectory);

/// <summary>Read-only view of one scheduled task. <c>State</c>: 0 Unknown, 1 Disabled, 2 Queued, 3 Ready, 4 Running.</summary>
internal sealed record ScheduledTaskInfo(
    string Path,
    bool Enabled,
    int State,
    DateTimeOffset LastRunTime,
    int LastTaskResult,
    DateTimeOffset NextRunTime,
    IReadOnlyList<ScheduledTaskAction> Actions);

/// <summary>Walks the Task Scheduler through late-bound COM (locale-independent). Calls only read members.</summary>
internal static class ScheduledTaskReader
{
    private const int IncludeHidden = 1;
    private const int ExecActionType = 0;

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<ScheduledTaskInfo> Read(out int deniedFolders)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        var tasks = new List<ScheduledTaskInfo>();
        var denied = 0;
        Walk(service.GetFolder("\\"));
        deniedFolders = denied;
        return tasks;

        void Walk(dynamic folder)
        {
            var subfolders = new List<dynamic>();
            var accessDenied = false;
            try
            {
                foreach (var task in folder.GetTasks(IncludeHidden))
                {
                    tasks.Add(ReadTask(task));
                }
            }
            catch (UnauthorizedAccessException)
            {
                accessDenied = true;
            }

            try
            {
                foreach (var sub in folder.GetFolders(0))
                {
                    subfolders.Add(sub);
                }
            }
            catch (UnauthorizedAccessException)
            {
                accessDenied = true;
            }

            if (accessDenied)
            {
                denied++;
            }

            foreach (var sub in subfolders)
            {
                Walk(sub);
            }
        }
    }

    private static ScheduledTaskInfo ReadTask(dynamic task)
    {
        var actions = new List<ScheduledTaskAction>();
        foreach (var action in task.Definition.Actions)
        {
            if ((int)action.Type == ExecActionType)
            {
                actions.Add(new ScheduledTaskAction((string?)action.Path ?? string.Empty, (string?)action.Arguments, (string?)action.WorkingDirectory));
            }
        }

        return new ScheduledTaskInfo(
            (string)task.Path,
            (bool)task.Enabled,
            (int)task.State,
            Local((DateTime)task.LastRunTime),
            (int)task.LastTaskResult,
            Local((DateTime)task.NextRunTime),
            actions);
    }

    private static DateTimeOffset Local(DateTime value) =>
        value.Year < 2 ? DateTimeOffset.MinValue : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Local));
}
