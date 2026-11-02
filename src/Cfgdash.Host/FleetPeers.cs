using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core;

namespace Cfgdash.Host;

/// <summary>Whether a peer answers fleet requests; serialized lowercase.</summary>
internal enum PeerCfgdash { Ok, Absent, Refused, Error }

internal sealed record FleetSelf(string Name, string? DnsName, string Os);

/// <summary>One fleet peer. <c>DnsName</c> has no trailing dot.</summary>
internal sealed record FleetPeer(string Name, string DnsName, string Os, bool Online, PeerCfgdash Cfgdash, string? Version = null, string? Reason = null);

/// <summary>The <c>GET /api/v1/fleet</c> answer (fleet-v1).</summary>
internal sealed record FleetStatus(FleetSelf Self, IReadOnlyList<FleetPeer> Peers);

/// <summary><c>Known</c> false: not a current fleet peer. Otherwise <c>Snapshot</c>, or the short <c>Reason</c> the peer is unavailable.</summary>
internal sealed record PeerSnapshot(bool Known, JsonObject? Snapshot, string? Reason);

/// <summary>Fleet peer discovery from <c>tailscale status --json</c>, the health probe of each peer, and the peer snapshot fetch. The whole discovery is cached for 60 s, one run at a time.</summary>
internal sealed class FleetPeers
{
    public const int PeerPort = 7820;
    private const int MaxHealthBytes = 64 * 1024;
    private const int MaxSnapshotBytes = 16 * 1024 * 1024;
    private const int MaxVersionLength = 64;
    private const string NotReachable = "port 7820 not reachable on the tailnet";
    private const string NotRunning = "cfgdash not running on the peer";
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    private readonly IProcessRunner processes;
    private readonly string? executable;
    private readonly IReadOnlyList<string> allowedLogins;
    private readonly IReadOnlyList<string> allowedTags;
    private readonly MachineEnvironment environment;
    private readonly TimeProvider time;
    private readonly HttpClient http;
    private readonly Lock gate = new();
    private Task<FleetStatus>? current;
    private DateTimeOffset? expires;
    private Task<bool>? kernelTun;
    private DateTimeOffset kernelTunExpires;

