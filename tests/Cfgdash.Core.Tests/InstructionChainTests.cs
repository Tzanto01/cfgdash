using Cfgdash.Core.Collectors;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

/// <summary>
/// A config-repo layout: <c>home/user/.claude</c> links to <c>ws/config/.claude</c>, the target is <c>ws/config/app</c>
/// inside the git root <c>ws/config</c>, plus a temp managed dir.
/// </summary>
public sealed class InstructionChainFixture : IDisposable
{
    public InstructionChainFixture()
    {
        Tree.Write("managed/CLAUDE.md", "managed");
        Tree.Dir("ws/config/.git");
        Tree.Write("ws/config/.claude/CLAUDE.md", "user\n@~/notes/home.md\n");
        Tree.Write("home/user/notes/home.md", "home notes");
        Tree.Write("ws/config/.claude/rules/style.md", "style");
        Tree.Write("ws/config/.claude/rules/scoped/web.md", "---\npaths: \"**/*.tsx\"\n---\nweb");
        Tree.Write("ws/config/.claude/settings.json", """{ "agent": "helper", "outputStyle": "Terse" }""");
        Tree.Write("ws/config/.claude/agents/helper.md", "---\nname: helper\ndescription: helps\n---\nbody");
        Tree.Write("ws/config/.claude/output-styles/terse.md", "---\nname: Terse\ndescription: short\n---\nbe brief");
        Tree.Write("ws/config/CLAUDE.md", "config root");
        Tree.Write("ws/config/app/CLAUDE.md", """
            # App
            @d1.md
            @my\ notes.md
            @"quoted.md"
            Use `@code.md` and `docs/guide.md` here.
            ```text
            @fenced.md
            docs/fenced-ref.md
            ```
            @missing.md
            @cyc1.md
            @shared.md
            @../../outside.md
            See [override](AGENTS.override.md#top) and https://x.example/remote.md
            """);
        Tree.Write("ws/config/app/d1.md", "@d2.md");
        Tree.Write("ws/config/app/d2.md", "@d3.md");
        Tree.Write("ws/config/app/d3.md", "@d4.md");
        Tree.Write("ws/config/app/d4.md", "@d5.md");
        Tree.Write("ws/config/app/d5.md", "deep");
        Tree.Write("ws/config/app/my notes.md", "notes");
        Tree.Write("ws/config/app/quoted.md", "quoted");
        Tree.Write("ws/config/app/code.md", "code");
        Tree.Write("ws/config/app/fenced.md", "fenced");
        Tree.Write("ws/config/app/docs/fenced-ref.md", "fenced ref");
        Tree.Write("ws/config/app/docs/guide.md", "guide");
        Tree.Write("ws/config/app/cyc1.md", "@cyc2.md");
        Tree.Write("ws/config/app/cyc2.md", "@cyc1.md");
        Tree.Write("ws/config/app/shared.md", "shared");
        Tree.Write("ws/config/app/AGENTS.override.md", "override");
        Tree.Write("ws/config/app/AGENTS.md", "agents");
        Tree.Write("ws/outside.md", "outside");
        Tree.Write("ws/config/app/CLAUDE.local.md", "@shared.md\nAlso read AGENTS.override.md.\n");
        Tree.Write("ws/config/app/.claude/rules/always.md", "always");
        Tree.Write("ws/config/app/.claude/rules/web/ts.md", "---\npaths:\n  - src/**/*.ts\n  - lib/*.ts\n---\nts");
        Tree.Write("ws/config/app/web/CLAUDE.md", "lazy web");
        Tree.Write("ws/config/app/pkg/.claude/CLAUDE.md", "lazy pkg");
        Tree.Write("ws/config/app/node_modules/x/CLAUDE.md", "skipped");
        Tree.Write("ws/config/app/.hidden/CLAUDE.md", "skipped");

        var environment = TestEnvironments.Create(Tree);
        var configDir = Tree.Path("home/user/.claude");
        CatalogSnapshotFixture.CreateDirectoryLink(configDir, Tree.Path("ws/config/.claude"));
        environment = environment with { Paths = [TestEnvironments.ContextPath(PathKeys.ClaudeConfigDir, configDir)] };
        Result = Collect(Tree, environment, Tree.Path("ws/config/app"), Tree.Path("managed"));
    }

