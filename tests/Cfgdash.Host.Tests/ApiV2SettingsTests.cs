using System.Text.Json;

namespace Cfgdash.Host.Tests;

public sealed class ApiV2SettingsTests
{
    private const string Route = "/api/v2/cfgdash-settings";

    private static string ConfigFile(ServerHost host) => Path.Combine(host.ConfigDir, "config.json");

    private static Task<ServerHost> StartAsync(FakeAutostart os) =>
        ServerHost.StartAsync(ServerHost.TailnetOptions(), autostart: os.Factory);

    private static Task<Resp> PutAsync(ServerHost host, string json, bool marker = true, string? origin = null) =>
        UiRequests.SendAsync(host, HttpMethod.Put, Route, json, marker, origin);

    [Fact]
    public async Task Get_answers_defaults_the_os_state_and_forward_slash_paths_without_a_config_file()
    {
        await using var host = await StartAsync(new FakeAutostart("off"));

        var response = await host.GetAsync(Route);

        Assert.Equal(200, response.Status);
        var values = response.Json.GetProperty("values");
        Assert.Equal("system", values.GetProperty("theme").GetString());
        Assert.Equal(2.0, values.GetProperty("assistant").GetProperty("maxBudgetUsd").GetDouble());
        Assert.Equal(JsonValueKind.Null, values.GetProperty("defaultDir").ValueKind);
        var start = response.Json.GetProperty("startAtLogin");
        Assert.False(start.GetProperty("configured").GetBoolean());
        Assert.Equal("off", start.GetProperty("actual").GetString());
        Assert.False(start.TryGetProperty("detail", out _));
        var paths = response.Json.GetProperty("paths");
        Assert.EndsWith("/config.json", paths.GetProperty("config").GetString());
        Assert.EndsWith("/overrides.json", paths.GetProperty("overrides").GetString());
        Assert.DoesNotContain('\\', paths.GetProperty("config").GetString()!);
        Assert.False(File.Exists(ConfigFile(host)));
    }

