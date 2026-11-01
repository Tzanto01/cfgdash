using System.Text.Json.Nodes;
using Cfgdash.Core.Health;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Tests;

public sealed class HealthRulesTests
{
    private static HarnessSnapshot Snap(IReadOnlyList<CollectorStatus>? collectors = null, IReadOnlyList<Section>? sections = null, string dir = "/home/user/proj", DateTimeOffset? at = null) =>
        new(
            HarnessSnapshot.CurrentSchemaVersion,
            new MachineInfo("host.example", null, new OsInfo(OsFamily.Linux, "test os", "x64"), at ?? DateTimeOffset.UnixEpoch, "0.0.0"),
            new SnapshotContext(dir, []),
            collectors ?? [],
            sections ?? [],
            []);

    private static CollectorStatus Status(string id, string view, CollectorState state = CollectorState.Active, string? reason = null) =>
        new(id, id, "1", view, state, reason, new Requirements(), 0);

    private static Section Sect(string collectorId, string view, params Block[] blocks) => new(collectorId, view, collectorId, blocks);

    private static TableBlock Table(string id, params TableRow[] rows) =>
        new(id, id, [new TableColumn("event", "Event"), new TableColumn("script", "Script")], rows);

    private static TableRow TRow(string id, string first, string? script, string? flag, string? fileId = null) =>
        new(id, new Dictionary<string, JsonNode?> { ["event"] = first, ["script"] = script }, flag is null ? null : [flag], fileId);

    private static KvBlock Kv(string id, params KvRow[] rows) => new(id, id, rows);

    private static KvRow KRow(string key, string flag, string? fileId = null) =>
        new(key, JsonValue.Create("v"), fileId is null ? null : new SourceRef("label", fileId), false, [flag]);

    private static FindingsReport Evaluate(params Section[] sections) => CfgdashCore.Findings(Snap(sections: sections));

    [Fact]
    public void Missing_script_on_a_table_row_is_critical_and_names_the_script()
    {
        var report = Evaluate(Sect("hooks.registered", "hooks", Table("hooks.registered/hooks", TRow("SessionStart-0-0", "SessionStart", "/home/user/start.ps1", "missing-script", "f1"))));

        var finding = Assert.Single(report.Findings);
        Assert.Equal("missing-script:hooks.registered:hooks.registered/hooks:SessionStart-0-0", finding.Id);
        Assert.Equal("missing-script", finding.Rule);
        Assert.Equal(FindingSeverity.Critical, finding.Severity);
        Assert.Equal("SessionStart runs /home/user/start.ps1, which does not exist.", finding.Detail);
        Assert.Equal(new FindingEvidence("hooks.registered", "hooks.registered/hooks", "SessionStart-0-0", null, "f1"), finding.Evidence);
        Assert.Equal("hooks", finding.View);
    }

    [Fact]
    public void A_flagged_kv_row_is_a_finding_keyed_by_its_key_with_the_source_file()
    {
        var report = Evaluate(Sect("settings.effective", "settings", Kv("settings.effective/main", KRow("statusLine", "missing-script", "f9"))));

        var finding = Assert.Single(report.Findings);
        Assert.Equal("missing-script:settings.effective:settings.effective/main:statusLine", finding.Id);
        Assert.Equal(new FindingEvidence("settings.effective", "settings.effective/main", null, "statusLine", "f9"), finding.Evidence);
        Assert.Equal("settings", finding.View);
    }

    [Fact]
    public void Missing_command_on_an_mcp_row_is_a_warning()
    {
        var report = Evaluate(Sect("mcp.servers", "mcp", Table("mcp.servers/servers", TRow("project:tools", "tools", "node-missing", "missing-command"))));

        var finding = Assert.Single(report.Findings);
        Assert.Equal("missing-command:mcp.servers:mcp.servers/servers:project:tools", finding.Id);
        Assert.Equal("missing-command", finding.Rule);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(new FindingEvidence("mcp.servers", "mcp.servers/servers", "project:tools"), finding.Evidence);
        Assert.Equal("mcp", finding.View);
    }

