using System.Text.Json;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class SettingsNeverReadTests
{
    [LinuxFact]
    public async Task A_project_settings_file_linked_to_a_never_read_file_is_not_opened()
    {
        using var tree = new TempTree();
        var config = tree.Dir("home/user/.claude");
        tree.Write("home/user/.codex/auth.json", """{ "canaryKeyQ7": "canary-value-Zx9vLm" }""");
        var target = tree.Dir("proj");
        tree.Dir("proj/.git");
        tree.Dir("proj/.claude");
        File.CreateSymbolicLink(tree.Path("proj/.claude/settings.json"), tree.Path("home/user/.codex/auth.json"));
        var environment = TestEnvironments.Create(tree, paths: [TestEnvironments.ContextPath(PathKeys.ClaudeConfigDir, config)]);
        var catalog = CollectorCatalog.Create(new FakeEnvironmentReader(), new PlatformLocations(tree.Dir("managed"), null, _ => null));

        var result = await new SnapshotBuilder(environment, catalog, new Redactor(), new FakeProcessRunner(), "0.1.0").BuildAsync(target, CancellationToken.None);

        var json = JsonSerializer.Serialize(result.Snapshot, SnapshotJson.Options);
        Assert.DoesNotContain("canaryKeyQ7", json, StringComparison.Ordinal);
        Assert.DoesNotContain("canary-value-Zx9vLm", json, StringComparison.Ordinal);
        var layers = result.Snapshot.Sections.Single(s => s.CollectorId == "settings.effective").Blocks.OfType<TableBlock>().Single(b => b.Id == "layers");
        Assert.Contains("error", layers.Rows.Single(r => r.Id == "project").Flags!);
    }
}
