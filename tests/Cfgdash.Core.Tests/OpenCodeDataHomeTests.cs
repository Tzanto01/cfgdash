using System.Text.Json;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Environment;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class OpenCodeDataHomeTests
{
    private static async Task<(SnapshotResult Result, string Config)> BuildAsync(bool xdgDataHome)
    {
        using var tree = new TempTree();
        var target = tree.Dir("work/proj");
        tree.Dir("work/proj/.git");
        var config = tree.Write("data/opencode/custom.json", """{ "theme": "canary-xdg-data-1" }""");
        var reader = new FakeEnvironmentReader();
        reader.Process["OPENCODE_CONFIG"] = config;
        if (xdgDataHome)
        {
            reader.Process["XDG_DATA_HOME"] = tree.Path("data");
        }

        var result = await new SnapshotBuilder(TestEnvironments.Create(tree), [new OpenCodeConfigCollector(reader)], new Redactor(), new FakeProcessRunner(), "0.1.0")
            .BuildAsync(target, CancellationToken.None);
        return (result, config);
    }

    [Fact]
    public async Task A_config_file_under_xdg_data_home_opencode_is_never_registered_or_read()
    {
        var (result, config) = await BuildAsync(xdgDataHome: true);

        Assert.DoesNotContain(result.Files.All, f => PathUtil.PathEquals(f.FullPath, config));
        Assert.DoesNotContain("canary-xdg-data-1", JsonSerializer.Serialize(result.Snapshot, SnapshotJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_same_file_is_read_when_xdg_data_home_does_not_point_at_it()
    {
        var (result, config) = await BuildAsync(xdgDataHome: false);

        Assert.Contains(result.Files.All, f => PathUtil.PathEquals(f.FullPath, config));
    }

    [Fact]
    public void The_allowlist_reader_accepts_xdg_data_home()
    {
        Assert.Null(Record.Exception(() => new AllowlistEnvironmentReader().Get("XDG_DATA_HOME")));
    }
}
