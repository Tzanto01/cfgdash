using Cfgdash.Core;

namespace Cfgdash.Host.Tests;

public sealed class SystemdAutostartTests : IDisposable
{
    private const string Systemctl = "systemctl-fake";
    private const string Exe = "/opt/cfg dash/a\"b\\c%d$e/cfgdash";

    private readonly TempDir temp = new();

    private string UnitPath => temp.Combine(Path.Combine("systemd", "user", "cfgdash.service"));

    public void Dispose() => temp.Dispose();

    private static FakeProcesses Systemd(string running = "running", string enabled = "enabled", Func<RecordedCall, ProcessResult?>? custom = null) =>
        new(call => custom?.Invoke(call) ?? call.Args switch
        {
            ["--user", "is-system-running"] => new ProcessResult(running == "running" ? 0 : 1, running, string.Empty, false),
            ["--user", "is-enabled", _] => FakeProcesses.Ok(enabled),
            ["--user", "enable", "--now", _] or ["--user", "disable", "--now", "--no-block", _] => FakeProcesses.Ok(),
            _ => throw new InvalidOperationException(call.Line),
        });

    private static string[][] ChangingCalls(FakeProcesses processes) =>
        [.. processes.Calls.Where(c => c.Args is ["--user", "enable" or "disable", ..]).Select(c => c.Args)];

    [Fact]
    public async Task On_writes_the_exact_unit_and_enables_it_with_one_systemctl_call()
    {
        var processes = Systemd();
        var autostart = new SystemdAutostart(processes, Systemctl, UnitPath, Exe);

        var failure = await autostart.SetAsync(true, default);

        Assert.Null(failure);
        var lines = File.ReadAllLines(UnitPath);
        Assert.Contains("ExecStart=\"/opt/cfg dash/a\\\"b\\\\c%%d$$e/cfgdash\" run --autostart", lines);
        Assert.Contains("Restart=on-failure", lines);
        Assert.Contains("WantedBy=default.target", lines);
        var change = Assert.Single(ChangingCalls(processes));
        Assert.Equal(["--user", "enable", "--now", "cfgdash.service"], change);
        Assert.All(processes.Calls, c => Assert.Equal(Systemctl, c.Executable));
    }

    [Fact]
    public async Task On_keeps_the_callers_path_with_spaces_and_escaped_percent_signs()
    {
        var autostart = new SystemdAutostart(Systemd(), Systemctl, UnitPath, Exe, "/home/user/my bin:/opt/100%/bin:/usr/bin");

        Assert.Null(await autostart.SetAsync(true, default));

        Assert.Contains("Environment=\"PATH=/home/user/my bin:/opt/100%%/bin:/usr/bin\"", File.ReadAllLines(UnitPath));
        Assert.Equal("on", (await autostart.GetAsync(default)).Actual);
        Assert.Null((await autostart.GetAsync(default)).Detail);
    }

    [Fact]
    public void A_path_with_a_control_character_is_refused()
    {
        Assert.Null(SystemdAutostart.Unit("/opt/cfgdash", "/usr/bin\nExecStartPre=/bin/true"));
    }

    [Fact]
    public async Task Written_unit_reads_back_as_on_without_detail()
    {
        var autostart = new SystemdAutostart(Systemd(), Systemctl, UnitPath, Exe);
        await autostart.SetAsync(true, default);

        var state = await autostart.GetAsync(default);

        Assert.Equal(new AutostartState("on"), state);
    }

    [Fact]
    public async Task Failed_enable_removes_the_unit_it_just_wrote_and_returns_the_reason()
    {
        var processes = Systemd(custom: c => c.Args is ["--user", "enable", ..] ? new ProcessResult(1, string.Empty, "Failed to connect to bus\nmore", false) : null);
        var autostart = new SystemdAutostart(processes, Systemctl, UnitPath, Exe);

        var failure = await autostart.SetAsync(true, default);

        Assert.Contains("Failed to connect to bus", failure);
        Assert.False(File.Exists(UnitPath));
    }

    [Fact]
    public async Task Failed_enable_keeps_a_unit_that_existed_before()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        File.WriteAllText(UnitPath, "[Unit]\n");
        var processes = Systemd(custom: c => c.Args is ["--user", "enable", ..] ? FakeProcesses.Fail("denied") : null);

