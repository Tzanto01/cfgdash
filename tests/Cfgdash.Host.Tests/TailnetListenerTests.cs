using Cfgdash.Core;

namespace Cfgdash.Host.Tests;

public sealed class TailnetFixture : IAsyncLifetime
{
    internal ServerHost Host { get; private set; } = null!;

    public async Task InitializeAsync() => Host = await ServerHost.StartAsync(ServerHost.TailnetOptions());

    public async Task DisposeAsync() => await Host.DisposeAsync();
}

public sealed class TailnetListenerTests(TailnetFixture fixture) : IClassFixture<TailnetFixture>
{
    private static readonly (string, string)[] Identity = [("Tailscale-User-Login", ServerHost.Login)];

    private ServerHost Host => fixture.Host;

    [Theory]
    [InlineData("{2}:{0}")]
    [InlineData("{2}")]
    [InlineData("PC.Example.TS.net:{0}")]
    public async Task Tailnet_dns_name_hosts_are_served(string template)
    {
        var host = string.Format(template, Host.Server.HttpsPort, Host.BackendPort, ServerHost.DnsName);

        var response = await Host.TailAsync("/api/v1/health", host: host, headers: Identity);

        Assert.Equal(200, response.Status);
    }

    [Theory]
    [InlineData("127.0.0.1:{1}")]
    [InlineData("localhost:{1}")]
    [InlineData("other.example.ts.net:{0}")]
    [InlineData("pc.example.ts.net:{3}")]
    public async Task Other_hosts_are_rejected_on_the_tailnet_listener(string template)
    {
        var host = string.Format(template, Host.Server.HttpsPort, Host.BackendPort, ServerHost.DnsName, Host.Server.HttpsPort + 1);

        var response = await Host.TailAsync("/api/v1/health", host: host, headers: Identity);

        Assert.Equal(400, response.Status);
        Assert.Equal("bad-host", response.Error);
    }

