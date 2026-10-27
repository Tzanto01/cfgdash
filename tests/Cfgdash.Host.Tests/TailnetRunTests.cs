using Cfgdash.Core;

namespace Cfgdash.Host.Tests;

public sealed class TailnetRunTests
{
    [Theory]
    [InlineData("notool")]
    [InlineData("notailnet")]
    [InlineData("state")]
    [InlineData("magicdns")]
    [InlineData("certs")]
    [InlineData("noidentity")]
    public async Task Run_tailnet_with_an_unmet_precondition_exits_2_and_publishes_nothing(string flaw)
    {
        var tailscale = new FakeTailscale();
        using var env = new CliEnv(TailnetEnv.Machine(flaw), tailscale);

        var exit = await env.RunAsync("run", "--tailnet", "--port", "0", "--no-open", "--dir", env.Dir);

        Assert.Equal(2, exit);
        Assert.NotEmpty(env.Error.Text);
        Assert.DoesNotContain(tailscale.Calls, c => c.Args.Contains("--bg"));
        Assert.Null(env.Store.Read());
        tailscale.AssertNeverFunnelOrReset();
    }

    [Theory]
    [InlineData("tcp", FakeTailscale.Dns)]
    [InlineData("web-of-another-name", "other.example.ts.net")]
    public async Task Run_tailnet_refuses_an_https_port_that_already_has_a_handler(string kind, string dns)
    {
        var tailscale = new FakeTailscale();
        using var env = new CliEnv(TailnetEnv.Machine(), tailscale);
        var port = CliEnv.FreePort();
        if (kind == "tcp")
        {
            tailscale.Tcp.Add(port.ToString());
        }
        else
        {
            tailscale.Web[$"{dns}:{port}"] = "http://127.0.0.1:1";
        }

        var exit = await env.RunAsync("run", "--tailnet", "--port", port.ToString(), "--no-open", "--dir", env.Dir);

        Assert.Equal(2, exit);
        Assert.DoesNotContain(tailscale.Calls, c => c.Args.Contains("--bg"));
        Assert.DoesNotContain(tailscale.Calls, c => c.Args.Contains("off"));
        Assert.Null(env.Store.Read());
        Assert.True(CliEnv.PortIsFree(port));
    }

    [Fact]
    public async Task Run_tailnet_publishes_the_backend_and_removes_exactly_its_own_handler_on_shutdown()
    {
        var tailscale = new FakeTailscale();
        using var env = new CliEnv(TailnetEnv.Machine(), tailscale);
        var port = CliEnv.FreePort();

        var (exit, state) = await env.StartRunAsync("run", "--tailnet", "--port", port.ToString(), "--no-open", "--dir", env.Dir);
        await Poll.UntilAsync(() => env.Out.Text.Contains("Tagged devices"));
        var backend = state.Tailnet!.BackendPort;
        var printed = env.Out.Text;
        var servedWith = tailscale.Calls.Where(c => c.Args.Contains("--bg")).Select(c => c.Args).ToList();
        var published = tailscale.Web.ToDictionary(w => w.Key, w => w.Value);
        await env.ShutdownAsync(state);
        var code = await exit;

        Assert.Equal(0, code);
        Assert.Equal(new RunTailnet(port, backend, FakeTailscale.Dns), state.Tailnet);
        Assert.NotEqual(port, backend);
        Assert.Equal([["serve", "--bg", $"--https={port}", $"http://127.0.0.1:{backend}"]], servedWith);
        Assert.Equal($"http://127.0.0.1:{backend}", published[$"{FakeTailscale.Dns}:{port}"]);
        Assert.Contains($"https://{FakeTailscale.Dns}:{port}/?token={state.Token}", printed);
        Assert.Contains("Allowed login: user@github", printed);
        Assert.Contains("Tagged devices of example.github allowed", printed);
        var last = tailscale.Calls.TakeLast(2).Select(c => c.Args).ToList();
        Assert.Equal([["serve", $"--https={port}", "off"], ["serve", "status", "--json"]], last);
        Assert.Empty(tailscale.Web);
        Assert.False(File.Exists(env.Store.FilePath));
        tailscale.AssertNeverFunnelOrReset(port);
    }

    [Fact]
    public async Task Failed_serve_off_with_a_broken_error_stream_still_removes_run_state_and_logs_the_stop()
    {
        var tailscale = new FakeTailscale();
        using var env = new CliEnv(TailnetEnv.Machine(), tailscale);
        var port = CliEnv.FreePort();
        var exit = CfgdashCli.RunAsync(
            ["run", "--tailnet", "--port", port.ToString(), "--no-open", "--dir", env.Dir],
            TestCore.Deps(env.Core, env.Cache, env.Out, new ThrowingWriter(), env.Time, env.Opened.Add, env.Exe));
        await Poll.UntilAsync(() => exit.IsCompleted || (env.Store.Read() is not null && env.Out.Text.Contains("Tagged devices")));
        var state = env.Store.Read()!;
        tailscale.OffFails = true;

        await env.ShutdownAsync(state);
        var code = await exit;

        var log = string.Concat(Directory.GetFiles(Path.Combine(env.Cache, "logs"), "*.log").Select(File.ReadAllText));
        Assert.Equal(0, code);
        Assert.True(tailscale.Called("serve", $"--https={port}", "off"));
        Assert.False(File.Exists(env.Store.FilePath));
        Assert.Contains("stop requested", log);
    }

