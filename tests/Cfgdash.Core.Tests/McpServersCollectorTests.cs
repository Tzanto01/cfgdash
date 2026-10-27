using System.Text.Json.Nodes;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

/// <summary>Servers in every scope: .claude.json (user + two spellings of the target as local keys), project .mcp.json and one enabled plugin.</summary>
public sealed class McpFixture : IDisposable
{
    public McpFixture()
    {
        Secret = "gh" + "p_" + new string('c', 36);
        Target = Tree.Dir("proj");
        Tree.Dir("proj/.git");
        Config = Tree.Dir("config/.claude");
        var variant = OperatingSystem.IsWindows() ? Target.ToUpperInvariant() : PathUtil.ToJson(Target) + "/";
        Tree.Write("config/.claude/.claude.json", new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["shared"] = new JsonObject { ["command"] = "user-cmd" },
                ["remote"] = new JsonObject { ["type"] = "http", ["url"] = "https://mcp.example/api", ["headers"] = new JsonObject { ["X-Team"] = "blue" } },
            },
            ["projects"] = new JsonObject
            {
                [variant] = new JsonObject
                {
                    ["mcpServers"] = new JsonObject
                    {
                        ["shared"] = new JsonObject
                        {
                            ["command"] = "local-cmd",
                            ["args"] = new JsonArray("--token", Secret),
                            ["env"] = new JsonObject { ["API_KEY"] = Secret, ["MODE"] = "${MODE}" },
                        },
                    },
                },
                [Target] = new JsonObject
                {
                    ["mcpServers"] = new JsonObject
                    {
                        ["shared"] = new JsonObject { ["command"] = "second-spelling" },
                        ["second"] = new JsonObject { ["command"] = "b" },
                    },
                },
            },
        }.ToJsonString());
        ProjectMcp = Tree.Write("proj/.mcp.json", """
            { "mcpServers": { "shared": { "command": "project-cmd" }, "tools": { "command": "node", "args": ["${HOME}/server.js"] } } }
            """);
        var plugin = Tree.Dir("plugins/tool");
        var otherProject = Tree.Dir("plugins/elsewhere");
        PluginMcp = Tree.Write("plugins/tool/.mcp.json", """{ "mcpServers": { "plug": { "command": "${CLAUDE_PLUGIN_ROOT}/srv" } } }""");
        Tree.Write("plugins/elsewhere/.mcp.json", """{ "mcpServers": { "wrong": { "command": "x" } } }""");
        Tree.Write("plugins/off/.mcp.json", """{ "mcpServers": { "disabled": { "command": "x" } } }""");
        Tree.Write("config/.claude/plugins/installed_plugins.json", new JsonObject
        {
            ["version"] = 2,
            ["plugins"] = new JsonObject
            {
                ["tool@mkt"] = new JsonArray(
                    new JsonObject { ["scope"] = "project", ["projectPath"] = Tree.Path("other-project"), ["installPath"] = otherProject },
                    new JsonObject { ["scope"] = "user", ["installPath"] = plugin, ["version"] = "1.0.0" }),
                ["off@mkt"] = new JsonObject { ["scope"] = "user", ["installPath"] = Tree.Path("plugins/off") },
            },
        }.ToJsonString());
        Tree.Write("config/.claude/settings.json", """{ "enabledPlugins": { "tool@mkt": true, "off@mkt": false } }""");
    }

    public TempTree Tree { get; } = new();

    public string Secret { get; }

    public string Target { get; }

    public string Config { get; }

    public string ProjectMcp { get; }

    public string PluginMcp { get; }

    public async Task<(TableBlock Servers, HarnessSnapshot Snapshot)> BuildAsync(string? managedDir = null)
    {
        var locations = new PlatformLocations(managedDir, null, _ => null);
        var builder = new SnapshotBuilder(
            TestContexts.WithConfigDir(Tree, Config),
            [new McpServersCollector(new SettingsLayers(locations), locations)],
            new Redactor(),
            new FakeProcessRunner(),
            "0.1.0");
        var snapshot = (await builder.BuildAsync(Target, CancellationToken.None)).Snapshot;
        return ((TableBlock)Assert.Single(Assert.Single(snapshot.Sections).Blocks), snapshot);
    }

    public void Dispose() => Tree.Dispose();
}

