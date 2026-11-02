using Cfgdash.Core.Collectors;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class InstructionChainProbeTests
{
    [Fact]
    public async Task A_never_read_reference_is_refused_before_its_existence_is_probed()
    {
        using var tree = new TempTree();
        tree.Dir("app/.git");
        tree.Write("app/CLAUDE.md", "See [keys](.ssh/notes.md) and docs/guide.md.\n");
        tree.Write("app/.ssh/notes.md", "secret notes");
        tree.Write("app/docs/guide.md", "guide");
        var (result, probed) = await CollectAsync(tree);

        Assert.DoesNotContain(probed, p => p.Contains(".ssh", StringComparison.Ordinal));
        Assert.Contains(probed, p => PathUtil.PathEquals(p, tree.Path("app/docs/guide.md")));
        var children = ((TreeBlock)result.Blocks.Single()).Nodes.SelectMany(n => n.Children).Select(c => c.Label).ToList();
        Assert.Contains("docs/guide.md", children);
        Assert.DoesNotContain(".ssh/notes.md", children);
    }

    [Fact]
    public async Task A_never_read_import_is_refused_before_its_existence_is_probed()
    {
        using var tree = new TempTree();
        tree.Dir("app/.git");
        tree.Write("app/CLAUDE.md", "@.ssh/notes.md\n@docs/guide.md\n");
        tree.Write("app/.ssh/notes.md", "secret notes");
        tree.Write("app/docs/guide.md", "guide");
        var (result, probed) = await CollectAsync(tree);

        Assert.DoesNotContain(probed, p => p.Contains(".ssh", StringComparison.Ordinal));
        Assert.Contains(probed, p => PathUtil.PathEquals(p, tree.Path("app/docs/guide.md")));
        var children = ((TreeBlock)result.Blocks.Single()).Nodes.SelectMany(n => n.Children).ToList();
        Assert.Equal("not readable", children.Single(c => c.Label == "@.ssh/notes.md").Meta!["error"]!.GetValue<string>());
        Assert.Equal(["import", "loaded"], children.Single(c => c.Label == "@docs/guide.md").Badges);
    }

    private static async Task<(CollectorResult Result, List<string> Probed)> CollectAsync(TempTree tree)
    {
        var probed = new List<string>();
        var locations = new PlatformLocations(null, null, _ => null);
        var collector = new InstructionChainCollector(new SettingsLayers(locations), locations)
        {
            ReferenceExists = path =>
            {
                lock (probed)
                {
                    probed.Add(path);
                }

                return File.Exists(path);
            },
        };
        return (await collector.CollectAsync(TestContexts.Create(TestEnvironments.Create(tree), tree.Path("app")), CancellationToken.None), probed);
    }
}