    public TempTree Tree { get; } = new();

    public CollectorResult Result { get; }

    public TreeBlock Block => (TreeBlock)Result.Blocks.Single();

    public string Json(string relative) => PathUtil.ToJson(Tree.Path(relative));

    public TreeNode Top(string relative) => Block.Nodes.Single(n => n.Label == Json(relative));

    public void Dispose() => Tree.Dispose();

    internal static CollectorResult Collect(TempTree tree, MachineEnvironment environment, string target, string? managedDir)
    {
        var locations = new PlatformLocations(managedDir, null, _ => null);
        var collector = new InstructionChainCollector(new SettingsLayers(locations), locations);
        return collector.CollectAsync(TestContexts.Create(environment, target), CancellationToken.None).GetAwaiter().GetResult();
    }
}

public sealed class InstructionChainTests(InstructionChainFixture fixture) : IClassFixture<InstructionChainFixture>
{
    private static IEnumerable<TreeNode> All(IEnumerable<TreeNode> nodes) => nodes.SelectMany(n => All(n.Children).Prepend(n));

    private static TreeNode Child(TreeNode parent, string label) => parent.Children.Single(c => c.Label == label);

    private static string? Error(TreeNode node) => node.Meta?.GetValueOrDefault("error")?.GetValue<string>();

    [Fact]
    public void Top_level_files_follow_the_documented_load_order()
    {
        var root = PathUtil.ToJson(fixture.Tree.Root);
        var labels = fixture.Block.Nodes.Select(n => n.Label).Where(l => l.StartsWith(root, StringComparison.Ordinal) || !l.Contains('/')).ToList();

        Assert.Equal(
            [
                fixture.Json("managed/CLAUDE.md"),
                fixture.Json("home/user/.claude/CLAUDE.md"),
                fixture.Json("home/user/.claude/rules/scoped/web.md"),
                fixture.Json("home/user/.claude/rules/style.md"),
                fixture.Json("ws/config/CLAUDE.md"),
                fixture.Json("ws/config/.claude/CLAUDE.md"),
                fixture.Json("ws/config/app/CLAUDE.md"),
                fixture.Json("ws/config/app/CLAUDE.local.md"),
                fixture.Json("ws/config/app/.claude/rules/always.md"),
                fixture.Json("ws/config/app/.claude/rules/web/ts.md"),
                "Agent: helper",
                "Output style: Terse",
                fixture.Json("ws/config/app/pkg/.claude/CLAUDE.md"),
                fixture.Json("ws/config/app/web/CLAUDE.md"),
            ],
            labels);
        Assert.Equal(["managed", "loaded"], fixture.Top("managed/CLAUDE.md").Badges);
        Assert.Equal(["user", "loaded"], fixture.Top("home/user/.claude/CLAUDE.md").Badges);
        Assert.Equal(["user", "rule", "loaded"], fixture.Top("home/user/.claude/rules/style.md").Badges);
        Assert.Equal(["local", "loaded"], fixture.Top("ws/config/app/CLAUDE.local.md").Badges);
        Assert.Equal("Load order for " + fixture.Json("ws/config/app"), fixture.Block.Title);
    }

    [Fact]
    public void Config_repo_claude_file_is_deduped_against_the_user_file_by_real_path()
    {
        var copy = fixture.Top("ws/config/.claude/CLAUDE.md");

        Assert.Equal(["project", "duplicate"], copy.Badges);
        Assert.Empty(copy.Children);
        Assert.Contains(fixture.Result.Notes!, n => n.Contains("Both CLAUDE.md and .claude/CLAUDE.md exist in " + fixture.Json("ws/config"), StringComparison.Ordinal));
    }

