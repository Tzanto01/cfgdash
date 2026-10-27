using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class SnapshotBuilderTests(TempTree tree) : IClassFixture<TempTree>
{
    private const string R = "[redacted]";

    private sealed class TestCollector(string id, Requirements requirements, Func<CollectContext, CancellationToken, Task<CollectorResult>> collect) : ICollector
    {
        public CollectorManifest Manifest { get; } = new(id, "Title " + id, "1.0.0", "view-" + id, requirements);

        public Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken) => collect(context, cancellationToken);
    }

    private static TestCollector Returning(string id, params Block[] blocks) =>
        new(id, new Requirements(), (_, _) => Task.FromResult(new CollectorResult(blocks)));

    private async Task<HarnessSnapshot> BuildAsync(MachineEnvironment environment, TimeSpan? timeout, params ICollector[] collectors)
    {
        var builder = new SnapshotBuilder(environment, collectors, new Redactor(), new FakeProcessRunner(), "0.1.0", timeout);
        return (await builder.BuildAsync(tree.Root, CancellationToken.None)).Snapshot;
    }

    [Theory]
    [InlineData(OsFamily.Linux, new[] { OsFamily.Windows }, "Windows only")]
    [InlineData(OsFamily.Windows, new[] { OsFamily.Linux }, "Linux only")]
    [InlineData(OsFamily.Windows, new[] { OsFamily.Linux, OsFamily.Macos }, "Linux and macOS only")]
    public async Task Os_mismatch_is_not_present_and_checked_before_tools(OsFamily os, OsFamily[] required, string reason)
    {
        var collector = new TestCollector("os", new Requirements(Os: required, Tools: ["missing-tool"]), (_, _) => throw new InvalidOperationException("must not run"));

        var snapshot = await BuildAsync(TestEnvironments.Create(tree, os: os), null, collector);

        var status = Assert.Single(snapshot.Collectors);
        Assert.Equal(CollectorState.NotPresent, status.State);
        Assert.Equal(reason, status.Reason);
        Assert.Empty(snapshot.Sections);
    }

    [Fact]
    public async Task Missing_tool_is_unavailable_and_checked_before_paths()
    {
        var collector = new TestCollector("tool", new Requirements(Tools: ["git"], Paths: [PathKeys.ConfigRepo]), (_, _) => throw new InvalidOperationException("must not run"));

        var snapshot = await BuildAsync(TestEnvironments.Create(tree), null, collector);

        Assert.Equal(CollectorState.Unavailable, snapshot.Collectors[0].State);
        Assert.Equal("git not installed", snapshot.Collectors[0].Reason);
    }

    [Fact]
    public async Task Missing_or_absent_path_is_not_present()
    {
        var environment = TestEnvironments.Create(
            tree,
            paths: [TestEnvironments.ContextPath(PathKeys.ConfigRepo, tree.Path("does-not-exist"))],
            tools: new Dictionary<string, string> { ["git"] = "git" });
        var configRepo = new TestCollector("path", new Requirements(Tools: ["git"], Paths: [PathKeys.ConfigRepo]), (_, _) => throw new InvalidOperationException("must not run"));
        var engram = new TestCollector("engram", new Requirements(Paths: [PathKeys.EngramDataDir]), (_, _) => throw new InvalidOperationException("must not run"));

        var snapshot = await BuildAsync(environment, null, configRepo, engram);

        Assert.All(snapshot.Collectors, c => Assert.Equal(CollectorState.NotPresent, c.State));
        Assert.Equal("no configRepo on this machine", snapshot.Collectors[0].Reason);
        Assert.Equal("no engramDataDir on this machine", snapshot.Collectors[1].Reason);
    }

    [Fact]
    public async Task Exception_becomes_a_one_line_redacted_reason_and_other_collectors_still_run()
    {
        var token = "gh" + "p_" + new string('a', 36);
        var failing = new TestCollector("fails", new Requirements(), (_, _) => throw new InvalidOperationException("first line\nsecond " + token));
        var healthy = Returning("ok", new KvBlock("kv", "KV", [new KvRow("model", "opus", null)]));

        var snapshot = await BuildAsync(TestEnvironments.Create(tree), null, failing, healthy);

        Assert.Equal(CollectorState.Unavailable, snapshot.Collectors[0].State);
        Assert.Equal("InvalidOperationException: first line second " + R, snapshot.Collectors[0].Reason);
        Assert.Equal(CollectorState.Active, snapshot.Collectors[1].State);
        Assert.Equal("ok", Assert.Single(snapshot.Sections).CollectorId);
    }

    [Fact]
    public async Task Collector_ignoring_cancellation_times_out_without_hanging_the_build()
    {
        var never = new TaskCompletionSource<CollectorResult>();
        var hanging = new TestCollector("hangs", new Requirements(), (_, _) => never.Task);
        var healthy = Returning("ok");
        var stopwatch = Stopwatch.StartNew();

        var snapshot = await BuildAsync(TestEnvironments.Create(tree), TimeSpan.FromMilliseconds(200), hanging, healthy);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(CollectorState.Unavailable, snapshot.Collectors[0].State);
        Assert.StartsWith("timed out after", snapshot.Collectors[0].Reason);
        Assert.Equal(CollectorState.Active, snapshot.Collectors[1].State);
    }

    [Fact]
    public async Task Own_cancellation_without_timeout_is_reported_as_an_exception()
    {
        var cancelling = new TestCollector("cancels", new Requirements(), (_, _) => throw new OperationCanceledException("inner operation aborted"));

        var snapshot = await BuildAsync(TestEnvironments.Create(tree), null, cancelling);

        Assert.Equal(CollectorState.Unavailable, snapshot.Collectors[0].State);
        Assert.Equal("OperationCanceledException: inner operation aborted", snapshot.Collectors[0].Reason);
    }

    [Fact]
    public async Task Unavailable_reason_and_notes_are_reported_and_statuses_keep_catalog_order()
    {
        var unavailable = new TestCollector("b-unavailable", new Requirements(), (_, _) => Task.FromResult(new CollectorResult([], "git status failed: boom", ["partial"])));
        var first = Returning("z-first", new KvBlock("kv", "KV", []));
        var notPresent = new TestCollector("a-last", new Requirements(Paths: [PathKeys.CompanyLayer]), (_, _) => throw new InvalidOperationException());

        var snapshot = await BuildAsync(TestEnvironments.Create(tree), null, first, unavailable, notPresent);

        Assert.Equal(["z-first", "b-unavailable", "a-last"], snapshot.Collectors.Select(c => c.Id));
        Assert.Equal(CollectorState.Unavailable, snapshot.Collectors[1].State);
        Assert.Equal("git status failed: boom", snapshot.Collectors[1].Reason);
        Assert.Equal(["partial"], snapshot.Collectors[1].Notes!);
        var section = Assert.Single(snapshot.Sections);
        Assert.Equal(("z-first", "Title z-first", "view-z-first"), (section.CollectorId, section.Title, section.View));
    }

    [Fact]
    public async Task Final_pass_redacts_kv_table_and_tree_values_the_collector_missed()
    {
        var token = "gh" + "p_" + new string('a', 36);
        var kv = new KvBlock("kv", "KV",
        [
            new KvRow("env.API_TOKEN", "plain", new SourceRef("user"), Contributions: [new Contribution("plain", new SourceRef("user"), ContributionStatus.Applied)]),
            new KvRow("note", "uses " + token, null),
            new KvRow("model", "opus", null),
        ]);
        var table = new TableBlock("t", "T", [new TableColumn("a", "A"), new TableColumn("b", "B")],
        [
            new TableRow("r1", new Dictionary<string, JsonNode?> { ["a"] = "x " + token, ["b"] = new JsonObject { ["text"] = token, ["fileId"] = "f1" } }),
        ]);
        var treeBlock = new TreeBlock("tree", "Tree",
        [
            new TreeNode("n1", "label " + token, [new TreeNode("n1.1", token, [])], Meta: new Dictionary<string, JsonNode?> { ["cmd"] = token, ["tokens"] = 4 }),
        ]);

        var snapshot = await BuildAsync(TestEnvironments.Create(tree), null, Returning("leaky", kv, table, treeBlock));

        var blocks = Assert.Single(snapshot.Sections).Blocks;
        var rows = ((KvBlock)blocks[0]).Rows;
        Assert.Equal(R, rows[0].Value!.GetValue<string>());
        Assert.True(rows[0].Redacted);
        Assert.Contains("redacted", rows[0].Flags!);
        Assert.Equal(R, rows[0].Contributions![0].Value!.GetValue<string>());
        Assert.Equal("uses " + R, rows[1].Value!.GetValue<string>());
        Assert.True(rows[1].Redacted);
        Assert.False(rows[2].Redacted);
        Assert.Null(rows[2].Flags);
        var cells = ((TableBlock)blocks[1]).Rows[0].Cells;
        Assert.Equal("x " + R, cells["a"]!.GetValue<string>());
        Assert.Equal(R, cells["b"]!["text"]!.GetValue<string>());
        Assert.Equal("f1", cells["b"]!["fileId"]!.GetValue<string>());
        var node = ((TreeBlock)blocks[2]).Nodes[0];
        Assert.Equal("label " + R, node.Label);
        Assert.Equal(R, node.Children[0].Label);
        Assert.Equal(R, node.Meta!["cmd"]!.GetValue<string>());
        Assert.Equal(4, node.Meta["tokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task Files_are_listed_sorted_by_path_and_served_from_the_build_catalog()
    {
        var b = tree.Write("files/b.md", "b");
        var a = tree.Write("files/a.md", "a");
        var registering = new TestCollector("files", new Requirements(), (context, _) =>
        {
            context.Files.Register(b, "instruction");
            context.Files.Register(a, "instruction");
            return Task.FromResult(new CollectorResult([]));
        });
        var builder = new SnapshotBuilder(TestEnvironments.Create(tree), [registering], new Redactor(), new FakeProcessRunner(), "0.1.0");

        var result = await builder.BuildAsync(tree.Root, CancellationToken.None);

        Assert.Equal([a.Replace('\\', '/'), b.Replace('\\', '/')], result.Snapshot.Files.Select(f => f.Path));
        Assert.True(result.Files.TryGet(result.Snapshot.Files[0].Id, out _));
    }

    [Fact]
    public async Task Serialized_snapshot_uses_camel_case_enum_strings_and_omits_nulls()
    {
        var stopped = new TailnetStatus("Stopped", "devbox.example.ts.net", "example.github", "example.ts.net", true, [], [], "user@github");
        var environment = TestEnvironments.Create(tree, tailnet: stopped);
        var active = Returning("ok", new KvBlock("kv", "KV", [new KvRow("model", "opus", null)]));
        var missing = new TestCollector("gone", new Requirements(Paths: [PathKeys.ConfigRepo]), (_, _) => throw new InvalidOperationException());

        var snapshot = await BuildAsync(environment, null, active, missing);
        var json = JsonNode.Parse(JsonSerializer.Serialize(snapshot, SnapshotJson.Options))!;

        Assert.Equal(1, json["schemaVersion"]!.GetValue<int>());
        Assert.Null(json["machine"]!["tailnet"]);
        Assert.Equal(TestEnvironments.CurrentOs.ToString().ToLowerInvariant(), json["machine"]!["os"]!["family"]!.GetValue<string>());
        Assert.Equal(tree.Root.Replace('\\', '/'), json["context"]!["targetDir"]!.GetValue<string>());
        var collectors = json["collectors"]!.AsArray();
        Assert.Equal("active", collectors[0]!["state"]!.GetValue<string>());
        Assert.False(collectors[0]!.AsObject().ContainsKey("reason"));
        Assert.Equal("notPresent", collectors[1]!["state"]!.GetValue<string>());
        var row = json["sections"]![0]!["blocks"]![0]!;
        Assert.Equal("kv", row["type"]!.GetValue<string>());
        Assert.False(row["rows"]![0]!.AsObject().ContainsKey("source"));
    }
}