    public FleetPeers(
        IProcessRunner processes,
        string? executable,
        IReadOnlyList<string> allowedLogins,
        IReadOnlyList<string> allowedTags,
        MachineEnvironment environment,
        TimeProvider time,
        HttpMessageHandler? handler = null)
    {
        this.processes = processes;
        this.executable = executable;
        this.allowedLogins = allowedLogins;
        this.allowedTags = allowedTags;
        this.environment = environment;
        this.time = time;
        http = new HttpClient(handler ?? new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>The cached fleet; a new discovery when the cache is empty, expired or the last run faulted.</summary>
    public Task<FleetStatus> GetAsync(CancellationToken cancellationToken)
    {
        Task<FleetStatus> task;
        lock (gate)
        {
            if (current is null || current.IsFaulted || (expires is { } until && time.GetUtcNow() >= until))
            {
                expires = null;
                current = DiscoverAsync();
            }

            task = current;
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>True when <c>tailscale status --json</c> reports a kernel TUN device (root <c>TUN</c> is <c>true</c>); false without tailscale, on any failure, or in userspace-networking mode. Any result is kept for 60 s, one run at a time.</summary>
    public Task<bool> KernelTunAsync(CancellationToken cancellationToken)
    {
        Task<bool> task;
        lock (gate)
        {
            if (kernelTun is null || kernelTun.IsFaulted || (kernelTun.IsCompletedSuccessfully && time.GetUtcNow() >= kernelTunExpires))
            {
                kernelTun = ProbeKernelTunAsync();
            }

            task = kernelTun;
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>The snapshot of the peer <paramref name="dnsName"/> (trailing dot and case ignored) for its start dir; only for a current peer whose state is <c>ok</c>.</summary>
    public async Task<PeerSnapshot> FetchSnapshotAsync(string dnsName, CancellationToken cancellationToken)
    {
        var name = TailnetRules.TrimDot(dnsName);
        var fleet = await GetAsync(cancellationToken);
        if (fleet.Peers.FirstOrDefault(p => string.Equals(p.DnsName, name, StringComparison.OrdinalIgnoreCase)) is not { } peer)
        {
            return new PeerSnapshot(false, null, null);
        }

        if (peer.Cfgdash != PeerCfgdash.Ok)
        {
            return new PeerSnapshot(true, null, peer.Reason ?? "peer not available");
        }

        using var timeout = new CancellationTokenSource(FetchTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await SendAsync(peer.DnsName, "/api/v1/snapshot", linked.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new PeerSnapshot(true, null, "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }

            return await ReadJsonAsync(response, MaxSnapshotBytes, linked.Token) is JsonObject snapshot
                ? new PeerSnapshot(true, snapshot, null)
                : new PeerSnapshot(true, null, "invalid snapshot");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PeerSnapshot(true, null, "peer timed out");
        }
        catch (InvalidDataException)
        {
            return new PeerSnapshot(true, null, "snapshot too large");
        }
        catch (IOException)
        {
            return new PeerSnapshot(true, null, "peer connection failed");
        }
        catch (JsonException)
        {
            return new PeerSnapshot(true, null, "invalid snapshot");
        }
        catch (HttpRequestException e)
        {
            return new PeerSnapshot(true, null, Classify(e).Reason);
        }
    }

    private async Task<FleetStatus> DiscoverAsync()
    {
        await Task.Yield();
        var fallback = new FleetStatus(
            new FleetSelf(environment.Hostname, environment.Tailnet is { } tailnet ? TailnetRules.TrimDot(tailnet.DnsName) : null, environment.Os.ToString().ToLowerInvariant()),
            []);
        if (executable is null)
        {
            return Cache(fallback);
        }

        List<(string Name, string DnsName, string Os)> candidates;
        FleetSelf self;
        try
        {
            var result = await processes.RunAsync(executable, ["status", "--json"], StatusTimeout, CancellationToken.None);
            if (result.TimedOut || result.ExitCode != 0 || Parse(result.StandardOutput, fallback.Self) is not { } parsed)
            {
                return Cache(fallback);
            }

            (self, candidates) = parsed;
        }
        catch (Exception)
        {
            return Cache(fallback);
        }

        var peers = await Task.WhenAll(candidates.Select(c => ProbeAsync(c.Name, c.DnsName, c.Os)));
        return Cache(new FleetStatus(self, [.. peers.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.DnsName, StringComparer.OrdinalIgnoreCase)]));
    }

    private async Task<bool> ProbeKernelTunAsync()
    {
        await Task.Yield();
        var tun = false;
        if (executable is not null)
        {
            try
            {
                var result = await processes.RunAsync(executable, ["status", "--json"], StatusTimeout, CancellationToken.None);
                if (!result.TimedOut && result.ExitCode == 0)
                {
                    using var document = JsonDocument.Parse(result.StandardOutput);
                    tun = document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("TUN", out var value)
                        && value.ValueKind == JsonValueKind.True;
                }
            }
            catch (Exception)
            {
                tun = false;
            }
        }

        lock (gate)
        {
            kernelTunExpires = time.GetUtcNow() + CacheFor;
        }

        return tun;
    }

    private FleetStatus Cache(FleetStatus status)
    {
        lock (gate)
        {
            expires = time.GetUtcNow() + CacheFor;
        }

        return status;
    }

    private (FleetSelf Self, List<(string Name, string DnsName, string Os)> Candidates)? Parse(string json, FleetSelf fallbackSelf)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Self", out var selfNode) || selfNode.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var self = new FleetSelf(
                Text(selfNode, "HostName") ?? fallbackSelf.Name,
                Text(selfNode, "DNSName") is { Length: > 0 } selfDns ? TailnetRules.TrimDot(selfDns) : fallbackSelf.DnsName,
                Text(selfNode, "OS")?.ToLowerInvariant() ?? fallbackSelf.Os);

            var suffix = (root.TryGetProperty("CurrentTailnet", out var tailnet) && tailnet.ValueKind == JsonValueKind.Object ? Text(tailnet, "MagicDNSSuffix") : null)
                ?? Text(root, "MagicDNSSuffix")
                ?? string.Empty;

            var users = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("User", out var userMap) && userMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var user in userMap.EnumerateObject())
                {
                    if (user.Value.ValueKind == JsonValueKind.Object && Text(user.Value, "LoginName") is { } login)
                    {
                        users[user.Name] = login;
                    }
                }
            }

            var candidates = new List<(string Name, string DnsName, string Os)>();
            if (root.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in peerMap.EnumerateObject())
                {
                    var peer = entry.Value;
                    if (peer.ValueKind != JsonValueKind.Object
                        || !peer.TryGetProperty("Online", out var online) || online.ValueKind != JsonValueKind.True
                        || Text(peer, "HostName") is not { Length: > 0 } name
                        || Text(peer, "DNSName") is not { } rawDns
                        || TailnetRules.TrimDot(rawDns) is not { Length: > 0 } dnsName
                        || Uri.CheckHostName(dnsName) != UriHostNameType.Dns)
                    {
                        continue;
                    }

                    List<string> tags = peer.TryGetProperty("Tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array
                        ? [.. tagArray.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!)]
                        : [];
                    var allowed = tags.Count > 0
                        ? allowedTags.Count > 0 && TailnetRules.IsAllowedTaggedNode(new WhoisNode(dnsName, tags, null), suffix, allowedTags)
                        : TailnetRules.IsInTailnet(dnsName, suffix)
                            && UserId(peer) is { } userId
                            && users.TryGetValue(userId, out var owner)
                            && allowedLogins.Contains(owner, StringComparer.OrdinalIgnoreCase);
                    if (allowed)
                    {
                        candidates.Add((name, dnsName, Text(peer, "OS")?.ToLowerInvariant() ?? string.Empty));
                    }
                }
            }

            return (self, candidates);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<FleetPeer> ProbeAsync(string name, string dnsName, string os)
    {
        using var timeout = new CancellationTokenSource(ProbeTimeout, time);
        try
        {
            using var response = await SendAsync(dnsName, "/api/v1/health", timeout.Token);
            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    var health = await ReadJsonAsync(response, MaxHealthBytes, timeout.Token) as JsonObject;
                    if (health?["status"] is JsonValue status && status.TryGetValue<string>(out var value) && value == "ok")
                    {
                        var version = health["version"] is JsonValue v && v.TryGetValue<string>(out var text) && text.Length <= MaxVersionLength ? text : null;
                        return new FleetPeer(name, dnsName, os, true, PeerCfgdash.Ok, version);
                    }

                    return new FleetPeer(name, dnsName, os, true, PeerCfgdash.Error, Reason: "unexpected health answer");
                case HttpStatusCode.NotFound:
                    return new FleetPeer(name, dnsName, os, true, PeerCfgdash.Absent, Reason: NotRunning);
                case HttpStatusCode.Forbidden:
                    return new FleetPeer(name, dnsName, os, true, PeerCfgdash.Refused, Reason: "peer refused fleet access");
                case HttpStatusCode.Unauthorized:
                    return new FleetPeer(name, dnsName, os, true, PeerCfgdash.Refused, Reason: "peer cfgdash has no fleet support");
                default:
                    return new FleetPeer(name, dnsName, os, true, PeerCfgdash.Error, Reason: "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
            }
        }
        catch (OperationCanceledException)
        {
            return new FleetPeer(name, dnsName, os, true, PeerCfgdash.Refused, Reason: NotReachable);
        }
        catch (Exception e)
        {
            var (state, reason) = Classify(e);
            return new FleetPeer(name, dnsName, os, true, state, Reason: reason);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string dnsName, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://{dnsName}:{PeerPort.ToString(CultureInfo.InvariantCulture)}{path}"));
        request.Headers.Add(CfgdashServer.FleetHeader, "1");
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private static (PeerCfgdash State, string Reason) Classify(Exception e)
    {
        for (var inner = e; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return (PeerCfgdash.Absent, NotRunning);
            }
        }

        return e is TimeoutException
            or HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError }
            || e.InnerException is SocketException
            ? (PeerCfgdash.Refused, NotReachable)
            : (PeerCfgdash.Error, e.GetType().Name);
    }

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidDataException("response too large");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return JsonNode.Parse(buffer, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
    }

    private static string? UserId(JsonElement peer) =>
        peer.TryGetProperty("UserID", out var id)
            ? id.ValueKind switch
            {
                JsonValueKind.Number => id.GetRawText(),
                JsonValueKind.String => id.GetString(),
                _ => null,
            }
            : null;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
