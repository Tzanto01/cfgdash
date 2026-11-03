using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Host.Tests;

public sealed class AssistantApplyModeTests
{
    private static string SettingsFile(AssistantHost host) => Path.Combine(host.Project, ".claude", "settings.json");

    private static string Propose(string key, object value) =>
        JsonSerializer.Serialize(new { changes = new[] { new { key, scope = "project", value } }, rationale = "r" });

    private static async Task SetApplyAsync(AssistantHost host, bool on)
    {
        var put = await host.UiAsync(HttpMethod.Put, "/api/v2/cfgdash-settings", JsonSerializer.Serialize(new { assistant = new { applyEnabled = on } }));
        Assert.Equal(200, put.Status);
    }

    private static async Task<(string Id, SseCapture Events, FakeRun Run)> StartTurnAsync(AssistantHost host)
    {
        host.Claude.Configure(turn: FakeTurn.Stall);
        var id = await host.CreateSessionAsync();
        var events = await host.EventsAsync(id);
        await host.MessageAsync(id);
        Assert.True(await events.WaitForAsync("delta"));
        return (id, events, await host.Claude.WaitRunAsync(1));
    }

    [LinuxFact]
    public async Task With_apply_on_propose_change_writes_the_file_emits_applied_and_audits_mode_auto_and_undo_restores_it()
    {
        await using var host = await AssistantHost.StartAsync();
        await SetApplyAsync(host, true);
        var (id, events, run) = await StartTurnAsync(host);
        using var sse = events;

        var tool = await host.InternalAsync("propose_change", run.Capability, Propose("model", "opus"));

        Assert.True(tool.Json.GetProperty("applied").GetBoolean(), tool.Body);
        Assert.False(tool.Json.TryGetProperty("confirm", out _));
        Assert.Contains("opus", tool.Json.GetProperty("files")[0].GetProperty("diff").GetString());
        Assert.Equal("opus", JsonNode.Parse(await File.ReadAllTextAsync(SettingsFile(host)))!["model"]!.GetValue<string>());
        Assert.True(await events.WaitForAsync("applied"));
        Assert.DoesNotContain("proposal", events.Names);
        var applied = events.First("applied");
        var auditId = applied.GetProperty("auditId").GetString()!;
        Assert.Equal(tool.Json.GetProperty("auditId").GetString(), auditId);
        Assert.False(applied.TryGetProperty("confirm", out _));

        var audit = (await host.GetAsync("/api/v2/audit")).Json.GetProperty("entries")[0];
        Assert.Equal(auditId, audit.GetProperty("auditId").GetString());
        Assert.Equal("auto", audit.GetProperty("mode").GetString());
        Assert.Equal("assistant", audit.GetProperty("origin").GetString());
        Assert.Equal(id, audit.GetProperty("sessionId").GetString());

        var revert = await host.UiAsync(HttpMethod.Post, $"/api/v2/audit/{auditId}/revert");
        Assert.Equal(200, revert.Status);
        var undo = await host.UiAsync(HttpMethod.Post, $"/api/v2/proposals/{revert.Json.GetProperty("id").GetString()}/apply", JsonSerializer.Serialize(new { confirm = revert.Json.GetProperty("confirm").GetString() }));
        Assert.Equal(200, undo.Status);
        Assert.False(File.Exists(SettingsFile(host)));
    }

    [LinuxFact]
    public async Task With_apply_on_a_denied_key_and_a_widening_tighten_only_change_are_still_refused_and_write_nothing()
    {
        await using var host = await AssistantHost.StartAsync();
        await SetApplyAsync(host, true);
        var (_, events, run) = await StartTurnAsync(host);
        using var sse = events;

        var denied = await host.InternalAsync("propose_change", run.Capability, Propose("permissions.defaultMode", "acceptEdits"));
        var widened = await host.InternalAsync("propose_change", run.Capability, Propose("permissions.allow", new[] { "Bash" }));

        Assert.Equal("permissions.defaultMode", denied.Json.GetProperty("denied").GetString());
        Assert.True(widened.Json.TryGetProperty("denied", out var widenedKey), widened.Body);
        Assert.Equal("permissions.allow", widenedKey.GetString());
        Assert.False(denied.Json.TryGetProperty("applied", out _));
        Assert.False(File.Exists(SettingsFile(host)));
        Assert.DoesNotContain("applied", events.Names);
        Assert.DoesNotContain((await host.GetAsync("/api/v2/audit")).Json.GetProperty("entries").EnumerateArray(), e => e.TryGetProperty("origin", out _));
    }

