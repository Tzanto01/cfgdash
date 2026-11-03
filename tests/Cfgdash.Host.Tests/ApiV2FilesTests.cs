using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cfgdash.Core;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Host.Tests;

public sealed class ApiV2FilesTests : IAsyncLifetime
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false });

    private readonly TempDir temp = new();
    private ServerHost host = null!;
    private string project = null!;
    private string claudeMd = null!;

    public async Task InitializeAsync()
    {
        project = CfgdashServer.NormalizeDir(temp.Sub("proj"))!;
        claudeMd = Path.Combine(project, "CLAUDE.md");
        File.WriteAllText(claudeMd, "# notes\r\n");
        var secret = Path.Combine(project, "AGENTS.md");
        File.WriteAllText(secret, "token sk-ant-" + new string('b', 30) + "\n");
        var snapshots = new FakeSnapshots
        {
            Files = new FakeCatalog(
                new RegisteredFile(new FileRef("md", claudeMd, "instruction", 9, 3, false), claudeMd),
                new RegisteredFile(new FileRef("secret", secret, "instruction", 45, 12, false), secret)),
        };
        var environment = TestCore.Machine(home: temp.Sub("home"), paths: [new ContextPath(PathKeys.ClaudeConfigDir, temp.Sub("claude-config"), null, true)]);
        host = await ServerHost.StartAsync(ServerHost.TailnetOptions(), snapshots: snapshots, environment: environment);
    }

    public async Task DisposeAsync()
    {
        await host.DisposeAsync();
        temp.Dispose();
    }

    private string Query => "?dir=" + Uri.EscapeDataString(project);

    private async Task<Resp> PostAsync(string path, string json, bool marker = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{host.Port}{path}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", host.Token);
        request.Headers.TryAddWithoutValidation("Origin", $"http://127.0.0.1:{host.Port}");
        if (marker)
        {
            request.Headers.TryAddWithoutValidation("X-Cfgdash-Request", "1");
        }

        using var response = await Http.SendAsync(request);
        return new Resp(response, await response.Content.ReadAsStringAsync());
    }

    private static string ProposalBody(string content, string sha256Before) => JsonSerializer.Serialize(new { content, sha256Before });

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task An_editable_file_answer_carries_edit_info_and_its_raw_text()
    {
        var response = await host.GetAsync("/api/v1/files/md" + Query);

        Assert.Equal(200, response.Status);
        Assert.Equal("# notes\r\n", response.Json.GetProperty("content").GetString());
        var edit = response.Json.GetProperty("edit");
        Assert.True(edit.GetProperty("editable").GetBoolean());
        Assert.Equal(Sha256("# notes\r\n"), edit.GetProperty("sha256").GetString());
        Assert.False(edit.GetProperty("gitTracked").GetBoolean());
    }

    [Fact]
    public async Task A_non_editable_file_answer_has_a_reason_and_no_sha256()
    {
        var response = await host.GetAsync("/api/v1/files/secret" + Query);

        var edit = response.Json.GetProperty("edit");
        Assert.False(edit.GetProperty("editable").GetBoolean());
        Assert.Equal("contains-redacted-values", edit.GetProperty("reason").GetString());
        Assert.False(edit.TryGetProperty("sha256", out _));
        Assert.Equal("content of secret", response.Json.GetProperty("content").GetString());
    }

    [Fact]
    public async Task The_tailnet_listener_answers_files_without_edit_info_and_the_proposals_route_with_404()
    {
        var file = await host.TailAsync("/api/v1/files/md" + Query, headers: [("Tailscale-User-Login", ServerHost.Login)]);
        var post = await host.SendAsync(HttpMethod.Post, "/api/v2/files/md/proposals" + Query, host.BackendPort, host.TailnetHost, "", ("Tailscale-User-Login", ServerHost.Login));

        Assert.Equal(200, file.Status);
        Assert.False(file.Json.TryGetProperty("edit", out _));
        Assert.Equal(404, post.Status);
    }

    [Fact]
    public async Task Propose_and_apply_over_http_writes_the_content_as_sent()
    {
        var proposal = await PostAsync("/api/v2/files/md/proposals" + Query, ProposalBody("# edited\r\nline", Sha256("# notes\r\n")));
        Assert.Equal(200, proposal.Status);
        Assert.Equal("file", proposal.Json.GetProperty("files")[0].GetProperty("scope").GetString());

        var apply = await PostAsync($"/api/v2/proposals/{proposal.Json.GetProperty("id").GetString()}/apply", JsonSerializer.Serialize(new { confirm = proposal.Json.GetProperty("confirm").GetString() }));

        Assert.Equal(200, apply.Status);
        Assert.Equal("# edited\r\nline"u8.ToArray(), File.ReadAllBytes(claudeMd));
    }

    [Fact]
    public async Task Stale_sha_answers_409_and_a_non_editable_file_400()
    {
        var stale = await PostAsync("/api/v2/files/md/proposals" + Query, ProposalBody("# x\n", Sha256("# other\n")));
        var invalid = await PostAsync("/api/v2/files/secret/proposals" + Query, ProposalBody("# x\n", Sha256("# other\n")));

        Assert.Equal(409, stale.Status);
        Assert.Equal("stale", stale.Error);
        Assert.Equal(400, invalid.Status);
        Assert.Equal("contains-redacted-values", invalid.Reason);
    }

    [Fact]
    public async Task The_body_cap_is_320_KiB_here_and_stays_64_KiB_for_settings_proposals()
    {
        var large = ProposalBody(new string('a', 200 * 1024), Sha256("# notes\r\n"));
        var tooLarge = ProposalBody(new string('a', 330 * 1024), Sha256("# notes\r\n"));

        Assert.Equal(200, (await PostAsync("/api/v2/files/md/proposals" + Query, large)).Status);
        Assert.Equal(403, (await PostAsync("/api/v2/files/md/proposals" + Query, tooLarge)).Status);
        Assert.Equal(403, (await PostAsync("/api/v2/proposals", large)).Status);
    }

    [Fact]
    public async Task The_request_rules_and_the_dir_query_are_required()
    {
        var body = ProposalBody("# x\n", Sha256("# notes\r\n"));

        Assert.Equal(403, (await PostAsync("/api/v2/files/md/proposals" + Query, body, marker: false)).Status);
        Assert.Equal("bad-dir", (await PostAsync("/api/v2/files/md/proposals", body)).Error);
        Assert.Equal("bad-request", (await PostAsync("/api/v2/files/md/proposals" + Query, "{\"content\":\"# x\"}")).Reason);
    }
}
