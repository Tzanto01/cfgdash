using System.Text.Json;
using Cfgdash.Core;

namespace Cfgdash.Host.Tests;

public sealed class CliParseTests
{
    [Fact]
    public void Defaults_are_run_on_port_7820_in_the_current_directory()
    {
        var options = CfgdashCli.Parse([], out var error);

        Assert.Null(error);
        Assert.Equal("run", options!.Command);
        Assert.Equal(7820, options.Port);
        Assert.Equal(CfgdashServer.NormalizeDir(Directory.GetCurrentDirectory()), options.Dir);
        Assert.False(options.Tailnet);
        Assert.False(options.NoOpen);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1024", 1024)]
    [InlineData("65535", 65535)]
    public void Ports_are_accepted_as_separate_value_or_with_equals(string value, int expected)
    {
        Assert.Equal(expected, CfgdashCli.Parse(["run", "--port", value], out _)!.Port);
        Assert.Equal(expected, CfgdashCli.Parse([$"--port={value}"], out _)!.Port);
    }

    [Theory]
    [InlineData("1023")]
    [InlineData("80")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public void Invalid_ports_are_rejected(string value)
    {
        Assert.Null(CfgdashCli.Parse(["--port", value], out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--bogus")]
    [InlineData("--no-open=yes")]
    public void Missing_values_and_unknown_options_are_rejected(string argument)
    {
        Assert.Null(CfgdashCli.Parse([argument], out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("stop", "--tailnet")]
    [InlineData("update", "--port", "7821")]
    [InlineData("export", "--no-open")]
    public void Unknown_commands_and_options_foreign_to_the_command_are_rejected(params string[] args)
    {
        Assert.Null(CfgdashCli.Parse(args, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Run_flags_and_dir_are_parsed()
    {
        using var temp = new TempDir();

        var options = CfgdashCli.Parse(["run", "--tailnet", "--no-open", "--dir", temp.Root], out _)!;

        Assert.True(options.Tailnet);
        Assert.True(options.NoOpen);
        Assert.Equal(CfgdashServer.NormalizeDir(temp.Root), options.Dir);
        Assert.True(CfgdashCli.Parse(["update", "--check"], out _)!.Check);
    }

    [Fact]
    public async Task Nonexistent_dir_exits_1_with_a_message_and_starts_nothing()
    {
        using var env = new CliEnv();

        var exit = await env.RunAsync("run", "--port", "0", "--dir", Path.Combine(env.Root, "missing"));

        Assert.Equal(1, exit);
        Assert.NotEmpty(env.Error.Text);
        Assert.Null(env.Store.Read());
    }
}

public sealed class CliRunTests
{
    [Fact]
    public async Task Run_records_state_prints_the_token_url_and_cleans_up_after_a_requested_shutdown()
    {
        using var env = new CliEnv();

        var (exit, state) = await env.StartRunAsync("run", "--port", "0", "--no-open", "--dir", env.Dir);
        var url = $"http://127.0.0.1:{state.Port}/?token={state.Token}";
        var printed = env.Out.Text;
        await env.ShutdownAsync(state);
        var code = await exit;

        Assert.Equal(0, code);
        Assert.True(state.Port > 0);
        Assert.NotEmpty(state.Token);
        Assert.Equal(env.Dir, state.StartDir);
        Assert.Null(state.Tailnet);
        Assert.Contains(url, printed);
        Assert.False(File.Exists(env.Store.FilePath));
        Assert.Empty(env.Opened);
    }

    [Fact]
    public async Task Run_on_a_headless_session_prints_the_hint_and_never_opens_a_browser()
    {
        using var env = new CliEnv(variables: new Dictionary<string, string> { ["SSH_CONNECTION"] = "192.0.2.1 50000 192.0.2.2 22" });

        var (exit, state) = await env.StartRunAsync("run", "--port", "0", "--dir", env.Dir);
        await env.ShutdownAsync(state);
        await exit;

        Assert.Contains("No desktop session", env.Out.Text);
        Assert.Empty(env.Opened);
    }

    [Fact]
    public async Task Second_run_while_the_recorded_process_is_live_reports_the_running_instance_and_starts_nothing()
    {
        using var env = new CliEnv();
        env.Store.Write(env.LiveRecord(12345, "recorded-token", new RunTailnet(443, 3000, "pc.example.ts.net")));

        var exit = await env.RunAsync("run", "--port", "0", "--no-open");

        Assert.Equal(0, exit);
        Assert.Contains("already running", env.Out.Text);
        Assert.Contains("http://127.0.0.1:12345/?token=recorded-token", env.Out.Text);
        Assert.Contains("https://pc.example.ts.net/?token=recorded-token", env.Out.Text);
        Assert.Equal(12345, env.Store.Read()!.Port);
    }

    [Fact]
    public async Task Stop_without_run_state_reports_not_running()
    {
        using var env = new CliEnv();

        var exit = await env.RunAsync("stop");

        Assert.Equal(0, exit);
        Assert.Contains("not running", env.Out.Text);
    }

    [Fact]
    public async Task Stale_state_is_removed_by_stop()
    {
        using var env = new CliEnv();
        env.Store.Write(CliEnv.DeadRecord());

        var exit = await env.RunAsync("stop");

        Assert.Equal(0, exit);
        Assert.False(File.Exists(env.Store.FilePath));
    }

    [Theory]
    [InlineData("host.example", "host.example")]
    [InlineData("a/b\\c d", "a_b_c_d")]
    public async Task Export_writes_the_snapshot_to_a_timestamped_file_in_the_cache_dir(string hostname, string expectedName)
    {
        using var env = new CliEnv(TestCore.Machine(hostname: hostname));

        var exit = await env.RunAsync("export", "--dir", env.Dir);

        var path = env.Out.Text.Trim();
        Assert.Equal(0, exit);
        Assert.Equal(Path.Combine(env.Cache, "exports", $"{expectedName}-20260102T030405Z.json"), path);
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(env.Dir, json.RootElement.GetProperty("context").GetProperty("targetDir").GetString());
    }

    [Fact]
    public async Task Second_export_in_the_same_second_exits_1_and_leaves_the_first_file_untouched()
    {
        using var env = new CliEnv();
        var first = await env.RunAsync("export", "--dir", env.Dir);
        var path = env.Out.Text.Trim();
        var content = File.ReadAllText(path);

        var second = await env.RunAsync("export", "--dir", env.Dir);

        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.StartsWith("cfgdash:", env.Error.Text);
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFiles(Path.Combine(env.Cache, "exports")));
    }

    [Theory]
    [InlineData("export")]
    [InlineData("run")]
    public async Task A_cache_dir_that_cannot_be_created_exits_1_with_a_message_instead_of_an_exception(string command)
    {
        using var env = new CliEnv();
        File.WriteAllText(env.Cache, "a file where the cache dir should be");

        var exit = await env.RunAsync(command, "--dir", env.Dir);

        Assert.Equal(1, exit);
        Assert.StartsWith("cfgdash:", env.Error.Text);
    }

    [Fact]
    public void A_recorded_process_start_within_5_seconds_is_live_and_beyond_it_is_not()
    {
        using var env = new CliEnv();
        var record = env.LiveRecord(12345, "recorded-token");

        Assert.True(RunStateStore.IsLive(record with { ProcessStart = record.ProcessStart.AddSeconds(3) }));
        Assert.True(RunStateStore.IsLive(record with { ProcessStart = record.ProcessStart.AddSeconds(-3) }));
        Assert.False(RunStateStore.IsLive(record with { ProcessStart = record.ProcessStart.AddSeconds(10) }));
        Assert.False(RunStateStore.IsLive(record with { ProcessStart = record.ProcessStart.AddSeconds(-10) }));
    }

    [LinuxFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Run_state_is_user_only_on_linux()
    {
        using var env = new CliEnv();
        var (exit, state) = await env.StartRunAsync("run", "--port", "0", "--no-open", "--dir", env.Dir);
        var fileMode = File.GetUnixFileMode(env.Store.FilePath);
        var dirMode = File.GetUnixFileMode(env.Cache);
        await env.ShutdownAsync(state);
        await exit;

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, fileMode);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, dirMode);
    }

    [LinuxFact]
    public async Task Run_with_a_display_opens_the_token_url_unless_no_open_is_given()
    {
        using var env = new CliEnv(variables: new Dictionary<string, string> { ["DISPLAY"] = ":0" });

        var (exit, state) = await env.StartRunAsync("run", "--port", "0", "--dir", env.Dir);
        await Poll.UntilAsync(() => env.Opened.Count > 0);
        var opened = env.Opened.ToList();
        await env.ShutdownAsync(state);
        await exit;

        Assert.Equal([$"http://127.0.0.1:{state.Port}/?token={state.Token}"], opened);
    }

    [LinuxFact]
    public void Desktop_needs_a_display_and_no_ssh_session_on_linux()
    {
        Assert.True(CfgdashCli.HasDesktop(new FakeEnv(new Dictionary<string, string> { ["DISPLAY"] = ":0" })));
        Assert.True(CfgdashCli.HasDesktop(new FakeEnv(new Dictionary<string, string> { ["WAYLAND_DISPLAY"] = "wayland-0" })));
        Assert.False(CfgdashCli.HasDesktop(new FakeEnv()));
        Assert.False(CfgdashCli.HasDesktop(new FakeEnv(new Dictionary<string, string> { ["DISPLAY"] = ":0", ["SSH_CONNECTION"] = "192.0.2.1 1 192.0.2.2 22" })));
    }

    [Fact]
    public void Ssh_sessions_have_no_desktop()
    {
        Assert.False(CfgdashCli.HasDesktop(new FakeEnv(new Dictionary<string, string> { ["SSH_TTY"] = "/dev/pts/0" })));
    }
}