    [Fact]
    public void An_error_badge_on_an_instruction_chain_node_is_an_import_error_at_any_depth()
    {
        var tree = new TreeBlock(
            "instructions.chain/tree",
            "Chain",
            [new TreeNode("root", "root", [new TreeNode("imp1", "imported", [], "f2", ["error"])])]);

        var finding = Assert.Single(Evaluate(Sect("instructions.chain", "instructions", tree)).Findings);

        Assert.Equal("import-error:instructions.chain:instructions.chain/tree:imp1", finding.Id);
        Assert.Equal("import-error", finding.Rule);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(new FindingEvidence("instructions.chain", "instructions.chain/tree", "imp1", FileId: "f2"), finding.Evidence);
        Assert.Equal("instructions", finding.View);
    }

    [Fact]
    public void An_error_flag_anywhere_else_is_a_plain_error_warning()
    {
        var otherTree = new TreeBlock("agents/tree", "Agents", [new TreeNode("n1", "n1", [], null, ["error"])]);

        var report = Evaluate(
            Sect("settings.effective", "settings", Kv("settings.effective/main", KRow("hooks", "error"))),
            Sect("agents.all", "agents", Table("agents.all/list", TRow("a1", "a1", null, "error")), otherTree));

        Assert.Equal(
            [
                "error:agents.all:agents.all/list:a1",
                "error:agents.all:agents/tree:n1",
                "error:settings.effective:settings.effective/main:hooks",
            ],
            report.Findings.Select(f => f.Id));
        Assert.All(report.Findings, f =>
        {
            Assert.Equal("error", f.Rule);
            Assert.Equal(FindingSeverity.Warning, f.Severity);
        });
    }

    [Fact]
    public void Duplicate_on_table_and_kv_rows_is_a_warning()
    {
        var report = Evaluate(
            Sect("agents.all", "agents", Table("agents.all/list", TRow("a1", "a1", null, "duplicate"))),
            Sect("settings.effective", "settings", Kv("settings.effective/main", KRow("env", "duplicate"))));

        Assert.Equal(
            ["duplicate:agents.all:agents.all/list:a1", "duplicate:settings.effective:settings.effective/main:env"],
            report.Findings.Select(f => f.Id));
        Assert.All(report.Findings, f =>
        {
            Assert.Equal("duplicate", f.Rule);
            Assert.Equal(FindingSeverity.Warning, f.Severity);
        });
    }

    [Fact]
    public void Overridden_on_a_kv_row_is_info()
    {
        var finding = Assert.Single(Evaluate(Sect("settings.effective", "settings", Kv("settings.effective/main", KRow("model", "overridden")))).Findings);

        Assert.Equal("overridden:settings.effective:settings.effective/main:model", finding.Id);
        Assert.Equal("overridden", finding.Rule);
        Assert.Equal(FindingSeverity.Info, finding.Severity);
        Assert.Equal(new FindingEvidence("settings.effective", "settings.effective/main", null, "model"), finding.Evidence);
        Assert.Equal("settings", finding.View);
    }

    [Theory]
    [InlineData("timed out after 20 s")]
    [InlineData("IOException: disk")]
    [InlineData("git status failed: not a repository")]
    public void An_unavailable_collector_with_a_runtime_reason_is_a_collector_error_warning(string reason)
    {
        var report = CfgdashCore.Findings(Snap([Status("git.status", "overview", CollectorState.Unavailable, reason)]));

        var finding = Assert.Single(report.Findings);
        Assert.Equal("collector-error:git.status", finding.Id);
        Assert.Equal("collector-error", finding.Rule);
        Assert.Equal(FindingSeverity.Warning, finding.Severity);
        Assert.Equal(new FindingEvidence("git.status"), finding.Evidence);
        Assert.Equal("overview", finding.View);
    }

