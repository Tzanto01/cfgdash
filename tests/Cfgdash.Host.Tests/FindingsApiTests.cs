using System.Text.Json.Nodes;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Host.Tests;

internal static class HealthSections
{
    public const string MissingScriptId = "missing-script:hooks.registered:hooks.registered/hooks:SessionStart-0-0";

    public const string MissingScriptDetail = "SessionStart runs /home/user/start.ps1, which does not exist.";

    public static IReadOnlyList<Section> MissingScript() => [Hooks("missing-script")];

    public static IReadOnlyList<Section> DuplicateOnly() => [Hooks("duplicate")];

    private static Section Hooks(string flag) =>
        new(
            "hooks.registered",
            "hooks",
            "Hooks",
            [
                new TableBlock(
                    "hooks.registered/hooks",
                    "Hooks",
                    [new TableColumn("event", "Event"), new TableColumn("script", "Script")],
                    [new TableRow("SessionStart-0-0", new Dictionary<string, JsonNode?> { ["event"] = "SessionStart", ["script"] = "/home/user/start.ps1" }, [flag])]),
            ]);
}

public sealed class FindingsApiTests
{
    private static string Query(string dir) => "dir=" + Uri.EscapeDataString(dir);

    [Fact]
    public async Task Findings_come_from_the_snapshot_of_the_requested_dir()
    {
        await using var host = await ServerHost.StartAsync(snapshots: new FakeSnapshots { Sections = HealthSections.MissingScript() });

        var response = await host.GetAsync($"/api/v1/findings?{Query(host.StartDir)}");

        Assert.Equal(200, response.Status);
        Assert.Equal(host.StartDir, response.Json.GetProperty("dir").GetString());
        var counts = response.Json.GetProperty("counts");
        Assert.Equal((1, 0, 0), (counts.GetProperty("critical").GetInt32(), counts.GetProperty("warning").GetInt32(), counts.GetProperty("info").GetInt32()));
        var finding = Assert.Single(response.Json.GetProperty("findings").EnumerateArray());
        Assert.Equal(HealthSections.MissingScriptId, finding.GetProperty("id").GetString());
        Assert.Equal("critical", finding.GetProperty("severity").GetString());
        Assert.Equal(HealthSections.MissingScriptDetail, finding.GetProperty("detail").GetString());
        Assert.Equal("hooks", finding.GetProperty("view").GetString());
    }

    [Fact]
    public async Task Findings_share_the_snapshot_cache_and_refresh_rebuilds_it()
    {
        var snapshots = new FakeSnapshots();
        await using var host = await ServerHost.StartAsync(snapshots: snapshots);

        await host.GetAsync($"/api/v1/snapshot?{Query(host.StartDir)}");
        await host.GetAsync($"/api/v1/findings?{Query(host.StartDir)}");
        var cached = snapshots.CountFor(host.StartDir);
        await host.GetAsync($"/api/v1/findings?{Query(host.StartDir)}&refresh=true");

        Assert.Equal(1, cached);
        Assert.Equal(2, snapshots.CountFor(host.StartDir));
    }

    [Fact]
    public async Task Findings_reject_a_missing_dir_as_bad_dir()
    {
        await using var host = await ServerHost.StartAsync();

        var response = await host.GetAsync($"/api/v1/findings?{Query(Path.Combine(host.StartDir, "missing"))}");

        Assert.Equal(400, response.Status);
        Assert.Equal("bad-dir", response.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Findings_require_the_token(string? bearer)
    {
        await using var host = await ServerHost.StartAsync();

        var response = await host.GetAsync("/api/v1/findings", bearer);

        Assert.Equal(401, response.Status);
    }
}
