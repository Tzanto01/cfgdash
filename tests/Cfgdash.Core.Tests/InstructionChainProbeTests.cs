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

        var result = await collector.CollectAsync(TestContexts.Create(TestEnvironments.Create(tree), tree.Path("app")), CancellationToken.None);

        Assert.DoesNotContain(probed, p => p.Contains(".ssh", StringComparison.Ordinal));
        Assert.Contains(probed, p => PathUtil.PathEquals(p, tree.Path("app/docs/guide.md")));
        var children = ((TreeBlock)result.Blocks.Single()).Nodes.SelectMany(n => n.Children).Select(c => c.Label).ToList();
        Assert.Contains("docs/guide.md", children);
        Assert.DoesNotContain(".ssh/notes.md", children);
    }
}