public sealed class McpServersCollectorTests(McpFixture fixture) : IClassFixture<McpFixture>
{
    private static string? Text(JsonNode? cell) => cell is JsonObject obj ? obj["text"]?.GetValue<string>() : cell?.GetValue<string>();

    private static TableRow Row(TableBlock servers, string id) => servers.Rows.Single(r => r.Id == id);

    [Fact]
    public async Task Higher_scope_wins_by_name_and_lower_entries_are_overridden()
    {
        var (servers, _) = await fixture.BuildAsync();

        Assert.Equal(
            [
                ("local:shared", "applied"), ("local:second", "applied"), ("project:shared", "overridden"), ("project:tools", "applied"),
                ("user:shared", "overridden"), ("user:remote", "applied"), ("plugin:tool@mkt:plug", "applied"),
            ],
            servers.Rows.Select(r => (r.Id, Text(r.Cells["status"]))));
        Assert.Contains("overridden", Row(servers, "user:shared").Flags!);
        Assert.Equal("local-cmd --token [redacted]", Text(Row(servers, "local:shared").Cells["target"]));
    }

    [Fact]
    public async Task Env_and_header_values_are_redacted_and_variable_references_are_kept_as_written()
    {
        var (servers, snapshot) = await fixture.BuildAsync();

        Assert.Equal("API_KEY=[redacted]; MODE=${MODE}", Text(Row(servers, "local:shared").Cells["env"]));
        Assert.Equal("X-Team=[redacted]", Text(Row(servers, "user:remote").Cells["headers"]));
        Assert.Equal(("http", "https://mcp.example/api"), (Text(Row(servers, "user:remote").Cells["transport"]), Text(Row(servers, "user:remote").Cells["target"])));
        Assert.Equal(("stdio", "node ${HOME}/server.js"), (Text(Row(servers, "project:tools").Cells["transport"]), Text(Row(servers, "project:tools").Cells["target"])));
        Assert.Equal("${CLAUDE_PLUGIN_ROOT}/srv", Text(Row(servers, "plugin:tool@mkt:plug").Cells["target"]));
        Assert.DoesNotContain(fixture.Secret, System.Text.Json.JsonSerializer.Serialize(snapshot, SnapshotJson.Options));
    }

    [Fact]
    public async Task Sources_point_at_registered_mcp_files_and_claude_json_is_never_registered()
    {
        var (servers, snapshot) = await fixture.BuildAsync();

        Assert.Equal(".claude.json (local)", Text(Row(servers, "local:shared").Cells["source"]));
        Assert.Equal(PathUtil.ToJson(fixture.ProjectMcp), Text(Row(servers, "project:tools").Cells["source"]));
        Assert.Equal(PathUtil.ToJson(fixture.PluginMcp), Text(Row(servers, "plugin:tool@mkt:plug").Cells["source"]));
        Assert.Contains(snapshot.Files, f => f.Path == PathUtil.ToJson(fixture.ProjectMcp) && f.Kind == "mcp");
        Assert.DoesNotContain(snapshot.Files, f => f.Path.EndsWith(".claude.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Managed_mcp_file_replaces_every_other_server()
    {
        using var managed = new TempTree();
        managed.Write("managed-mcp.json", """{ "mcpServers": { "corp": { "url": "https://corp.example/mcp" } } }""");

        var (servers, snapshot) = await fixture.BuildAsync(managed.Root);

        Assert.Equal(("managed:corp", "applied"), (servers.Rows[0].Id, Text(servers.Rows[0].Cells["status"])));
        Assert.All(servers.Rows.Skip(1), r => Assert.Equal("overridden", Text(r.Cells["status"])));
        Assert.Contains(snapshot.Collectors[0].Notes!, n => n.Contains("managed-mcp.json", StringComparison.Ordinal));
    }
}
