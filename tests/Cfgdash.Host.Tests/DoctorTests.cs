using System.Text.Json;
using Cfgdash.Core;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Host.Tests;

public sealed class DoctorTests
{
    [Fact]
    public async Task No_findings_exits_0_with_only_the_header()
    {
        using var env = new CliEnv();

        var exit = await env.RunAsync("doctor", "--dir", env.Dir);

        Assert.Equal(0, exit);
        Assert.Contains($"{env.Dir}: 0 critical, 0 warning, 0 info", env.Out.Text);
        Assert.DoesNotContain("Critical (", env.Out.Text);
    }

    [Fact]
    public async Task A_critical_finding_exits_3_and_is_listed_under_its_severity()
    {
        using var env = new CliEnv(snapshots: new FakeSnapshots { Sections = HealthSections.MissingScript() });

        var exit = await env.RunAsync("doctor", "--dir", env.Dir);

        Assert.Equal(CfgdashCli.ExitCritical, exit);
        Assert.Contains($"{env.Dir}: 1 critical, 0 warning, 0 info", env.Out.Text);
        Assert.Contains("Critical (1)", env.Out.Text);
        Assert.Contains(": " + HealthSections.MissingScriptDetail, env.Out.Text);
    }

    [Fact]
    public async Task Warnings_alone_do_not_fail_the_run()
    {
        using var env = new CliEnv(snapshots: new FakeSnapshots { Sections = HealthSections.DuplicateOnly() });

        var exit = await env.RunAsync("doctor", "--dir", env.Dir);

        Assert.Equal(0, exit);
        Assert.Contains("Warning (1)", env.Out.Text);
        Assert.DoesNotContain("Critical (", env.Out.Text);
    }

    [Fact]
    public async Task A_failing_snapshot_exits_1()
    {
        using var env = new CliEnv(snapshots: new FakeSnapshots { FailWith = (_, _) => new InvalidOperationException("collector exploded") });

        var exit = await env.RunAsync("doctor", "--dir", env.Dir);

        Assert.Equal(1, exit);
        Assert.NotEmpty(env.Error.Text);
    }

    [Fact]
    public async Task Json_prints_the_endpoint_body_and_writes_nothing()
    {
        var snapshots = new FakeSnapshots { Sections = HealthSections.MissingScript() };
        using var env = new CliEnv(snapshots: snapshots);
        var expected = JsonSerializer.Serialize(CfgdashCore.Findings((await snapshots.BuildAsync(env.Dir, CancellationToken.None)).Snapshot), SnapshotJson.Options);

        var exit = await env.RunAsync("doctor", "--json", "--dir", env.Dir);

        Assert.Equal(CfgdashCli.ExitCritical, exit);
        Assert.Equal(expected, env.Out.Text.TrimEnd('\r', '\n'));
        Assert.False(Directory.Exists(env.Cache));
        Assert.Null(env.Store.Read());
    }

    [Fact]
    public async Task Text_mode_does_not_create_the_cache_dir()
    {
        using var env = new CliEnv();

        await env.RunAsync("doctor", "--dir", env.Dir);

        Assert.False(Directory.Exists(env.Cache));
    }

    [Fact]
    public void Json_is_accepted_for_doctor_and_rejected_for_other_commands()
    {
        using var temp = new TempDir();

        var doctor = CfgdashCli.Parse(["doctor", "--json", "--dir", temp.Root], out var doctorError);
        var export = CfgdashCli.Parse(["export", "--json"], out var exportError);

        Assert.Null(doctorError);
        Assert.Equal("doctor", doctor!.Command);
        Assert.Equal(CfgdashServer.NormalizeDir(temp.Root), doctor.Dir);
        Assert.Null(export);
        Assert.NotNull(exportError);
    }
}
