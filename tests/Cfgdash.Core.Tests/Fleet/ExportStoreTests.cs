using System.Text.Json.Nodes;
using Cfgdash.Core.Fleet;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests.Fleet;

public sealed class ExportStoreTests
{
    private static string Export(string host, string collectedAt) =>
        $$"""{"machine":{"hostname":"{{host}}","collectedAt":"{{collectedAt}}"},"context":{"targetDir":"/home/user/proj"},"sections":[]}""";

    [Fact]
    public void List_returns_valid_top_level_json_exports_newest_first_and_skips_everything_else()
    {
        using var tree = new TempTree();
        tree.Write("old.json", Export("old.example", "2026-01-01T00:00:00Z"));
        tree.Write("new.json", Export("new.example", "2026-03-01T00:00:00Z"));
        tree.Write("mid.json", Export("mid.example", "2026-02-01T00:00:00Z"));
        tree.Write("sub/nested.json", Export("nested.example", "2026-04-01T00:00:00Z"));
        tree.Write("notes.txt", Export("text.example", "2026-04-01T00:00:00Z"));
        tree.Write("broken.json", "{ not json");
        tree.Write("bad id.json", Export("space.example", "2026-04-01T00:00:00Z"));

        var list = ExportStore.List(tree.Root);

        Assert.Equal(["new", "mid", "old"], list.Select(e => e.Id));
        Assert.Equal("new.example", list[0].Machine);
        Assert.Equal(DateTimeOffset.Parse("2026-03-01T00:00:00Z"), list[0].CollectedAt);
        Assert.Equal("/home/user/proj", list[0].Dir);
    }

    [LinuxFact]
    public void List_skips_a_symlinked_export()
    {
        using var tree = new TempTree();
        var real = tree.Write("real/target.json", Export("real.example", "2026-01-01T00:00:00Z"));
        File.CreateSymbolicLink(tree.Path("link.json"), real);

        Assert.Empty(ExportStore.List(tree.Path("")));
        Assert.Null(ExportStore.Load(tree.Path(""), "link"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("%2e%2e")]
    [InlineData("x\n")]
    [InlineData("")]
    [InlineData("nested")]
    public void Load_returns_null_for_traversal_invalid_or_nested_ids(string id)
    {
        using var tree = new TempTree();
        tree.Write("exports/x.json", Export("x.example", "2026-01-01T00:00:00Z"));
        tree.Write("exports/sub/nested.json", Export("n.example", "2026-01-01T00:00:00Z"));
        tree.Write("secret.json", Export("s.example", "2026-01-01T00:00:00Z"));

        Assert.Null(ExportStore.Load(tree.Path("exports"), id));
    }

    [Fact]
    public void Load_returns_null_for_an_absolute_path_id()
    {
        using var tree = new TempTree();
        var file = tree.Write("exports/x.json", Export("x.example", "2026-01-01T00:00:00Z"));

        Assert.Null(ExportStore.Load(tree.Path("exports"), file));
        Assert.Null(ExportStore.Load(tree.Path("exports"), file[..^".json".Length]));
    }

    [Fact]
    public void Load_returns_the_snapshot_object_for_a_valid_id()
    {
        using var tree = new TempTree();
        tree.Write("host-1.example.json", Export("host.example", "2026-01-01T00:00:00Z"));

        var snapshot = ExportStore.Load(tree.Root, "host-1.example");

        Assert.Equal("host.example", (string?)snapshot?["machine"]?["hostname"]);
    }

    [Fact]
    public void Load_throws_for_json_that_is_not_a_snapshot()
    {
        using var tree = new TempTree();
        tree.Write("other.json", """{"hello":"world"}""");
        tree.Write("array.json", "[1,2]");

        Assert.Throws<InvalidDataException>(() => ExportStore.Load(tree.Root, "other"));
        Assert.Throws<InvalidDataException>(() => ExportStore.Load(tree.Root, "array"));
    }
}
