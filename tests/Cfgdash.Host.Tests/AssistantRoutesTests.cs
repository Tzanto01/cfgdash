using System.Text.Json;

namespace Cfgdash.Host.Tests;

public sealed class AssistantRoutesTests(LiveAssistantFixture fixture) : IClassFixture<LiveAssistantFixture>
{
    private AssistantHost Host => fixture.Host;

    private string Id => fixture.SessionId;

    private async Task AssertSessionUntouchedAsync()
    {
        var sessions = (await Host.GetAsync("/api/v2/assistant/status")).Json.GetProperty("sessions");
        Assert.Equal(1, sessions.GetArrayLength());
        Assert.True(sessions[0].GetProperty("live").GetBoolean());
        Assert.Single(Host.Claude.Runs);
    }

    [LinuxTheory]
    [InlineData("GET", "/api/v2/assistant/status")]
    [InlineData("POST", "/api/v2/assistant/sessions")]
    [InlineData("POST", "/api/v2/assistant/sessions/{id}/messages")]
    [InlineData("POST", "/api/v2/assistant/sessions/{id}/check")]
    [InlineData("GET", "/api/v2/assistant/sessions/{id}/events")]
    [InlineData("DELETE", "/api/v2/assistant/sessions/{id}")]
    public async Task Every_public_route_answers_404_on_the_tailnet_listener_and_changes_nothing(string method, string path)
    {
        var response = await Host.SendAsync(
            new HttpMethod(method),
            path.Replace("{id}", Id),
            method == "GET" ? null : """{"dir":"/x","text":"hi"}""",
            "",
            Host.BackendPort,
            Host.TailnetHost,
            ("Tailscale-User-Login", ServerHost.Login),
            ("Origin", "https://" + Host.TailnetHost),
            ("X-Cfgdash-Request", "1"));

        Assert.Equal(404, response.Status);
        await AssertSessionUntouchedAsync();
    }

    [LinuxTheory]
    [InlineData("POST", "/api/v2/assistant/sessions", "no-origin")]
    [InlineData("POST", "/api/v2/assistant/sessions", "no-marker")]
    [InlineData("POST", "/api/v2/assistant/sessions/{id}/messages", "no-origin")]
    [InlineData("POST", "/api/v2/assistant/sessions/{id}/messages", "no-marker")]
    [InlineData("POST", "/api/v2/assistant/sessions/{id}/check", "no-origin")]
    [InlineData("POST", "/api/v2/assistant/sessions/{id}/check", "no-marker")]
    [InlineData("DELETE", "/api/v2/assistant/sessions/{id}", "no-origin")]
    [InlineData("DELETE", "/api/v2/assistant/sessions/{id}", "no-marker")]
    public async Task Mutating_routes_without_the_origin_or_marker_header_answer_403_and_change_nothing(string method, string path, string violation)
    {
        (string, string)[] headers = violation == "no-origin" ? [("X-Cfgdash-Request", "1")] : [("Origin", Host.Origin)];

        var response = await Host.SendAsync(new HttpMethod(method), path.Replace("{id}", Id), $$"""{"dir":"{{Host.Project}}","text":"hi"}""", headers: headers);

        Assert.Equal(403, response.Status);
        await AssertSessionUntouchedAsync();
    }

    [LinuxFact]
    public async Task Status_of_a_working_install_names_the_cli_models_and_sessions_and_never_the_account_email()
    {
        var status = await Host.GetAsync("/api/v2/assistant/status");

        Assert.True(status.Json.GetProperty("available").GetBoolean());
        Assert.Equal("2.1.316 (Claude Code)", status.Json.GetProperty("claudeVersion").GetString());
        Assert.Equal(Path.Combine(Host.Claude.BinDir, "claude"), status.Json.GetProperty("claudePath").GetString());
        Assert.Contains("sonnet", status.Json.GetProperty("models").EnumerateArray().Select(m => m.GetString()));
        var session = status.Json.GetProperty("sessions")[0];
        Assert.Equal(Id, session.GetProperty("id").GetString());
        Assert.Equal("sonnet", session.GetProperty("model").GetString());
        Assert.DoesNotContain("user" + "@example.com", status.Body);
        Assert.DoesNotContain("example.com", status.Body);
    }
}

public sealed class AssistantStatusTests
{
    [LinuxTheory]
    [InlineData("no-claude", "")]
    [InlineData("missing-flag", "--verbose")]
    [InlineData("logged-out", "")]
    public async Task Status_is_unavailable_with_a_reason_and_never_leaks_the_account_email(string variant, string expectedInReason)
    {
        await using var host = await AssistantHost.StartAsync(new AssistantHostOptions { ClaudeOnPath = variant != "no-claude" });
        host.Claude.Configure(loggedIn: variant != "logged-out", helpOmit: variant == "missing-flag" ? "--verbose" : null);

        var status = await host.GetAsync("/api/v2/assistant/status");
        var create = await host.UiAsync(HttpMethod.Post, "/api/v2/assistant/sessions", JsonSerializer.Serialize(new { dir = host.Project }));

        Assert.False(status.Json.GetProperty("available").GetBoolean());
        Assert.False(string.IsNullOrEmpty(status.Reason));
        Assert.Contains(expectedInReason, status.Reason);
        Assert.Empty(status.Json.GetProperty("models").EnumerateArray());
        Assert.DoesNotContain("example.com", status.Body);
        Assert.Equal(503, create.Status);
        Assert.Empty(host.Claude.Runs);
    }

