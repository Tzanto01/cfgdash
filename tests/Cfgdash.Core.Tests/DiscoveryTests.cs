using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Environment;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class DiscoveryTests
{
    private static string Json(string path) => path.Replace('\\', '/');

    private static DiscoveryOptions Options(TempTree tree) =>
        new(OsFamily.Windows, tree.Dir("home/user"), tree.Path("cfg/overrides.json"), null);

    private static Task<MachineEnvironment> Discover(TempTree tree, FakeEnvironmentReader env, FakeProcessRunner? processes = null) =>
        MachineEnvironmentDiscovery.DiscoverAsync(env, processes ?? new FakeProcessRunner(), Options(tree), CancellationToken.None);

    private static ContextPath PathOf(MachineEnvironment environment, string key) => environment.Paths.Single(p => p.Key == key);

    private static void MakeConfigRepo(TempTree tree, string repo) => tree.Write(repo + "/.claude/hooks/lib/agent-core.ps1", "# core");

    [Fact]
    public void Tool_probe_ignores_relative_path_entries()
    {
        using var tree = new TempTree();
        var relative = "probe-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(relative);
        try
        {
            File.WriteAllText(Path.Combine(relative, "git"), string.Empty);
            File.WriteAllText(Path.Combine(relative, "git.exe"), string.Empty);
            var env = new FakeEnvironmentReader();
            env.Process["PATH"] = relative + Path.PathSeparator + tree.Dir("bin");

            var tools = ToolProbe.Probe(env, OperatingSystem.IsWindows() ? OsFamily.Windows : OsFamily.Linux, tree.Dir("home/user"), null);

            Assert.False(tools.ContainsKey("git"));
        }
        finally
        {
            Directory.Delete(relative, recursive: true);
        }
    }

    [Fact]
    public async Task Process_env_wins_over_user_scope_which_wins_over_machine_scope()
    {
        using var tree = new TempTree();
        var env = new FakeEnvironmentReader();
        env.Process["CLAUDE_CONFIG_DIR"] = tree.Dir("process");
        env.User["CLAUDE_CONFIG_DIR"] = tree.Dir("user");
        env.Machine["CLAUDE_CONFIG_DIR"] = tree.Dir("machine");
        env.User["WORKSPACE_ROOT"] = tree.Dir("ws-user");
        env.Machine["WORKSPACE_ROOT"] = tree.Dir("ws-machine");
        env.Machine["ENGRAM_DATA_DIR"] = tree.Path("engram-missing");

        var environment = await Discover(tree, env);

        Assert.Equal((Json(tree.Path("process")), "env:CLAUDE_CONFIG_DIR"), (PathOf(environment, PathKeys.ClaudeConfigDir).Value, PathOf(environment, PathKeys.ClaudeConfigDir).Source));
        Assert.Equal((Json(tree.Path("ws-user")), "env-user:WORKSPACE_ROOT"), (PathOf(environment, PathKeys.WorkspaceRoot).Value, PathOf(environment, PathKeys.WorkspaceRoot).Source));
        var engram = PathOf(environment, PathKeys.EngramDataDir);
        Assert.Equal((Json(tree.Path("engram-missing")), "env-machine:ENGRAM_DATA_DIR", false), (engram.Value, engram.Source, engram.Exists));
    }

    [Fact]
    public async Task Defaults_without_env_use_home_and_leave_the_rest_absent()
    {
        using var tree = new TempTree();

        var environment = await Discover(tree, new FakeEnvironmentReader());

        Assert.Equal([PathKeys.WorkspaceRoot, PathKeys.ClaudeConfigDir, PathKeys.EngramDataDir, PathKeys.ConfigRepo, PathKeys.CompanyLayer], environment.Paths.Select(p => p.Key));
        var config = PathOf(environment, PathKeys.ClaudeConfigDir);
        Assert.Equal((Json(tree.Path("home/user/.claude")), "default:home", false), (config.Value, config.Source, config.Exists));
        foreach (var key in new[] { PathKeys.WorkspaceRoot, PathKeys.EngramDataDir, PathKeys.ConfigRepo, PathKeys.CompanyLayer })
        {
            Assert.Equal(new ContextPath(key, null, null, false), PathOf(environment, key));
        }
    }

    [Fact]
    public async Task Config_repo_is_derived_from_the_config_dir_parent_and_the_workspace_from_the_repo_parent()
    {
        using var tree = new TempTree();
        MakeConfigRepo(tree, "ws/config");
        var env = new FakeEnvironmentReader();
        env.Process["CLAUDE_CONFIG_DIR"] = tree.Path("ws/config/.claude");

        var environment = await Discover(tree, env);

        Assert.Equal((Json(tree.Path("ws/config")), "derived:claudeConfigDir-parent", true), (PathOf(environment, PathKeys.ConfigRepo).Value, PathOf(environment, PathKeys.ConfigRepo).Source, PathOf(environment, PathKeys.ConfigRepo).Exists));
        Assert.Equal((Json(tree.Path("ws")), "derived:configRepo-parent"), (PathOf(environment, PathKeys.WorkspaceRoot).Value, PathOf(environment, PathKeys.WorkspaceRoot).Source));
    }

    [Fact]
    public async Task Config_repo_falls_back_to_workspace_root_config()
    {
        using var tree = new TempTree();
        MakeConfigRepo(tree, "ws/config");
        var env = new FakeEnvironmentReader();
        env.Process["WORKSPACE_ROOT"] = tree.Path("ws");

        var environment = await Discover(tree, env);

        Assert.Equal((Json(tree.Path("ws/config")), "derived:workspaceRoot-config"), (PathOf(environment, PathKeys.ConfigRepo).Value, PathOf(environment, PathKeys.ConfigRepo).Source));
        Assert.Equal("env:WORKSPACE_ROOT", PathOf(environment, PathKeys.WorkspaceRoot).Source);
        Assert.Equal("default:home", PathOf(environment, PathKeys.ClaudeConfigDir).Source);
    }

    [Fact]
    public async Task Override_file_values_win_and_carry_their_source()
    {
        using var tree = new TempTree();
        var env = new FakeEnvironmentReader();
        env.Process["CLAUDE_CONFIG_DIR"] = tree.Dir("from-env");
        var overrides = new JsonObject
        {
            ["claudeConfigDir"] = tree.Dir("from-override"),
            ["engramDataDir"] = tree.Dir("engram"),
            ["allowedLogins"] = new JsonArray("user@example"),
            ["allowedTags"] = new JsonArray("tag:pc"),
        };
        tree.Write("cfg/overrides.json", overrides.ToJsonString());

        var environment = await Discover(tree, env);

        Assert.Equal((Json(tree.Path("from-override")), "override-file"), (PathOf(environment, PathKeys.ClaudeConfigDir).Value, PathOf(environment, PathKeys.ClaudeConfigDir).Source));
        Assert.Equal("override-file", PathOf(environment, PathKeys.EngramDataDir).Source);
        Assert.Equal(["user@example"], environment.AllowedLogins);
        Assert.Equal(["tag:pc"], environment.AllowedTags);
    }

    [Fact]
    public async Task Invalid_override_file_is_ignored()
    {
        using var tree = new TempTree();
        tree.Write("cfg/overrides.json", "{ \"claudeConfigDir\": ");

        var environment = await Discover(tree, new FakeEnvironmentReader());

        Assert.Equal("default:home", PathOf(environment, PathKeys.ClaudeConfigDir).Source);
    }

    [Fact]
    public async Task Company_paths_psd1_is_parsed_as_text_with_relative_entries_under_the_workspace()
    {
        using var tree = new TempTree();
        MakeConfigRepo(tree, "ws/config");
        tree.Dir("ws/acme");
        tree.Dir("ws/beta/sub");
        var absolute = tree.Dir("elsewhere/corp");
        tree.Dir("ws/commented");
        tree.Write("ws/config/.claude/hooks/company-paths.psd1", $"@{{\n    CompanyPathPrefixes = @(\n        'missing'\n        # 'commented'\n        'acme',\n        'beta\\sub',\n        \"{absolute}\"\n    )\n}}\n");
        var env = new FakeEnvironmentReader();
        env.Process["CLAUDE_CONFIG_DIR"] = tree.Path("ws/config/.claude");

        var environment = await Discover(tree, env);

        Assert.Equal([Json(tree.Path("ws/acme")), Json(tree.Path("ws/beta/sub")), Json(absolute)], environment.CompanyPrefixes);
        var company = PathOf(environment, PathKeys.CompanyLayer);
        Assert.Equal((Json(tree.Path("ws/acme")), "file:company-paths.psd1", true), (company.Value, company.Source, company.Exists));
    }

    public static TheoryData<bool, string?, string> TailnetCases() => new()
    {
        { false, "user@example", "user@example" },
        { true, null, "user@github" },
    };

    [Theory]
    [MemberData(nameof(TailnetCases))]
    public async Task Tailnet_status_is_parsed_and_allowed_login_derived(bool tagged, string? selfLogin, string allowed)
    {
        using var tree = new TempTree();
        var tailscale = tree.Write("bin/tailscale.exe", string.Empty);
        var env = new FakeEnvironmentReader();
        env.Process["PATH"] = tree.Path("bin");
        var status = new JsonObject
        {
            ["BackendState"] = "Running",
            ["MagicDNSSuffix"] = "example.ts.net",
            ["CertDomains"] = new JsonArray("devbox.example.ts.net"),
            ["Self"] = new JsonObject
            {
                ["DNSName"] = "devbox.example.ts.net.",
                ["UserID"] = 123,
                ["Tags"] = tagged ? new JsonArray("tag:pc") : null,
            },
            ["CurrentTailnet"] = new JsonObject { ["Name"] = "user.github", ["MagicDNSEnabled"] = true },
            ["User"] = new JsonObject { ["123"] = new JsonObject { ["LoginName"] = "user@example" } },
        };
        var processes = new FakeProcessRunner().On(tailscale, ["status", "--json"], status.ToJsonString());

        var environment = await Discover(tree, env, processes);

        var tailnet = environment.Tailnet!;
        Assert.Equal(("Running", "devbox.example.ts.net", "user.github", "example.ts.net", true), (tailnet.BackendState, tailnet.DnsName, tailnet.TailnetName, tailnet.MagicDnsSuffix, tailnet.MagicDnsEnabled));
        Assert.Equal(["devbox.example.ts.net"], tailnet.CertDomains);
        Assert.Equal(selfLogin, tailnet.SelfUserLogin);
        Assert.Equal(tagged ? new[] { "tag:pc" } : [], tailnet.Tags);
        Assert.Equal([allowed], environment.AllowedLogins);
    }

    [Fact]
    public async Task Unparseable_tailscale_output_leaves_tailnet_null()
    {
        using var tree = new TempTree();
        var tailscale = tree.Write("bin/tailscale.exe", string.Empty);
        var env = new FakeEnvironmentReader();
        env.Process["PATH"] = tree.Path("bin");

        var environment = await Discover(tree, env, new FakeProcessRunner().On(tailscale, ["status", "--json"], "not json"));

        Assert.Equal(tailscale, environment.Tools["tailscale"]);
        Assert.Null(environment.Tailnet);
        Assert.Empty(environment.AllowedLogins);
    }

    [Fact]
    public async Task Known_project_dirs_are_existing_deduplicated_and_tolerate_case_duplicate_keys()
    {
        using var tree = new TempTree();
        var project = tree.Dir("projects/app");
        var forward = Json(project);
        var keys = new[] { forward, project, forward.ToUpperInvariant(), Json(tree.Path("projects/gone")) };
        var json = "{\"projects\":{" + string.Join(",", keys.Select(k => JsonSerializer.Serialize(k) + ":{\"allowedTools\":[]}")) + "},\"oauthAccount\":{\"x\":1}}";
        tree.Write("home/user/.claude.json", json);

        var environment = await Discover(tree, new FakeEnvironmentReader());

        Assert.Equal([forward], environment.KnownProjectDirs);
    }
}
