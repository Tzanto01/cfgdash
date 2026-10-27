using System.Diagnostics;
using System.Text.Json.Nodes;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

/// <summary>One build of agents.definitions + skills.catalog over managed, two project levels, user and one enabled plugin.</summary>
public sealed class CatalogSnapshotFixture : IDisposable
{
    public CatalogSnapshotFixture()
    {
        Tree.Dir("repo/.git");
        var target = Tree.Dir("repo/sub");
        var config = Tree.Dir("config/.claude");
        var plugin = Tree.Dir("config/.claude/plugins/cache/tool");
        Tree.Write("config/.claude/settings.json", """{ "outputStyle": "Terse", "enabledPlugins": { "tool@mkt": true } }""");
        Tree.Write("config/.claude/plugins/installed_plugins.json", new JsonObject
        {
            ["plugins"] = new JsonObject { ["tool@mkt"] = new JsonArray(new JsonObject { ["scope"] = "user", ["installPath"] = plugin }) },
        }.ToJsonString());

        foreach (var (dir, scope) in new[] { ("managed/.claude", "managed"), ("repo/sub/.claude", "near"), ("repo/.claude", "far"), ("config/.claude", "user"), ("config/.claude/plugins/cache/tool", "plugin") })
        {
            Tree.Write($"{dir}/agents/shared.md", $"---\nname: shared\ndescription: {scope} copy\n---\n");
        }

        NearProj = Tree.Write("repo/sub/.claude/agents/proj.md", "---\nname: proj\ndescription: near\n---\n");
        FarProj = Tree.Write("repo/.claude/agents/proj.md", "---\nname: proj\ndescription: far\n---\n");
        Tree.Write("config/.claude/agents/team/reviewer.md", """
            ---
            name: reviewer
            description: |
              Reviews   changes
              line by line.
            model: sonnet
            tools: [Read, Grep]
            permissionMode: plan
            effort: high
            hooks:
              PreToolUse:
                - matcher: Bash
                  hooks:
                    - type: command
                      command: ./a.sh
                    - type: command
                      command: ./b.sh
              Stop:
                - hooks:
                    - type: prompt
                      prompt: check
            ---
            """);
        Tree.Write("config/.claude/agents/broken.md", "---\nname: broken\n---\n");
        CoreFile = Tree.Write("company/acme/.claude/custom/agents/reviewer/core.md", "core content");

        Tree.Write("config/.claude/skills/both/SKILL.md", "---\ndescription: user skill\n---\n");
        Tree.Write("repo/sub/.claude/skills/both/SKILL.md", "---\ndescription: project skill\n---\n");
        Tree.Write("config/.claude/plugins/cache/tool/skills/helper/SKILL.md", "---\ndescription: plugin skill\n---\n");
        LinkTarget = Path.GetDirectoryName(Tree.Write("external/skills/linked/SKILL.md", "---\ndescription: linked skill\n---\n"))!;
        LinkDir = Tree.Path("config/.claude/skills/linked");
        CreateDirectoryLink(LinkDir, LinkTarget);

        Tree.Write("config/.claude/commands/frontend/component.md", "Make a component");
        Tree.Write("config/.claude/plugins/cache/tool/commands/run.md", "Run it");
        Tree.Write("config/.claude/output-styles/terse.md", "---\nname: Terse\ndescription: short answers\n---\n");

        var environment = TestEnvironments.Create(
            Tree,
            paths: [TestEnvironments.ContextPath(PathKeys.ClaudeConfigDir, config)],
            companyPrefixes: [Tree.Path("company/acme")]);
        var settings = new SettingsLayers(new PlatformLocations(Tree.Path("managed"), null, _ => null));
        var locations = new PlatformLocations(Tree.Path("managed"), null, _ => null);
        var builder = new SnapshotBuilder(
            environment,
            [new AgentDefinitionsCollector(settings, locations), new SkillsCatalogCollector(settings, locations)],
            new Redactor(),
            new FakeProcessRunner(),
            "0.1.0");
        Snapshot = builder.BuildAsync(target, CancellationToken.None).GetAwaiter().GetResult().Snapshot;
    }

    public TempTree Tree { get; } = new();

    public string NearProj { get; }

    public string FarProj { get; }

    public string CoreFile { get; }

    public string LinkDir { get; }

    public string LinkTarget { get; }

    public HarnessSnapshot Snapshot { get; }

    public IReadOnlyList<TableRow> Rows(string collectorId, string blockId) =>
        Snapshot.Sections.Single(s => s.CollectorId == collectorId).Blocks.OfType<TableBlock>().Single(b => b.Id == blockId).Rows;

    public void Dispose() => Tree.Dispose();

    internal static void CreateDirectoryLink(string link, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo("cmd", ["/c", "mklink", "/J", link, target]) { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            process.WaitForExit();
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
    }
}

public sealed class HarnessCatalogTests(CatalogSnapshotFixture fixture) : IClassFixture<CatalogSnapshotFixture>
{
    private static string? Text(JsonNode? cell) => cell is JsonObject obj ? obj["text"]?.GetValue<string>() : cell?.GetValue<string>();

    private static string? FileId(JsonNode? cell) => (cell as JsonObject)?["fileId"]?.GetValue<string>();

