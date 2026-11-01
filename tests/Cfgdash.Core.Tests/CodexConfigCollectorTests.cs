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

public sealed class CodexConfigCollectorTests
{
    private const string Codex = "home/user/.codex";

    private static async Task<(CollectorResult Result, CollectContext Context)> Collect(TempTree tree, string target, FakeEnvironmentReader? reader = null)
    {
        var context = TestContexts.Create(TestEnvironments.Create(tree), target);
        var result = await new CodexConfigCollector(reader ?? new FakeEnvironmentReader()).CollectAsync(context, CancellationToken.None);
        return (result, context);
    }

    private static Task<SnapshotResult> Build(TempTree tree, string target) =>
        new SnapshotBuilder(TestEnvironments.Create(tree), [new CodexConfigCollector(new FakeEnvironmentReader())], new Redactor(), new FakeProcessRunner(), "0.1.0")
            .BuildAsync(target, CancellationToken.None);

    private static TableBlock Table(CollectorResult result, string id) => result.Blocks.OfType<TableBlock>().Single(b => b.Id == id);

    private static KvBlock Settings(CollectorResult result) => result.Blocks.OfType<KvBlock>().Single(b => b.Id == "settings");

    [Fact]
    public async Task Reports_not_present_without_CODEX_HOME_or_home_codex_dir()
    {
        using var tree = new TempTree();

        var (result, _) = await Collect(tree, tree.Dir("proj"));

        Assert.Equal("no Codex home (CODEX_HOME or ~/.codex)", result.NotPresentReason);
        Assert.Empty(result.Blocks);
    }

    [Fact]
    public async Task CODEX_HOME_is_used_over_home_codex_dir()
    {
        using var tree = new TempTree();
        tree.Write(Codex + "/config.toml", "model = \"default-model\"\n");
        tree.Write("custom/codex/config.toml", "model = \"custom-model\"\n");
        var reader = new FakeEnvironmentReader();
        reader.Process["CODEX_HOME"] = tree.Path("custom/codex");

        var (custom, _) = await Collect(tree, tree.Dir("proj"), reader);
        reader.Process["CODEX_HOME"] = tree.Path("custom/missing");
        var (missing, _) = await Collect(tree, tree.Dir("proj"), reader);

        Assert.Equal("custom-model", (string?)Settings(custom).Rows.Single(r => r.Key == "model").Value);
        Assert.NotNull(missing.NotPresentReason);
    }

