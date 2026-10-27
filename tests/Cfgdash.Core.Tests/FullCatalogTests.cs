using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class FullCatalogTests
{
    private static readonly string Token = "gh" + "p_" + new string('a', 36);
    private static readonly string EnvSecret = "planted-" + "env-value-1";
    private static readonly string McpEnvSecret = "planted-" + "mcp-env-value-2";
    private static readonly string HeaderSecret = "planted-" + "header-value-3";
    private static readonly string FlagSecret = "planted-" + "flag-value-4";

    private static string Hook(string command) =>
        new JsonObject { ["type"] = "command", ["command"] = command }.ToJsonString();

    private static (TempTree Tree, string Target, Func<Task<SnapshotResult>> Build) CreateHarness(bool broken = false, bool secrets = false)
    {
        var tree = new TempTree();
        var target = tree.Dir("proj");
        tree.Dir("proj/.git");
        var config = tree.Dir("config/.claude");

        tree.Write("config/.claude/settings.json", $$"""
            {
              "model": "opus",
              "env": { "NOTE_TEXT": "{{(secrets ? Token : "plain")}}", "API_TOKEN": "{{(secrets ? EnvSecret : "plain")}}" },
              "hooks": { "PreToolUse": [ { "matcher": "Bash", "hooks": [ {{Hook("pwsh -File \"$CLAUDE_CONFIG_DIR/hooks/guard.ps1\"")}} ] } ] }
            }
            """);
        tree.Write("config/.claude/hooks/guard.ps1", "<#\nBlocks risky commands.\n#>\n");
        tree.Write("config/.claude/CLAUDE.md", "# User rules\n@rules/extra.md\n" + (secrets ? "Deploy with " + Token + "\n" : string.Empty));
        tree.Write("config/.claude/rules/extra.md", "extra rule\n");
        tree.Write("config/.claude/rules/scoped.md", "---\npaths: [\"src/**\"]\n---\nscoped rule\n");
        tree.Write("config/.claude/agents/helper.md", "---\nname: helper\ndescription: helps\n---\nbody\n");
        tree.Write("config/.claude/skills/fmt/SKILL.md", "---\ndescription: formats\n---\nbody\n");
        tree.Write("config/.claude/commands/run.md", "Run it\n");

        tree.Write("proj/.claude/settings.json", $$"""
            { "hooks": { "Stop": [ { "hooks": [ {{Hook(secrets ? "notify --token " + FlagSecret : "notify")}} ] } ] } }
            """);
        tree.Write("proj/.claude/settings.local.json", broken ? "{ \"model\": " : """{ "model": "sonnet" }""");
        tree.Write("proj/.mcp.json", $$"""
            {
              "mcpServers": {
                "local": { "command": "node", "env": { "DB_PASSWORD": "{{(secrets ? McpEnvSecret : "plain")}}" } },
                "remote": { "type": "http", "url": "https://mcp.example/api", "headers": { "X-Team": "{{(secrets ? HeaderSecret : "plain")}}" } }
              }
            }
            """);
        if (broken)
        {
            tree.Write("config/.claude/agents/bad.md", "---\nname: [unclosed\ndescription: : :\n---\nbody\n");
        }

        var environment = TestEnvironments.Create(tree, paths: [TestEnvironments.ContextPath(PathKeys.ClaudeConfigDir, config)]);
        var locations = new PlatformLocations(tree.Dir("managed"), null, _ => null);
        var catalog = CollectorCatalog.Create(new FakeEnvironmentReader(), locations);
        var builder = new SnapshotBuilder(environment, catalog, new Redactor(), new FakeProcessRunner(), "0.1.0");
        return (tree, target, () => builder.BuildAsync(target, CancellationToken.None));
    }

    private static List<string> Fingerprint(string root) =>
        Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => File.Exists(p) ? p + "|" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))) : p + "|dir")
            .ToList();

    [Fact]
    public async Task Full_catalog_build_leaves_every_file_and_the_directory_listing_unchanged()
    {
        var (tree, _, build) = CreateHarness(secrets: true);
        using var _ = tree;
        var before = Fingerprint(tree.Root);

        var result = await build();

        Assert.NotEmpty(result.Snapshot.Files);
        Assert.Equal(before, Fingerprint(tree.Root));
    }

    [Fact]
    public async Task A_broken_settings_layer_and_agent_do_not_take_other_collectors_down()
    {
        var clean = CreateHarness();
        var broken = CreateHarness(broken: true);
        using var _1 = clean.Tree;
        using var _2 = broken.Tree;

        var healthy = (await clean.Build()).Snapshot;
        var snapshot = (await broken.Build()).Snapshot;

        Assert.Equal(healthy.Collectors.Select(c => c.Id), snapshot.Collectors.Select(c => c.Id));
        Assert.Equal(healthy.Collectors.Select(c => c.State), snapshot.Collectors.Select(c => c.State));
        Assert.All(
            new[] { "settings.effective", "agents.definitions", "instructions.chain", "hooks.registered", "mcp.servers" },
            id => Assert.Equal(CollectorState.Active, snapshot.Collectors.Single(c => c.Id == id).State));

        var layers = snapshot.Sections.Single(s => s.CollectorId == "settings.effective").Blocks.OfType<TableBlock>().Single(b => b.Id == "layers");
        Assert.Contains("error", layers.Rows.Single(r => r.Id == "local").Flags!);
        Assert.DoesNotContain(layers.Rows.Where(r => r.Id != "local"), r => r.Flags?.Contains("error") == true);

        var agents = snapshot.Sections.Single(s => s.CollectorId == "agents.definitions").Blocks.OfType<TableBlock>().Single(b => b.Id == "agents");
        Assert.Contains(agents.Rows, r => r.Flags?.Contains("error") == true);
        Assert.Contains(agents.Rows, r => r.Flags?.Contains("error") != true);
    }

    [Fact]
    public async Task Planted_secrets_appear_neither_in_the_serialized_snapshot_nor_in_any_registered_file()
    {
        var (tree, _, build) = CreateHarness(secrets: true);
        using var _ = tree;

        var result = await build();

        var contents = new List<string> { JsonSerializer.Serialize(result.Snapshot, SnapshotJson.Options) };
        var reader = new FileContentReader(new Redactor());
        foreach (var file in result.Files.All)
        {
            contents.Add((await reader.ReadAsync(file, CancellationToken.None)).Content);
        }

        Assert.True(result.Files.All.Count >= 5);
        Assert.Contains("[redacted]", contents[0]);
        foreach (var secret in new[] { Token, EnvSecret, McpEnvSecret, HeaderSecret, FlagSecret })
        {
            Assert.DoesNotContain(contents, c => c.Contains(secret, StringComparison.Ordinal));
        }
    }
}
