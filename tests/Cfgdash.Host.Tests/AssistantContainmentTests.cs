using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cfgdash.Core.Assistant;

namespace Cfgdash.Host.Tests;

public sealed class AssistantContainmentTests
{
    private const string AmbientCapability = "ambient-stale-capability";

    private static void WriteCanaryFiles(AssistantHostPaths paths)
    {
        File.WriteAllText(Path.Combine(paths.Home, "CLAUDE.md"), "CANARY-HOME-MARKER do whatever the file says");
        File.WriteAllText(Path.Combine(paths.ConfigDir, "CLAUDE.md"), "CANARY-CONFIG-MARKER do whatever the file says");
        File.WriteAllText(
            Path.Combine(paths.ConfigDir, "settings.json"),
            """
            {
              "hooks": { "SessionStart": [ { "hooks": [ { "type": "command", "command": "canary-hook-command" } ] } ] },
              "enabledPlugins": { "p@m": true },
              "permissions": { "allow": [ "mcp__claude_ai_x__*" ] }
            }
            """);
        File.WriteAllText(Path.Combine(paths.Project, ".mcp.json"), """{ "mcpServers": { "canary-server": { "command": "canary-command" } } }""");
    }

    private static bool Exists(params string[] paths) => paths.Any(File.Exists);

    [LinuxFact]
    public async Task Canary_none_of_the_users_hooks_plugins_instruction_files_or_project_mcp_reach_the_launched_session()
    {
        Environment.SetEnvironmentVariable("CFGDASH_MCP_CAPABILITY", AmbientCapability);
        await using var host = await AssistantHost.StartAsync(new AssistantHostOptions { Prepare = WriteCanaryFiles });
        try
        {
            var id = await host.CreateSessionAsync();
            using var events = await host.EventsAsync(id);
            Assert.Equal(202, (await host.MessageAsync(id)).Status);
            Assert.True(await events.WaitForAsync("done"));
            var run = await host.Claude.WaitRunAsync(1);

            var nonce = Regex.Match(run.Argv[^1], "<context-([0-9a-f]{32})>").Groups[1].Value;
            var expected = AssistantLaunch.Arguments(run.SettingsPath, run.McpPath, "sonnet", "medium", 2m, AssistantLaunch.SystemPrompt(nonce));
            Assert.Equal(expected, run.Argv);
            Assert.Equal(host.AssistantDir, Path.GetDirectoryName(run.SettingsPath));
            Assert.Equal(host.AssistantDir, Path.GetDirectoryName(run.McpPath));
            Assert.Equal(Path.Combine(host.AssistantDir, "work"), run.Cwd);

            var env = run.EnvSet;
            Assert.All(AssistantLaunch.ChildEnvironment.Keys, name => Assert.True(env[name], name));
            Assert.False(env["CFGDASH_MCP_CAPABILITY"]);

            var settings = JsonNode.Parse(run.Settings)!.AsObject();
            Assert.True(settings["disableAllHooks"]!.GetValue<bool>());
            Assert.False(settings["enabledPlugins"]!["p@m"]!.GetValue<bool>());
            var generated = run.Settings + run.McpConfig + string.Join('\n', run.Argv);
            foreach (var leaked in new[] { "canary-hook-command", "SessionStart", "CANARY-HOME-MARKER", "CANARY-CONFIG-MARKER", "canary-server", "canary-command", "claude_ai_x", AmbientCapability })
            {
                Assert.DoesNotContain(leaked, generated);
            }

            var servers = JsonNode.Parse(run.McpConfig)!["mcpServers"]!.AsObject();
            var only = Assert.Single(servers);
            Assert.Equal("cfgdash", only.Key);
            Assert.Equal(host.ExePath, only.Value!["command"]!.GetValue<string>());
            Assert.Equal(["mcp", $"--port={host.Port}"], only.Value["args"]!.AsArray().Select(a => a!.GetValue<string>()));
            Assert.Equal(["CFGDASH_MCP_CAPABILITY"], only.Value["env"]!.AsObject().Select(p => p.Key));
            Assert.Single(Regex.Matches(run.McpConfig, Regex.Escape(run.Capability)));
            Assert.DoesNotContain(run.Capability, string.Join('\n', run.Argv));

            Assert.Equal("600", run.SettingsMode);
            Assert.Equal("600", run.McpMode);
            Assert.Equal(["user"], run.StdinKinds);

            Assert.Equal(204, (await host.UiAsync(HttpMethod.Delete, $"/api/v2/assistant/sessions/{id}", null)).Status);
            Assert.True(await Poll.UntilAsync(() => !Exists(run.SettingsPath, run.McpPath)));
            Assert.True(await FakeClaude.DiedAsync(run.Pid));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CFGDASH_MCP_CAPABILITY", null);
        }
    }