    [Fact]
    public async Task Ui_listener_does_not_accept_the_tailnet_dns_name()
    {
        var response = await Host.SendAsync(HttpMethod.Get, "/api/v1/health", Host.Port, Host.TailnetHost, "");

        Assert.Equal(400, response.Status);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/api/v1/health")]
    public async Task Tailnet_requests_need_the_token_even_with_a_valid_identity(string path)
    {
        var response = await Host.TailAsync(path, bearer: null, headers: Identity);

        Assert.Equal(401, response.Status);
        Assert.Equal("unauthenticated", response.Error);
    }

    [Fact]
    public async Task Query_token_with_identity_redirects_with_a_secure_cookie()
    {
        var response = await Host.TailAsync($"/?token={Host.Token}", bearer: null, headers: Identity);

        Assert.Equal(302, response.Status);
        var cookie = response.SetCookie!;
        Assert.StartsWith($"cfgdash_session={Host.Token}", cookie);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Query_token_without_a_known_identity_is_forbidden_and_sets_no_cookie()
    {
        var response = await Host.TailAsync($"/?token={Host.Token}", bearer: null, headers: [("Tailscale-User-Login", "other@example.com")]);

        Assert.Equal(403, response.Status);
        Assert.Equal("tailnet-identity", response.Reason);
        Assert.Null(response.SetCookie);
    }

    [Fact]
    public async Task Allowed_login_header_matches_case_insensitively_and_the_session_cookie_works()
    {
        var bearer = await Host.TailAsync("/api/v1/health", headers: [("Tailscale-User-Login", "USER@GitHub")]);
        var cookie = await Host.TailAsync("/api/v1/health", bearer: null, headers: [.. Identity, ("Cookie", $"cfgdash_session={Host.Token}")]);

        Assert.Equal(200, bearer.Status);
        Assert.Equal(200, cookie.Status);
    }

    [Fact]
    public async Task Tailnet_https_origin_is_same_origin_and_http_origin_is_not()
    {
        var https = await Host.TailAsync("/api/v1/health", headers: [.. Identity, ("Origin", $"https://{Host.TailnetHost}")]);
        var http = await Host.TailAsync("/api/v1/health", headers: [.. Identity, ("Origin", $"http://{Host.TailnetHost}")]);

        Assert.Equal(200, https.Status);
        Assert.Equal(403, http.Status);
        Assert.Equal("cross-site", http.Reason);
    }

    [Fact]
    public async Task Another_users_login_header_is_forbidden_without_asking_tailscale()
    {
        await using var host = await StartAsync(() => throw new InvalidOperationException("whois must not run"));

        var response = await host.TailAsync("/api/v1/health", headers: [("Tailscale-User-Login", "other@example.com"), ("X-Forwarded-For", "192.0.2.10")]);

        Assert.Equal(403, response.Status);
        Assert.Equal("forbidden", response.Error);
        Assert.Equal("tailnet-identity", response.Reason);
        Assert.Empty(host.Processes.Calls);
    }

    [Theory]
    [InlineData("tagged", "192.0.2.10", "", 200)]
    [InlineData("tagged", "192.0.2.10", "tag:pc", 200)]
    [InlineData("untagged", "192.0.2.10", "", 403)]
    [InlineData("shared-in", "192.0.2.10", "", 403)]
    [InlineData("lookalike", "192.0.2.10", "", 403)]
    [InlineData("tagged", "192.0.2.10", "tag:server", 403)]
    [InlineData("tagged", null, "", 403)]
    [InlineData("tagged", "192.0.2.10, 192.0.2.11", "", 403)]
    [InlineData("tagged", "192.0.2.10|192.0.2.11", "", 403)]
    [InlineData("tagged", "not-an-ip", "", 403)]
    [InlineData("fails", "192.0.2.10", "", 403)]
    public async Task Tagged_devices_are_identified_by_whois_of_the_forwarded_address(string node, string? forwardedFor, string allowedTag, int expected)
    {
        var answer = node switch
        {
            "tagged" => ServerHost.Whois("laptop.example.ts.net.", "tag:pc"),
            "untagged" => ServerHost.Whois("laptop.example.ts.net."),
            "shared-in" => ServerHost.Whois("laptop.other.ts.net.", "tag:pc"),
            "lookalike" => ServerHost.Whois("laptop.evilexample.ts.net.", "tag:pc"),
            _ => null,
        };
        await using var host = await StartAsync(() => answer ?? throw new FailWhois(), allowedTag.Length == 0 ? null : [allowedTag]);
        (string, string)[] headers = forwardedFor is null ? [] : [.. forwardedFor.Split('|').Select(v => ("X-Forwarded-For", v))];

        var response = await host.TailAsync("/api/v1/health", headers: headers);

        Assert.Equal(expected, response.Status);
        if (expected == 403)
        {
            Assert.Equal("tailnet-identity", response.Reason);
        }
    }

    [Fact]
    public async Task Whois_answers_are_cached_for_60_seconds_per_address()
    {
        var answer = ServerHost.Whois("laptop.example.ts.net.", "tag:pc");
        await using var host = await StartAsync(() => answer);
        (string, string)[] headers = [("X-Forwarded-For", "192.0.2.10")];

        var first = await host.TailAsync("/api/v1/health", headers: headers);
        var second = await host.TailAsync("/api/v1/health", headers: headers);
        answer = ServerHost.Whois("laptop.example.ts.net.");
        host.Time.Advance(TimeSpan.FromSeconds(30));
        var stillCached = await host.TailAsync("/api/v1/health", headers: headers);
        host.Time.Advance(TimeSpan.FromSeconds(31));
        var refreshed = await host.TailAsync("/api/v1/health", headers: headers);

        Assert.Equal([200, 200, 200, 403], [first.Status, second.Status, stillCached.Status, refreshed.Status]);
        Assert.Equal(2, host.Processes.Calls.Count);
        var call = host.Processes.Calls[0];
        Assert.Equal("tailscale-fake", call.Executable);
        Assert.Equal(["whois", "--json", "192.0.2.10"], call.Args);
    }

    private static Task<ServerHost> StartAsync(Func<string> whois, IReadOnlyList<string>? allowedTags = null) =>
        ServerHost.StartAsync(ServerHost.TailnetOptions(allowedTags), processes: new FakeProcesses(call =>
        {
            try
            {
                return call.Args is ["whois", "--json", _] ? FakeProcesses.Ok(whois()) : throw new InvalidOperationException(call.Line);
            }
            catch (FailWhois)
            {
                return FakeProcesses.Fail("no such node");
            }
        }));

    private sealed class FailWhois : Exception;
}