    [Fact]
    public async Task Lists_settings_mcp_servers_instructions_in_load_order_prompts_and_skills()
    {
        using var tree = new TempTree();
        tree.Write(Codex + "/config.toml", """
            model = "gpt-test"
            approval_policy = "on-request"
            sandbox_mode = "workspace-write"
            project_doc_fallback_filenames = ["../x.md", "TEAM.md"]

            [profiles.fast]
            model = "gpt-fast"

            [mcp_servers.local]
            command = "npx"
            args = ["-y", "server-pkg"]
            env = { MODE = "${MODE}" }

            [mcp_servers.remote]
            url = "https://mcp.example/api"
            enabled = false
            """);
        var globalOverride = tree.Write(Codex + "/AGENTS.override.md", "global override");
        tree.Write(Codex + "/AGENTS.md", "global");
        tree.Write("AGENTS.md", "above the repo");
        tree.Dir("proj/.git");
        var projectRoot = tree.Write("proj/AGENTS.md", "root");
        var subOverride = tree.Write("proj/sub/AGENTS.override.md", "sub override");
        tree.Write("proj/sub/AGENTS.md", "sub");
        tree.Write("proj/sub/x.md", "traversal target");
        var fallback = tree.Write("proj/sub/deep/TEAM.md", "team");
        tree.Write(Codex + "/prompts/review.md", "---\ndescription: Review the diff\n---\nbody\n");
        tree.Write(Codex + "/prompts/nested/hidden.md", "not top level");
        tree.Write(Codex + "/skills/writer/SKILL.md", "---\nname: doc-writer\ndescription: Writes docs\n---\nbody\n");
        tree.Write(Codex + "/skills/group/plain/SKILL.md", "no frontmatter");
        tree.Write(Codex + "/skills/a/b/c/d/e/SKILL.md", "too deep");

        var (result, context) = await Collect(tree, tree.Path("proj/sub/deep"));

        var keys = Settings(result).Rows.Select(r => r.Key).ToList();
        foreach (var key in new[] { "model", "approval_policy", "sandbox_mode", "profiles.fast.model" })
        {
            Assert.Contains(key, keys);
        }

        Assert.DoesNotContain(keys, k => k.StartsWith("mcp_servers", StringComparison.Ordinal));

        var mcp = Table(result, "mcp").Rows.ToDictionary(r => r.Id);
        Assert.Equal("stdio", (string?)mcp["local"].Cells["transport"]);
        Assert.Equal("npx -y server-pkg", (string?)mcp["local"].Cells["target"]);
        Assert.Equal("MODE=${MODE}", (string?)mcp["local"].Cells["env"]);
        Assert.Equal("http", (string?)mcp["remote"].Cells["transport"]);
        Assert.Equal("https://mcp.example/api", (string?)mcp["remote"].Cells["target"]);
        Assert.False((bool)mcp["remote"].Cells["enabled"]!);

        var instructions = Table(result, "instructions").Rows;
        Assert.Equal(
            [FileRegistry.IdFor(globalOverride), FileRegistry.IdFor(projectRoot), FileRegistry.IdFor(subOverride), FileRegistry.IdFor(fallback)],
            instructions.Select(r => r.FileId));
        Assert.Equal(["global", "project", "project", "project"], instructions.Select(r => (string?)r.Cells["scope"]));
        Assert.Contains(result.Notes!, n => n.Contains("project_doc_fallback_filenames", StringComparison.Ordinal));

        var prompt = Assert.Single(Table(result, "prompts").Rows);
        Assert.Equal("review", (string?)prompt.Cells["name"]);
        Assert.Equal("Review the diff", (string?)prompt.Cells["description"]);
        Assert.Equal(["plain", "doc-writer"], Table(result, "skills").Rows.Select(r => (string?)r.Cells["name"]));

        var fileIds = result.Blocks.OfType<TableBlock>().SelectMany(b => b.Rows).Select(r => r.FileId)
            .Concat(Settings(result).Rows.Select(r => r.Source?.FileId)).ToList();
        Assert.All(fileIds, id => Assert.True(id is not null && context.Files.TryGet(id, out _)));
    }

