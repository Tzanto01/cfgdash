using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cfgdash.Core;
using Microsoft.Extensions.Time.Testing;

namespace Cfgdash.Host.Tests;

internal sealed class FakePeerHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return respond(request, cancellationToken);
    }

    public static Task<HttpResponseMessage> Json(HttpStatusCode status, string body = "") =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
}

public sealed class FleetRoutesTests
{
    private const string Export = """
        {"machine":{"hostname":"other.example","collectedAt":"2026-01-01T00:00:00+00:00","os":{"family":"linux"}},
         "context":{"targetDir":"/x","paths":[]},
         "collectors":[{"id":"c9","state":"ok"}],
         "sections":[{"collectorId":"c9","blocks":[{"id":"b1","type":"kv","rows":[{"key":"k","value":"v"}]}]}]}
        """;

    private static readonly string Status = JsonSerializer.Serialize(new
    {
        Self = new { HostName = "pc", DNSName = "pc.example.ts.net.", OS = "linux" },
        CurrentTailnet = new { MagicDNSSuffix = "example.ts.net" },
        User = new Dictionary<string, object> { ["1"] = new { LoginName = ServerHost.Login }, ["2"] = new { LoginName = "other-user" } },
        Peer = new Dictionary<string, object>
        {
            ["a"] = new { HostName = "alpha", DNSName = "alpha.example.ts.net.", OS = "linux", Online = true, UserID = 1 },
            ["b"] = new { HostName = "bravo", DNSName = "bravo.example.ts.net.", OS = "windows", Online = true, UserID = 9, Tags = new[] { "tag:pc" } },
            ["c"] = new { HostName = "charlie", DNSName = "charlie.example.ts.net.", OS = "linux", Online = false, UserID = 1 },
            ["d"] = new { HostName = "delta", DNSName = "delta.example.ts.net.", OS = "linux", Online = true, UserID = 2 },
            ["e"] = new { HostName = "echo", DNSName = "echo.other.ts.net.", OS = "linux", Online = true, UserID = 9, Tags = new[] { "tag:pc" } },
        },
    });

    private static FleetPeers NewFleet(FakeProcesses processes, FakeTimeProvider time, HttpMessageHandler handler, IReadOnlyList<string>? allowedTags = null) =>
        new(processes, "tailscale-fake", [ServerHost.Login], allowedTags ?? [], TestCore.Machine(), time, handler);

    private static FakeProcesses StatusProcesses(string status) =>
        new(call => call.Args is ["status", "--json"] ? FakeProcesses.Ok(status) : throw new InvalidOperationException(call.Line));

    private static string SinglePeerStatus() => JsonSerializer.Serialize(new
    {
        Self = new { HostName = "pc", DNSName = "pc.example.ts.net.", OS = "linux" },
        CurrentTailnet = new { MagicDNSSuffix = "example.ts.net" },
        User = new Dictionary<string, object> { ["1"] = new { LoginName = ServerHost.Login } },
        Peer = new Dictionary<string, object> { ["a"] = new { HostName = "alpha", DNSName = "alpha.example.ts.net.", OS = "linux", Online = true, UserID = 1 } },
    });