    [Fact]
    public void Same_name_agents_follow_managed_project_user_plugin_priority()
    {
        var shared = fixture.Rows("agents.definitions", "agents").Where(r => Text(r.Cells["name"]) == "shared").ToList();

        Assert.Equal(["managed copy", "near copy", "far copy", "user copy", "plugin copy"], shared.Select(r => Text(r.Cells["description"])));
        Assert.Equal(["managed", "project", "project", "user", "plugin"], shared.Select(r => Text(r.Cells["scope"])));
        Assert.DoesNotContain("overridden", shared[0].Flags ?? []);
        Assert.All(shared.Skip(1), r => Assert.Contains("overridden", r.Flags!));
    }

    [Fact]
    public void Nearest_project_agent_wins_and_colliding_row_ids_stay_unique()
    {
        var proj = fixture.Rows("agents.definitions", "agents").Where(r => Text(r.Cells["name"]) == "proj").ToList();

        Assert.Equal([FileRegistry.IdFor(fixture.NearProj), FileRegistry.IdFor(fixture.FarProj)], proj.Select(r => r.FileId));
        Assert.Null(proj[0].Flags);
        Assert.Equal("project:proj", proj[0].Id);
        Assert.Equal("project:proj@" + FileRegistry.IdFor(fixture.FarProj), proj[1].Id);
    }

    [Fact]
    public void Agent_columns_summarize_frontmatter()
    {
        var reviewer = fixture.Rows("agents.definitions", "agents").Single(r => Text(r.Cells["name"]) == "reviewer");

        Assert.Equal("sonnet", Text(reviewer.Cells["model"]));
        Assert.Equal("Read, Grep", Text(reviewer.Cells["tools"]));
        Assert.Equal("PreToolUse (2), Stop (1)", Text(reviewer.Cells["hooks"]));
        Assert.Equal("Reviews changes line by line.", Text(reviewer.Cells["description"]));
        Assert.Equal("permissionMode: plan; effort: high", Text(reviewer.Cells["other"]));
    }

    [Fact]
    public void Agent_without_description_is_flagged_error()
    {
        var broken = fixture.Rows("agents.definitions", "agents").Single(r => Text(r.Cells["name"]) == "broken");

        Assert.Contains("error", broken.Flags!);
    }

    [Fact]
    public void Company_core_marker_comes_from_presence_only()
    {
        var rows = fixture.Rows("agents.definitions", "agents");

        Assert.True(rows.Single(r => Text(r.Cells["name"]) == "reviewer").Cells["companyCore"]!.GetValue<bool>());
        Assert.False(rows.Single(r => r.Id == "user:broken").Cells["companyCore"]!.GetValue<bool>());
        Assert.DoesNotContain(fixture.Snapshot.Files, f => f.Id == FileRegistry.IdFor(fixture.CoreFile));
    }

    [Fact]
    public void User_skill_overrides_project_skill_and_plugin_skills_are_namespaced()
    {
        var rows = fixture.Rows("skills.catalog", "skills");

        Assert.Equal("active", Text(rows.Single(r => r.Id == "user:both").Cells["status"]));
        var project = rows.Single(r => r.Id == "project:both");
        Assert.Equal("overridden", Text(project.Cells["status"]));
        Assert.Contains("overridden", project.Flags!);
        Assert.Equal("plugin skill", Text(rows.Single(r => r.Id == "plugin:tool:helper").Cells["description"]));
    }

    [Fact]
    public void Skill_dir_exposed_through_a_link_shows_its_target()
    {
        var linked = fixture.Rows("skills.catalog", "skills").Single(r => r.Id == "user:linked");

        Assert.Contains("link", linked.Flags!);
        Assert.Equal($"{PathUtil.ToJson(fixture.LinkDir)} -> {PathUtil.ToJson(fixture.LinkTarget)}", Text(linked.Cells["exposedVia"]));
        Assert.Null(fixture.Rows("skills.catalog", "skills").Single(r => r.Id == "user:both").Cells["exposedVia"]);
        Assert.Contains(fixture.Snapshot.Files, f => f.Id == linked.FileId && f.Kind == "skill");
    }

    [Fact]
    public void Commands_use_colon_namespaces_for_subdirectories_and_plugins()
    {
        var ids = fixture.Rows("skills.catalog", "commands").Select(r => r.Id);

        Assert.Equal(["user:frontend:component", "plugin:tool:run"], ids);
    }

    [Fact]
    public void Output_styles_list_built_ins_and_mark_the_effective_setting_active()
    {
        var rows = fixture.Rows("skills.catalog", "output-styles");

        Assert.Equal(["Default", "Proactive", "Concise", "Explanatory", "Learning", "Terse"], rows.Select(r => Text(r.Cells["name"])));
        Assert.All(rows.Take(5), r => Assert.Equal(["built-in"], r.Flags!));
        Assert.All(rows.Take(5), r => Assert.Null(r.Cells["status"]));
        var terse = rows.Single(r => r.Id == "user:Terse");
        Assert.Equal("active", Text(terse.Cells["status"]));
        Assert.NotNull(FileId(terse.Cells["file"]));
    }
}
