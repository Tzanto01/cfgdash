using System.Text.Json;
using Xunit.Abstractions;

namespace Cfgdash.Host.Tests;

internal sealed class RemoteInstanceFactAttribute : FactAttribute
{
    public RemoteInstanceFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CFGDASH_SMOKE_REMOTE_URL"))
            || !File.Exists(Environment.GetEnvironmentVariable("CFGDASH_SMOKE_REMOTE_RUNSTATE") ?? string.Empty))
        {
            Skip = "CFGDASH_SMOKE_REMOTE_URL and CFGDASH_SMOKE_REMOTE_RUNSTATE (a local copy of that instance's run.json) are not set";
        }
    }
}

/// <summary>Smokes an already running instance, for example another machine over the tailnet. Opt in with CFGDASH_SMOKE_REMOTE_URL (base URL) and CFGDASH_SMOKE_REMOTE_RUNSTATE; the token is read from that run.json and never printed.</summary>
[Trait("Category", "RemoteSmoke")]
public sealed class RemoteInstanceTests(ITestOutputHelper output)
{
    [RemoteInstanceFact]
    public async Task Running_instance_requires_the_token_serves_its_snapshot_and_ignores_a_forged_identity_header()
    {
        var baseUrl = Environment.GetEnvironmentVariable("CFGDASH_SMOKE_REMOTE_URL")!.TrimEnd('/');
        string token;
        using (var runState = JsonDocument.Parse(File.ReadAllText(Environment.GetEnvironmentVariable("CFGDASH_SMOKE_REMOTE_RUNSTATE")!)))
        {
            token = runState.RootElement.GetProperty("token").GetString()!;
        }

        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
        var bearer = ("Authorization", "Bearer " + token);

        async Task<(int Status, string Body)> GetAsync(string path, params (string Name, string Value)[] headers)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await http.SendAsync(request);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(200, (await GetAsync("/api/v1/health", bearer)).Status);
        Assert.Equal(401, (await GetAsync("/api/v1/health")).Status);
        Assert.Equal(200, (await GetAsync("/api/v1/health", bearer, ("Tailscale-User-Login", "other@example.com"))).Status);

        using (var login = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/?token={Uri.EscapeDataString(token)}"))
        using (var loginResponse = await http.SendAsync(login))
        {
            Assert.Equal(302, (int)loginResponse.StatusCode);
            var cookie = loginResponse.Headers.TryGetValues("Set-Cookie", out var values) ? values.Single().Split(';')[0] : null;
            Assert.True(cookie?.StartsWith("cfgdash_session=", StringComparison.Ordinal) == true, "no cfgdash_session cookie was set");
            Assert.Equal(200, (await GetAsync("/api/v1/health", ("Cookie", cookie!))).Status);
        }

        var (snapshotStatus, snapshotBody) = await GetAsync("/api/v1/snapshot", bearer);
        Assert.Equal(200, snapshotStatus);
        using var snapshot = JsonDocument.Parse(snapshotBody);
        var root = snapshot.RootElement;
        var machine = root.GetProperty("machine");
        var collectors = root.GetProperty("collectors").EnumerateArray().ToList();
        var hostname = machine.GetProperty("hostname").GetString();
        output.WriteLine($"hostname: {hostname}");
        output.WriteLine($"os: {machine.GetProperty("os").GetProperty("family").GetString()} ({machine.GetProperty("os").GetProperty("description").GetString()}, {machine.GetProperty("os").GetProperty("arch").GetString()})");
        foreach (var collector in collectors)
        {
            var reason = collector.TryGetProperty("reason", out var r) ? r.GetString() : null;
            output.WriteLine($"  {collector.GetProperty("id").GetString()} -> {collector.GetProperty("state").GetString()}{(reason is null ? string.Empty : ": " + reason)}");
        }

        var sections = root.GetProperty("sections").GetArrayLength();
        var files = root.GetProperty("files").EnumerateArray().ToList();
        output.WriteLine($"sections: {sections}, files: {files.Count}");

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(string.IsNullOrEmpty(hostname));
        Assert.All(collectors, collector =>
        {
            var state = collector.GetProperty("state").GetString();
            Assert.Contains(state, new[] { "active", "notPresent", "unavailable" });
            if (state != "active")
            {
                Assert.False(string.IsNullOrWhiteSpace(collector.TryGetProperty("reason", out var reason) ? reason.GetString() : null), $"{collector.GetProperty("id").GetString()} is {state} without a reason");
            }
        });

        if (files.Count > 0)
        {
            var id = files[0].GetProperty("id").GetString()!;
            var (fileStatus, fileBody) = await GetAsync($"/api/v1/files/{id}", bearer);
            Assert.Equal(200, fileStatus);
            using var file = JsonDocument.Parse(fileBody);
            Assert.Equal(id, file.RootElement.GetProperty("id").GetString());
        }

        var term = hostname!.Length >= 2 ? hostname : collectors[0].GetProperty("id").GetString()!;
        var (searchStatus, searchBody) = await GetAsync($"/api/v1/search?q={Uri.EscapeDataString(term)}", bearer);
        Assert.Equal(200, searchStatus);
        using var search = JsonDocument.Parse(searchBody);
        Assert.Equal(JsonValueKind.Array, search.RootElement.GetProperty("results").ValueKind);
    }
}
