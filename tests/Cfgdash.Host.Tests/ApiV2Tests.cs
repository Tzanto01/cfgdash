using System.Net;
using System.Text;
using System.Text.Json;
using Cfgdash.Core;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Host.Tests;

public sealed class EditsFixture : IAsyncLifetime
{
    private readonly TempDir temp = new();

    internal ServerHost Host { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var config = temp.Sub("claude-config");
        var environment = TestCore.Machine(home: temp.Sub("home"), paths: [new ContextPath(PathKeys.ClaudeConfigDir, config, null, true)]);
        Host = await ServerHost.StartAsync(ServerHost.TailnetOptions(), environment: environment);
    }

    public async Task DisposeAsync()
    {
        await Host.DisposeAsync();
        temp.Dispose();
    }

    internal string NewProject() => CfgdashServer.NormalizeDir(temp.Sub("proj-" + Guid.NewGuid().ToString("N")))!;
}

public sealed class ApiV2Tests(EditsFixture fixture) : IClassFixture<EditsFixture>
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false });

    private ServerHost Host => fixture.Host;

    private string Origin => $"http://127.0.0.1:{Host.Port}";

    private static string SettingsFile(string project) => Path.Combine(project, ".claude", "settings.json");

    private static string Body(string dir, params (string Key, object Value)[] changes) =>
        JsonSerializer.Serialize(new { dir, changes = changes.Select(c => new { key = c.Key, scope = "project", value = c.Value }) });

    private static string ConfirmBody(string confirm) => JsonSerializer.Serialize(new { confirm });

    private async Task<Resp> PostAsync(string path, HttpContent content, int? port = null, string? host = null, string? bearer = "", params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port ?? Host.Port}{path}") { Content = content };
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (bearer is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (bearer.Length == 0 ? Host.Token : bearer));
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await Http.SendAsync(request);
        return new Resp(response, await response.Content.ReadAsStringAsync());
    }

    private Task<Resp> UiPostAsync(string path, string json, params (string Name, string Value)[] extra) =>
        PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"), headers: [("Origin", Origin), ("X-Cfgdash-Request", "1"), .. extra]);

    private Task<Resp> ProposeAsync(string project, params (string Key, object Value)[] changes) =>
        UiPostAsync("/api/v2/proposals", Body(project, changes));

    private Task<Resp> ApplyAsync(Resp proposal, string? confirm = null) =>
        UiPostAsync($"/api/v2/proposals/{proposal.Json.GetProperty("id").GetString()}/apply", ConfirmBody(confirm ?? proposal.Json.GetProperty("confirm").GetString()!));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Health_reports_edits_only_on_the_ui_listener(bool ui, bool expected)
    {
        var response = ui
            ? await Host.GetAsync("/api/v1/health")
            : await Host.TailAsync("/api/v1/health", headers: [("Tailscale-User-Login", ServerHost.Login)]);

        Assert.Equal(200, response.Status);
        Assert.Equal(expected, response.Json.GetProperty("edits").GetBoolean());
    }

    [Theory]
    [InlineData("GET", "/api/v2/settings")]
    [InlineData("POST", "/api/v2/proposals")]
    [InlineData("POST", "/api/v2/proposals/x/apply")]
    [InlineData("GET", "/api/v2/audit")]
    [InlineData("GET", "/api/v2/whatever")]
    [InlineData("DELETE", "/api/v2/settings")]
    public async Task Tailnet_listener_answers_every_v2_route_with_not_found(string method, string path)
    {
        var identity = ("Tailscale-User-Login", ServerHost.Login);
        var response = method == "POST"
            ? await PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"), Host.BackendPort, Host.TailnetHost, headers: [identity, ("Origin", $"https://{Host.TailnetHost}"), ("X-Cfgdash-Request", "1")])
            : await Host.TailAsync(path, method: new HttpMethod(method), headers: [identity]);

        Assert.Equal(404, response.Status);
        Assert.Equal("not-found", response.Error);
    }

    [Fact]
    public async Task Unauthenticated_v2_requests_answer_401_on_both_listeners()
    {
        var ui = await Host.GetAsync("/api/v2/audit", bearer: null);
        var uiPost = await PostAsync("/api/v2/proposals", new StringContent("{}"), bearer: null, headers: [("Origin", Origin), ("X-Cfgdash-Request", "1")]);
        var tail = await Host.TailAsync("/api/v2/audit", bearer: null, headers: [("Tailscale-User-Login", ServerHost.Login)]);

        Assert.Equal(401, ui.Status);
        Assert.Equal(401, uiPost.Status);
        Assert.Equal(401, tail.Status);
    }

    [Theory]
    [InlineData("no-origin")]
    [InlineData("other-port")]
    [InlineData("https")]
    [InlineData("other-host")]
    [InlineData("two-origins")]
    [InlineData("no-marker")]
    [InlineData("marker-true")]
    [InlineData("oversize-length")]
    [InlineData("oversize-chunked")]
    public async Task Post_rules_violations_answer_403(string violation)
    {
        var project = fixture.NewProject();
        var valid = Body(project, ("alwaysThinkingEnabled", false));
        var padded = valid[..^1] + ",\"pad\":\"" + new string('a', 64 * 1024) + "\"}";
        HttpContent content = violation switch
        {
            "oversize-length" => new StringContent(padded, Encoding.UTF8, "application/json"),
            "oversize-chunked" => new ChunkedContent(Encoding.UTF8.GetBytes(padded)),
            _ => new StringContent(valid, Encoding.UTF8, "application/json"),
        };
        (string, string)[] headers = violation switch
        {
            "no-origin" => [("X-Cfgdash-Request", "1")],
            "other-port" => [("Origin", $"http://127.0.0.1:{Host.Port + 1}"), ("X-Cfgdash-Request", "1")],
            "https" => [("Origin", $"https://127.0.0.1:{Host.Port}"), ("X-Cfgdash-Request", "1")],
            "other-host" => [("Origin", $"http://other.example:{Host.Port}"), ("X-Cfgdash-Request", "1")],
            "two-origins" => [("Origin", Origin), ("Origin", Origin), ("X-Cfgdash-Request", "1")],
            "no-marker" => [("Origin", Origin)],
            "marker-true" => [("Origin", Origin), ("X-Cfgdash-Request", "true")],
            _ => [("Origin", Origin), ("X-Cfgdash-Request", "1")],
        };

        var response = await PostAsync("/api/v2/proposals", content, headers: headers);

        Assert.Equal(403, response.Status);
        Assert.Equal("forbidden", response.Error);
        Assert.False(File.Exists(SettingsFile(project)));
    }

    [Fact]
    public async Task Body_of_exactly_64_KiB_is_within_the_size_rule()
    {
        var head = "{\"dir\":" + JsonSerializer.Serialize(Host.StartDir) + ",\"changes\":[],\"pad\":\"";
        var body = head + new string('a', 64 * 1024 - head.Length - 2) + "\"}";
        Assert.Equal(64 * 1024, Encoding.UTF8.GetByteCount(body));

        var response = await UiPostAsync("/api/v2/proposals", body);

        Assert.Equal(400, response.Status);
        Assert.Equal("bad-request", response.Reason);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public async Task Post_accepts_both_loopback_origins(string name)
    {
        var project = fixture.NewProject();
        var origin = $"http://{name}:{Host.Port}";

        var response = await PostAsync(
            "/api/v2/proposals",
            new StringContent(Body(project, ("alwaysThinkingEnabled", false)), Encoding.UTF8, "application/json"),
            host: $"{name}:{Host.Port}",
            headers: [("Origin", origin), ("X-Cfgdash-Request", "1")]);

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task Settings_lists_categories_and_the_project_file_of_the_dir()
    {
        var project = fixture.NewProject();

        var response = await Host.GetAsync("/api/v2/settings?dir=" + Uri.EscapeDataString(project));

        Assert.Equal(200, response.Status);
        Assert.True(response.Json.GetProperty("categories").GetArrayLength() > 0);
        var file = response.Json.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("scope").GetString() == "project");
        Assert.Equal(SettingsFile(project).Replace('\\', '/'), file.GetProperty("path").GetString()!.Replace('\\', '/'));
        Assert.False(file.GetProperty("exists").GetBoolean());
    }

    [Fact]
    public async Task Proposal_writes_nothing_and_apply_writes_audits_and_is_single_use()
    {
        var project = fixture.NewProject();

        var first = await ProposeAsync(project, ("alwaysThinkingEnabled", false));
        Assert.Equal(200, first.Status);
        Assert.False(string.IsNullOrEmpty(first.Json.GetProperty("confirm").GetString()));
        Assert.Contains("alwaysThinkingEnabled", first.Json.GetProperty("files")[0].GetProperty("diff").GetString());
        Assert.False(File.Exists(SettingsFile(project)));

        var applied = await ApplyAsync(first);
        Assert.Equal(200, applied.Status);
        Assert.True(applied.Json.GetProperty("applied").GetBoolean());
        using (var written = JsonDocument.Parse(await File.ReadAllTextAsync(SettingsFile(project))))
        {
            Assert.False(written.RootElement.GetProperty("alwaysThinkingEnabled").GetBoolean());
        }

        var second = await ProposeAsync(project, ("alwaysThinkingEnabled", true));
        var secondApplied = await ApplyAsync(second);
        Assert.Equal(200, secondApplied.Status);

        var audit = await Host.GetAsync("/api/v2/audit");
        var entries = audit.Json.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("auditId").GetString()).ToList();
        Assert.Equal(secondApplied.Json.GetProperty("auditId").GetString(), entries[0]);
        Assert.Equal(applied.Json.GetProperty("auditId").GetString(), entries[1]);

        var again = await ApplyAsync(first);
        Assert.Equal(404, again.Status);
    }

    [Fact]
    public async Task Snapshot_cache_is_dropped_after_a_successful_apply_only()
    {
        var project = fixture.NewProject();
        var path = "/api/v1/snapshot?dir=" + Uri.EscapeDataString(project);
        await Host.GetAsync(path);
        var cached = Host.Snapshots.CountFor(project);
        var proposal = await ProposeAsync(project, ("alwaysThinkingEnabled", false));

        var rejected = await ApplyAsync(proposal, "wrong");
        await Host.GetAsync(path);
        var afterRejected = Host.Snapshots.CountFor(project);
        var applied = await ApplyAsync(proposal);
        await Host.GetAsync(path);

        Assert.Equal(400, rejected.Status);
        Assert.Equal(200, applied.Status);
        Assert.Equal(cached, afterRejected);
        Assert.Equal(cached + 1, Host.Snapshots.CountFor(project));
    }

    [Fact]
    public async Task Unknown_key_is_an_invalid_change()
    {
        var response = await ProposeAsync(fixture.NewProject(), ("noSuchSetting", true));

        Assert.Equal(400, response.Status);
        Assert.Equal("invalid-change", response.Error);
        Assert.Equal("noSuchSetting", response.Json.GetProperty("key").GetString());
        Assert.Equal("unknown-key", response.Reason);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    public async Task Malformed_proposal_body_is_a_bad_request(string body)
    {
        var response = await UiPostAsync("/api/v2/proposals", body);

        Assert.Equal(400, response.Status);
        Assert.Equal("invalid-change", response.Error);
        Assert.Equal("bad-request", response.Reason);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("")]
    public async Task Relative_dir_is_rejected_for_proposals_and_settings(string dir)
    {
        var proposal = await UiPostAsync("/api/v2/proposals", Body(dir, ("alwaysThinkingEnabled", false)));
        var settings = await Host.GetAsync("/api/v2/settings?dir=" + Uri.EscapeDataString(dir));

        Assert.Equal(400, proposal.Status);
        Assert.Equal("bad-dir", proposal.Error);
        Assert.Equal(400, settings.Status);
        Assert.Equal("bad-dir", settings.Error);
    }

    [Fact]
    public async Task Missing_dir_is_rejected()
    {
        var missing = Path.Combine(fixture.NewProject(), "does-not-exist");

        var response = await UiPostAsync("/api/v2/proposals", Body(missing, ("alwaysThinkingEnabled", false)));

        Assert.Equal(400, response.Status);
        Assert.Equal("bad-dir", response.Error);
    }

    [Fact]
    public async Task Wrong_or_missing_confirm_is_a_mismatch_and_writes_nothing()
    {
        var project = fixture.NewProject();
        var wrong = await ApplyAsync(await ProposeAsync(project, ("alwaysThinkingEnabled", false)), "0000");
        var proposal = await ProposeAsync(project, ("alwaysThinkingEnabled", false));
        var missing = await UiPostAsync($"/api/v2/proposals/{proposal.Json.GetProperty("id").GetString()}/apply", "{}");

        Assert.Equal(400, wrong.Status);
        Assert.Equal("confirm-mismatch", wrong.Error);
        Assert.Equal(400, missing.Status);
        Assert.Equal("confirm-mismatch", missing.Error);
        Assert.False(File.Exists(SettingsFile(project)));
    }

    [Fact]
    public async Task Unknown_proposal_id_is_not_found()
    {
        var response = await UiPostAsync("/api/v2/proposals/unknown-id/apply", ConfirmBody("abc"));

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task File_changed_after_the_proposal_is_stale_and_keeps_its_content()
    {
        var project = fixture.NewProject();
        Directory.CreateDirectory(Path.Combine(project, ".claude"));
        await File.WriteAllTextAsync(SettingsFile(project), "{ \"model\": \"one\" }\n");
        var proposal = await ProposeAsync(project, ("alwaysThinkingEnabled", false));
        const string Changed = "{ \"model\": \"two\" }\n";
        await File.WriteAllTextAsync(SettingsFile(project), Changed);

        var response = await ApplyAsync(proposal);

        Assert.Equal(409, response.Status);
        Assert.Equal("stale", response.Error);
        Assert.Equal(Changed, await File.ReadAllTextAsync(SettingsFile(project)));
    }

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
