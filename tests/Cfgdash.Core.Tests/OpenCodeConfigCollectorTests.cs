using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Search;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class OpenCodeConfigCollectorTests
{
    private const string Outside = "not opened (outside project and config dirs)";

    private static string? Text(JsonNode? cell) => cell is JsonObject obj ? obj["text"]?.GetValue<string>() : cell?.GetValue<string>();

    private static string[] Cells(TableRow row, params string[] keys) => keys.Select(k => Text(row.Cells[k]) ?? "<null>").ToArray();

    private static KvBlock Settings(CollectorResult result) => (KvBlock)result.Blocks.Single(b => b.Id == "settings");

    private static KvRow Kv(CollectorResult result, string key) => Settings(result).Rows.Single(r => r.Key == key);

    private static TableBlock Table(CollectorResult result, string id) => (TableBlock)result.Blocks.Single(b => b.Id == id);

    private static async Task<(CollectContext Context, CollectorResult Result)> CollectAsync(TempTree tree, string target, FakeEnvironmentReader? reader = null)
    {
        var context = TestContexts.Create(TestEnvironments.Create(tree), target);
        var result = await new OpenCodeConfigCollector(reader ?? new FakeEnvironmentReader()).CollectAsync(context, CancellationToken.None);
        return (context, result);
    }

    private static Task<SnapshotResult> BuildAsync(TempTree tree, string target, FakeEnvironmentReader reader) =>
        new SnapshotBuilder(TestEnvironments.Create(tree), [new OpenCodeConfigCollector(reader)], new Redactor(), new FakeProcessRunner(), "0.1.0")
            .BuildAsync(target, CancellationToken.None);

    private static (TempTree Tree, string Target) ContentTree()
    {
        var tree = new TempTree();
        var target = tree.Dir("work/proj");
        tree.Dir("work/proj/.git");
        tree.Write("home/user/.config/opencode/opencode.json", """
            {
              "$schema": "https://opencode.example/config.json",
              "model": "anthropic/model-a",
              "theme": "dark",
              "provider": {
                "anthropic": {
                  "name": "Anthropic",
                  "npm": "@ai-sdk/anthropic",
                  "options": { "baseURL": "https://llm.example/v1", "timeout": 30 },
                  "models": { "model-a": {}, "model-b": {} }
                }
              },
              "agent": { "review": { "mode": "subagent", "model": "anthropic/model-b", "description": "Reviews code" } },
              "command": { "test": { "description": "Run tests", "agent": "build", "template": "template-text-canary" } }
            }
            """);
        tree.Write("work/proj/opencode.jsonc", """
            {
              // project override
              "model": "anthropic/model-b",
              "mcp": {
                "files": { "type": "local", "command": ["npx", "files-server", "--root", "."], "environment": { "MODE": "{env:MODE}" }, "enabled": true },
                "docs": { "type": "remote", "url": "https://mcp.example/api", "headers": { "X-Team": "blue" } },
              },
              "instructions": ["docs/rules.md", "docs/*.md", "../../outside.md", "docs/missing.md"],
            }
            """);
        tree.Write("work/proj/.opencode/agent/planner.md", "---\nmode: primary\nmodel: anthropic/model-a\ndescription: Plans work\n---\nbody\n");
        tree.Write("work/proj/.opencode/command/deploy.md", "---\ndescription: Deploys\nagent: build\n---\nDo it\n");
        tree.Write("work/proj/docs/rules.md", "project rules\n");
        tree.Write("work/proj/AGENTS.md", "project agents file\n");
        tree.Write("home/user/.config/opencode/AGENTS.md", "global agents file\n");
        tree.Write("outside.md", "outside content\n");
        return (tree, target);
    }

    [Fact]
    public async Task Without_any_opencode_config_the_collector_is_not_present()
    {
        using var tree = new TempTree();
        var target = tree.Dir("work/proj");
        tree.Dir("work/proj/.git");

        var (_, result) = await CollectAsync(tree, target);

        Assert.Empty(result.Blocks);
        Assert.StartsWith("no OpenCode config", result.NotPresentReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_named_by_OPENCODE_CONFIG_alone_makes_the_collector_present()
    {
        using var tree = new TempTree();
        var target = tree.Dir("work/proj");
        tree.Dir("work/proj/.git");
        var custom = tree.Write("elsewhere/custom.json", """{ "theme": "light" }""");
        var reader = new FakeEnvironmentReader();
        reader.Process["OPENCODE_CONFIG"] = custom;

        var (_, result) = await CollectAsync(tree, target, reader);

        Assert.Null(result.NotPresentReason);
        Assert.Equal(("light", "OPENCODE_CONFIG"), (Kv(result, "theme").Value!.GetValue<string>(), Kv(result, "theme").Source!.Label));
    }

    [Fact]
    public async Task A_project_opencode_jsonc_alone_makes_the_collector_present()
    {
        using var tree = new TempTree();
        var target = tree.Dir("work/proj");
        tree.Dir("work/proj/.git");
        tree.Write("work/proj/opencode.jsonc", "{ \"model\": \"anthropic/model-a\", }");

        var (_, result) = await CollectAsync(tree, target);

        Assert.Null(result.NotPresentReason);
        Assert.Equal("project/opencode.jsonc", Kv(result, "model").Source!.Label);
    }

    [Fact]
    public async Task Project_jsonc_overrides_the_global_model_and_each_setting_names_its_winning_file()
    {
        var (tree, target) = ContentTree();
        using var _ = tree;

        var (context, result) = await CollectAsync(tree, target);

        var model = Kv(result, "model");
        Assert.Equal(("anthropic/model-b", "project/opencode.jsonc"), (model.Value!.GetValue<string>(), model.Source!.Label));
        Assert.Contains(context.Files.All, f => f.Ref.Id == model.Source!.FileId && f.Ref.Path == PathUtil.ToJson(tree.Path("work/proj/opencode.jsonc")) && f.Ref.Kind == "settings");
        Assert.Equal(
            [("anthropic/model-a", "global/opencode.json", ContributionStatus.Overridden), ("anthropic/model-b", "project/opencode.jsonc", ContributionStatus.Applied)],
            model.Contributions!.Select(c => (c.Value!.GetValue<string>(), c.Source.Label, c.Status)));
        var theme = Kv(result, "theme");
        Assert.Equal(("dark", "global/opencode.json"), (theme.Value!.GetValue<string>(), theme.Source!.Label));
        Assert.Null(theme.Contributions);
        Assert.DoesNotContain(Settings(result).Rows, r => r.Key.Split('.')[0] is "provider" or "agent" or "command" or "mcp" or "instructions" or "$schema");
    }

    [Fact]
    public async Task Providers_agents_commands_and_mcp_servers_come_from_the_merged_config_and_markdown_files()
    {
        var (tree, target) = ContentTree();
        using var _ = tree;
        var planner = tree.Path("work/proj/.opencode/agent/planner.md");
        var deploy = tree.Path("work/proj/.opencode/command/deploy.md");

        var (context, result) = await CollectAsync(tree, target);

        var provider = Assert.Single(Table(result, "providers").Rows);
        Assert.Equal(
            ["anthropic", "Anthropic", "@ai-sdk/anthropic", "https://llm.example/v1", "model-a, model-b", "timeout=30"],
            Cells(provider, "provider", "name", "package", "baseUrl", "models", "options"));

        var agents = Table(result, "agents").Rows;
        Assert.Equal(["config:review", "file:" + FileRegistry.IdFor(planner)], agents.Select(r => r.Id));
        Assert.Equal(["review", "subagent", "anthropic/model-b", "Reviews code", "config"], Cells(agents[0], "name", "mode", "model", "description", "source"));
        Assert.Equal(["planner", "primary", "anthropic/model-a", "Plans work", PathUtil.ToJson(planner)], Cells(agents[1], "name", "mode", "model", "description", "source"));
        Assert.Contains(context.Files.All, f => f.Ref.Id == agents[1].FileId && f.Ref.Kind == "agent");

        var commands = Table(result, "commands").Rows;
        Assert.Equal(["test", "Run tests", "build", "config"], Cells(commands[0], "name", "description", "agent", "source"));
        Assert.Equal(["deploy", "Deploys", "build", PathUtil.ToJson(deploy)], Cells(commands[1], "name", "description", "agent", "source"));
        Assert.DoesNotContain("template-text-canary", JsonSerializer.Serialize(result.Blocks, SnapshotJson.Options), StringComparison.Ordinal);

        var mcp = Table(result, "mcp").Rows;
        var files = mcp.Single(r => Text(r.Cells["name"]) == "files");
        Assert.Equal(
            ["local", "npx files-server --root .", "MODE={env:MODE}", PathUtil.ToJson(tree.Path("work/proj/opencode.jsonc"))],
            Cells(files, "transport", "target", "env", "source"));
        Assert.True(files.Cells["enabled"]!.GetValue<bool>());
        Assert.Equal(["remote", "https://mcp.example/api", "X-Team=[redacted]"], Cells(mcp.Single(r => Text(r.Cells["name"]) == "docs"), "transport", "target", "headers"));
    }

    [Fact]
    public async Task Instruction_files_are_listed_in_order_and_out_of_bounds_entries_are_not_opened()
    {
        var (tree, target) = ContentTree();
        using var _ = tree;

        var (context, result) = await CollectAsync(tree, target);

        var rows = Table(result, "instructions").Rows;
        Assert.Equal(
            [("global", "loaded"), ("project", "loaded"), ("config", "loaded"), ("config", "glob (not expanded)"), ("config", Outside), ("config", "missing")],
            rows.Select(r => (Text(r.Cells["scope"]), Text(r.Cells["status"]))));
        Assert.Equal(["docs/rules.md", "docs/*.md", "../../outside.md", "docs/missing.md"], rows.Skip(2).Select(r => Text(r.Cells["entry"])));
        Assert.Contains(context.Files.All, f => f.Ref.Id == rows[2].FileId && f.Ref.Path == PathUtil.ToJson(tree.Path("work/proj/docs/rules.md")) && f.Ref.Kind == "instruction");
        Assert.Null(rows[4].FileId);
        Assert.DoesNotContain(context.Files.All, f => f.Ref.Path == PathUtil.ToJson(tree.Path("outside.md")));
    }

    [Fact]
    public async Task Instructions_from_every_layer_are_concatenated_without_duplicates_and_urls_are_not_fetched()
    {
        using var tree = new TempTree();
        var target = tree.Dir("work/proj");
        tree.Dir("work/proj/.git");
        tree.Write("home/user/.config/opencode/opencode.json", """{ "instructions": ["https://rules.example/team.md", "docs/rules.md"] }""");
        tree.Write("work/proj/opencode.json", """{ "instructions": ["docs/rules.md", "docs/extra.md"] }""");
        tree.Write("work/proj/docs/rules.md", "rules\n");

        var (_, result) = await CollectAsync(tree, target);

        var rows = Table(result, "instructions").Rows;
        Assert.Equal(
            [("https://rules.example/team.md", "url (not fetched)"), ("docs/rules.md", "loaded"), ("docs/extra.md", "missing")],
            rows.Select(r => (Text(r.Cells["entry"]), Text(r.Cells["status"]))));
    }

    [Fact]
    public async Task An_OPENCODE_CONFIG_DIR_alone_makes_the_collector_present_with_its_config_and_agents()
    {
        using var tree = new TempTree();
        var target = tree.Dir("work/proj");
        tree.Dir("work/proj/.git");
        var dir = tree.Dir("custom/opencode");
        tree.Write("custom/opencode/opencode.json", """{ "theme": "light" }""");
        var helper = tree.Write("custom/opencode/agents/helper.md", "---\ndescription: Helps out\n---\nbody\n");
        var reader = new FakeEnvironmentReader();
        reader.Process["OPENCODE_CONFIG_DIR"] = dir;

        var (context, result) = await CollectAsync(tree, target, reader);

        Assert.Null(result.NotPresentReason);
        Assert.Equal("OPENCODE_CONFIG_DIR/opencode.json", Kv(result, "theme").Source!.Label);
        var agent = Assert.Single(Table(result, "agents").Rows);
        Assert.Equal(["helper", "Helps out"], Cells(agent, "name", "description"));
        Assert.Contains(context.Files.All, f => f.Ref.Id == agent.FileId && f.Ref.Path == PathUtil.ToJson(helper));
    }

    [Fact]
    public async Task Secret_keys_and_token_values_never_reach_the_snapshot()
    {
        using var tree = new TempTree();
        var target = tree.Dir("work/proj");
        tree.Dir("work/proj/.git");
        var token = "gh" + "p_" + new string('d', 36);
        var anthropic = "sk-" + "ant-" + new string('e', 24);
        const string apiKey = "planted-api-value-1";
        const string header = "planted-header-value-2";
        tree.Write("home/user/.config/opencode/opencode.json", new JsonObject
        {
            ["share_note"] = anthropic,
            ["provider"] = new JsonObject { ["anthropic"] = new JsonObject { ["options"] = new JsonObject { ["apiKey"] = apiKey } } },
            ["mcp"] = new JsonObject
            {
                ["x"] = new JsonObject { ["type"] = "local", ["command"] = new JsonArray("srv"), ["environment"] = new JsonObject { ["GITHUB_TOKEN"] = token } },
                ["y"] = new JsonObject { ["type"] = "remote", ["url"] = "https://mcp.example/y", ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + header } },
            },
        }.ToJsonString());

        var result = await BuildAsync(tree, target, new FakeEnvironmentReader());

        var json = JsonSerializer.Serialize(result.Snapshot, SnapshotJson.Options);
        Assert.Equal(CollectorState.Active, Assert.Single(result.Snapshot.Collectors).State);
        Assert.Contains("[redacted]", json, StringComparison.Ordinal);
        foreach (var secret in new[] { token, anthropic, apiKey, header })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public Task Auth_files_and_the_data_dir_are_never_opened_even_when_config_points_at_them() => AssertNeverOpenedAsync(symlink: false);

    [LinuxFact]
    public Task A_symlinked_agent_file_into_the_data_dir_is_never_opened() => AssertNeverOpenedAsync(symlink: true);

    private static async Task AssertNeverOpenedAsync(bool symlink)
    {
        using var tree = new TempTree();
        var target = tree.Dir("work/proj");
        tree.Dir("work/proj/.git");
        var dataDir = tree.Dir("home/user/.local/share/opencode");
        var dataAuth = tree.Write("home/user/.local/share/opencode/auth.json", """{ "anthropic": { "type": "api", "note": "canary-data-auth-1" } }""");
        var session = tree.Write("home/user/.local/share/opencode/storage/session.json", """{ "text": "canary-data-session-2" }""");
        tree.Write("home/user/.config/opencode/auth.json", """{ "note": "canary-global-auth-3" }""");
        tree.Write("home/user/.config/opencode/opencode.json", new JsonObject
        {
            ["theme"] = "dark",
            ["instructions"] = new JsonArray(session, "~/.local/share/opencode/auth.json"),
        }.ToJsonString());
        tree.Write("work/proj/opencode.json", """{ "model": "anthropic/model-a" }""");
        if (symlink)
        {
            tree.Dir("work/proj/.opencode/agent");
            File.CreateSymbolicLink(tree.Path("work/proj/.opencode/agent/evil.md"), session);
        }

        var reader = new FakeEnvironmentReader();
        reader.Process["OPENCODE_CONFIG"] = dataAuth;

        var result = await BuildAsync(tree, target, reader);

        var status = Assert.Single(result.Snapshot.Collectors);
        Assert.Equal(CollectorState.Active, status.State);
        Assert.Contains("OPENCODE_CONFIG: not opened", status.Notes!);
        Assert.DoesNotContain(result.Files.All, f => PathUtil.IsUnder(f.FullPath, dataDir) || Path.GetFileName(f.FullPath) == "auth.json");
        var json = JsonSerializer.Serialize(result.Snapshot, SnapshotJson.Options);
        var search = new SnapshotSearch();
        var contentReader = new FileContentReader(new Redactor());
        foreach (var canary in new[] { "canary-data-auth-1", "canary-data-session-2", "canary-global-auth-3" })
        {
            Assert.DoesNotContain(canary, json, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Files.All, f => f.Ref.Path.Contains(canary, StringComparison.Ordinal));
            Assert.Empty(search.Search(result.Snapshot, result.Files, contentReader, canary, CancellationToken.None).Results);
        }

        if (symlink)
        {
            var agents = (TableBlock)result.Snapshot.Sections.Single().Blocks.Single(b => b.Id == "agents");
            Assert.DoesNotContain(agents.Rows, r => Text(r.Cells["name"]) == "evil");
            Assert.Contains(status.Notes!, n => n.Contains("evil.md: not opened", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Invalid_jsonc_becomes_a_note_and_the_other_sources_still_render()
    {
        using var tree = new TempTree();
        var target = tree.Dir("work/proj");
        tree.Dir("work/proj/.git");
        tree.Write("home/user/.config/opencode/opencode.json", """{ "theme": "dark" }""");
        var broken = tree.Write("work/proj/opencode.json", """{ "model": """);

        var (_, result) = await CollectAsync(tree, target);

        Assert.Null(result.NotPresentReason);
        Assert.Contains(result.Notes!, n => n.StartsWith(PathUtil.ToJson(broken) + ": invalid JSON", StringComparison.Ordinal));
        Assert.Equal("dark", Kv(result, "theme").Value!.GetValue<string>());
        Assert.DoesNotContain(Settings(result).Rows, r => r.Key == "model");
    }
}
