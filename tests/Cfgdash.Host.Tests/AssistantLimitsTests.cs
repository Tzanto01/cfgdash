using System.Text.Json;

namespace Cfgdash.Host.Tests;

public sealed class AssistantLimitsTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    private static async Task<(string Id, SseCapture Events, FakeRun Run)> StartTurnAsync(AssistantHost host, string waitFor)
    {
        var id = await host.CreateSessionAsync();
        var events = await host.EventsAsync(id);
        Assert.Equal(202, (await host.MessageAsync(id)).Status);
        Assert.True(await events.WaitForAsync(waitFor), $"no {waitFor} frame; got {string.Join(",", events.Names)}");
        return (id, events, await host.Claude.WaitRunAsync(1));
    }

    [LinuxFact]
    public async Task A_second_message_while_a_turn_runs_answers_409_and_a_third_session_answers_429()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(turn: FakeTurn.Stall);
        var (id, events, _) = await StartTurnAsync(host, "delta");
        using var _1 = events;

        var second = await host.MessageAsync(id);
        var other = await host.CreateSessionAsync();
        var third = await host.UiAsync(HttpMethod.Post, "/api/v2/assistant/sessions", JsonSerializer.Serialize(new { dir = host.Project }));

        Assert.Equal(409, second.Status);
        Assert.Equal("turn-running", second.Error);
        Assert.NotEqual(id, other);
        Assert.Equal(429, third.Status);
        Assert.Equal(1, host.Claude.Runs.Count(r => r.Stdin.Count > 0));
    }

    [LinuxFact]
    public async Task An_idle_panel_ends_its_process_after_10_minutes_and_the_next_message_starts_a_new_one_with_a_new_capability()
    {
        await using var host = await AssistantHost.StartAsync();
        var (id, events, first) = await StartTurnAsync(host, "done");
        using var _1 = events;

        Assert.True(await host.AdvanceUntilAsync(() => !FakeClaude.IsAlive(first.Pid), Minute, 40));
        Assert.True(await Poll.UntilAsync(() => !File.Exists(first.SettingsPath)));
        var sessions = (await host.GetAsync("/api/v2/assistant/status")).Json.GetProperty("sessions");
        Assert.False(sessions[0].GetProperty("live").GetBoolean());
        using (await host.EventsAsync(id))
        {
            await Task.Delay(300);
            Assert.Single(host.Claude.Runs);
        }

        Assert.Equal(202, (await host.MessageAsync(id)).Status);
        var second = await host.Claude.WaitRunAsync(2);
        Assert.True(await Poll.UntilAsync(() => events.Names.Count(n => n == "done") == 2));

        Assert.NotEqual(first.Capability, second.Capability);
        Assert.Equal(401, (await host.InternalAsync("get_findings", first.Capability)).Status);
        Assert.Equal(200, (await host.InternalAsync("get_findings", second.Capability)).Status);
        Assert.True((await host.GetAsync("/api/v2/assistant/status")).Json.GetProperty("sessions")[0].GetProperty("live").GetBoolean());
    }

    [LinuxFact]
    public async Task A_turn_over_10_minutes_is_interrupted_then_killed_with_a_turn_timeout_error()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(turn: FakeTurn.Stall, grandchild: true);
        var (_, events, run) = await StartTurnAsync(host, "delta");
        using var _1 = events;

        host.Time.Advance(11 * Minute);
        Assert.True(await host.AdvanceUntilAsync(() => events.Names.Contains("error"), TimeSpan.FromSeconds(6)));

        Assert.Equal("turn-timeout", events.First("error").GetProperty("error").GetString());
        Assert.DoesNotContain("done", events.Names);
        Assert.Equal(["user", "control_request"], run.StdinKinds);
        Assert.True(await FakeClaude.DiedAsync(run.Pid));
        Assert.True(await FakeClaude.DiedAsync(run.GrandchildPid!.Value));
    }

    [LinuxFact]
    public async Task The_41st_tool_call_of_a_turn_is_refused_with_tool_limit()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(turn: FakeTurn.Stall);
        var (_, events, run) = await StartTurnAsync(host, "delta");
        using var _1 = events;

        for (var i = 0; i < 40; i++)
        {
            var ok = await host.InternalAsync("get_findings", run.Capability);
            Assert.Equal(200, ok.Status);
            Assert.False(ok.Json.TryGetProperty("error", out _), $"call {i + 1}: {ok.Body}");
        }

        var refused = await host.InternalAsync("get_findings", run.Capability);

        Assert.Equal(200, refused.Status);
        Assert.Equal("tool-limit", refused.Error);
    }

    [LinuxFact]
    public async Task A_stdout_line_over_1_MiB_ends_the_turn_with_line_too_long_and_kills_the_process()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(turn: FakeTurn.BigLine, grandchild: true);
        var (_, events, run) = await StartTurnAsync(host, "error");
        using var _1 = events;

        Assert.Equal("line-too-long", events.First("error").GetProperty("error").GetString());
        Assert.DoesNotContain("done", events.Names);
        Assert.True(await FakeClaude.DiedAsync(run.Pid));
        Assert.True(await FakeClaude.DiedAsync(run.GrandchildPid!.Value));
    }

    [LinuxFact]
    public async Task An_inbound_control_request_is_ignored_and_the_turn_still_completes()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(turn: FakeTurn.ControlRequest);
        var (_, events, run) = await StartTurnAsync(host, "done");
        using var _1 = events;

        Assert.DoesNotContain("error", events.Names);
        Assert.Equal("hello\n", events.DeltaText());
        Assert.Equal(["user"], run.StdinKinds);
    }

    [LinuxFact]
    public async Task Deleting_a_session_kills_the_whole_process_tree_and_removes_its_files()
    {
        await using var host = await AssistantHost.StartAsync();
        host.Claude.Configure(turn: FakeTurn.Stall, grandchild: true);
        var (id, events, run) = await StartTurnAsync(host, "delta");
        using var _1 = events;
        Assert.True(FakeClaude.IsAlive(run.GrandchildPid!.Value));

        var deleted = await host.UiAsync(HttpMethod.Delete, $"/api/v2/assistant/sessions/{id}", null);

        Assert.Equal(204, deleted.Status);
        Assert.True(await FakeClaude.DiedAsync(run.Pid));
        Assert.True(await FakeClaude.DiedAsync(run.GrandchildPid.Value));
        Assert.True(await Poll.UntilAsync(() => !File.Exists(run.SettingsPath) && !File.Exists(run.McpPath)));
        Assert.Equal(404, (await host.UiAsync(HttpMethod.Delete, $"/api/v2/assistant/sessions/{id}", null)).Status);
    }

    [LinuxFact]
    public async Task Stopping_the_server_kills_the_process_tree_of_every_live_session()
    {
        var host = await AssistantHost.StartAsync();
        FakeRun run;
        try
        {
            host.Claude.Configure(grandchild: true);
            await host.CreateSessionAsync();
            run = await host.Claude.WaitRunAsync(1);
            Assert.True(FakeClaude.IsAlive(run.GrandchildPid!.Value));
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        var pids = (run.Pid, run.GrandchildPid.Value);
        var files = (run.SettingsPath, run.McpPath);
        await host.Server.DisposeAsync();
        try
        {
            Assert.True(await FakeClaude.DiedAsync(pids.Item1));
            Assert.True(await FakeClaude.DiedAsync(pids.Item2));
            Assert.False(File.Exists(files.Item1) || File.Exists(files.Item2));
        }
        finally
        {
            host.Claude.Dispose();
        }
    }
}
