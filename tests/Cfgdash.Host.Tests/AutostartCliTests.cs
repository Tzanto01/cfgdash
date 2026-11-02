using System.Text.Json;

namespace Cfgdash.Host.Tests;

public sealed class AutostartCliTests
{
    private static string ConfigFile(CliEnv env) => Path.Combine(env.ConfigDir, "config.json");

    private static void WriteConfig(CliEnv env, string json)
    {
        Directory.CreateDirectory(env.ConfigDir);
        File.WriteAllText(ConfigFile(env), json);
    }

    private static IEnumerable<JsonElement> AuditLines(CliEnv env)
    {
        var path = Path.Combine(env.Cache, "edits", "audit.jsonl");
        return File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement) : [];
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public async Task Autostart_changes_the_os_entry_and_records_the_choice_in_config_and_audit(string action, bool expected)
    {
        var os = new FakeAutostart(actual: expected ? "off" : "on");
        using var env = new CliEnv(autostart: os.Factory);
        if (!expected)
        {
            WriteConfig(env, "{\"startAtLogin\": true}");
        }

        var exit = await env.RunAsync("autostart", action);

        Assert.Equal(0, exit);
        Assert.Equal([expected], os.Sets);
        Assert.Equal(expected, JsonDocument.Parse(File.ReadAllText(ConfigFile(env))).RootElement.GetProperty("startAtLogin").GetBoolean());
        var audit = Assert.Single(AuditLines(env));
        Assert.Equal("cfgdash-settings", audit.GetProperty("kind").GetString());
        Assert.Equal("cli", audit.GetProperty("listener").GetString());
    }

    [Fact]
    public async Task Autostart_on_with_a_failing_os_entry_exits_1_with_the_reason_and_writes_no_config()
    {
        var os = new FakeAutostart(failure: "systemctl --user enable failed: denied");
        using var env = new CliEnv(autostart: os.Factory);

        var exit = await env.RunAsync("autostart", "on");

        Assert.Equal(1, exit);
        Assert.Contains("denied", env.Error.Text);
        Assert.False(File.Exists(ConfigFile(env)));
    }

    [Theory]
    [InlineData("on")]
    [InlineData("off")]
    [InlineData("status")]
    public async Task Autostart_without_support_exits_2_with_the_reason_and_changes_nothing(string action)
    {
        var os = new FakeAutostart("unsupported", "no user systemd (offline)");
        using var env = new CliEnv(autostart: os.Factory);

        var exit = await env.RunAsync("autostart", action);

        Assert.Equal(2, exit);
        Assert.Contains("no user systemd (offline)", env.Error.Text);
        Assert.Empty(os.Sets);
        Assert.False(File.Exists(ConfigFile(env)));
    }

    [Theory]
    [InlineData("on", null, true, "on")]
    [InlineData("off", null, false, "off")]
    [InlineData("on", null, false, "mismatch")]
    [InlineData("off", null, true, "mismatch")]
    [InlineData("on", "the unit file differs from the expected one", true, "mismatch")]
    public async Task Status_prints_on_off_or_mismatch(string actual, string? detail, bool configured, string expected)
    {
        using var env = new CliEnv(autostart: new FakeAutostart(actual, detail).Factory);
        WriteConfig(env, $"{{\"startAtLogin\": {configured.ToString().ToLowerInvariant()}}}");

        var exit = await env.RunAsync("autostart", "status");

        Assert.Equal(0, exit);
        Assert.Equal(expected, env.Out.Text.Trim());
    }

    [Theory]
    [InlineData("autostart")]
    [InlineData("autostart", "toggle")]
    [InlineData("autostart", "on", "now")]
    public void Autostart_needs_exactly_one_known_action(params string[] args)
    {
        Assert.Null(CfgdashCli.Parse(args, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Autostart_parses_its_action_and_run_autostart_implies_no_open()
    {
        var status = CfgdashCli.Parse(["autostart", "status"], out _)!;
        var run = CfgdashCli.Parse(["run", "--autostart"], out _)!;

        Assert.Equal(("autostart", "status"), (status.Command, status.Action));
        Assert.True(run.Autostart);
        Assert.True(run.NoOpen);
        Assert.False(run.DirGiven);
    }

    [LinuxFact]
    public async Task A_second_run_opens_the_browser_on_the_live_url_in_a_desktop_session()
    {
        using var env = new CliEnv(variables: new Dictionary<string, string> { ["DISPLAY"] = ":0" });
        env.Store.Write(env.LiveRecord(12345, "recorded-token"));

        var exit = await env.RunAsync("run", "--port", "0");

        Assert.Equal(0, exit);
        Assert.Equal(["http://127.0.0.1:12345/?token=recorded-token"], env.Opened);
    }

    [Fact]
    public async Task A_second_run_with_no_open_only_prints_the_live_url()
    {
        using var env = new CliEnv(variables: new Dictionary<string, string> { ["DISPLAY"] = ":0" });
        env.Store.Write(env.LiveRecord(12345, "recorded-token"));

        var exit = await env.RunAsync("run", "--port", "0", "--no-open");

        Assert.Equal(0, exit);
        Assert.Empty(env.Opened);
        Assert.Contains("http://127.0.0.1:12345/?token=recorded-token", env.Out.Text);
    }

    [Fact]
    public async Task Run_uses_the_configured_default_dir_when_no_dir_is_given()
    {
        using var env = new CliEnv();
        var other = Directory.CreateDirectory(Path.Combine(env.Root, "configured")).FullName;
        WriteConfig(env, $"{{\"defaultDir\": {UiRequests.Quote(other)}}}");

        var (exit, state) = await env.StartRunAsync("run", "--port", "0", "--no-open");
        await env.ShutdownAsync(state);
        await exit;

        Assert.Equal(CfgdashServer.NormalizeDir(other), state.StartDir);
    }

    [Fact]
    public async Task An_explicit_dir_beats_the_configured_default_dir()
    {
        using var env = new CliEnv();
        var other = Directory.CreateDirectory(Path.Combine(env.Root, "configured")).FullName;
        WriteConfig(env, $"{{\"defaultDir\": {UiRequests.Quote(other)}}}");

        var (exit, state) = await env.StartRunAsync("run", "--port", "0", "--no-open", "--dir", env.Dir);
        await env.ShutdownAsync(state);
        await exit;

        Assert.Equal(env.Dir, state.StartDir);
    }

    [Fact]
    public async Task Run_autostart_without_a_default_dir_starts_in_the_home_dir_and_never_opens_a_browser()
    {
        using var home = new TempDir();
        using var env = new CliEnv(TestCore.Machine(home: home.Root), variables: new Dictionary<string, string> { ["DISPLAY"] = ":0" });

        var (exit, state) = await env.StartRunAsync("run", "--autostart", "--port", "0");
        await env.ShutdownAsync(state);
        await exit;

        Assert.Equal(CfgdashServer.NormalizeDir(home.Root), state.StartDir);
        Assert.Empty(env.Opened);
    }

    [Fact]
    public async Task Configured_tailnet_enforces_the_tailnet_preconditions_without_the_flag()
    {
        using var env = new CliEnv(TailnetEnv.Machine("notool"));
        WriteConfig(env, "{\"tailnet\": true}");

        var exit = await env.RunAsync("run", "--port", "0", "--no-open", "--dir", env.Dir);

        Assert.Equal(2, exit);
        Assert.Contains("tailscale is not installed", env.Error.Text);
        Assert.Null(env.Store.Read());
    }
}