    [LinuxFact]
    public async Task Events_for_an_unknown_session_answer_404_and_start_no_process()
    {
        await using var host = await AssistantHost.StartAsync();

        var response = await host.GetAsync("/api/v2/assistant/sessions/0123456789abcdef/events");

        Assert.Equal(404, response.Status);
        Assert.Empty(host.Claude.Runs);
    }
}

public sealed class AssistantProposalTests
{
    private const string Rationale = "Dark is easier on the eyes. Do not store AKIAABCDEFGHIJKLMNOP anywhere.";

    private static string Propose(string key, string value) =>
        JsonSerializer.Serialize(new { changes = new[] { new { key, scope = "project", value } }, rationale = Rationale });

    [LinuxFact]
    public async Task Streamed_deltas_are_redacted_even_when_a_secret_is_split_across_two_deltas()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(turn: FakeTurn.SplitSecret, splitA: "key AKIAABCDEF", splitB: "GHIJKLMNOP end\\n");
        var id = await host.CreateSessionAsync();
        using var events = await host.EventsAsync(id);

        await host.MessageAsync(id);
        Assert.True(await events.WaitForAsync("done"));

        var text = events.DeltaText();
        Assert.DoesNotContain("AKIAABCDEF", text);
        Assert.DoesNotContain("GHIJKLMNOP", text);
        Assert.Contains("[redacted]", text);
        Assert.Contains("end", text);
    }

    [LinuxFact]
    public async Task A_proposal_reaches_the_panel_with_confirm_and_a_redacted_rationale_applies_audited_and_is_logged_without_confirm_or_results()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(turn: FakeTurn.Stall);
        var id = await host.CreateSessionAsync();
        using var events = await host.EventsAsync(id);
        await host.MessageAsync(id);
        Assert.True(await events.WaitForAsync("delta"));
        var run = await host.Claude.WaitRunAsync(1);

        var tool = await host.InternalAsync("propose_change", run.Capability, Propose("theme", "dark"));

        Assert.Equal(200, tool.Status);
        Assert.False(tool.Json.TryGetProperty("confirm", out _));
        Assert.True(await events.WaitForAsync("proposal"));
        var proposal = events.First("proposal");
        var proposalId = proposal.GetProperty("proposalId").GetString()!;
        Assert.Equal(proposalId, tool.Json.GetProperty("proposalId").GetString());
        Assert.False(string.IsNullOrEmpty(proposal.GetProperty("confirm").GetString()));
        Assert.Contains("theme", proposal.GetProperty("files")[0].GetProperty("diff").GetString());
        var rationale = proposal.GetProperty("rationale").GetString()!;
        Assert.DoesNotContain("AKIAABCDEF", rationale);
        Assert.Contains("Dark is easier", rationale);

        var settingsFile = Path.Combine(host.Project, ".claude", "settings.json");
        Assert.False(File.Exists(settingsFile));
        var applied = await host.UiAsync(HttpMethod.Post, $"/api/v2/proposals/{proposalId}/apply", JsonSerializer.Serialize(new { confirm = proposal.GetProperty("confirm").GetString() }));
        Assert.Equal(200, applied.Status);
        Assert.Equal("dark", JsonDocument.Parse(await File.ReadAllTextAsync(settingsFile)).RootElement.GetProperty("theme").GetString());

        var audit = (await host.GetAsync("/api/v2/audit")).Json.GetProperty("entries")[0];
        Assert.Equal("assistant", audit.GetProperty("origin").GetString());
        Assert.Equal(id, audit.GetProperty("sessionId").GetString());

        var logPath = Path.Combine(host.AssistantDir, id + ".jsonl");
        var log = await File.ReadAllTextAsync(logPath);
        var entry = JsonDocument.Parse(log.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]).RootElement;
        Assert.Equal("propose_change", entry.GetProperty("tool").GetString());
        Assert.DoesNotContain(proposal.GetProperty("confirm").GetString()!, log);
        Assert.DoesNotContain("confirm", log);
        Assert.DoesNotContain(proposalId, log);
        Assert.DoesNotContain("diff", log);
        Assert.DoesNotContain("expiresAt", log);
        Assert.DoesNotContain("AKIAABCDEF", log);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(logPath));
        }
    }

    [LinuxFact]
    public async Task A_change_the_assistant_policy_denies_answers_the_model_with_denied_and_creates_no_proposal()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(turn: FakeTurn.Stall);
        var id = await host.CreateSessionAsync();
        using var events = await host.EventsAsync(id);
        await host.MessageAsync(id);
        Assert.True(await events.WaitForAsync("delta"));
        var run = await host.Claude.WaitRunAsync(1);

        var tool = await host.InternalAsync("propose_change", run.Capability, Propose("permissions.defaultMode", "acceptEdits"));

        Assert.Equal(200, tool.Status);
        Assert.True(tool.Json.TryGetProperty("denied", out var denied), tool.Body);
        Assert.Equal("permissions.defaultMode", denied.GetString());
        Assert.Equal("assistant-policy", tool.Reason);
        Assert.DoesNotContain("proposal", events.Names);
        Assert.False(File.Exists(Path.Combine(host.Project, ".claude", "settings.json")));
    }
}
