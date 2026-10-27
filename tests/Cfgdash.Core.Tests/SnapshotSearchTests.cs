using System.Text.Json.Nodes;
using Cfgdash.Core.Files;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Search;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class SnapshotSearchTests(TempTree tree) : IClassFixture<TempTree>
{
    private readonly SnapshotSearch search = new();
    private readonly FileContentReader reader = new(new Redactor());

    private static HarnessSnapshot Snapshot(params Section[] sections) =>
        new(1, new MachineInfo("devbox", null, new OsInfo(OsFamily.Linux, "Test OS", "x64"), DateTimeOffset.UnixEpoch, "0.1.0"),
            new SnapshotContext("/home/user", []), [], sections, []);

    [Fact]
    public void Finds_rows_nodes_and_file_lines_with_ids()
    {
        var registry = new FileRegistry([]);
        var owned = registry.Register(tree.Write("search/owned.md", "intro\nthe Needle line\n"), "instruction")!;
        var loose = registry.Register(tree.Write("search/loose.txt", "needle"), "other")!;
        var kv = new KvBlock("kv", "KV", [new KvRow("model", "needle-model", new SourceRef("user", "kvfile")), new KvRow("other", 42, null)]);
        var table = new TableBlock("t", "T", [new TableColumn("name", "Name")],
            [new TableRow("row-1", new Dictionary<string, JsonNode?> { ["name"] = new JsonObject { ["text"] = "NEEDLE agent", ["fileId"] = owned.Id } }, FileId: "rowfile")]);
        var treeBlock = new TreeBlock("chain", "Chain",
            [new TreeNode("n1", "root", [new TreeNode("n1.1", "child", [], Meta: new Dictionary<string, JsonNode?> { ["path"] = "x/needle.md" })])]);
        var snapshot = Snapshot(new Section("c1", "agents", "C1", [kv, table, treeBlock]));

        var response = search.Search(snapshot, registry, reader, "  needle ", CancellationToken.None);

        Assert.Equal("needle", response.Query);
        Assert.False(response.Truncated);
        Assert.Collection(response.Results,
            h => Assert.Equal(("row", "kv", "model", "kvfile"), (h.Kind, h.BlockId, h.ItemId, h.FileId)),
            h => Assert.Equal(("row", "t", "row-1", "rowfile"), (h.Kind, h.BlockId, h.ItemId, h.FileId)),
            h => Assert.Equal(("node", "chain", "n1.1", (string?)null), (h.Kind, h.BlockId, h.ItemId, h.FileId)),
            h =>
            {
                Assert.Equal(("file", loose.Id, 1), (h.Kind, h.FileId, h.Line));
                Assert.Equal(("files", "", ""), (h.View, h.CollectorId, h.BlockId));
            },
            h =>
            {
                Assert.Equal(("file", owned.Id, 2), (h.Kind, h.FileId, h.Line));
                Assert.Equal(("agents", "c1", "t", "row-1"), (h.View, h.CollectorId, h.BlockId, h.ItemId));
                Assert.Equal("the Needle line", h.Snippet);
            });
    }

    [Fact]
    public void File_hits_come_from_redacted_content()
    {
        var token = "gh" + "p_" + new string('z', 36);
        var registry = new FileRegistry([]);
        registry.Register(tree.Write("search-redacted/notes.txt", "key " + token), "other");

        var raw = search.Search(Snapshot(), registry, reader, new string('z', 20), CancellationToken.None);
        var redacted = search.Search(Snapshot(), registry, reader, "[redacted]", CancellationToken.None);

        Assert.Empty(raw.Results);
        Assert.Equal("key [redacted]", Assert.Single(redacted.Results).Snippet);
    }

    [Fact]
    public void Query_shorter_than_two_chars_returns_nothing()
    {
        var snapshot = Snapshot(new Section("c", "v", "C", [new KvBlock("kv", "KV", [new KvRow("a", "a", null)])]));

        Assert.Empty(search.Search(snapshot, new FileRegistry([]), reader, " a ", CancellationToken.None).Results);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(250, true)]
    public void Results_are_capped_at_200_and_flag_truncation(int rows, bool truncated)
    {
        var table = new TableBlock("t", "T", [new TableColumn("c", "C")],
            [.. Enumerable.Range(0, rows).Select(i => new TableRow($"r{i}", new Dictionary<string, JsonNode?> { ["c"] = "match " + i }))]);

        var response = search.Search(Snapshot(new Section("c", "v", "C", [table])), new FileRegistry([]), reader, "match", CancellationToken.None);

        Assert.Equal(200, response.Results.Count);
        Assert.Equal(truncated, response.Truncated);
    }
}
