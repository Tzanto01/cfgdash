using System.Net;
using System.Text.Json;

namespace Cfgdash.Host.Tests;

internal sealed class FakeOwner(bool daemon) : IConnectionOwner
{
    public List<(IPEndPoint Remote, IPEndPoint Local)> Asked { get; } = [];

    public bool IsTailscaleDaemon(IPEndPoint remote, IPEndPoint local)
    {
        Asked.Add((remote, local));
        return daemon;
    }
}

public sealed class FleetReceivingTests
{
    private const string Peer = "192.0.2.10";
    private static readonly (string, string) Fleet = ("X-Cfgdash-Fleet", "1");
    private static readonly (string, string) Forwarded = ("X-Forwarded-For", Peer);

    internal static string WhoisOf(string name, string login, params string[] tags) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Node"] = tags.Length == 0 ? new Dictionary<string, object> { ["Name"] = name } : new Dictionary<string, object> { ["Name"] = name, ["Tags"] = tags },
            ["UserProfile"] = new Dictionary<string, object> { ["LoginName"] = login },
        });

    private static int WhoisCalls(ServerHost host) => host.Processes.Calls.Count(c => c.Args is ["whois", ..]);

    private static async Task<(ServerHost Host, FakeOwner Owner)> StartAsync(string? whois, bool? enabled = true, bool daemon = true, IReadOnlyList<string>? allowedTags = null, string status = """{"TUN": true}""")
    {
        var host = await ServerHost.StartAsync(ServerHost.TailnetOptions(allowedTags), processes: new FakeProcesses(call =>
            call.Args is ["status", "--json"] ? FakeProcesses.Ok(status)
            : call.Args is ["whois", "--json", _] && whois is not null ? FakeProcesses.Ok(whois) : FakeProcesses.Fail("no such node")));
        var owner = new FakeOwner(daemon);
        if (enabled is { } on)
        {
            host.Server.FleetEnabled = () => on;
        }

        host.Server.ConnectionOwner = owner;
        return (host, owner);
    }

    [Theory]
    [InlineData("/api/v1/health", false)]
    [InlineData("/api/v1/snapshot", false)]
    [InlineData("/api/v1/findings", false)]
    [InlineData("/api/v1/health", true)]
    [InlineData("/api/v1/snapshot", true)]
    [InlineData("/api/v1/findings", true)]
    public async Task Allowed_peers_read_the_three_paths_without_a_token(string path, bool ownUntaggedDevice)
    {
        var whois = ownUntaggedDevice
            ? WhoisOf("laptop.example.ts.net.", ServerHost.Login.ToUpperInvariant())
            : WhoisOf("laptop.example.ts.net.", "tagged-devices", "tag:pc");
        var (host, owner) = await StartAsync(whois, allowedTags: ["tag:pc"]);
        await using var _ = host;

        var response = await host.TailAsync(path, bearer: null, headers: [Fleet, Forwarded]);

        Assert.Equal(200, response.Status);
        var asked = Assert.Single(owner.Asked);
        Assert.Equal(host.BackendPort, asked.Local.Port);
        Assert.Equal(IPAddress.Loopback, asked.Remote.Address);
    }

    [Theory]
    [InlineData("GET", "/api/v1/health", "2")]
    [InlineData("GET", "/api/v1/health", "true")]
    [InlineData("GET", "/api/v1/health", "1|1")]
    [InlineData("POST", "/api/v1/health", "1")]
    [InlineData("HEAD", "/api/v1/health", "1")]
    [InlineData("GET", "/api/v1/files/x", "1")]
    [InlineData("GET", "/api/v1/search?q=ab", "1")]
    [InlineData("GET", "/api/v1/dirs", "1")]
    [InlineData("GET", "/api/v2/settings", "1")]
    [InlineData("GET", "/", "1")]
    [InlineData("GET", "/api/v1/snapshot?dir=/tmp", "1")]
    [InlineData("GET", "/api/v1/findings?refresh=true", "1")]
    public async Task Malformed_fleet_requests_are_forbidden_without_asking_tailscale(string method, string path, string headerValues)
    {
        var (host, owner) = await StartAsync(WhoisOf("laptop.example.ts.net.", "tagged-devices", "tag:pc"), allowedTags: ["tag:pc"]);
        await using var _ = host;
        (string, string)[] headers = [.. headerValues.Split('|').Select(v => ("X-Cfgdash-Fleet", v)), Forwarded];

        var response = await host.TailAsync(path, bearer: null, method: new HttpMethod(method), headers: headers);

        Assert.Equal(403, response.Status);
        Assert.Equal(0, WhoisCalls(host));
        Assert.Empty(owner.Asked);
    }

    [Fact]
    public async Task Forbidden_fleet_requests_answer_with_the_fleet_reason()
    {
        var (host, _) = await StartAsync(null);
        await using var _ = host;

        var response = await host.TailAsync("/api/v1/health", bearer: null, headers: [Fleet, Forwarded]);

        Assert.Equal(403, response.Status);
        Assert.Equal("forbidden", response.Error);
        Assert.Equal("fleet", response.Reason);
    }

    [Fact]
    public async Task Fleet_disabled_refuses_even_an_allowed_peer()
    {
        var (host, _) = await StartAsync(WhoisOf("laptop.example.ts.net.", "tagged-devices", "tag:pc"), enabled: false, allowedTags: ["tag:pc"]);
        await using var _ = host;

        var response = await host.TailAsync("/api/v1/health", bearer: null, headers: [Fleet, Forwarded]);

        Assert.Equal(403, response.Status);
        Assert.Equal(0, WhoisCalls(host));
    }

    [Fact]
    public async Task Fleet_enabled_comes_from_the_cfgdash_config_file()
    {
        var (host, _) = await StartAsync(WhoisOf("laptop.example.ts.net.", "tagged-devices", "tag:pc"), enabled: null, allowedTags: ["tag:pc"]);
        await using var _ = host;

        var before = await host.TailAsync("/api/v1/health", bearer: null, headers: [Fleet, Forwarded]);
        Directory.CreateDirectory(host.ConfigDir);
        File.WriteAllText(Path.Combine(host.ConfigDir, "config.json"), """{ "fleet": { "enabled": true } }""");
        var after = await host.TailAsync("/api/v1/health", bearer: null, headers: [Fleet, Forwarded]);

        Assert.Equal([403, 200], [before.Status, after.Status]);
    }

    [Fact]
    public async Task A_connection_not_owned_by_the_daemon_is_refused_before_whois()
    {
        var (host, _) = await StartAsync(WhoisOf("laptop.example.ts.net.", "tagged-devices", "tag:pc"), daemon: false, allowedTags: ["tag:pc"]);
        await using var _ = host;

        var response = await host.TailAsync("/api/v1/health", bearer: null, headers: [Fleet, Forwarded]);

        Assert.Equal(403, response.Status);
        Assert.Equal(0, WhoisCalls(host));
    }

    [Theory]
    [InlineData("foreign-owner", null)]
    [InlineData("shared-in", "tag:pc")]
    [InlineData("shared-in-own-login", null)]
    [InlineData("tag-mismatch", "tag:server")]
    [InlineData("unknown-node", null)]
    public async Task Peers_outside_the_owners_tailnet_identity_are_refused(string node, string? allowedTag)
    {
        var whois = node switch
        {
            "foreign-owner" => WhoisOf("laptop.example.ts.net.", "other-user"),
            "shared-in" => WhoisOf("laptop.other.ts.net.", "tagged-devices", "tag:pc"),
            "shared-in-own-login" => WhoisOf("laptop.other.ts.net.", ServerHost.Login),
            "tag-mismatch" => WhoisOf("laptop.example.ts.net.", "tagged-devices", "tag:pc"),
            _ => null,
        };
        var (host, _) = await StartAsync(whois, allowedTags: allowedTag is null ? null : [allowedTag]);
        await using var _ = host;

        var response = await host.TailAsync("/api/v1/health", bearer: null, headers: [Fleet, Forwarded]);

        Assert.Equal(403, response.Status);
        Assert.Equal("fleet", response.Reason);
    }

    [Fact]
    public async Task A_tagged_node_is_refused_without_allowed_tags_and_accepted_with_its_tag()
    {
        var whois = WhoisOf("laptop.example.ts.net.", "tagged-devices", "tag:pc");
        var (noTags, _) = await StartAsync(whois);
        await using var _1 = noTags;
        var (withTag, _) = await StartAsync(whois, allowedTags: ["tag:pc"]);
        await using var _2 = withTag;

        var refused = await noTags.TailAsync("/api/v1/health", bearer: null, headers: [Fleet, Forwarded]);
        var accepted = await withTag.TailAsync("/api/v1/health", bearer: null, headers: [Fleet, Forwarded]);

        Assert.Equal(403, refused.Status);
        Assert.Equal("fleet", refused.Reason);
        Assert.Equal(200, accepted.Status);
    }

    [Theory]
    [InlineData("""{"TUN": false}""")]
    [InlineData("""{"BackendState": "Running"}""")]
    [InlineData("not json")]
    public async Task A_userspace_tailscaled_is_refused_before_the_owner_and_whois_checks(string status)
    {
        var (host, owner) = await StartAsync(WhoisOf("laptop.example.ts.net.", ServerHost.Login), status: status);
        await using var _ = host;

        var response = await host.TailAsync("/api/v1/health", bearer: null, headers: [Fleet, Forwarded]);

        Assert.Equal(403, response.Status);
        Assert.Equal("fleet", response.Reason);
        Assert.Equal(0, WhoisCalls(host));
        Assert.Empty(owner.Asked);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("192.0.2.10|192.0.2.11")]
    [InlineData("192.0.2.10, 192.0.2.11")]
    [InlineData("not-an-ip")]
    public async Task Spoofed_forwarded_addresses_are_refused_without_whois(string? forwardedFor)
    {
        var (host, _) = await StartAsync(WhoisOf("laptop.example.ts.net.", "tagged-devices", "tag:pc"), allowedTags: ["tag:pc"]);
        await using var _ = host;
        (string, string)[] headers = [Fleet, .. (forwardedFor?.Split('|') ?? []).Select(v => ("X-Forwarded-For", v))];

        var response = await host.TailAsync("/api/v1/health", bearer: null, headers: headers);

        Assert.Equal(403, response.Status);
        Assert.Equal(0, WhoisCalls(host));
    }

    [Fact]
    public async Task A_fleet_request_does_not_restart_the_idle_window()
    {
        var (host, _) = await StartAsync(WhoisOf("laptop.example.ts.net.", "tagged-devices", "tag:pc"), allowedTags: ["tag:pc"]);
        await using var _ = host;
        var stopped = host.Server.WaitForShutdownAsync();

        host.Time.Advance(TimeSpan.FromMinutes(20));
        var response = await host.TailAsync("/api/v1/health", bearer: null, headers: [Fleet, Forwarded]);
        host.Time.Advance(TimeSpan.FromMinutes(11));

        Assert.Equal(200, response.Status);
        Assert.Same(stopped, await Task.WhenAny(stopped, Task.Delay(10_000)));
        Assert.Equal("idle", await stopped);
    }

    [Fact]
    public async Task The_ui_listener_ignores_the_fleet_header()
    {
        var (host, _) = await StartAsync(null);
        await using var _ = host;

        var withoutToken = await host.GetAsync("/api/v1/health", null, Fleet);
        var withToken = await host.GetAsync("/api/v1/health", "", Fleet);

        Assert.Equal(401, withoutToken.Status);
        Assert.Equal(200, withToken.Status);
    }
}