    [Fact]
    public void Rules_with_paths_frontmatter_are_conditional_and_not_counted()
    {
        var always = fixture.Top("ws/config/app/.claude/rules/always.md");
        var scoped = fixture.Top("ws/config/app/.claude/rules/web/ts.md");

        Assert.Equal(["project", "rule", "loaded"], always.Badges);
        Assert.Equal(["project", "rule", "conditional"], scoped.Badges);
        Assert.Equal("src/**/*.ts, lib/*.ts", scoped.Meta!["paths"]!.GetValue<string>());
        Assert.Equal(["user", "rule", "conditional"], fixture.Top("home/user/.claude/rules/scoped/web.md").Badges);
    }

    [Fact]
    public void Imports_resolve_relative_home_and_escaped_paths_and_skip_quotes_and_code()
    {
        var app = fixture.Top("ws/config/app/CLAUDE.md");
        var imports = app.Children.Where(c => c.Badges!.Contains("import")).Select(c => c.Label).ToList();

        Assert.Equal(["@d1.md", @"@my\ notes.md", "@missing.md", "@cyc1.md", "@shared.md", "@../../outside.md"], imports);
        Assert.Equal(["import", "loaded"], Child(app, @"@my\ notes.md").Badges);
        Assert.NotNull(Child(app, @"@my\ notes.md").FileId);
        Assert.Equal(["import", "loaded"], Child(fixture.Top("home/user/.claude/CLAUDE.md"), "@~/notes/home.md").Badges);
        Assert.Equal("missing", Error(Child(app, "@missing.md")));
        Assert.DoesNotContain(All(fixture.Block.Nodes), n => n.Label.Contains("fenced", StringComparison.Ordinal));
        Assert.Equal(["referenced"], Child(app, "docs/guide.md").Badges);
        Assert.DoesNotContain(All(fixture.Block.Nodes), n => n.Label.Contains("code.md", StringComparison.Ordinal));
        Assert.Equal(["referenced"], Child(app, "quoted.md").Badges);
        Assert.Equal("n7.1", Child(app, "@d1.md").Id);
    }

    [Fact]
    public void Imports_follow_four_hops_and_stop_at_the_fifth()
    {
        var d1 = Child(fixture.Top("ws/config/app/CLAUDE.md"), "@d1.md");
        var d4 = Child(Child(Child(d1, "@d2.md"), "@d3.md"), "@d4.md");
        var d5 = Child(d4, "@d5.md");

        Assert.Equal(["import", "loaded"], d4.Badges);
        Assert.Equal(["import", "error"], d5.Badges);
        Assert.Equal("depth", Error(d5));
        Assert.Empty(d5.Children);
    }

    [Fact]
    public void Cycles_are_shown_once_and_repeated_imports_are_duplicates()
    {
        var app = fixture.Top("ws/config/app/CLAUDE.md");
        var back = Child(Child(Child(app, "@cyc1.md"), "@cyc2.md"), "@cyc1.md");

        Assert.Equal("cycle", Error(back));
        Assert.Empty(back.Children);
        Assert.Equal(["import", "duplicate"], Child(fixture.Top("ws/config/app/CLAUDE.local.md"), "@shared.md").Badges);
    }

    [Fact]
    public void Project_imports_outside_the_project_root_are_external()
    {
        var app = fixture.Top("ws/config/app/CLAUDE.md");

        Assert.Equal(["import", "loaded", "external"], Child(app, "@../../outside.md").Badges);
        Assert.DoesNotContain("external", Child(app, "@d1.md").Badges!);
    }

    [Fact]
    public void Agents_override_is_referenced_via_link_and_plain_path_without_counting()
    {
        var link = Child(fixture.Top("ws/config/app/CLAUDE.md"), "AGENTS.override.md");
        var plain = Child(fixture.Top("ws/config/app/CLAUDE.local.md"), "AGENTS.override.md");

        Assert.Equal(["referenced"], link.Badges);
        Assert.Equal(["referenced"], plain.Badges);
        Assert.Equal(link.FileId, plain.FileId);
        Assert.Equal(8L, link.Meta!["sizeBytes"]!.GetValue<long>());
    }

