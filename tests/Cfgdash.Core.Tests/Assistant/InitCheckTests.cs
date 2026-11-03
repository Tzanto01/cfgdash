using System.Text.Json.Nodes;
using Cfgdash.Core.Assistant;

namespace Cfgdash.Core.Tests.Assistant;

public sealed class InitCheckTests
{
    private static JsonObject GoodInit() =>
        JsonNode.Parse("""
            {
              "type": "system",
              "subtype": "init",
              "tools": [
                "mcp__cfgdash__get_snapshot",
                "mcp__cfgdash__get_findings",
                "mcp__cfgdash__get_file",
                "mcp__cfgdash__search",
                "mcp__cfgdash__propose_change",
                "mcp__cfgdash__propose_file_edit"
              ],
              "mcp_servers": [ { "name": "cfgdash", "status": "connected" } ],
              "agents": [],
              "skills": [],
              "slash_commands": [],
              "plugins": []
            }
            """)!.AsObject();

    public static IEnumerable<object[]> Violations() =>
    [
        ["extra tool", new Action<JsonObject>(o => o["tools"]!.AsArray().Add("Bash"))],
        ["duplicate tool replacing one", new Action<JsonObject>(o => o["tools"]!.AsArray()[4] = "mcp__cfgdash__search")],
        ["duplicate tool on top", new Action<JsonObject>(o => o["tools"]!.AsArray().Add("mcp__cfgdash__search"))],
        ["missing tool", new Action<JsonObject>(o => o["tools"]!.AsArray().RemoveAt(0))],
        ["the five tools without propose_file_edit", new Action<JsonObject>(o => o["tools"]!.AsArray().RemoveAt(5))],
        ["no tools", new Action<JsonObject>(o => o["tools"] = new JsonArray())],
        ["tools field missing", new Action<JsonObject>(o => o.Remove("tools"))],
        ["second server", new Action<JsonObject>(o => o["mcp_servers"]!.AsArray().Add(new JsonObject { ["name"] = "other", ["status"] = "connected" }))],
        ["wrong server", new Action<JsonObject>(o => o["mcp_servers"] = new JsonArray(new JsonObject { ["name"] = "other" }))],
        ["no server", new Action<JsonObject>(o => o["mcp_servers"] = new JsonArray())],
        ["mcp_servers missing", new Action<JsonObject>(o => o.Remove("mcp_servers"))],
        ["agents present", new Action<JsonObject>(o => o["agents"] = new JsonArray("reviewer"))],
        ["skills present", new Action<JsonObject>(o => o["skills"] = new JsonArray("lint"))],
        ["slash_commands present", new Action<JsonObject>(o => o["slash_commands"] = new JsonArray("compact"))],
        ["agents missing", new Action<JsonObject>(o => o.Remove("agents"))],
        ["skills missing", new Action<JsonObject>(o => o.Remove("skills"))],
        ["slash_commands missing", new Action<JsonObject>(o => o.Remove("slash_commands"))],
        ["plugins missing", new Action<JsonObject>(o => o.Remove("plugins"))],
        ["plugins not an array", new Action<JsonObject>(o => o["plugins"] = "none")],
    ];

    [Fact]
    public void The_exact_good_init_is_ok()
    {
        var verdict = InitCheck.Evaluate(GoodInit());

        Assert.True(verdict.Ok);
        Assert.False(verdict.PluginsOnly);
        Assert.Empty(verdict.PluginIds);
        Assert.Null(verdict.Reason);
    }

    [Theory]
    [MemberData(nameof(Violations))]
    public void Any_deviation_fails_closed_without_the_plugins_retry(string name, Action<JsonObject> mutate)
    {
        var init = GoodInit();
        mutate(init);

        var verdict = InitCheck.Evaluate(init);

        Assert.False(verdict.Ok, name);
        Assert.False(verdict.PluginsOnly, name);
        Assert.Empty(verdict.PluginIds);
        Assert.False(string.IsNullOrEmpty(verdict.Reason), name);
    }

    [Fact]
    public void Non_empty_plugins_alone_ask_for_the_retry_with_the_plugin_sources()
    {
        var init = GoodInit();
        init["plugins"] = new JsonArray(
            new JsonObject { ["name"] = "alpha", ["source"] = "alpha@market" },
            new JsonObject { ["name"] = "beta" });

        var verdict = InitCheck.Evaluate(init);

        Assert.False(verdict.Ok);
        Assert.True(verdict.PluginsOnly);
        Assert.Equal(["alpha@market", "beta"], verdict.PluginIds);
    }

    [Fact]
    public void Plugins_together_with_another_violation_are_not_plugins_only()
    {
        var init = GoodInit();
        init["plugins"] = new JsonArray(new JsonObject { ["source"] = "alpha@market" });
        init["agents"] = new JsonArray("reviewer");

        var verdict = InitCheck.Evaluate(init);

        Assert.False(verdict.Ok);
        Assert.False(verdict.PluginsOnly);
        Assert.Empty(verdict.PluginIds);
    }

    [Fact]
    public void A_plugin_entry_without_source_or_name_cannot_be_retried()
    {
        var init = GoodInit();
        init["plugins"] = new JsonArray(new JsonObject { ["version"] = "1" });

        var verdict = InitCheck.Evaluate(init);

        Assert.False(verdict.Ok);
        Assert.False(verdict.PluginsOnly);
    }
}