        var failure = await new SystemdAutostart(processes, Systemctl, UnitPath, Exe).SetAsync(true, default);

        Assert.NotNull(failure);
        Assert.True(File.Exists(UnitPath));
    }

    [Fact]
    public async Task Off_disables_without_blocking_and_deletes_the_unit()
    {
        var processes = Systemd();
        var autostart = new SystemdAutostart(processes, Systemctl, UnitPath, Exe);
        await autostart.SetAsync(true, default);

        var failure = await autostart.SetAsync(false, default);

        Assert.Null(failure);
        Assert.False(File.Exists(UnitPath));
        Assert.Equal(["--user", "disable", "--now", "--no-block", "cfgdash.service"], ChangingCalls(processes)[^1]);
    }

    [Fact]
    public async Task Off_keeps_the_unit_when_disable_fails()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        File.WriteAllText(UnitPath, "[Unit]\n");
        var processes = Systemd(custom: c => c.Args is ["--user", "disable", ..] ? FakeProcesses.Fail("denied") : null);

        var failure = await new SystemdAutostart(processes, Systemctl, UnitPath, Exe).SetAsync(false, default);

        Assert.Contains("denied", failure);
        Assert.True(File.Exists(UnitPath));
    }

    [Fact]
    public async Task Off_without_a_unit_makes_no_systemctl_call()
    {
        var processes = Systemd();

        var failure = await new SystemdAutostart(processes, Systemctl, UnitPath, Exe).SetAsync(false, default);

        Assert.Null(failure);
        Assert.Empty(processes.Calls);
    }

    [Theory]
    [InlineData("no-tool")]
    [InlineData("offline")]
    public async Task Without_a_user_systemd_get_reports_unsupported_and_on_writes_nothing(string cause)
    {
        var processes = Systemd(running: "offline");
        var autostart = new SystemdAutostart(processes, cause == "no-tool" ? null : Systemctl, UnitPath, Exe);

        var state = await autostart.GetAsync(default);
        var failure = await autostart.SetAsync(true, default);

        Assert.Equal("unsupported", state.Actual);
        Assert.False(string.IsNullOrEmpty(state.Detail));
        Assert.False(string.IsNullOrEmpty(failure));
        Assert.False(File.Exists(UnitPath));
        Assert.Empty(ChangingCalls(processes));
    }

    [Fact]
    public async Task A_degraded_user_systemd_is_supported()
    {
        var processes = Systemd(custom: c => c.Args is ["--user", "is-system-running"] ? new ProcessResult(1, "degraded\n", string.Empty, false) : null);

        var failure = await new SystemdAutostart(processes, Systemctl, UnitPath, Exe).SetAsync(true, default);

        Assert.Null(failure);
        Assert.True(File.Exists(UnitPath));
    }

    [Fact]
    public async Task An_executable_path_with_a_newline_is_refused_before_anything_is_written()
    {
        var processes = Systemd();

        var failure = await new SystemdAutostart(processes, Systemctl, UnitPath, "/opt/cfgdash\nExecStop=/bin/true").SetAsync(true, default);

        Assert.False(string.IsNullOrEmpty(failure));
        Assert.False(File.Exists(UnitPath));
        Assert.Empty(ChangingCalls(processes));
    }

    [Fact]
    public async Task Get_is_off_without_a_unit_file()
    {
        var state = await new SystemdAutostart(Systemd(), Systemctl, UnitPath, Exe).GetAsync(default);

        Assert.Equal(new AutostartState("off"), state);
    }

    [Fact]
    public async Task Get_reports_a_detail_when_exec_start_points_at_another_executable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
        File.WriteAllText(UnitPath, "[Service]\nExecStart=/tmp/other run\n");

        var state = await new SystemdAutostart(Systemd(), Systemctl, UnitPath, Exe).GetAsync(default);

        Assert.Equal("on", state.Actual);
        Assert.False(string.IsNullOrEmpty(state.Detail));
    }

    [Fact]
    public async Task Status_is_on_when_only_the_path_line_differs_from_the_callers_path()
    {
        Assert.Null(await new SystemdAutostart(Systemd(), Systemctl, UnitPath, Exe, "/home/user/.local/bin:/usr/bin").SetAsync(true, default));

        var state = await new SystemdAutostart(Systemd(), Systemctl, UnitPath, Exe, "/usr/bin").GetAsync(default);

        Assert.Equal(new AutostartState("on"), state);
    }

    [Fact]
    public async Task Get_reports_a_detail_when_the_unit_is_not_enabled()
    {
        var autostart = new SystemdAutostart(Systemd(enabled: "disabled"), Systemctl, UnitPath, Exe);
        await autostart.SetAsync(true, default);

        var state = await autostart.GetAsync(default);

        Assert.Equal("on", state.Actual);
        Assert.False(string.IsNullOrEmpty(state.Detail));
    }
}