    [Fact]
    public async Task Run_tailnet_prints_the_override_login_and_the_allowed_tags()
    {
        var tailscale = new FakeTailscale();
        using var env = new CliEnv(TailnetEnv.Machine(logins: ["other@example.com"], tags: ["tag:pc"]), tailscale);

        var (exit, state) = await env.StartRunAsync("run", "--tailnet", "--port", "0", "--no-open", "--dir", env.Dir);
        await Poll.UntilAsync(() => env.Out.Text.Contains("Tagged devices"));
        var printed = env.Out.Text;
        await env.ShutdownAsync(state);
        await exit;

        Assert.Contains("Allowed login: other@example.com", printed);
        Assert.DoesNotContain("user@github", printed);
        Assert.Contains("tag:pc", printed);
    }

    [Fact]
    public async Task Serve_failure_exits_2_removes_run_state_frees_the_port_and_removes_nothing()
    {
        var tailscale = new FakeTailscale { ServeFails = true };
        using var env = new CliEnv(TailnetEnv.Machine(), tailscale);
        var port = CliEnv.FreePort();

        var exit = await env.RunAsync("run", "--tailnet", "--port", port.ToString(), "--no-open", "--dir", env.Dir);

        Assert.Equal(2, exit);
        Assert.NotEmpty(env.Error.Text);
        Assert.False(File.Exists(env.Store.FilePath));
        Assert.True(CliEnv.PortIsFree(port));
        Assert.DoesNotContain(tailscale.Calls, c => c.Args.Contains("off"));
        tailscale.AssertNeverFunnelOrReset();
    }

    [Theory]
    [InlineData("ours", true)]
    [InlineData("other-proxy", false)]
    [InlineData("other-name", false)]
    [InlineData("other-port", false)]
    [InlineData("empty", false)]
    [InlineData("tcp-only", false)]
    public async Task Stale_tailnet_state_turns_serve_off_only_when_status_proves_the_handler_is_ours(string scenario, bool expectOff)
    {
        var tailscale = new FakeTailscale();
        switch (scenario)
        {
            case "ours":
                tailscale.Claim(443, FakeTailscale.Dns, "http://127.0.0.1:3000");
                break;
            case "other-proxy":
                tailscale.Claim(443, FakeTailscale.Dns, "http://127.0.0.1:9999");
                break;
            case "other-name":
                tailscale.Claim(443, "other.example.ts.net", "http://127.0.0.1:3000");
                break;
            case "other-port":
                tailscale.Claim(8443, FakeTailscale.Dns, "http://127.0.0.1:3000");
                break;
            case "tcp-only":
                tailscale.Tcp.Add("443");
                break;
        }

        using var env = new CliEnv(TailnetEnv.Machine(), tailscale);
        env.Store.Write(CliEnv.DeadRecord(new RunTailnet(443, 3000, FakeTailscale.Dns)));

        var exit = await env.RunAsync("stop");

        Assert.Equal(0, exit);
        Assert.Equal(expectOff, tailscale.Called("serve", "--https=443", "off"));
        Assert.False(File.Exists(env.Store.FilePath));
        tailscale.AssertNeverFunnelOrReset(443);
    }

    [Fact]
    public async Task Stale_tailnet_state_without_tailscale_installed_only_removes_the_record()
    {
        var tailscale = new FakeTailscale();
        using var env = new CliEnv(TailnetEnv.Machine("notool"), tailscale);
        env.Store.Write(CliEnv.DeadRecord(new RunTailnet(443, 3000, FakeTailscale.Dns)));

        var exit = await env.RunAsync("stop");

        Assert.Equal(0, exit);
        Assert.Empty(tailscale.Calls);
        Assert.False(File.Exists(env.Store.FilePath));
    }

    [Fact]
    public void Allowed_login_override_beats_every_derived_identity()
    {
        var environment = TailnetEnv.Machine(logins: ["other@example.com"]);

        Assert.Equal(["other@example.com"], TailnetRules.AllowedLogins(environment));
    }

    [Fact]
    public void Allowed_login_of_an_untagged_node_is_its_user()
    {
        Assert.Equal(["user@github"], TailnetRules.AllowedLogins(TailnetEnv.Machine()));
    }

    [Fact]
    public void Allowed_login_of_a_tagged_node_on_a_github_tailnet_is_derived_from_the_tailnet_name()
    {
        var status = TailnetEnv.Machine().Tailnet! with { SelfUserLogin = null };

        Assert.Equal(["example@github"], TailnetRules.AllowedLogins(TestCore.Machine(tailnet: status)));
    }

    [Fact]
    public void No_identity_can_be_derived_for_other_tailnets_or_without_a_tailnet()
    {
        var other = TailnetEnv.Machine().Tailnet! with { SelfUserLogin = null, TailnetName = "example.org" };

        Assert.Empty(TailnetRules.AllowedLogins(TestCore.Machine(tailnet: other)));
        Assert.Empty(TailnetRules.AllowedLogins(TestCore.Machine()));
    }
}
