using System.Text.Json.Nodes;
using Cfgdash.Core.Assistant;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests.Assistant;

public sealed class AssistantLaunchTests : IDisposable
{
    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    [Fact]
    public void Arguments_are_exactly_the_rev_2_1_flag_list_in_flag_equals_value_form()
    {
        var args = AssistantLaunch.Arguments("/tmp/s/settings.json", "/tmp/s/mcp.json", "opus", "high", 1.5m, "prompt text");

        Assert.Equal(
        [
            "--print",
            "--input-format=stream-json",
            "--output-format=stream-json",
            "--include-partial-messages",
            "--verbose",
            "--no-session-persistence",
            "--setting-sources=",
            "--settings=/tmp/s/settings.json",
            "--mcp-config=/tmp/s/mcp.json",
            "--strict-mcp-config",
            "--tools=",
            "--allowedTools=mcp__cfgdash__*",
            "--permission-mode=dontAsk",
            "--disable-slash-commands",
            "--max-budget-usd=1.5",
            "--model=opus",
            "--effort=high",
            "--append-system-prompt=prompt text",
        ], args);
    }

    [Theory]
    [InlineData("opus\n", "high")]
    [InlineData("opus ", "high")]
    [InlineData("--dangerously", "high")]
    [InlineData("op us", "high")]
    [InlineData("", "high")]
    [InlineData("opus", "ultra")]
    [InlineData("opus", "High")]
    [InlineData("opus", "")]
    public void An_invalid_model_or_effort_throws(string model, string effort)
    {
        Assert.Throws<ArgumentException>(() => AssistantLaunch.Arguments("s", "m", model, effort, 1m, "p"));
    }

    [Theory]
    [InlineData("opus")]
    [InlineData("claude-opus-4.5")]
    [InlineData("sonnet[1m]")]
    public void Plausible_model_names_are_accepted(string model)
    {
        Assert.Contains("--model=" + model, AssistantLaunch.Arguments("s", "m", model, "low", 1m, "p"));
    }

    [Fact]
    public void A_non_positive_budget_throws()
    {
        Assert.Throws<ArgumentException>(() => AssistantLaunch.Arguments("s", "m", "opus", "low", 0m, "p"));
    }

    [Fact]
    public void The_settings_json_switches_off_every_ambient_source_and_each_given_plugin()
    {
        var settings = JsonNode.Parse(AssistantLaunch.SettingsJson(["alpha@market", "beta@market", "alpha@market"]))!.AsObject();

        Assert.True((bool)settings["disableAllHooks"]!);
        Assert.True((bool)settings["disableClaudeAiConnectors"]!);
        foreach (var key in new[] { "autoMemoryEnabled", "includeGitInstructions", "enableAllProjectMcpServers", "syncClaudeAiPlugins", "syncClaudeAiSkills" })
        {
            Assert.False((bool)settings[key]!, key);
        }

        var plugins = settings["enabledPlugins"]!.AsObject();
        Assert.Equal(["alpha@market", "beta@market"], plugins.Select(p => p.Key).ToArray());
        Assert.All(plugins, p => Assert.False((bool)p.Value!));
    }

    [Fact]
    public void The_settings_json_has_an_empty_plugin_map_without_plugins()
    {
        var settings = JsonNode.Parse(AssistantLaunch.SettingsJson([]))!.AsObject();

        Assert.Empty(settings["enabledPlugins"]!.AsObject());
    }

    private MachineEnvironment Env(string? configDir) =>
        TestEnvironments.Create(tree, configDir is null ? [] : [TestEnvironments.ContextPath(PathKeys.ClaudeConfigDir, configDir)]);

    [Fact]
    public void User_plugin_ids_are_the_enabled_plugins_keys_of_the_user_settings()
    {
        var dir = tree.Dir("cfg");
        tree.Write("cfg/settings.json", """{"model":"opus","enabledPlugins":{"alpha@market":true,"beta@market":false}}""");

        Assert.Equal(["alpha@market", "beta@market"], AssistantLaunch.UserPluginIds(Env(dir)));
    }

    [Fact]
    public void User_plugin_ids_are_empty_when_the_file_is_absent_unparseable_or_has_no_plugins()
    {
        var absent = tree.Dir("cfg-absent");
        var broken = tree.Dir("cfg-broken");
        tree.Write("cfg-broken/settings.json", "{ not json");
        var none = tree.Dir("cfg-none");
        tree.Write("cfg-none/settings.json", """{"model":"opus"}""");

        Assert.Empty(AssistantLaunch.UserPluginIds(Env(absent)));
        Assert.Empty(AssistantLaunch.UserPluginIds(Env(broken)));
        Assert.Empty(AssistantLaunch.UserPluginIds(Env(none)));
        Assert.Empty(AssistantLaunch.UserPluginIds(Env(null)));
    }

    [Fact]
    public void The_mcp_config_puts_the_capability_only_in_env_and_never_in_args()
    {
        var capability = "cap-" + new string('c', 24);

        var json = AssistantLaunch.McpConfigJson("/opt/cfgdash/cfgdash", 4711, capability);

        var servers = JsonNode.Parse(json)!["mcpServers"]!.AsObject();
        var server = Assert.Single(servers).Value!.AsObject();
        Assert.Equal("cfgdash", Assert.Single(servers).Key);
        Assert.Equal("stdio", (string?)server["type"]);
        Assert.Equal("/opt/cfgdash/cfgdash", (string?)server["command"]);
        Assert.Equal(["mcp", "--port=4711"], server["args"]!.AsArray().Select(a => (string)a!).ToArray());
        Assert.Equal(capability, (string?)server["env"]!["CFGDASH_MCP_CAPABILITY"]);
        Assert.Single(server["env"]!.AsObject());
        Assert.Equal(1, json.Split(capability).Length - 1);
    }

    [Fact]
    public void The_child_environment_has_exactly_the_four_containment_variables()
    {
        Assert.Equal(
            ["CLAUDE_AGENT_SDK_DISABLE_BUILTIN_AGENTS", "CLAUDE_CODE_DISABLE_AUTO_MEMORY", "CLAUDE_CODE_DISABLE_CLAUDE_MDS", "CLAUDE_CODE_DISABLE_GIT_INSTRUCTIONS"],
            AssistantLaunch.ChildEnvironment.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.All(AssistantLaunch.ChildEnvironment.Values, v => Assert.Equal("1", v));
        Assert.DoesNotContain("CFGDASH_MCP_CAPABILITY", AssistantLaunch.ChildEnvironment.Keys);
    }
}