    [Fact]
    public async Task Get_shows_the_os_state_and_detail_even_when_the_file_disagrees()
    {
        await using var host = await StartAsync(new FakeAutostart("on", "the unit file differs from the expected one"));

        var start = (await host.GetAsync(Route)).Json.GetProperty("startAtLogin");

        Assert.False(start.GetProperty("configured").GetBoolean());
        Assert.Equal("on", start.GetProperty("actual").GetString());
        Assert.Equal("the unit file differs from the expected one", start.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Put_persists_a_partial_and_get_returns_it_with_the_rest_unchanged()
    {
        await using var host = await StartAsync(new FakeAutostart());

        var put = await PutAsync(host, "{ \"theme\": \"dark\", \"assistant\": { \"effort\": \"high\" } }");
        var get = await host.GetAsync(Route);

        Assert.Equal(200, put.Status);
        Assert.Equal("dark", put.Json.GetProperty("values").GetProperty("theme").GetString());
        var values = get.Json.GetProperty("values");
        Assert.Equal("dark", values.GetProperty("theme").GetString());
        Assert.Equal("high", values.GetProperty("assistant").GetProperty("effort").GetString());
        Assert.Equal("sonnet", values.GetProperty("assistant").GetProperty("model").GetString());
        using var file = JsonDocument.Parse(File.ReadAllText(ConfigFile(host)));
        Assert.Equal("dark", file.RootElement.GetProperty("theme").GetString());
    }

    [Theory]
    [InlineData("{ \"theme\": \"neon\" }", "theme", "bad-value")]
    [InlineData("{ \"assistant\": { \"maxBudgetUsd\": 500 } }", "assistant.maxBudgetUsd", "bad-value")]
    [InlineData("{ \"fleet\": { \"enabled\": \"yes\" } }", "fleet.enabled", "bad-type")]
    [InlineData("{ \"assistant\": { \"colour\": 1 } }", "assistant.colour", "unknown-key")]
    [InlineData("{}", null, "bad-request")]
    [InlineData("[1]", null, "bad-request")]
    public async Task Put_rejects_invalid_settings_with_the_dotted_key_and_writes_nothing(string body, string? key, string reason)
    {
        await using var host = await StartAsync(new FakeAutostart());

        var response = await PutAsync(host, body);

        Assert.Equal(400, response.Status);
        Assert.Equal("invalid-setting", response.Error);
        Assert.Equal(key, response.Json.GetProperty("key").GetString());
        Assert.Equal(reason, response.Reason);
        Assert.False(File.Exists(ConfigFile(host)));
    }

    [Fact]
    public async Task Put_with_unparseable_json_answers_400_and_writes_nothing()
    {
        await using var host = await StartAsync(new FakeAutostart());

        var response = await PutAsync(host, "{ not json");

        Assert.Equal(400, response.Status);
        Assert.False(File.Exists(ConfigFile(host)));
    }

    [Theory]
    [InlineData("no-marker")]
    [InlineData("foreign-origin")]
    public async Task Put_that_breaks_the_edit_request_rules_answers_403_and_changes_nothing(string violation)
    {
        var os = new FakeAutostart();
        await using var host = await StartAsync(os);

        var response = await PutAsync(host, "{ \"startAtLogin\": true }", marker: violation != "no-marker", origin: violation == "foreign-origin" ? "http://app.example" : null);

        Assert.Equal(403, response.Status);
        Assert.Empty(os.Sets);
        Assert.False(File.Exists(ConfigFile(host)));
    }

    [Fact]
    public async Task Failed_os_entry_answers_500_autostart_failed_and_leaves_the_file_byte_identical()
    {
        var os = new FakeAutostart(failure: "systemctl --user enable failed: denied");
        await using var host = await StartAsync(os);
        await PutAsync(host, "{ \"theme\": \"dark\" }");
        var before = await File.ReadAllBytesAsync(ConfigFile(host));

        var response = await PutAsync(host, "{ \"startAtLogin\": true, \"theme\": \"light\" }");

        Assert.Equal(500, response.Status);
        Assert.Equal("autostart-failed", response.Error);
        Assert.Contains("denied", response.Reason);
        Assert.Equal(before, await File.ReadAllBytesAsync(ConfigFile(host)));
    }

    [Fact]
    public async Task Start_at_login_changes_the_os_entry_before_the_file_is_written()
    {
        var os = new FakeAutostart();
        await using var host = await StartAsync(os);
        var fileExistedAtSet = true;
        os.OnSet = _ => fileExistedAtSet = File.Exists(ConfigFile(host));

        var response = await PutAsync(host, "{ \"startAtLogin\": true }");

        Assert.Equal(200, response.Status);
        Assert.False(fileExistedAtSet);
        Assert.Equal([true], os.Sets);
        Assert.True(response.Json.GetProperty("startAtLogin").GetProperty("configured").GetBoolean());
        Assert.Equal("on", response.Json.GetProperty("startAtLogin").GetProperty("actual").GetString());
    }

    [Fact]
    public async Task An_unparseable_config_file_answers_409_and_leaves_the_file_and_the_os_entry_alone()
    {
        var os = new FakeAutostart();
        await using var host = await StartAsync(os);
        Directory.CreateDirectory(host.ConfigDir);
        await File.WriteAllTextAsync(ConfigFile(host), "{ \"theme\": \"dark\", }");
        var before = await File.ReadAllBytesAsync(ConfigFile(host));

        var startAtLogin = await PutAsync(host, "{ \"startAtLogin\": true }");
        var theme = await PutAsync(host, "{ \"theme\": \"light\" }");

        Assert.All([startAtLogin, theme], r => Assert.Equal((409, "config-unreadable"), (r.Status, r.Error)));
        Assert.Empty(os.Sets);
        Assert.Equal(before, await File.ReadAllBytesAsync(ConfigFile(host)));
    }

    [Fact]
    public async Task Get_reports_config_error_only_for_an_existing_unparseable_file()
    {
        await using var host = await StartAsync(new FakeAutostart());
        var missing = await host.GetAsync(Route);
        Directory.CreateDirectory(host.ConfigDir);
        await File.WriteAllTextAsync(ConfigFile(host), "{ \"theme\": \"dark\", }");

        var broken = await host.GetAsync(Route);

        Assert.False(missing.Json.TryGetProperty("configError", out _));
        Assert.False(string.IsNullOrEmpty(broken.Json.GetProperty("configError").GetString()));
        Assert.Equal("system", broken.Json.GetProperty("values").GetProperty("theme").GetString());
    }

    [Fact]
    public async Task A_put_without_start_at_login_never_touches_the_os_entry()
    {
        var os = new FakeAutostart();
        await using var host = await StartAsync(os);

        await PutAsync(host, "{ \"theme\": \"light\" }");

        Assert.Empty(os.Sets);
    }

    [Fact]
    public async Task Put_appends_a_cfgdash_settings_audit_line_for_the_ui_listener()
    {
        await using var host = await StartAsync(new FakeAutostart());

        await PutAsync(host, "{ \"theme\": \"dark\" }");

        var line = Assert.Single(File.ReadAllLines(Path.Combine(host.CacheDir, "edits", "audit.jsonl")));
        using var audit = JsonDocument.Parse(line);
        Assert.Equal("cfgdash-settings", audit.RootElement.GetProperty("kind").GetString());
        Assert.Equal("ui", audit.RootElement.GetProperty("listener").GetString());
        Assert.Equal("theme", audit.RootElement.GetProperty("changes")[0].GetProperty("key").GetString());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    public async Task Tailnet_listener_answers_not_found_and_does_not_apply_the_put(string method)
    {
        var os = new FakeAutostart();
        await using var host = await StartAsync(os);

        var response = await UiRequests.SendAsync(host, new HttpMethod(method), Route, method == "PUT" ? "{ \"startAtLogin\": true }" : null, tailnet: true);

        Assert.Equal(404, response.Status);
        Assert.Empty(os.Sets);
        Assert.False(File.Exists(ConfigFile(host)));
    }
}
