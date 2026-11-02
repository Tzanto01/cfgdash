using System.Text.Json.Nodes;
using Cfgdash.Core.Fleet;

namespace Cfgdash.Core.Tests.Fleet;

public sealed class SnapshotDiffTests
{
    private static JsonObject Snap(string host = "left.example", string family = "linux", string dir = "/home/user/proj", string paths = "[]", string collectors = "[]", string sections = "[]") =>
        (JsonObject)JsonNode.Parse(
            $$$"""
            {"machine":{"hostname":"{{{host}}}","collectedAt":"2026-01-02T03:04:05Z","os":{"family":"{{{family}}}"}},
             "context":{"targetDir":"{{{dir}}}","paths":{{{paths}}}},
             "collectors":{{{collectors}}},"sections":{{{sections}}}}
            """)!;

    private static string Section(string collectorId, string blocks) => $$"""{"collectorId":"{{collectorId}}","blocks":{{blocks}}}""";

    private static string KvBlock(string id, params (string Key, string Value)[] rows) =>
        $$"""{"id":"{{id}}","type":"kv","rows":[{{string.Join(",", rows.Select(r => $$"""{"key":"{{r.Key}}","value":"{{r.Value}}"}"""))}}]}""";

    private static string TableBlock(string id, params (string Id, string Text, string FileId)[] rows) =>
        $$"""{"id":"{{id}}","type":"table","rows":[{{string.Join(",", rows.Select(r => $$$$"""{"id":"{{{{r.Id}}}}","cells":{"name":{"text":"{{{{r.Text}}}}","fileId":"{{{{r.FileId}}}}"}}}"""))}}]}""";

    private static string Active(string id) => $$"""{"id":"{{id}}","state":"active"}""";

    [Fact]
    public void Rows_are_keyed_by_collector_block_and_item_with_counts_matching()
    {
        var left = Snap(
            collectors: $"[{Active("c1")},{Active("c2")}]",
            sections: $"""
            [{Section("c1", $"[{KvBlock("kv", ("same", "1"), ("diff", "a"), ("gone", "x"))},{TableBlock("tbl", ("r1", "one", "f1"), ("r2", "two", "f2"))}]")},
             {Section("c2", $"[{KvBlock("kv", ("k", "v"))}]")}]
            """);
        var right = Snap(
            host: "right.example",
            collectors: $"[{Active("c1")},{Active("c2")}]",
            sections: $"""
            [{Section("c1", $"[{KvBlock("kv", ("same", "1"), ("diff", "b"), ("new", "y"))},{TableBlock("tbl", ("r1", "one", "f1"), ("r2", "TWO", "f2"))}]")},
             {Section("c2", $"[{KvBlock("kv", ("k", "v"))}]")}]
            """);

        var report = SnapshotDiff.Compare(left, right);

        var collector = Assert.Single(report.Collectors);
        Assert.Equal("c1", collector.Id);
        var rows = collector.Rows.Select(r => (r.BlockId, r.ItemId, r.Change)).ToList();
        Assert.Equal([("kv", "diff", "changed"), ("kv", "gone", "onlyLeft"), ("kv", "new", "onlyRight"), ("tbl", "r2", "changed")], rows);
        Assert.Equal(new DiffCounts(2, 1, 1), report.Counts);
        var diff = collector.Rows[0];
        Assert.Equal("a", (string?)diff.Left);
        Assert.Equal("b", (string?)diff.Right);
        Assert.Null(collector.Rows[1].Right);
        Assert.Null(collector.Rows[2].Left);
    }

    [Fact]
    public void Tree_nodes_are_keyed_by_node_id_at_any_depth()
    {
        string Tree(string label) => $$"""[{"id":"t","type":"tree","nodes":[{"id":"root","label":"r","children":[{"id":"leaf","label":"{{label}}","badges":["loaded"],"children":[]}]}]}]""";
        var report = SnapshotDiff.Compare(
            Snap(sections: $"[{Section("c", Tree("one"))}]"),
            Snap(sections: $"[{Section("c", Tree("two"))}]"));

        var row = Assert.Single(Assert.Single(report.Collectors).Rows);
        Assert.Equal(("t", "leaf", "changed"), (row.BlockId, row.ItemId, row.Change));
    }

    [Fact]
    public void A_collector_with_only_a_state_difference_is_included_and_identical_ones_are_omitted()
    {
        var left = Snap(collectors: """[{"id":"a","state":"active"},{"id":"b","state":"active"}]""");
        var right = Snap(collectors: """[{"id":"a","state":"active"},{"id":"b","state":"unavailable"}]""");

        var report = SnapshotDiff.Compare(left, right);

        var collector = Assert.Single(report.Collectors);
        Assert.Equal("b", collector.Id);
        Assert.Equal(new DiffStates("active", "unavailable"), collector.State);
        Assert.Empty(collector.Rows);
        Assert.Equal(new DiffCounts(0, 0, 0), report.Counts);
    }

