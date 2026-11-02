using System.Text.Json;

namespace Cfgdash.Host.Tests;

public sealed class ApiV2RevertTests(EditsFixture fixture) : IClassFixture<EditsFixture>
{
    private ServerHost Host => fixture.Host;

    private static string SettingsFile(string project) => Path.Combine(project, ".claude", "settings.json");

    private Task<Resp> PostAsync(string path, string json, bool marker = true, bool tailnet = false) =>
        UiRequests.SendAsync(Host, HttpMethod.Post, path, json, marker, tailnet: tailnet);

    private async Task<string> ApplyChangeAsync(string project, bool value)
    {
        var body = JsonSerializer.Serialize(new { dir = project, changes = new[] { new { key = "alwaysThinkingEnabled", scope = "project", value } } });
        var proposal = await PostAsync("/api/v2/proposals", body);
        var applied = await ApplyAsync(proposal);
        Assert.Equal(200, applied.Status);
        return applied.Json.GetProperty("auditId").GetString()!;
    }

    private Task<Resp> ApplyAsync(Resp proposal) =>
        PostAsync($"/api/v2/proposals/{proposal.Json.GetProperty("id").GetString()}/apply", JsonSerializer.Serialize(new { confirm = proposal.Json.GetProperty("confirm").GetString() }));

    private static bool ThinkingEnabled(string project)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(SettingsFile(project)));
        return document.RootElement.GetProperty("alwaysThinkingEnabled").GetBoolean();
    }

    [Fact]
    public async Task Revert_proposes_the_backup_and_applying_it_restores_the_file_and_audits_revert_of()
    {
        var project = fixture.NewProject();
        await ApplyChangeAsync(project, false);
        var second = await ApplyChangeAsync(project, true);

        var proposal = await PostAsync($"/api/v2/audit/{second}/revert", "{}");

        Assert.Equal(200, proposal.Status);
        Assert.True(ThinkingEnabled(project));
        var applied = await ApplyAsync(proposal);
        Assert.Equal(200, applied.Status);
        Assert.False(ThinkingEnabled(project));
        var audit = await Host.GetAsync("/api/v2/audit");
        var newest = audit.Json.GetProperty("entries")[0];
        Assert.Equal(applied.Json.GetProperty("auditId").GetString(), newest.GetProperty("auditId").GetString());
        Assert.Equal(second, newest.GetProperty("revertOf").GetString());
    }

    [Fact]
    public async Task Reverting_an_apply_that_created_the_file_deletes_it()
    {
        var project = fixture.NewProject();
        var created = await ApplyChangeAsync(project, false);

        var proposal = await PostAsync($"/api/v2/audit/{created}/revert", "{}");
        var applied = await ApplyAsync(proposal);

        Assert.Equal(200, applied.Status);
        Assert.False(File.Exists(SettingsFile(project)));
    }

    [Fact]
    public async Task Revert_of_an_unknown_audit_id_answers_404()
    {
        var response = await PostAsync("/api/v2/audit/20260101T000000Z-deadbeef/revert", "{}");

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public async Task Revert_after_the_file_changed_answers_409_stale_and_proposes_nothing()
    {
        var project = fixture.NewProject();
        await ApplyChangeAsync(project, false);
        var second = await ApplyChangeAsync(project, true);
        await File.WriteAllTextAsync(SettingsFile(project), "{ \"alwaysThinkingEnabled\": true, \"edited\": 1 }");

        var response = await PostAsync($"/api/v2/audit/{second}/revert", "{}");

        Assert.Equal(409, response.Status);
        Assert.Equal("stale", response.Error);
        Assert.False(response.Json.TryGetProperty("confirm", out _));
    }

    [Fact]
    public async Task Revert_without_the_request_marker_answers_403_and_leaves_the_file()
    {
        var project = fixture.NewProject();
        await ApplyChangeAsync(project, false);
        var second = await ApplyChangeAsync(project, true);

        var response = await PostAsync($"/api/v2/audit/{second}/revert", "{}", marker: false);

        Assert.Equal(403, response.Status);
        Assert.True(ThinkingEnabled(project));
    }

    [Fact]
    public async Task Revert_on_the_tailnet_listener_answers_404()
    {
        var project = fixture.NewProject();
        var applied = await ApplyChangeAsync(project, false);

        var response = await PostAsync($"/api/v2/audit/{applied}/revert", "{}", tailnet: true);

        Assert.Equal(404, response.Status);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("{ not json")]
    public async Task Revert_with_a_body_that_is_not_a_json_object_answers_400(string body)
    {
        var project = fixture.NewProject();
        var applied = await ApplyChangeAsync(project, false);

        var response = await PostAsync($"/api/v2/audit/{applied}/revert", body);

        Assert.Equal(400, response.Status);
    }
}