    private static FakeTimeProvider Clock() => new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    [Fact]
    public async Task Fleet_route_lists_only_own_online_or_tagged_peers_and_probes_each_without_credentials()
    {
        var handler = new FakePeerHandler((_, _) => FakePeerHandler.Json(HttpStatusCode.OK, """{"status":"ok","version":"1.2.3"}"""));
        await using var host = await ServerHost.StartAsync(ServerHost.TailnetOptions(), processes: StatusProcesses(Status));
        host.Server.Fleet = NewFleet(host.Processes, host.Time, handler, ["tag:pc"]);

        var response = await host.GetAsync("/api/v1/fleet");

        Assert.Equal(200, response.Status);
        Assert.Equal("pc", response.Json.GetProperty("self").GetProperty("name").GetString());
        var peers = response.Json.GetProperty("peers").EnumerateArray().ToList();
        Assert.Equal(["alpha.example.ts.net", "bravo.example.ts.net"], peers.Select(p => p.GetProperty("dnsName").GetString()));
        Assert.All(peers, p =>
        {
            Assert.Equal("ok", p.GetProperty("cfgdash").GetString());
            Assert.Equal("1.2.3", p.GetProperty("version").GetString());
        });
        Assert.Equal(["https://alpha.example.ts.net:7820/api/v1/health", "https://bravo.example.ts.net:7820/api/v1/health"], handler.Requests.Select(r => r.RequestUri!.ToString()).Order());
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(["1"], r.Headers.GetValues("X-Cfgdash-Fleet"));
            Assert.False(r.Headers.Contains("Authorization"));
            Assert.False(r.Headers.Contains("Cookie"));
        });
    }

    [Fact]
    public async Task Discovery_omits_a_tagged_peer_without_allowed_tags_and_lists_it_with_a_matching_tag()
    {
        var handler = new FakePeerHandler((_, _) => FakePeerHandler.Json(HttpStatusCode.OK, """{"status":"ok"}"""));
        var without = NewFleet(StatusProcesses(Status), Clock(), handler);
        var mismatched = NewFleet(StatusProcesses(Status), Clock(), handler, ["tag:server"]);
        var matching = NewFleet(StatusProcesses(Status), Clock(), handler, ["tag:pc"]);

        Assert.Equal(["alpha"], (await without.GetAsync(CancellationToken.None)).Peers.Select(p => p.Name));
        Assert.Equal(["alpha"], (await mismatched.GetAsync(CancellationToken.None)).Peers.Select(p => p.Name));
        Assert.Equal(["alpha", "bravo"], (await matching.GetAsync(CancellationToken.None)).Peers.Select(p => p.Name));
    }

    [Theory]
    [InlineData("""{"TUN": true}""", true)]
    [InlineData("""{"TUN": false}""", false)]
    [InlineData("""{"Self": {}}""", false)]
    public async Task Kernel_tun_requires_the_root_tun_flag(string status, bool expected)
    {
        var fleet = NewFleet(new FakeProcesses(call => call.Args is ["status", "--json"] ? FakeProcesses.Ok(status) : throw new InvalidOperationException(call.Line)), Clock(), new FakePeerHandler((_, _) => throw new InvalidOperationException()));

        Assert.Equal(expected, await fleet.KernelTunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Kernel_tun_is_cached_for_60_seconds_either_way()
    {
        var time = Clock();
        var tun = false;
        var processes = new FakeProcesses(call => FakeProcesses.Ok(tun ? """{"TUN": true}""" : """{"TUN": false}"""));
        var fleet = NewFleet(processes, time, new FakePeerHandler((_, _) => throw new InvalidOperationException()));

        var first = await fleet.KernelTunAsync(CancellationToken.None);
        tun = true;
        time.Advance(TimeSpan.FromSeconds(30));
        var cached = await fleet.KernelTunAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(31));
        var retried = await fleet.KernelTunAsync(CancellationToken.None);
        tun = false;
        time.Advance(TimeSpan.FromSeconds(30));
        var cachedTrue = await fleet.KernelTunAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(31));
        var switched = await fleet.KernelTunAsync(CancellationToken.None);

        Assert.Equal([false, false, true, true, false], [first, cached, retried, cachedTrue, switched]);
        Assert.Equal(3, processes.Calls.Count);
    }

    [Theory]
    [InlineData("ok", "ok", null)]
    [InlineData("conn-error", "refused", "port 7820 not reachable on the tailnet")]
    [InlineData("conn-refused", "absent", null)]
    [InlineData("403", "refused", null)]
    [InlineData("401", "refused", null)]
    [InlineData("500", "error", null)]
    public async Task Probe_outcomes_map_to_peer_states(string outcome, string state, string? reason)
    {
        var handler = new FakePeerHandler((_, _) => outcome switch
        {
            "ok" => FakePeerHandler.Json(HttpStatusCode.OK, """{"status":"ok","version":"1.2.3"}"""),
            "conn-error" => throw new HttpRequestException(HttpRequestError.ConnectionError, "unreachable"),
            "conn-refused" => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused)),
            _ => FakePeerHandler.Json((HttpStatusCode)int.Parse(outcome)),
        });
        var fleet = NewFleet(StatusProcesses(SinglePeerStatus()), Clock(), handler);

        var peer = Assert.Single((await fleet.GetAsync(CancellationToken.None)).Peers);

        Assert.Equal(state, peer.Cfgdash.ToString().ToLowerInvariant());
        Assert.Equal(outcome == "ok" ? "1.2.3" : null, peer.Version);
        if (reason is not null)
        {
            Assert.Equal(reason, peer.Reason);
        }
        else if (state == "absent")
        {
            Assert.NotNull(peer.Reason);
        }
    }

    [Fact]
    public async Task A_peer_that_never_answers_is_refused_as_unreachable_after_three_seconds()
    {
        var time = Clock();
        var entered = new TaskCompletionSource();
        var handler = new FakePeerHandler(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        var fleet = NewFleet(StatusProcesses(SinglePeerStatus()), time, handler);

        var pending = fleet.GetAsync(CancellationToken.None);
        await entered.Task;
        time.Advance(TimeSpan.FromSeconds(4));
        var peer = Assert.Single((await pending).Peers);

        Assert.Equal(PeerCfgdash.Refused, peer.Cfgdash);
        Assert.Equal("port 7820 not reachable on the tailnet", peer.Reason);
    }

    [Fact]
    public async Task Discovery_is_cached_for_60_seconds()
    {
        var time = Clock();
        var processes = StatusProcesses(SinglePeerStatus());
        var fleet = NewFleet(processes, time, new FakePeerHandler((_, _) => FakePeerHandler.Json(HttpStatusCode.OK, """{"status":"ok"}""")));

        await fleet.GetAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(30));
        await fleet.GetAsync(CancellationToken.None);
        var within = processes.Calls.Count;
        time.Advance(TimeSpan.FromSeconds(31));
        await fleet.GetAsync(CancellationToken.None);

        Assert.Equal(1, within);
        Assert.Equal(2, processes.Calls.Count);
    }

    [Fact]
    public async Task Exports_route_lists_top_level_json_files_newest_first()
    {
        await using var host = await ServerHost.StartAsync();
        Directory.CreateDirectory(Path.Combine(host.ExportsDir, "nested"));
        File.WriteAllText(Path.Combine(host.ExportsDir, "old.json"), Export);
        File.WriteAllText(Path.Combine(host.ExportsDir, "new.json"), Export.Replace("2026-01-01", "2026-06-01"));
        File.WriteAllText(Path.Combine(host.ExportsDir, "nested", "deep.json"), Export);
        File.WriteAllText(Path.Combine(host.ExportsDir, "notes.txt"), "x");

        var response = await host.GetAsync("/api/v1/exports");

        Assert.Equal(200, response.Status);
        var exports = response.Json.GetProperty("exports").EnumerateArray().ToList();
        Assert.Equal(["new", "old"], exports.Select(e => e.GetProperty("id").GetString()));
        Assert.Equal("other.example", exports[0].GetProperty("machine").GetString());
        Assert.Equal("/x", exports[0].GetProperty("dir").GetString());
    }

    [Fact]
    public async Task Compare_against_an_export_returns_the_diff()
    {
        await using var host = await ServerHost.StartAsync();
        Directory.CreateDirectory(host.ExportsDir);
        File.WriteAllText(Path.Combine(host.ExportsDir, "e1.json"), Export);

        var response = await host.GetAsync("/api/v1/compare?against=export:e1");

        Assert.Equal(200, response.Status);
        Assert.Equal("host.example", response.Json.GetProperty("left").GetProperty("machine").GetString());
        Assert.Equal("other.example", response.Json.GetProperty("right").GetProperty("machine").GetString());
        var counts = response.Json.GetProperty("counts");
        Assert.Equal([0, 0, 1], [counts.GetProperty("changed").GetInt32(), counts.GetProperty("onlyLeft").GetInt32(), counts.GetProperty("onlyRight").GetInt32()]);
        var collector = Assert.Single(response.Json.GetProperty("collectors").EnumerateArray());
        Assert.Equal("c9", collector.GetProperty("id").GetString());
        var row = Assert.Single(collector.GetProperty("rows").EnumerateArray());
        Assert.Equal("b1", row.GetProperty("blockId").GetString());
        Assert.Equal("k", row.GetProperty("itemId").GetString());
        Assert.Equal("onlyRight", row.GetProperty("change").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("&against=export:")]
    [InlineData("&against=export:..%2f..%2fx")]
    [InlineData("&against=export:a/b")]
    [InlineData("&against=other:x")]
    public async Task Compare_rejects_a_missing_or_malformed_against(string against)
    {
        await using var host = await ServerHost.StartAsync();

        var response = await host.GetAsync("/api/v1/compare?x=1" + against);

        Assert.Equal(400, response.Status);
        Assert.Equal("bad-against", response.Error);
    }

    [Fact]
    public async Task Compare_against_an_unknown_export_is_404_and_a_non_snapshot_export_is_422()
    {
        await using var host = await ServerHost.StartAsync();
        Directory.CreateDirectory(host.ExportsDir);
        File.WriteAllText(Path.Combine(host.ExportsDir, "junk.json"), """{"hello":"world"}""");

        var unknown = await host.GetAsync("/api/v1/compare?against=export:missing");
        var junk = await host.GetAsync("/api/v1/compare?against=export:junk");

        Assert.Equal(404, unknown.Status);
        Assert.Equal(422, junk.Status);
        Assert.Equal("bad-export", junk.Error);
    }

    [Theory]
    [InlineData("peer:alpha.example.ts.net", 200)]
    [InlineData("peer:ALPHA.example.ts.net.", 200)]
    [InlineData("peer:ghost.example.ts.net", 404)]
    [InlineData("peer:delta.example.ts.net", 404)]
    [InlineData("peer:bravo.example.ts.net", 502)]
    public async Task Compare_against_a_peer_diffs_its_snapshot_only_when_the_peer_is_ok(string against, int expected)
    {
        var handler = new FakePeerHandler((request, _) => (request.RequestUri!.Host, request.RequestUri.AbsolutePath) switch
        {
            ("bravo.example.ts.net", _) => FakePeerHandler.Json(HttpStatusCode.Forbidden),
            (_, "/api/v1/snapshot") => FakePeerHandler.Json(HttpStatusCode.OK, Export),
            _ => FakePeerHandler.Json(HttpStatusCode.OK, """{"status":"ok"}"""),
        });
        await using var host = await ServerHost.StartAsync(ServerHost.TailnetOptions(), processes: StatusProcesses(Status));
        host.Server.Fleet = NewFleet(host.Processes, host.Time, handler, ["tag:pc"]);

        var response = await host.GetAsync("/api/v1/compare?against=" + against);

        Assert.Equal(expected, response.Status);
        if (expected == 200)
        {
            Assert.Equal("other.example", response.Json.GetProperty("right").GetProperty("machine").GetString());
        }
        else if (expected == 502)
        {
            Assert.Equal("peer-unavailable", response.Error);
        }
    }

    [Fact]
    public async Task Compare_against_an_ok_peer_that_returns_invalid_json_is_502()
    {
        var handler = new FakePeerHandler((request, _) => request.RequestUri!.AbsolutePath == "/api/v1/snapshot"
            ? FakePeerHandler.Json(HttpStatusCode.OK, "not json")
            : FakePeerHandler.Json(HttpStatusCode.OK, """{"status":"ok"}"""));
        await using var host = await ServerHost.StartAsync(ServerHost.TailnetOptions(), processes: StatusProcesses(Status));
        host.Server.Fleet = NewFleet(host.Processes, host.Time, handler);

        var response = await host.GetAsync("/api/v1/compare?against=peer:alpha.example.ts.net");

        Assert.Equal(502, response.Status);
        Assert.Equal("peer-unavailable", response.Error);
    }
}