    [Fact]
    public void Agents_md_is_not_loaded_when_a_claude_file_is_in_the_chain()
    {
        Assert.DoesNotContain(All(fixture.Block.Nodes), n => n.Badges!.Contains("agents-md"));
    }

    [Fact]
    public void Lazy_subdirectory_files_skip_ignored_dirs_and_are_not_counted()
    {
        var lazy = fixture.Block.Nodes.Where(n => n.Badges!.Contains("lazy")).ToList();

        Assert.Equal([fixture.Json("ws/config/app/pkg/.claude/CLAUDE.md"), fixture.Json("ws/config/app/web/CLAUDE.md")], lazy.Select(n => n.Label));
        Assert.All(lazy, n => Assert.Equal(["lazy"], n.Badges));
        Assert.All(lazy, n => Assert.False(n.Meta!.ContainsKey("order")));
    }

    [Fact]
    public void Main_thread_agent_and_custom_output_style_are_counted_last()
    {
        var agent = fixture.Block.Nodes.Single(n => n.Label == "Agent: helper");
        var style = fixture.Block.Nodes.Single(n => n.Label == "Output style: Terse");

        Assert.Equal(["agent", "loaded"], agent.Badges);
        Assert.Equal(["output-style", "loaded"], style.Badges);
        Assert.NotNull(agent.FileId);
        Assert.NotNull(style.FileId);
        Assert.Equal(agent.Meta!["order"]!.GetValue<int>() + 1, style.Meta!["order"]!.GetValue<int>());
    }

    [Fact]
    public void Summary_equals_the_sum_of_counted_nodes()
    {
        var counted = All(fixture.Block.Nodes).Where(n => n.Badges!.Contains("loaded") && n.FileId is not null).ToList();

        Assert.Equal(counted.Count, fixture.Block.Summary!.Files);
        Assert.Equal(counted.Sum(n => n.Meta!["sizeBytes"]!.GetValue<long>()), fixture.Block.Summary.SizeBytes);
        Assert.Equal(counted.Sum(n => n.Meta!["tokens"]!.GetValue<long>()), fixture.Block.Summary.Tokens);
        Assert.DoesNotContain(counted, n => n.Badges!.Intersect(["lazy", "referenced", "conditional", "duplicate", "error"]).Any());
        Assert.Contains(counted, n => n.Label == "@d4.md");
    }
}

public sealed class InstructionChainAgentsMdTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    [Fact]
    public void Agents_md_loads_natively_without_claude_files_and_built_in_style_is_not_counted()
    {
        var config = tree.Dir("cfg");
        tree.Write("cfg/settings.json", """{ "outputStyle": "Concise" }""");
        var agentsMd = tree.Write("proj/AGENTS.md", "@extra.md");
        tree.Write("proj/extra.md", "extra");
        var environment = TestContexts.WithConfigDir(tree, config);

        var result = InstructionChainFixture.Collect(tree, environment, tree.Path("proj"), null);
        var block = (TreeBlock)result.Blocks.Single();
        var agents = block.Nodes.Single(n => n.Label == PathUtil.ToJson(agentsMd));
        var style = block.Nodes.Single(n => n.Label == "Output style: Concise");

        Assert.Equal(["project", "agents-md", "loaded"], agents.Badges);
        Assert.Equal(["import", "loaded"], Child(agents, "@extra.md").Badges);
        Assert.Equal(["built-in", "loaded"], style.Badges);
        Assert.Null(style.FileId);
        Assert.Equal(2, block.Summary!.Files);
        Assert.Contains(result.Notes!, n => n.Contains("2.1.277", StringComparison.Ordinal));
    }

    private static TreeNode Child(TreeNode parent, string label) => parent.Children.Single(c => c.Label == label);
}
