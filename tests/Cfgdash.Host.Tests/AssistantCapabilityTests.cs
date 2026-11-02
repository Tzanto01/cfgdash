using System.Text.Json;

namespace Cfgdash.Host.Tests;

public sealed class LiveAssistantFixture : IAsyncLifetime
{
    internal AssistantHost Host { get; private set; } = null!;

    internal FakeRun Run { get; private set; } = null!;

    internal string SessionId { get; private set; } = null!;

    internal SseCapture Events { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Host = await AssistantHost.StartAsync(new AssistantHostOptions { Tailnet = true });
        Host.Claude.Configure(turn: FakeTurn.Stall, grandchild: true);
        SessionId = await Host.CreateSessionAsync();
        Events = await Host.EventsAsync(SessionId);
        await Host.MessageAsync(SessionId);
        Assert.True(await Events.WaitForAsync("delta"));
        Run = await Host.Claude.WaitRunAsync(1);
    }

    public async Task DisposeAsync()
    {
        if (Host is null)
        {
            return;
        }

        Events.Dispose();
        await Host.DisposeAsync();
    }
}

public sealed class AssistantCapabilityTests(LiveAssistantFixture fixture) : IClassFixture<LiveAssistantFixture>
{
    private AssistantHost Host => fixture.Host;

    private string Capability => fixture.Run.Capability;

    [LinuxFact]
    public async Task Valid_capability_answers_200_with_the_findings_of_the_bound_dir_even_when_the_body_names_another_dir()
    {
        var other = Path.Combine(Host.Paths.Root, "other-project");
        Directory.CreateDirectory(other);

        var response = await Host.InternalAsync("get_findings", Capability, JsonSerializer.Serialize(new { dir = other }));

        Assert.Equal(200, response.Status);
        Assert.True(response.Json.TryGetProperty("findings", out _), response.Body);
        Assert.True(Host.Snapshots.CountFor(Host.Project) > 0);
        Assert.Equal(0, Host.Snapshots.CountFor(other));
    }

    [LinuxFact]
    public async Task Missing_wrong_main_token_and_cookie_credentials_are_all_refused_with_401()
    {
        var none = await Host.SendAsync(HttpMethod.Post, "/api/v2/assistant/internal/get_findings", "{}", bearer: null);
        var wrong = await Host.InternalAsync("get_findings", "not-the-capability");
        var mainToken = await Host.InternalAsync("get_findings", Host.Token);
        var cookieOnly = await Host.SendAsync(HttpMethod.Post, "/api/v2/assistant/internal/get_findings", "{}", bearer: null, headers: ("Cookie", "cfgdash_session=anything"));

        Assert.All(new[] { none, wrong, mainToken, cookieOnly }, r => Assert.Equal(401, r.Status));
    }

    [LinuxFact]
    public async Task Valid_capability_together_with_any_cookie_is_refused_with_401()
    {
        var response = await Host.InternalAsync("get_findings", Capability, "{}", ("Cookie", "a=b"));

        Assert.Equal(401, response.Status);
    }

    [LinuxFact]
    public async Task Capability_is_not_accepted_on_the_tailnet_listener()
    {
        var response = await Host.SendAsync(
            HttpMethod.Post,
            "/api/v2/assistant/internal/get_findings",
            "{}",
            Capability,
            Host.BackendPort,
            Host.TailnetHost,
            ("Tailscale-User-Login", ServerHost.Login));

        Assert.Contains(response.Status, new[] { 401, 404 });
        Assert.DoesNotContain("findings", response.Body);
    }

    [LinuxFact]
    public async Task Internal_routes_answer_404_on_the_tailnet_listener_even_for_an_authenticated_tailnet_caller()
    {
        var response = await Host.SendAsync(
            HttpMethod.Post,
            "/api/v2/assistant/internal/get_findings",
            "{}",
            "",
            Host.BackendPort,
            Host.TailnetHost,
            ("Tailscale-User-Login", ServerHost.Login));

        Assert.Equal(404, response.Status);
    }

    [LinuxTheory]
    [InlineData("/api/v2/assistant/internal/../settings")]
    [InlineData("/api/v2/assistant/internal/../v2/settings")]
    [InlineData("/api/v2/assistant/internal/%2E%2E/settings")]
    [InlineData("/api/v2/assistant/internal/%2e%2e/v2/settings")]
    [InlineData("/api/v2/assistant/internal/..%2Fsettings")]
    [InlineData("/api/v2/assistant/internal/..%2fsettings")]
    [InlineData("/api/v2/assistant/internal/..%5Csettings")]
    [InlineData("/api/v2/assistant/internal/..\\settings")]
    [InlineData("/api/v2/Assistant/internal/get_findings")]
    [InlineData("/api/v2/assistant/Internal/get_findings")]
    [InlineData("/api/v2/settings")]
    public async Task Traversal_and_case_variants_never_get_past_the_main_token_gate(string target)
    {
        var withCapability = await Host.RawAsync("POST", target, Capability);
        var withoutCredentials = await Host.RawAsync("POST", target);
        var getWithoutCredentials = await Host.RawAsync("GET", target);

        Assert.Equal(401, withCapability.Status);
        Assert.Equal(401, withoutCredentials.Status);
        Assert.Equal(401, getWithoutCredentials.Status);
    }
}