    [Fact]
    public async Task Secrets_in_config_toml_never_reach_the_snapshot()
    {
        using var tree = new TempTree();
        var apiKey = "plain-api-key-value-0042";
        var github = "gh" + "p_" + new string('d', 36);
        var bearer = "bearer-value-0042";
        var anthropic = "sk-" + "ant-" + new string('e', 24);
        tree.Write(Codex + "/config.toml", $$"""
            api_key = "{{apiKey}}"
            note = "{{anthropic}}"

            [mcp_servers.x]
            command = "srv"
            env = { GITHUB_TOKEN = "{{github}}" }
            http_headers = { Authorization = "Bearer {{bearer}}" }
            """);
        var target = tree.Dir("proj");

        var result = await Build(tree, target);

        var json = JsonSerializer.Serialize(result.Snapshot, SnapshotJson.Options);
        Assert.Equal(CollectorState.Active, Assert.Single(result.Snapshot.Collectors).State);
        Assert.Contains("[redacted]", json, StringComparison.Ordinal);
        foreach (var secret in new[] { apiKey, github, bearer, anthropic })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }
    }

    private static string[] WriteNeverOpened(TempTree tree)
    {
        tree.Write(Codex + "/auth.json", """{ "token": "canary-auth-q7" }""");
        tree.Write(Codex + "/history.jsonl", """{ "text": "canary-history-q7" }""");
        tree.Write(Codex + "/sessions/2026/x.jsonl", """{ "text": "canary-session-q7" }""");
        tree.Write(Codex + "/log/codex.log", "canary-log-q7");
        tree.Write(Codex + "/config.toml", """
            model = "gpt-test"
            project_doc_fallback_filenames = ["auth.json", "history.jsonl"]
            """);
        return ["canary-auth-q7", "canary-history-q7", "canary-session-q7", "canary-log-q7"];
    }

    private static void AssertNeverOpened(TempTree tree, SnapshotResult result, IEnumerable<string> canaries)
    {
        var codex = tree.Path(Codex);
        string[] forbidden = [Path.Combine(codex, "auth.json"), Path.Combine(codex, "history.jsonl"), Path.Combine(codex, "sessions"), Path.Combine(codex, "log")];
        var json = JsonSerializer.Serialize(result.Snapshot, SnapshotJson.Options);
        var reader = new FileContentReader(new Redactor());

        Assert.Equal(CollectorState.Active, Assert.Single(result.Snapshot.Collectors).State);
        Assert.Contains(result.Files.All, f => PathUtil.PathEquals(f.FullPath, Path.Combine(codex, "config.toml")));
        Assert.DoesNotContain(result.Files.All, f => forbidden.Any(p => PathUtil.IsUnder(f.FullPath, p) || PathUtil.IsUnder(PathUtil.RealPath(f.FullPath), p)));
        foreach (var canary in canaries)
        {
            Assert.DoesNotContain(canary, json, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Files.All, f => f.FullPath.Contains(canary, StringComparison.Ordinal));
            Assert.Empty(new SnapshotSearch().Search(result.Snapshot, result.Files, reader, canary, CancellationToken.None).Results);
        }
    }

    [Fact]
    public async Task Auth_history_sessions_and_log_are_never_opened_even_when_named_as_fallbacks()
    {
        using var tree = new TempTree();
        var canaries = WriteNeverOpened(tree);

        var result = await Build(tree, tree.Path(Codex));

        AssertNeverOpened(tree, result, canaries);
    }

    [LinuxFact]
    public async Task Symlinks_to_never_opened_files_are_not_followed()
    {
        using var tree = new TempTree();
        var canaries = WriteNeverOpened(tree);
        var codex = tree.Path(Codex);
        tree.Write(Codex + "/sessions/linked/SKILL.md", "---\ndescription: canary-linked-q7\n---\n");
        tree.Dir(Codex + "/skills/evil");
        tree.Dir(Codex + "/prompts");
        File.CreateSymbolicLink(Path.Combine(codex, "skills/evil/SKILL.md"), Path.Combine(codex, "auth.json"));
        File.CreateSymbolicLink(Path.Combine(codex, "prompts/evil.md"), Path.Combine(codex, "history.jsonl"));
        Directory.CreateSymbolicLink(Path.Combine(codex, "skills/linked"), Path.Combine(codex, "sessions/linked"));

        var result = await Build(tree, tree.Dir("proj"));

        AssertNeverOpened(tree, result, [.. canaries, "canary-linked-q7"]);
        var section = Assert.Single(result.Snapshot.Sections);
        Assert.All(section.Blocks.OfType<TableBlock>().Where(b => b.Id is "skills" or "prompts"), b => Assert.Empty(b.Rows));
    }

    [Fact]
    public async Task Invalid_toml_is_a_note_and_the_rest_still_renders()
    {
        using var tree = new TempTree();
        tree.Write(Codex + "/config.toml", "model = \n[[[");
        var global = tree.Write(Codex + "/AGENTS.md", "global");

        var (result, _) = await Collect(tree, tree.Dir("proj"));

        Assert.Null(result.NotPresentReason);
        Assert.Contains(result.Notes!, n => n.Contains("invalid TOML", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Blocks, b => b is KvBlock);
        Assert.Equal(FileRegistry.IdFor(global), Assert.Single(Table(result, "instructions").Rows).FileId);
    }
}
