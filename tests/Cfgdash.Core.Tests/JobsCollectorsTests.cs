using Cfgdash.Core.Collectors;
using Cfgdash.Core.Files;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class JobsCollectorsTests : IDisposable
{
    private static readonly string FakeGitHubToken = "gh" + "p_" + new string('a', 36);
    private static readonly string FakeAnthropicKey = "sk-" + "ant-" + new string('b', 24);

    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private static CollectContext Context(MachineEnvironment environment, IProcessRunner processes) => new()
    {
        Environment = environment,
        TargetDir = environment.HomeDir,
        Files = new FileRegistry([]),
        Redactor = new Redactor(),
        Processes = processes,
    };

    private static TableBlock Table(CollectorResult result) => Assert.IsType<TableBlock>(Assert.Single(result.Blocks));

    private static string? Cell(TableRow row, string key) => row.Cells[key]?.ToString();

    private static ScheduledTaskInfo ScheduledTask(
        string path,
        ScheduledTaskAction action,
        int state = 3,
        int result = 0,
        DateTimeOffset? lastRun = null,
        DateTimeOffset? nextRun = null) =>
        new(path, true, state, lastRun ?? new DateTimeOffset(2026, 10, 26, 3, 0, 0, TimeSpan.FromHours(1)), result, nextRun ?? DateTimeOffset.MinValue, [action]);

    private MachineEnvironment WindowsEnvironment() => TestEnvironments.Create(
        tree,
        paths:
        [
            TestEnvironments.ContextPath(PathKeys.WorkspaceRoot, tree.Dir("ws")),
            TestEnvironments.ContextPath(PathKeys.ConfigRepo, tree.Dir("ws/config")),
        ],
        tools: new Dictionary<string, string> { ["schtasks"] = "schtasks" },
        os: OsFamily.Windows);

    private async Task<CollectorResult> CollectTasks(params ScheduledTaskInfo[] tasks) =>
        await new WindowsTasksCollector(() => tasks).CollectAsync(Context(WindowsEnvironment(), new FakeProcessRunner()), CancellationToken.None);

    [Fact]
    public async Task Windows_tasks_lists_only_tasks_whose_actions_reference_the_harness()
    {
        var workspaceUpperBackslash = tree.Path("ws").Replace('/', '\\').ToUpperInvariant();
        var configForwardSlash = tree.Path("ws/config").Replace('\\', '/');

        var result = await CollectTasks(
            ScheduledTask(@"\Harness\Path", new ScheduledTaskAction(workspaceUpperBackslash + @"\scripts\sync.cmd", null, null)),
            ScheduledTask(@"\Harness\WorkingDir", new ScheduledTaskAction("pwsh.exe", "-File run.ps1", configForwardSlash)),
            ScheduledTask(@"\Harness\EnvRef", new ScheduledTaskAction("pwsh.exe", "-Command & \"$env:workspace_root/tools/x.ps1\"", null)),
            ScheduledTask(@"\Harness\ConfigEnvRef", new ScheduledTaskAction("%CLAUDE_CONFIG_DIR%\\hooks\\job.cmd", null, null)),
            ScheduledTask(@"\Vendor\Updater", new ScheduledTaskAction(@"C:\Program Files\Vendor\update.exe", "/silent", @"C:\Program Files\Vendor")));

        Assert.Null(result.UnavailableReason);
        Assert.Equal(
            [@"\Harness\ConfigEnvRef", @"\Harness\EnvRef", @"\Harness\Path", @"\Harness\WorkingDir"],
            Table(result).Rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Windows_tasks_show_state_names_iso_times_result_meanings_and_error_flags()
    {
        var action = new ScheduledTaskAction("%WORKSPACE_ROOT%\\job.cmd", "--fast", null);
        var neverRun = new DateTimeOffset(1899, 12, 30, 0, 0, 0, TimeSpan.Zero);

        var rows = Table(await CollectTasks(
            ScheduledTask(@"\Failed", action, state: 1, result: 0x1),
            ScheduledTask(@"\Running", action, state: 4, result: 0x41301, nextRun: new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(1))),
            ScheduledTask(@"\Fresh", action, result: 0x41303, lastRun: neverRun),
            ScheduledTask(@"\Crashed", action, result: unchecked((int)0xC0000142)))).Rows.ToDictionary(r => r.Id);

        Assert.Equal("Disabled", Cell(rows[@"\Failed"], "state"));
        Assert.Equal("0x00000001: incorrect function", Cell(rows[@"\Failed"], "lastResult"));
        Assert.Equal(["error"], rows[@"\Failed"].Flags);
        Assert.Equal("2026-10-26T03:00:00+01:00", Cell(rows[@"\Failed"], "lastRun"));
        Assert.Null(Cell(rows[@"\Failed"], "nextRun"));

        Assert.Equal("Running", Cell(rows[@"\Running"], "state"));
        Assert.Equal("0x00041301: running", Cell(rows[@"\Running"], "lastResult"));
        Assert.Null(rows[@"\Running"].Flags);
        Assert.Equal("2026-10-27T09:30:00+01:00", Cell(rows[@"\Running"], "nextRun"));

        Assert.Equal("Ready", Cell(rows[@"\Fresh"], "state"));
        Assert.Null(Cell(rows[@"\Fresh"], "lastRun"));
        Assert.Equal("0x00041303: has not run yet", Cell(rows[@"\Fresh"], "lastResult"));
        Assert.Null(rows[@"\Fresh"].Flags);

        Assert.Equal("0xC0000142: DLL initialization failed", Cell(rows[@"\Crashed"], "lastResult"));
        Assert.Equal(["error"], rows[@"\Crashed"].Flags);
        Assert.Equal("\"%WORKSPACE_ROOT%\\job.cmd\" --fast", Cell(rows[@"\Crashed"], "action"));
    }

    [Fact]
    public async Task Windows_tasks_redact_secrets_in_action_arguments()
    {
        var row = Assert.Single(Table(await CollectTasks(
            ScheduledTask(@"\Harness\Call", new ScheduledTaskAction("%WORKSPACE_ROOT%\\call.cmd", "--key " + FakeAnthropicKey, null)))).Rows);

        Assert.DoesNotContain(FakeAnthropicKey, Cell(row, "action"));
        Assert.Contains(Redactor.Marker, Cell(row, "action"));
    }

    [Fact]
    public async Task Windows_tasks_unreachable_scheduler_is_unavailable_with_reason()
    {
        var result = await new WindowsTasksCollector(() => throw new InvalidOperationException("RPC server unavailable"))
            .CollectAsync(Context(WindowsEnvironment(), new FakeProcessRunner()), CancellationToken.None);

        Assert.Equal("Task Scheduler not reachable: RPC server unavailable", result.UnavailableReason);
    }

    [WindowsFact]
    public void Real_task_scheduler_walk_reads_without_throwing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tasks = ScheduledTaskReader.Read(out _);

        Assert.Contains(tasks, t => t.Actions.Count > 0);
        Assert.All(tasks, t => Assert.StartsWith(@"\", t.Path, StringComparison.Ordinal));
    }

    private static readonly string[] ShowArguments =
    [
        "--user", "show", "--no-pager",
        "--property=Id,Description,LoadState,ActiveState,SubState,Result,UnitFileState,ExecMainStatus,ExecMainExitTimestamp,LastTriggerUSec,NextElapseUSecRealtime,Triggers",
        "--",
    ];

    private MachineEnvironment LinuxEnvironment(string tool) => TestEnvironments.Create(
        tree, tools: new Dictionary<string, string> { [tool] = tool }, os: OsFamily.Linux);

    private (SystemdUserCollector Collector, string UnitDir) SystemdWithUnits()
    {
        var reader = new FakeEnvironmentReader();
        reader.Process["XDG_CONFIG_HOME"] = tree.Dir("xdg");
        tree.Write("xdg/systemd/user/backup.service", "[Service]\nExecStart=/usr/bin/true\n");
        tree.Write("xdg/systemd/user/backup.timer", "[Timer]\nOnCalendar=daily\n");
        tree.Write("xdg/systemd/user/notes.txt", "not a unit");
        tree.Write("xdg/systemd/user/default.target.wants/other.service", "[Service]\n");
        return (new SystemdUserCollector(reader), tree.Path("xdg/systemd/user"));
    }

    [Fact]
    public async Task Systemd_user_reports_defined_service_and_timer_and_flags_a_failed_unit()
    {
        var (collector, unitDir) = SystemdWithUnits();
        var show = string.Join('\n',
            "Id=backup.service", "Description=Backup to example.ts.net", "LoadState=loaded", "ActiveState=failed", "SubState=failed",
            "Result=exit-code", "UnitFileState=static", "ExecMainStatus=2", "ExecMainExitTimestamp=Mon 2026-10-26 03:00:05 UTC",
            "LastTriggerUSec=", "NextElapseUSecRealtime=", "Triggers=",
            "",
            "Id=backup.timer", "Description=Nightly backup", "LoadState=loaded", "ActiveState=active", "SubState=waiting",
            "Result=success", "UnitFileState=enabled", "ExecMainStatus=", "ExecMainExitTimestamp=",
            "LastTriggerUSec=Mon 2026-10-26 03:00:00 UTC", "NextElapseUSecRealtime=Tue 2026-10-27 03:00:00 UTC", "Triggers=backup.service",
            "");
        var processes = new FakeProcessRunner().On("systemctl", [.. ShowArguments, "backup.service", "backup.timer"], show);
        var context = Context(LinuxEnvironment("systemctl"), processes);

        var result = await collector.CollectAsync(context, CancellationToken.None);

        Assert.Null(result.UnavailableReason);
        var rows = Table(result).Rows.ToDictionary(r => r.Id);
        Assert.Equal(["backup.service", "backup.timer"], rows.Keys.ToArray());

        var service = rows["backup.service"];
        Assert.Equal("service", Cell(service, "kind"));
        Assert.Equal("failed/failed", Cell(service, "active"));
        Assert.Equal("exit-code (exit 2)", Cell(service, "result"));
        Assert.Equal("Mon 2026-10-26 03:00:05 UTC", Cell(service, "lastRun"));
        Assert.Equal(["error"], service.Flags);
        Assert.True(context.Files.TryGet(service.FileId!, out var file));
        Assert.Equal(Path.Combine(unitDir, "backup.service"), file.FullPath);

        var timer = rows["backup.timer"];
        Assert.Equal("timer", Cell(timer, "kind"));
        Assert.Equal("active/waiting", Cell(timer, "active"));
        Assert.Equal("success", Cell(timer, "result"));
        Assert.Equal("Mon 2026-10-26 03:00:00 UTC", Cell(timer, "lastRun"));
        Assert.Equal("Tue 2026-10-27 03:00:00 UTC", Cell(timer, "nextRun"));
        Assert.Equal("backup.service", Cell(timer, "triggers"));
        Assert.Equal("enabled", Cell(timer, "enabled"));
        Assert.Null(timer.Flags);
    }

    [Fact]
    public async Task Systemd_user_without_unit_files_is_active_and_empty_with_a_note()
    {
        var processes = new FakeProcessRunner();

        var result = await new SystemdUserCollector(new FakeEnvironmentReader())
            .CollectAsync(Context(LinuxEnvironment("systemctl"), processes), CancellationToken.None);

        Assert.Null(result.UnavailableReason);
        Assert.Empty(Table(result).Rows);
        var note = Assert.Single(result.Notes!);
        Assert.StartsWith("no user units in ", note);
        Assert.EndsWith("/home/user/.config/systemd/user", note);
        Assert.Empty(processes.Calls);
    }

    [Fact]
    public async Task Systemd_user_bus_failure_is_unavailable_with_first_stderr_line()
    {
        var (collector, _) = SystemdWithUnits();
        var processes = new FakeProcessRunner().On(
            "systemctl", [.. ShowArguments, "backup.service", "backup.timer"], string.Empty, 1,
            "Failed to connect to bus: No medium found\nmore detail\n");

        var result = await collector.CollectAsync(Context(LinuxEnvironment("systemctl"), processes), CancellationToken.None);

        Assert.Equal("systemd user manager not reachable: Failed to connect to bus: No medium found", result.UnavailableReason);
    }

    [Fact]
    public async Task Crontab_lists_jobs_and_env_with_redaction_blocks_and_log_last_write()
    {
        var log = tree.Write("home/user/logs/sync.log", "never read");
        File.SetLastWriteTime(log, new DateTime(2026, 10, 26, 4, 0, 0, DateTimeKind.Local));
        var crontab = string.Join('\n',
            "# plain comment",
            "MAILTO=user@host.example",
            "API_TOKEN=plainvalue",
            "",
            "# BEGIN cfg-sync",
            "*/15 * * * * /usr/bin/sync --token " + FakeGitHubToken + " >> $HOME/logs/sync.log 2>&1",
            "# END cfg-sync",
            "@reboot /usr/bin/start > /dev/null 2>&1",
            "");
        var processes = new FakeProcessRunner().On("crontab", ["-l"], crontab);

        var result = await new CrontabCollector().CollectAsync(Context(LinuxEnvironment("crontab"), processes), CancellationToken.None);

        var rows = Table(result).Rows.ToDictionary(r => r.Id);
        Assert.Equal(["line-2", "line-3", "line-6", "line-8"], rows.Keys.ToArray());

        Assert.Equal("env", Cell(rows["line-2"], "kind"));
        Assert.Equal("MAILTO=user@host.example", Cell(rows["line-2"], "command"));
        Assert.Equal("API_TOKEN=" + Redactor.Marker, Cell(rows["line-3"], "command"));

        var sync = rows["line-6"];
        Assert.Equal("job", Cell(sync, "kind"));
        Assert.Equal(6, sync.Cells["line"]!.GetValue<int>());
        Assert.Equal("*/15 * * * *", Cell(sync, "schedule"));
        Assert.DoesNotContain(FakeGitHubToken, Cell(sync, "command"));
        Assert.Contains(Redactor.Marker, Cell(sync, "command"));
        Assert.Equal("cfg-sync", Cell(sync, "block"));
        Assert.Equal(log.Replace('\\', '/'), Cell(sync, "logFile"));
        Assert.StartsWith("2026-10-26T04:00:00", Cell(sync, "logLastWrite"));

        var reboot = rows["line-8"];
        Assert.Equal("@reboot", Cell(reboot, "schedule"));
        Assert.Null(Cell(reboot, "block"));
        Assert.Null(Cell(reboot, "logFile"));
        Assert.Null(Cell(reboot, "logLastWrite"));
    }

    [Fact]
    public async Task Crontab_missing_for_user_is_active_and_empty_with_a_note()
    {
        var processes = new FakeProcessRunner().On("crontab", ["-l"], string.Empty, 1, "no crontab for user\n");

        var result = await new CrontabCollector().CollectAsync(Context(LinuxEnvironment("crontab"), processes), CancellationToken.None);

        Assert.Null(result.UnavailableReason);
        Assert.Empty(Table(result).Rows);
        Assert.Equal(["no crontab for this user"], result.Notes);
    }

    [Fact]
    public async Task Crontab_other_failure_is_unavailable_with_first_stderr_line()
    {
        var processes = new FakeProcessRunner().On("crontab", ["-l"], string.Empty, 1, "crontab: permission denied\n");

        var result = await new CrontabCollector().CollectAsync(Context(LinuxEnvironment("crontab"), processes), CancellationToken.None);

        Assert.Equal("crontab -l failed: crontab: permission denied", result.UnavailableReason);
    }
}