public sealed class ConnectionOwnerTests
{
    private static readonly IPEndPoint Client = new(IPAddress.Loopback, 50000);
    private static readonly IPEndPoint Server = new(IPAddress.Loopback, 8080);

    private const string Header = "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n";

    private static string Line(string local, string remote, int uid) =>
        $"   0: {local} {remote} 01 00000000:00000000 00:00000000 00000000 {uid,5}        0 12345 1 0000000000000000 100 0 0 10 0\n";

    [Theory]
    [InlineData("0100007F:C350", "0100007F:1F90", 0, true)]
    [InlineData("0100007F:C350", "0100007F:1F90", 1000, false)]
    [InlineData("0100007F:C351", "0100007F:1F90", 0, false)]
    [InlineData("0100007F:1F90", "0100007F:C350", 0, false)]
    public void Only_a_uid_0_socket_with_the_client_as_local_address_counts(string local, string remote, int uid, bool expected) =>
        Assert.Equal(expected, TailscaleConnectionOwner.IsRootOwned(Header + Line(local, remote, uid), Client, Server));

    [Theory]
    [InlineData("06")]
    [InlineData("04")]
    [InlineData("0A")]
    public void A_uid_0_socket_that_is_not_established_does_not_count(string state) =>
        Assert.False(TailscaleConnectionOwner.IsRootOwned(Header + Line("0100007F:C350", "0100007F:1F90", 0).Replace(" 01 ", $" {state} "), Client, Server));

    [Theory]
    [InlineData("00000000000000000000000001000000", "::1")]
    [InlineData("0000000000000000FFFF00000100007F", "::ffff:127.0.0.1")]
    public void Ipv6_table_rows_match_loopback_and_mapped_ipv4(string address, string ip)
    {
        var client = new IPEndPoint(IPAddress.Parse(ip), 50000);
        var server = new IPEndPoint(IPAddress.Parse(ip), 8080);

        Assert.True(TailscaleConnectionOwner.IsRootOwned(Header + Line($"{address}:C350", $"{address}:1F90", 0), client, server));
    }

    [Fact]
    public void Ambiguous_matches_fail_closed()
    {
        var table = Header + Line("0100007F:C350", "0100007F:1F90", 0) + Line("0100007F:C350", "0100007F:1F90", 1000);

        Assert.False(TailscaleConnectionOwner.IsRootOwned(table, Client, Server));
    }
}