    [Fact]
    public void Sides_carry_hostname_collected_at_and_target_dir()
    {
        var report = SnapshotDiff.Compare(Snap(host: "l.example", dir: "/home/user/a"), Snap(host: "r.example", dir: "/home/user/b"));

        Assert.Equal(new DiffSide("l.example", DateTimeOffset.Parse("2026-01-02T03:04:05Z"), "/home/user/a"), report.Left);
        Assert.Equal("r.example", report.Right.Machine);
        Assert.Equal("/home/user/b", report.Right.Dir);
        Assert.Empty(report.Collectors);
    }

    [Fact]
    public void Secrets_on_the_right_side_never_appear_in_the_output()
    {
        var right = Snap(
            collectors: $"[{Active("c")}]",
            sections: $"""
            [{Section("c", $"[{KvBlock("kv", ("auth", "Bearer abcdef0123456789abcdef"))},{TableBlock("tbl", ("r1", "run --token=abcdef0123456789abcdef", "f"))}]")}]
            """);

        var report = SnapshotDiff.Compare(Snap(), right);

        var output = System.Text.Json.JsonSerializer.Serialize(report);
        Assert.NotEmpty(report.Collectors);
        Assert.DoesNotContain("abcdef0123456789abcdef", output);
    }

    [Fact]
    public void Context_paths_compare_by_key_across_windows_and_linux_snapshots()
    {
        var windows = Snap(
            family: "windows",
            dir: "D:/ws/proj",
            paths: """[{"key":"claudeConfigDir","value":"D:/ws/config/.claude"}]""",
            collectors: $"[{Active("c")}]",
            sections: $"[{Section("c", $"[{KvBlock("kv", ("agent", "d:/WS/config/.claude/agents/a.md"))}]")}]");
        var linux = Snap(
            dir: "/home/user/ws/proj",
            paths: """[{"key":"claudeConfigDir","value":"/home/user/ws/config/.claude"}]""",
            collectors: $"[{Active("c")}]",
            sections: $"[{Section("c", $"[{KvBlock("kv", ("agent", "/home/user/ws/config/.claude/agents/a.md"))}]")}]");

        Assert.Empty(SnapshotDiff.Compare(windows, linux).Collectors);
    }

    [Fact]
    public void Differing_file_ids_alone_produce_no_table_row()
    {
        var left = Snap(sections: $"[{Section("c", $"[{TableBlock("tbl", ("r1", "same", "file-left"))}]")}]");
        var right = Snap(sections: $"[{Section("c", $"[{TableBlock("tbl", ("r1", "same", "file-right"))}]")}]");

        Assert.Empty(SnapshotDiff.Compare(left, right).Collectors);
    }

    [Fact]
    public void A_kv_value_under_a_secret_named_key_is_redacted_even_when_it_looks_harmless()
    {
        var right = Snap(
            collectors: $"[{Active("c")}]",
            sections: $"[{Section("c", $"[{KvBlock("kv", ("env.MY_TOKEN", "abc123plain"), ("env.MODE", "fast"))}]")}]");

        var report = SnapshotDiff.Compare(Snap(), right);

        var rows = Assert.Single(report.Collectors).Rows.ToDictionary(r => r.ItemId);
        Assert.Equal("[redacted]", (string?)rows["env.MY_TOKEN"].Right);
        Assert.Equal("fast", (string?)rows["env.MODE"].Right);
        Assert.DoesNotContain("abc123plain", System.Text.Json.JsonSerializer.Serialize(report));
    }

    [Fact]
    public void A_table_row_under_a_secret_named_id_is_redacted_even_when_it_looks_harmless()
    {
        var right = Snap(
            collectors: $"[{Active("c")}]",
            sections: $"[{Section("c", $"[{TableBlock("tbl", ("API_TOKEN", "abc123plain", "f1"), ("MODE", "fast", "f2"))}]")}]");

        var report = SnapshotDiff.Compare(Snap(), right);

        var rows = Assert.Single(report.Collectors).Rows.ToDictionary(r => r.ItemId);
        Assert.Equal("[redacted]", (string?)rows["API_TOKEN"].Right!["name"]);
        Assert.Equal("fast", (string?)rows["MODE"].Right!["name"]);
        Assert.DoesNotContain("abc123plain", System.Text.Json.JsonSerializer.Serialize(report));
    }

    [Fact]
    public void A_secret_shaped_item_id_is_redacted()
    {
        var right = Snap(
            collectors: $"[{Active("c")}]",
            sections: $"[{Section("c", $"[{KvBlock("kv", ("ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "x"))}]")}]");

        var report = SnapshotDiff.Compare(Snap(), right);

        Assert.DoesNotContain("ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", System.Text.Json.JsonSerializer.Serialize(report));
    }

    [Fact]
    public void A_secret_in_the_right_hostname_or_dir_is_redacted()
    {
        var secret = "sk-" + "ant-" + new string('a', 24);

        var report = SnapshotDiff.Compare(Snap(), Snap(host: "h-" + secret, dir: "/home/user/" + secret));

        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(report));
        Assert.Contains("[redacted]", report.Right.Machine);
        Assert.Contains("[redacted]", report.Right.Dir);
    }
}