public sealed class RunKeyAutostartTests
{
    private const string Exe = "C:/Program Files/cfgdash/cfgdash.exe";

    [Fact]
    public async Task On_sets_the_quoted_command_and_reads_back_as_on()
    {
        var key = new FakeRunKey();
        var autostart = new RunKeyAutostart(key, Exe);

        var failure = await autostart.SetAsync(true, default);

        Assert.Null(failure);
        Assert.Equal($"\"{Exe}\" run --autostart", key.Value);
        Assert.Equal(new AutostartState("on"), await autostart.GetAsync(default));
    }

    [Fact]
    public async Task Off_deletes_the_value()
    {
        var key = new FakeRunKey { Value = "anything" };
        var autostart = new RunKeyAutostart(key, Exe);

        var failure = await autostart.SetAsync(false, default);

        Assert.Null(failure);
        Assert.Null(key.Value);
        Assert.Equal(new AutostartState("off"), await autostart.GetAsync(default));
    }

    [Fact]
    public async Task A_different_value_reads_as_on_with_a_detail()
    {
        var key = new FakeRunKey { Value = "\"C:/elsewhere/cfgdash.exe\" run" };

        var state = await new RunKeyAutostart(key, Exe).GetAsync(default);

        Assert.Equal("on", state.Actual);
        Assert.False(string.IsNullOrEmpty(state.Detail));
    }

    [Fact]
    public async Task A_refused_write_returns_a_reason_instead_of_throwing()
    {
        var key = new FakeRunKey { Fails = true };

        var failure = await new RunKeyAutostart(key, Exe).SetAsync(true, default);

        Assert.False(string.IsNullOrEmpty(failure));
        Assert.Null(key.Value);
    }
}

public sealed class AutostartFactoryTests
{
    [Theory]
    [InlineData("/usr/share/dotnet/dotnet")]
    [InlineData("C:/Program Files/dotnet/dotnet.exe")]
    [InlineData(null)]
    public async Task A_dotnet_host_or_missing_exe_path_is_unsupported(string? exe)
    {
        var autostart = AutostartFactory.Create(TestCore.Create(), exe);

        var state = await autostart.GetAsync(default);

        Assert.Equal("unsupported", state.Actual);
        Assert.False(string.IsNullOrEmpty(state.Detail));
        Assert.False(string.IsNullOrEmpty(await autostart.SetAsync(true, default)));
    }

    [LinuxFact]
    public async Task Linux_unit_lives_under_xdg_config_home_when_it_is_absolute()
    {
        using var temp = new TempDir();
        var machine = TestCore.Machine(home: temp.Sub("home"), tools: new Dictionary<string, string> { ["systemctl"] = "systemctl-fake" });
        var processes = new FakeProcesses(c => c.Args is ["--user", "is-system-running"] ? FakeProcesses.Ok("running") : FakeProcesses.Ok());
        var core = TestCore.Create(machine, processes, reader: new FakeEnv(new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = temp.Sub("xdg") }));

        var failure = await AutostartFactory.Create(core, "/opt/cfgdash/cfgdash").SetAsync(true, default);

        Assert.Null(failure);
        Assert.True(File.Exists(Path.Combine(temp.Combine("xdg"), "systemd", "user", "cfgdash.service")));
        Assert.False(Directory.Exists(Path.Combine(temp.Combine("home"), ".config")));
    }
}