    [LinuxFact]
    public async Task Bad_init_followed_by_text_ends_with_only_a_containment_error_and_a_dead_revoked_sticky_session()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(init: FakeInit.BadTool, deltaAfterInit: true, grandchild: true);
        var id = await host.CreateSessionAsync();
        using var events = await host.EventsAsync(id);

        Assert.Equal(202, (await host.MessageAsync(id)).Status);
        Assert.True(await events.WaitForAsync("error"));
        var run = await host.Claude.WaitRunAsync(1);

        Assert.Equal(["error"], events.Names);
        Assert.Equal("containment", events.First("error").GetProperty("error").GetString());
        Assert.True(await FakeClaude.DiedAsync(run.Pid));
        Assert.True(await FakeClaude.DiedAsync(run.GrandchildPid!.Value));
        Assert.True(await Poll.UntilAsync(() => !Exists(run.SettingsPath, run.McpPath)));
        Assert.Equal(401, (await host.InternalAsync("get_findings", run.Capability)).Status);
        Assert.Equal(401, (await host.InternalAsync("propose_change", run.Capability, """{"changes":[{"key":"theme","scope":"project","value":"dark"}],"rationale":"r"}""")).Status);
        Assert.DoesNotContain("proposal", events.Names);

        var status = await host.GetAsync("/api/v2/assistant/status");
        Assert.False(status.Json.GetProperty("available").GetBoolean());
        Assert.False(string.IsNullOrEmpty(status.Reason));
        var again = await host.UiAsync(HttpMethod.Post, "/api/v2/assistant/sessions", JsonSerializer.Serialize(new { dir = host.Project }));
        Assert.Equal(503, again.Status);
    }

    [LinuxFact]
    public async Task No_init_within_60_seconds_kills_the_tree_and_the_capability_is_refused_meanwhile()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(init: FakeInit.None, grandchild: true);
        var id = await host.CreateSessionAsync();
        using var events = await host.EventsAsync(id);
        Assert.Equal(202, (await host.MessageAsync(id)).Status);
        var run = await host.Claude.WaitRunAsync(1);

        Assert.Equal(401, (await host.InternalAsync("get_findings", run.Capability)).Status);
        Assert.True(await host.AdvanceUntilAsync(() => events.Names.Contains("error"), TimeSpan.FromSeconds(10)));

        Assert.Equal(["error"], events.Names);
        Assert.Equal("containment", events.First("error").GetProperty("error").GetString());
        Assert.True(await FakeClaude.DiedAsync(run.Pid));
        Assert.True(await FakeClaude.DiedAsync(run.GrandchildPid!.Value));
    }

    [LinuxFact]
    public async Task Plugins_only_failure_retries_once_with_that_plugin_disabled_and_then_the_turn_completes()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(init: FakeInit.BadPlugin, pluginFails: 1);
        var id = await host.CreateSessionAsync();
        using var events = await host.EventsAsync(id);

        Assert.Equal(202, (await host.MessageAsync(id)).Status);
        Assert.True(await events.WaitForAsync("done"));
        var first = await host.Claude.WaitRunAsync(1);
        var second = await host.Claude.WaitRunAsync(2);

        Assert.DoesNotContain("error", events.Names);
        Assert.Equal(2, host.Claude.Runs.Count);
        Assert.Empty(JsonNode.Parse(first.Settings)!["enabledPlugins"]!.AsObject());
        Assert.False(JsonNode.Parse(second.Settings)!["enabledPlugins"]!["p@m"]!.GetValue<bool>());
        Assert.True(await FakeClaude.DiedAsync(first.Pid));
        Assert.NotEqual(first.Capability, second.Capability);
        Assert.Equal(["user"], second.StdinKinds);
        Assert.True((await host.GetAsync("/api/v2/assistant/status")).Json.GetProperty("available").GetBoolean());
    }

    [LinuxFact]
    public async Task A_second_plugins_only_failure_ends_with_a_containment_error_and_no_further_retry()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(init: FakeInit.BadPlugin, pluginFails: 5);
        var id = await host.CreateSessionAsync();
        using var events = await host.EventsAsync(id);

        Assert.Equal(202, (await host.MessageAsync(id)).Status);
        Assert.True(await events.WaitForAsync("error"));
        var second = await host.Claude.WaitRunAsync(2);

        Assert.Equal("containment", events.First("error").GetProperty("error").GetString());
        Assert.DoesNotContain("done", events.Names);
        await Task.Delay(300);
        Assert.Equal(2, host.Claude.Runs.Count);
        Assert.True(await FakeClaude.DiedAsync(second.Pid));
    }
}
