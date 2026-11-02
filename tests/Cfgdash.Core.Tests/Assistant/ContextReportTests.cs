using System.Text.Json.Nodes;
using Cfgdash.Core.Context;
using Cfgdash.Core.Snapshot;
using static Cfgdash.Core.Tests.Assistant.AssistantViewFixture;

namespace Cfgdash.Core.Tests.Assistant;

public sealed class ContextReportTests
{
    private static readonly string Token = "gh" + "p_" + new string('z', 36);

    private static readonly string[] SectionIds = ["instructions", "settings", "agents", "skills", "hooks", "mcp"];

    private static HarnessSnapshot Snapshot(string filePath = "/home/user/proj/notes.md")
    {
        Section[] sections =
        [
            new("instructions.chain", "instructions", "Instructions",
                [new TreeBlock("instructions.chain/tree", "Chain", [new TreeNode("n1", "root", [new TreeNode("n1.1", "child", [])])])]),
            new("settings.effective", "settings", "Settings",
            [
                new KvBlock("settings.effective/main", "Main",
                [
                    Kv("model", value: Token),
                    Kv("statusLine", "f1", ["missing-script"]),
                ]),
            ]),
            new("agents.definitions", "agents", "Agents",
            [
                new TableBlock("agents.definitions/agents", "Agents", [new TableColumn("name", "Name"), new TableColumn("model", "Model")],
                    [new TableRow("agent-1", new Dictionary<string, JsonNode?> { ["name"] = "reviewer", ["model"] = "opus" })]),
            ]),
        ];
        return Snap(sections, [new FileRef("f1", filePath, "other", 1, 1, false)]);
    }

    [Fact]
    public void Build_yields_the_six_sections_in_order_with_items_and_missing_collectors_marked()
    {
        var report = ContextReport.Build(Snapshot());

        Assert.Equal(SectionIds, report.Sections.Select(s => s.Id).ToArray());
        Assert.Equal(["active", "active", "active", "missing", "missing", "missing"], report.Sections.Select(s => s.State).ToArray());
        Assert.Equal(["root", "child"], report.Sections[0].Items.Select(i => i.Label).ToArray());
        Assert.Equal([0, 1], report.Sections[0].Items.Select(i => i.Depth).ToArray());
        Assert.Equal(["model", "statusLine"], report.Sections[1].Items.Select(i => i.Label).ToArray());
        Assert.Equal("reviewer", Assert.Single(report.Sections[2].Items).Label);
        Assert.Equal("Model: opus", report.Sections[2].Items[0].Value);
    }

    [Fact]
    public void Build_redacts_secret_looking_values()
    {
        var report = ContextReport.Build(Snapshot());

        Assert.Equal("[redacted]", report.Sections[1].Items[0].Value);
        Assert.DoesNotContain(Token, report.ToJson());
        Assert.DoesNotContain(Token, report.ToText());
    }

    [Fact]
    public void ToJson_round_trips_with_the_section_ids()
    {
        var json = JsonNode.Parse(ContextReport.Build(Snapshot()).ToJson())!.AsObject();

        Assert.Equal(SectionIds, json["sections"]!.AsArray().Select(s => (string?)s!["id"]).ToArray());
        Assert.Equal("model", (string?)json["sections"]![1]!["items"]![0]!["label"]);
    }

    [Fact]
    public void Titles_resolve_a_row_a_table_row_a_node_a_file_and_a_finding()
    {
        var snapshot = Snapshot();
        var finding = CfgdashCore.Findings(snapshot).Findings.First(x => x.Rule == "missing-script");

        Assert.Equal("Settings / Main / model", Row(snapshot, "settings.effective", "settings.effective/main", "model"));
        Assert.Equal("Agents / Agents / reviewer", Row(snapshot, "agents.definitions", "agents.definitions/agents", "agent-1"));
        Assert.Equal("Instructions / Chain / child", Row(snapshot, "instructions.chain", "instructions.chain/tree", "n1.1"));
        Assert.Equal("File /home/user/proj/notes.md", ContextReport.Titles(snapshot, "file", new JsonObject { ["fileId"] = "f1" }));
        var title = ContextReport.Titles(snapshot, "finding", new JsonObject { ["findingId"] = finding.Id });
        Assert.StartsWith("Finding: ", title);
        Assert.Contains(finding.Title, title);
    }

    [Fact]
    public void Titles_answer_null_for_unknown_ids_unknown_kinds_extra_fields_non_string_ids_and_long_ids()
    {
        var snapshot = Snapshot();
        var long201 = new string('a', 201);

        Assert.Null(Row(snapshot, "settings.effective", "settings.effective/main", "nope"));
        Assert.Null(Row(snapshot, "nope", "settings.effective/main", "model"));
        Assert.Null(ContextReport.Titles(snapshot, "file", new JsonObject { ["fileId"] = "nope" }));
        Assert.Null(ContextReport.Titles(snapshot, "finding", new JsonObject { ["findingId"] = "nope" }));
        Assert.Null(ContextReport.Titles(snapshot, "secret", new JsonObject { ["fileId"] = "f1" }));
        Assert.Null(ContextReport.Titles(snapshot, "file", new JsonObject { ["fileId"] = "f1", ["extra"] = "x" }));
        Assert.Null(ContextReport.Titles(snapshot, "file", new JsonObject { ["fileId"] = 5 }));
        Assert.Null(ContextReport.Titles(snapshot, "file", new JsonObject { ["fileId"] = long201 }));
        Assert.Null(ContextReport.Titles(snapshot, "file", new JsonObject()));
        Assert.Null(ContextReport.Titles(snapshot, "row", new JsonObject { ["collectorId"] = "settings.effective", ["blockId"] = "settings.effective/main" }));
        Assert.NotNull(ContextReport.Titles(snapshot, "file", new JsonObject { ["kind"] = "file", ["fileId"] = "f1" }));
    }

    [Fact]
    public void Titles_are_redacted_single_line_and_capped_at_200_characters()
    {
        var secretPath = Snapshot("/home/user/" + Token + ".md");
        var longPath = Snapshot("/home/user/" + new string('d', 400) + ".md");

        var redacted = ContextReport.Titles(secretPath, "file", new JsonObject { ["fileId"] = "f1" });
        var capped = ContextReport.Titles(longPath, "file", new JsonObject { ["fileId"] = "f1" });

        Assert.DoesNotContain(Token, redacted);
        Assert.Equal(200, capped!.Length);
    }

    private static string? Row(HarnessSnapshot snapshot, string collectorId, string blockId, string itemId) =>
        ContextReport.Titles(snapshot, "row", new JsonObject { ["collectorId"] = collectorId, ["blockId"] = blockId, ["itemId"] = itemId });
}