    [LinuxFact]
    [UnsupportedOSPlatform("windows")]
    public async Task With_apply_on_an_apply_that_cannot_write_answers_the_error_emits_no_applied_and_keeps_the_file()
    {
        await using var host = await AssistantHost.StartAsync();
        await SetApplyAsync(host, true);
        var claudeDir = Directory.CreateDirectory(Path.Combine(host.Project, ".claude")).FullName;
        await File.WriteAllTextAsync(SettingsFile(host), "{}\n");
        var (_, events, run) = await StartTurnAsync(host);
        using var sse = events;
        File.SetUnixFileMode(claudeDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var tool = await host.InternalAsync("propose_change", run.Capability, Propose("model", "opus"));

            Assert.Equal("write-failed", tool.Json.GetProperty("error").GetString());
            Assert.False(tool.Json.TryGetProperty("applied", out _));
        }
        finally
        {
            File.SetUnixFileMode(claudeDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal("{}\n", await File.ReadAllTextAsync(SettingsFile(host)));
        Assert.DoesNotContain("applied", events.Names);
        Assert.DoesNotContain("proposal", events.Names);
    }

    [LinuxFact]
    public async Task Turning_apply_off_mid_session_makes_the_next_call_a_proposal_that_writes_nothing()
    {
        await using var host = await AssistantHost.StartAsync();
        await SetApplyAsync(host, true);
        var (_, events, run) = await StartTurnAsync(host);
        using var sse = events;
        Assert.True((await host.InternalAsync("propose_change", run.Capability, Propose("model", "opus"))).Json.GetProperty("applied").GetBoolean());

        await SetApplyAsync(host, false);
        var tool = await host.InternalAsync("propose_change", run.Capability, Propose("model", "haiku"));

        Assert.False(tool.Json.TryGetProperty("applied", out _));
        Assert.False(string.IsNullOrEmpty(tool.Json.GetProperty("proposalId").GetString()));
        Assert.True(await events.WaitForAsync("proposal"));
        Assert.Equal("opus", JsonNode.Parse(await File.ReadAllTextAsync(SettingsFile(host)))!["model"]!.GetValue<string>());
    }
}

public sealed class AssistantBudgetToggleTests
{
    [LinuxTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_budget_flag_follows_budget_enabled_and_every_other_flag_stays(bool enabled)
    {
        await using var host = await AssistantHost.StartAsync(new AssistantHostOptions
        {
            Prepare = paths =>
            {
                var configDir = Directory.CreateDirectory(Path.Combine(paths.Root, "config")).FullName;
                File.WriteAllText(Path.Combine(configDir, "config.json"), $$"""{ "assistant": { "maxBudgetUsd": 3.5, "budgetEnabled": {{(enabled ? "true" : "false")}} } }""");
            },
        });
        host.Claude.Configure();

        await host.CreateSessionAsync();
        var argv = (await host.Claude.WaitRunAsync(1)).Argv;

        Assert.Equal(enabled, argv.Contains("--max-budget-usd=3.5"));
        Assert.Equal(enabled, argv.Any(a => a.StartsWith("--max-budget-usd", StringComparison.Ordinal)));
        Assert.Contains("--tools=", argv);
        Assert.Contains("--setting-sources=", argv);
        Assert.Contains("--strict-mcp-config", argv);
        Assert.Contains("--permission-mode=dontAsk", argv);
        Assert.Contains("--no-session-persistence", argv);
    }
}

public sealed class AssistantContextResultsTests
{
    private static readonly IReadOnlyList<Section> Sections =
    [
        new("settings.effective", "settings", "Settings", [new KvBlock("settings.effective/kv", "Effective", [new KvRow("model", "opus", null)])]),
        new("usage.metrics", "usage", "Usage", [new KvBlock("usage.metrics/kv", "Usage", [new KvRow("tokens", 5, null)])]),
        .. HealthSections.MissingScript(),
    ];

    [LinuxFact]
    public async Task Messages_answer_a_state_per_reference_and_only_added_references_reach_the_model()
    {
        await using var host = await AssistantHost.StartAsync(new AssistantHostOptions { Sections = Sections });
        host.Claude.Configure(turn: FakeTurn.Stall);
        var id = await host.CreateSessionAsync();
        object[] context =
        [
            new { kind = "row", collectorId = "settings.effective", blockId = "settings.effective/kv", itemId = "model" },
            new { kind = "row", collectorId = "usage.metrics", blockId = "usage.metrics/kv", itemId = "tokens" },
            new { kind = "file", fileId = "nope" },
            new { kind = "bogus" },
            .. Enumerable.Range(0, 5).Select(_ => (object)new { kind = "finding", findingId = HealthSections.MissingScriptId }),
        ];

        var response = await host.UiAsync(HttpMethod.Post, $"/api/v2/assistant/sessions/{id}/messages", JsonSerializer.Serialize(new { text = "hi", context }));

        Assert.Equal(202, response.Status);
        var states = response.Json.GetProperty("context").EnumerateArray().ToList();
        Assert.Equal(Enumerable.Range(0, 9), states.Select(s => s.GetProperty("index").GetInt32()));
        Assert.Equal(
            ["added", "not-allowed", "not-found", "not-found", "added", "added", "added", "added", "too-many"],
            states.Select(s => s.GetProperty("state").GetString()));
        Assert.Contains("model", states[0].GetProperty("title").GetString());
        Assert.False(states[1].TryGetProperty("title", out _));

        var run = await host.Claude.WaitRunAsync(1);
        Assert.True(await Poll.UntilAsync(() => run.Stdin.Count > 0));
        var line = run.Stdin[0];
        Assert.Contains("settings.effective/kv", line);
        Assert.Contains(HealthSections.MissingScriptId, line);
        Assert.DoesNotContain("usage.metrics", line);
        Assert.DoesNotContain("nope", line);
        Assert.DoesNotContain("bogus", line);
    }

    [LinuxFact]
    public async Task Check_passes_all_of_up_to_50_known_finding_ids_to_the_model()
    {
        var rows = Enumerable.Range(0, 20).Select(i => new TableRow($"SessionStart-{i}-0", new Dictionary<string, JsonNode?> { ["event"] = "SessionStart", ["script"] = $"/home/user/start{i}.ps1" }, ["missing-script"])).ToList();
        Section hooks = new("hooks.registered", "hooks", "Hooks", [new TableBlock("hooks.registered/hooks", "Hooks", [new TableColumn("event", "Event"), new TableColumn("script", "Script")], rows)]);
        await using var host = await AssistantHost.StartAsync(new AssistantHostOptions { Sections = [hooks] });
        host.Claude.Configure(turn: FakeTurn.Stall);
        var id = await host.CreateSessionAsync();
        var ids = Enumerable.Range(0, 20).Select(i => $"missing-script:hooks.registered:hooks.registered/hooks:SessionStart-{i}-0").ToList();

        var response = await host.UiAsync(HttpMethod.Post, $"/api/v2/assistant/sessions/{id}/check", JsonSerializer.Serialize(new { findingIds = ids }));

        Assert.Equal(202, response.Status);
        var run = await host.Claude.WaitRunAsync(1);
        Assert.True(await Poll.UntilAsync(() => run.Stdin.Count > 0));
        Assert.All(ids, findingId => Assert.Contains(findingId, run.Stdin[0]));
    }

    [LinuxFact]
    public async Task Check_limits_the_prompt_to_known_finding_ids_and_ignores_unknown_ones()
    {
        await using var host = await AssistantHost.StartAsync(new AssistantHostOptions { Sections = Sections });
        host.Claude.Configure(turn: FakeTurn.Stall);
        var id = await host.CreateSessionAsync();

        var response = await host.UiAsync(HttpMethod.Post, $"/api/v2/assistant/sessions/{id}/check", JsonSerializer.Serialize(new { findingIds = new[] { HealthSections.MissingScriptId, "unknown:finding" } }));

        Assert.Equal(202, response.Status);
        var run = await host.Claude.WaitRunAsync(1);
        Assert.True(await Poll.UntilAsync(() => run.Stdin.Count > 0));
        Assert.Contains(HealthSections.MissingScriptId, run.Stdin[0]);
        Assert.Contains("Limit the check to these findings", run.Stdin[0]);
        Assert.DoesNotContain("unknown:finding", run.Stdin[0]);
    }

    [LinuxFact]
    public async Task Check_with_only_unknown_finding_ids_answers_400_no_findings_and_starts_no_full_check()
    {
        await using var host = await AssistantHost.StartAsync(new AssistantHostOptions { Sections = Sections });
        host.Claude.Configure(turn: FakeTurn.Stall);
        var id = await host.CreateSessionAsync();

        var response = await host.UiAsync(HttpMethod.Post, $"/api/v2/assistant/sessions/{id}/check", JsonSerializer.Serialize(new { findingIds = new[] { "unknown:finding" } }));

        Assert.Equal(400, response.Status);
        Assert.Equal("no-findings", response.Error);
        var run = await host.Claude.WaitRunAsync(1);
        Assert.False(await Poll.UntilAsync(() => run.Stdin.Count > 0, 1000));
    }

    [LinuxTheory]
    [InlineData("""{"findingIds":"x"}""")]
    [InlineData("""{"findingIds":[1]}""")]
    [InlineData("[]")]
    public async Task Check_with_a_malformed_body_answers_400_and_starts_no_turn(string body)
    {
        await using var host = await AssistantHost.StartAsync(new AssistantHostOptions { Sections = Sections });
        host.Claude.Configure(turn: FakeTurn.Stall);
        var id = await host.CreateSessionAsync();

        var response = await host.UiAsync(HttpMethod.Post, $"/api/v2/assistant/sessions/{id}/check", body);

        Assert.Equal(400, response.Status);
        var run = await host.Claude.WaitRunAsync(1);
        Assert.False(await Poll.UntilAsync(() => run.Stdin.Count > 0, 1000));
    }

    [LinuxFact]
    public async Task Check_with_more_than_50_finding_ids_answers_400()
    {
        await using var host = await AssistantHost.StartAsync(new AssistantHostOptions { Sections = Sections });
        host.Claude.Configure(turn: FakeTurn.Stall);
        var id = await host.CreateSessionAsync();

        var response = await host.UiAsync(HttpMethod.Post, $"/api/v2/assistant/sessions/{id}/check", JsonSerializer.Serialize(new { findingIds = Enumerable.Range(0, 51).Select(i => "f" + i) }));

        Assert.Equal(400, response.Status);
    }
}

public sealed class AssistantProvidersAndErrorsTests
{
    [LinuxFact]
    public async Task Status_lists_the_claude_provider_with_its_verified_models_and_efforts()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure();

        var provider = Assert.Single((await host.GetAsync("/api/v2/assistant/status")).Json.GetProperty("providers").EnumerateArray());

        Assert.Equal("claude", provider.GetProperty("id").GetString());
        Assert.True(provider.GetProperty("available").GetBoolean());
        var models = provider.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
        Assert.Contains("sonnet", models);
        Assert.Contains("opus", models);
        Assert.Contains("haiku", models);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], provider.GetProperty("efforts").EnumerateArray().Select(e => e.GetString()));
    }

    [LinuxFact]
    public async Task A_failed_cli_result_reaches_the_panel_as_a_cfgdash_message_with_a_redacted_detail()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(turn: FakeTurn.ErrorResult);
        var id = await host.CreateSessionAsync();
        using var events = await host.EventsAsync(id);

        await host.MessageAsync(id);
        Assert.True(await events.WaitForAsync("error"));

        var error = events.First("error");
        Assert.Equal("turn-failed", error.GetProperty("error").GetString());
        Assert.Equal("Claude is overloaded right now. Try again in a moment.", error.GetProperty("message").GetString());
        var detail = error.GetProperty("detail").GetString()!;
        Assert.Contains("529 Overloaded", detail);
        Assert.Contains("[redacted]", detail);
        Assert.DoesNotContain("GHIJKLMNOP", detail);
    }
}
