using System.Net;

namespace Cfgdash.Host.Tests;

public sealed class UiServerFixture : IAsyncLifetime
{
    internal ServerHost Host { get; private set; } = null!;

    public async Task InitializeAsync() => Host = await ServerHost.StartAsync();

    public async Task DisposeAsync() => await Host.DisposeAsync();
}

public sealed class SecurityTests(UiServerFixture fixture) : IClassFixture<UiServerFixture>
{
    private const string Csp = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";

    private ServerHost Host => fixture.Host;

    [Theory]
    [InlineData("127.0.0.1:{0}")]
    [InlineData("localhost:{0}")]
    [InlineData("LocalHost:{0}")]
    public async Task Loopback_host_names_with_the_bound_port_are_served(string template)
    {
        var response = await Host.SendAsync(HttpMethod.Get, "/api/v1/health", Host.Port, string.Format(template, Host.Port), "");

        Assert.Equal(200, response.Status);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:{1}")]
    [InlineData("evil.example:{0}")]
    [InlineData("127.0.0.1.example:{0}")]
    [InlineData("[::1]:{0}")]
    public async Task Other_host_headers_are_rejected_before_any_snapshot_is_built(string template)
    {
        var before = Host.Snapshots.CountFor(Host.StartDir);

        var response = await Host.SendAsync(HttpMethod.Get, "/api/v1/snapshot?refresh=true", Host.Port, string.Format(template, Host.Port, Host.Port + 1), "");

        Assert.Equal(400, response.Status);
        Assert.Equal("bad-host", response.Error);
        Assert.Equal(before, Host.Snapshots.CountFor(Host.StartDir));
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    [InlineData("Sec-Fetch-Site", "same-site")]
    [InlineData("Origin", "http://evil.example")]
    [InlineData("Origin", "null")]
    [InlineData("Origin", "http://127.0.0.1:{1}")]
    public async Task Cross_site_requests_are_forbidden(string header, string template)
    {
        var response = await Host.GetAsync("/api/v1/health", "", (header, string.Format(template, Host.Port, Host.Port + 1)));

        Assert.Equal(403, response.Status);
        Assert.Equal("forbidden", response.Error);
        Assert.Equal("cross-site", response.Reason);
    }

    [Theory]
    [InlineData("Sec-Fetch-Site", "none")]
    [InlineData("Sec-Fetch-Site", "same-origin")]
    [InlineData("Origin", "http://127.0.0.1:{0}")]
    public async Task Same_origin_browser_requests_are_served(string header, string template)
    {
        var response = await Host.GetAsync("/api/v1/health", "", (header, string.Format(template, Host.Port)));

        Assert.Equal(200, response.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Api_without_the_valid_token_is_unauthenticated(string? bearer)
    {
        var response = await Host.GetAsync("/api/v1/snapshot", bearer);

        Assert.Equal(401, response.Status);
        Assert.Equal("unauthenticated", response.Error);
    }

    [Fact]
    public async Task Api_accepts_the_bearer_token_and_the_session_cookie_but_not_the_raw_token_as_cookie()
    {
        var session = (await Host.GetAsync($"/?token={Host.Token}", null)).SessionCookieValue;

        var bearer = await Host.GetAsync("/api/v1/health");
        var cookie = await Host.GetAsync("/api/v1/health", null, ("Cookie", $"cfgdash_session={session}"));
        var rawTokenCookie = await Host.GetAsync("/api/v1/health", null, ("Cookie", $"cfgdash_session={Host.Token}"));
        var wrongCookie = await Host.GetAsync("/api/v1/health", null, ("Cookie", "cfgdash_session=wrong-token"));

        Assert.Equal(200, bearer.Status);
        Assert.Equal(200, cookie.Status);
        Assert.Equal(401, rawTokenCookie.Status);
        Assert.Equal(401, wrongCookie.Status);
    }

    [Fact]
    public async Task Valid_query_token_on_root_redirects_and_sets_a_strict_http_only_session_cookie()
    {
        var response = await Host.GetAsync($"/?token={Host.Token}", null);

        Assert.Equal(302, response.Status);
        Assert.Equal("/", response.Message.Headers.Location?.OriginalString);
        var cookie = response.SetCookie!;
        Assert.StartsWith("cfgdash_session=", cookie);
        Assert.NotEmpty(response.SessionCookieValue!);
        Assert.NotEqual(Host.Token, response.SessionCookieValue);
        Assert.DoesNotContain(Host.Token, cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_query_token_is_rejected_without_a_cookie()
    {
        var response = await Host.GetAsync("/?token=wrong-token", null);

        Assert.Equal(401, response.Status);
        Assert.Equal("unauthenticated", response.Error);
        Assert.Null(response.SetCookie);
    }

    [Fact]
    public async Task Root_and_spa_routes_are_public_on_the_ui_listener()
    {
        var root = await Host.GetAsync("/", null);
        var route = await Host.GetAsync("/some/client/route", null);

        Assert.Equal(200, root.Status);
        Assert.Equal(200, route.Status);
    }

    public static TheoryData<string, string> WriteAttempts()
    {
        var data = new TheoryData<string, string>();
        string[] paths = ["/api/v1/health", "/api/v1/snapshot", "/api/v1/files/f1", "/api/v1/search?q=ab", "/api/v1/dirs", "/api/v1/nope", "/api/v2/anything", "/api/v1/shutdown", "/"];
        foreach (var method in new[] { "POST", "PUT", "PATCH", "DELETE" })
        {
            foreach (var path in paths)
            {
                if (!(method == "POST" && path == "/api/v1/shutdown"))
                {
                    data.Add(method, path);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(WriteAttempts))]
    public async Task Authenticated_write_methods_never_reach_a_handler(string method, string path)
    {
        var response = await Host.SendAsync(new HttpMethod(method), path, Host.Port, null, "");

        Assert.True(response.Status is 404 or 405, $"{method} {path} answered {response.Status}");
    }

    [Fact]
    public async Task Api_responses_carry_the_security_headers_and_no_store()
    {
        var response = await Host.GetAsync("/api/v1/health");

        Assert.Equal(Csp, response.Header("Content-Security-Policy"));
        Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
        Assert.Equal("no-referrer", response.Header("Referrer-Policy"));
        Assert.Equal("no-store", response.Header("Cache-Control"));
    }

    [Fact]
    public async Task Static_responses_carry_the_security_headers()
    {
        var response = await Host.GetAsync("/", null);

        Assert.Equal(Csp, response.Header("Content-Security-Policy"));
        Assert.Equal("nosniff", response.Header("X-Content-Type-Options"));
        Assert.Equal("no-referrer", response.Header("Referrer-Policy"));
    }

    [Fact]
    public async Task Cookie_only_caller_cannot_shut_the_server_down()
    {
        await using var host = await ServerHost.StartAsync();
        var session = (await host.GetAsync($"/?token={host.Token}", null)).SessionCookieValue;

        var response = await host.SendAsync(HttpMethod.Post, "/api/v1/shutdown", host.Port, null, null, ("Cookie", $"cfgdash_session={session}"));
        var stopped = host.Server.WaitForShutdownAsync();
        await Task.WhenAny(stopped, Task.Delay(300));

        Assert.Equal(401, response.Status);
        Assert.False(stopped.IsCompleted);
    }

    [Fact]
    public async Task Bearer_shutdown_answers_202_and_stops_with_reason_requested()
    {
        await using var host = await ServerHost.StartAsync();
        var stopped = host.Server.WaitForShutdownAsync();

        var response = await host.SendAsync(HttpMethod.Post, "/api/v1/shutdown", host.Port, null, "");

        Assert.Equal(202, response.Status);
        Assert.Same(stopped, await Task.WhenAny(stopped, Task.Delay(10_000)));
        Assert.Equal("requested", await stopped);
    }

    [Fact]
    public async Task Request_log_records_method_path_and_status_but_never_the_token_or_query()
    {
        await using var host = await ServerHost.StartAsync();

        var response = await host.GetAsync($"/?token={host.Token}", null);
        await host.GetAsync("/api/v1/dirs?path=secret-query-value");
        await Poll.UntilAsync(() => host.ReadLog() is var text && text.Contains("GET / 302") && text.Contains("/api/v1/dirs"));
        var log = host.ReadLog();

        Assert.Equal(HttpStatusCode.Redirect, response.Message.StatusCode);
        Assert.Contains("GET / 302", log);
        Assert.Contains("GET /api/v1/dirs", log);
        Assert.DoesNotContain(host.Token, log);
        Assert.DoesNotContain("token=", log);
        Assert.DoesNotContain("secret-query-value", log);
    }

    [Fact]
    public async Task Request_log_caps_the_logged_path_at_256_characters()
    {
        await using var host = await ServerHost.StartAsync();

        await host.GetAsync("/" + new string('a', 600), null);
        await Poll.UntilAsync(() => host.ReadLog().Contains("GET /"));
        var log = host.ReadLog();

        Assert.Contains("GET /" + new string('a', 255) + " 200", log);
        Assert.DoesNotContain(new string('a', 256), log);
    }
}
