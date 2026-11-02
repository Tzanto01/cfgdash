using System.Text.Json.Nodes;
using Cfgdash.Core.Health;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Tests.Fleet;

public sealed class DriftRulesTests
{
    private const string Dir = "/home/user/proj";

    private static HarnessSnapshot Snap(IReadOnlyList<CollectorStatus> collectors, IReadOnlyList<Section> sections, params FileRef[] files) =>
        new(
            HarnessSnapshot.CurrentSchemaVersion,
            new MachineInfo("host.example", null, new OsInfo(OsFamily.Linux, "test os", "x64"), DateTimeOffset.UnixEpoch, "0.0.0"),
            new SnapshotContext(Dir, []),
            collectors,
            sections,
            files);

    private static CollectorStatus Status(string id, CollectorState state = CollectorState.Active) =>
        new(id, id, "1", "v", state, null, new Requirements(), 0);

    private static Section Sect(string collectorId, params Block[] blocks) => new(collectorId, "v", collectorId, blocks);

    private static TableRow Server(string id, string name, string target, string? status = null, bool? enabled = null)
    {
        var cells = new Dictionary<string, JsonNode?> { ["name"] = name, ["target"] = target };
        if (status is not null)
        {
            cells["status"] = status;
        }

        if (enabled is not null)
        {
            cells["enabled"] = enabled;
        }

        return new TableRow(id, cells);
    }

    private static TableBlock Servers(string id, params TableRow[] rows) => new(id, id, [], rows);

    private static IReadOnlyList<Finding> Drift(HarnessSnapshot snapshot, string rule) =>
        CfgdashCore.Findings(snapshot).Findings.Where(f => f.Rule == rule).ToList();

    private static HarnessSnapshot Mcp(TableRow claude, TableRow codex, CollectorState codexState = CollectorState.Active) =>
        Snap(
            [Status("mcp.servers"), Status("codex.config", codexState)],
            [Sect("mcp.servers", Servers("servers", claude)), Sect("codex.config", Servers("mcp", codex))]);

    [Fact]
    public void Mcp_drift_fires_when_claude_and_codex_define_the_same_name_with_different_targets()
    {
        var finding = Assert.Single(Drift(Mcp(Server("project:tools", "tools", "node a.js", "applied"), Server("tools", "tools", "node b.js")), "mcp-drift"));

        Assert.Equal("mcp-drift:mcp.servers:servers:project:tools", finding.Id);
        Assert.Equal(FindingSeverity.Info, finding.Severity);
        Assert.Contains("node a.js", finding.Detail);
        Assert.Contains("node b.js", finding.Detail);
    }

    [Fact]
    public void Mcp_drift_does_not_fire_when_targets_match()
    {
        Assert.Empty(Drift(Mcp(Server("p:tools", "tools", "node a.js", "applied"), Server("tools", "tools", "node a.js")), "mcp-drift"));
    }

    [Fact]
    public void Mcp_drift_does_not_fire_when_only_one_tool_defines_the_name()
    {
        Assert.Empty(Drift(Mcp(Server("p:tools", "tools", "node a.js", "applied"), Server("other", "other", "node b.js")), "mcp-drift"));
    }

    [Fact]
    public void Mcp_drift_does_not_fire_when_the_other_tools_collector_is_not_active()
    {
        Assert.Empty(Drift(Mcp(Server("p:tools", "tools", "node a.js", "applied"), Server("tools", "tools", "node b.js"), CollectorState.Unavailable), "mcp-drift"));
    }

    [Fact]
    public void Mcp_drift_ignores_a_disabled_codex_server()
    {
        Assert.Empty(Drift(Mcp(Server("p:tools", "tools", "node a.js", "applied"), Server("tools", "tools", "node b.js", enabled: false)), "mcp-drift"));
    }

    private static HarnessSnapshot Instructions(string path, bool claudeLoads, bool codexLoads)
    {
        var claudeNodes = claudeLoads ? [new TreeNode("n1", "AGENTS.md", [], "f1", ["loaded"])] : Array.Empty<TreeNode>();
        var codexRows = codexLoads ? [new TableRow("cx1", new Dictionary<string, JsonNode?> { ["path"] = path }, null, "f1")] : Array.Empty<TableRow>();
        return Snap(
            [Status("instructions.chain"), Status("codex.config")],
            [Sect("instructions.chain", new TreeBlock("tree", "tree", claudeNodes)), Sect("codex.config", Servers("instructions", codexRows))],
            new FileRef("f1", path, "instruction", 1, 1, false));
    }

    [Fact]
    public void Instructions_drift_fires_for_a_project_file_codex_loads_and_claude_does_not()
    {
        var finding = Assert.Single(Drift(Instructions(Dir + "/AGENTS.md", claudeLoads: false, codexLoads: true), "instructions-drift"));

        Assert.Equal("instructions-drift:codex.config:instructions:cx1", finding.Id);
        Assert.Equal(FindingSeverity.Info, finding.Severity);
        Assert.Contains("Codex", finding.Detail);
    }

    [Fact]
    public void Instructions_drift_fires_for_a_project_file_claude_loads_and_codex_does_not()
    {
        var finding = Assert.Single(Drift(Instructions(Dir + "/CLAUDE.md", claudeLoads: true, codexLoads: false), "instructions-drift"));

        Assert.Equal("instructions-drift:instructions.chain:tree:n1", finding.Id);
        Assert.Contains("not by Codex", finding.Detail);
    }

    [Fact]
    public void Instructions_drift_ignores_a_global_file_outside_the_target_dir_ancestry()
    {
        Assert.Empty(Drift(Instructions("/home/user/.codex/AGENTS.md", claudeLoads: false, codexLoads: true), "instructions-drift"));
    }

    [Fact]
    public void Instructions_drift_does_not_fire_when_both_tools_load_the_file()
    {
        Assert.Empty(Drift(Instructions(Dir + "/AGENTS.md", claudeLoads: true, codexLoads: true), "instructions-drift"));
    }
}