    [Theory]
    [InlineData("git not installed")]
    [InlineData("no engram.db in the engram data dir")]
    public void An_unavailable_collector_for_a_missing_tool_or_file_is_info(string reason)
    {
        var report = CfgdashCore.Findings(Snap([Status("git.status", "overview", CollectorState.Unavailable, reason)]));

        var finding = Assert.Single(report.Findings);
        Assert.Equal("collector-unavailable:git.status", finding.Id);
        Assert.Equal("collector-unavailable", finding.Rule);
        Assert.Equal(FindingSeverity.Info, finding.Severity);
        Assert.Equal(new FindingEvidence("git.status"), finding.Evidence);
        Assert.Equal("overview", finding.View);
    }

    [Fact]
    public void Not_present_collectors_are_never_findings()
    {
        var report = CfgdashCore.Findings(Snap([Status("company.core", "company", CollectorState.NotPresent, "no company layer")]));

        Assert.Empty(report.Findings);
        Assert.Equal(new FindingCounts(0, 0, 0), report.Counts);
    }

    [Fact]
    public void Tree_duplicate_badges_and_table_overridden_flags_are_not_findings()
    {
        var tree = new TreeBlock("skills/tree", "Skills", [new TreeNode("n1", "n1", [], null, ["duplicate"])]);

        var report = Evaluate(Sect("skills.all", "skills", tree, Table("skills.all/list", TRow("s1", "s1", null, "overridden"))));

        Assert.Empty(report.Findings);
    }

    [Fact]
    public void Findings_are_sorted_by_severity_then_id_and_counted()
    {
        var report = CfgdashCore.Findings(Snap(
            [Status("git.status", "overview", CollectorState.Unavailable, "git not installed")],
            [
                Sect("settings.effective", "settings", Kv("settings.effective/main", KRow("model", "overridden"))),
                Sect("agents.all", "agents", Table("agents.all/list", TRow("b", "b", null, "duplicate"), TRow("a", "a", null, "duplicate"))),
                Sect("hooks.registered", "hooks", Table("hooks.registered/hooks", TRow("h1", "h1", "/home/user/x.sh", "missing-script"))),
            ]));

        Assert.Equal(
            [
                "missing-script:hooks.registered:hooks.registered/hooks:h1",
                "duplicate:agents.all:agents.all/list:a",
                "duplicate:agents.all:agents.all/list:b",
                "collector-unavailable:git.status",
                "overridden:settings.effective:settings.effective/main:model",
            ],
            report.Findings.Select(f => f.Id));
        Assert.Equal(new FindingCounts(1, 2, 2), report.Counts);
    }

    [Fact]
    public void The_report_echoes_the_target_dir_and_collection_time()
    {
        var at = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        var report = CfgdashCore.Findings(Snap(dir: "/home/user/other", at: at));

        Assert.Equal("/home/user/other", report.Dir);
        Assert.Equal(at, report.CollectedAt);
    }

    [Fact]
    public void Secrets_in_cells_never_reach_the_detail()
    {
        var secret = "gh" + "p_" + new string('c', 36);

        var finding = Assert.Single(Evaluate(Sect("hooks.registered", "hooks", Table("hooks.registered/hooks", TRow("h1", "Stop", "run --token " + secret, "missing-script")))).Findings);

        Assert.DoesNotContain(secret, finding.Detail);
        Assert.Contains("Stop runs", finding.Detail);
    }

    [Fact]
    public void Ids_are_identical_for_equal_snapshots_collected_at_different_times()
    {
        HarnessSnapshot Build(DateTimeOffset at) => Snap(
            [Status("git.status", "overview", CollectorState.Unavailable, "timed out after 20 s")],
            [
                Sect("hooks.registered", "hooks", Table("hooks.registered/hooks", TRow("h1", "Stop", "/home/user/x.sh", "missing-script"))),
                Sect("settings.effective", "settings", Kv("settings.effective/main", KRow("model", "overridden"))),
            ],
            at: at);

        var first = CfgdashCore.Findings(Build(DateTimeOffset.UnixEpoch));
        var second = CfgdashCore.Findings(Build(DateTimeOffset.UnixEpoch.AddHours(5)));

        Assert.Equal(3, first.Findings.Count);
        Assert.Equal(first.Findings.Select(f => f.Id), second.Findings.Select(f => f.Id));
    }
}
