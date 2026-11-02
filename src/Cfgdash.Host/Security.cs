using System.Buffers.Text;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cfgdash.Core;

namespace Cfgdash.Host;

/// <summary>The per-run token: 32 CSPRNG bytes, base64url without padding, verified in constant time over SHA-256 hashes.</summary>
internal sealed class TokenVerifier(string token)
{
    private readonly byte[] expected = Hash(token);
    private readonly string uiSession = Session(token, "ui");
    private readonly string tailnetSession = Session(token, "tailnet");

    public static string CreateToken()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Base64Url.EncodeToString(secret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public bool Verify(string? presented) => Matches(presented, expected);

    /// <summary>The <c>cfgdash_session</c> cookie value of one listener: base64url HMAC-SHA256 of the listener name keyed by the token. Cookies ignore ports, so a value that reaches another service on the same host name works neither as a bearer nor on the other listener.</summary>
    public string SessionValue(bool tailnet) => tailnet ? tailnetSession : uiSession;

    public bool VerifySession(string? presented, bool tailnet) => Matches(presented, Hash(SessionValue(tailnet)));

    private static bool Matches(string? presented, byte[] expectedHash)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var hash = Hash(presented);
        try
        {
            return CryptographicOperations.FixedTimeEquals(hash, expectedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    private static string Session(string token, string listener) =>
        Base64Url.EncodeToString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes("cfgdash_session:" + listener)));

    private static byte[] Hash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        try
        {
            return SHA256.HashData(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

/// <summary>Node facts from <c>tailscale whois --json</c>. <c>Name</c> keeps tailscale's trailing dot; <c>Login</c> is <c>UserProfile.LoginName</c>, null when absent.</summary>
internal sealed record WhoisNode(string Name, IReadOnlyList<string> Tags, string? Login);

/// <summary>The tailscale CLI calls cfgdash makes, through <see cref="IProcessRunner"/>.</summary>
internal sealed class TailscaleCli(IProcessRunner processes, string? executable)
{
    private static readonly TimeSpan WhoisTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ServeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Null when tailscale is absent, fails, or answers without a node name.</summary>
    public async Task<WhoisNode?> WhoisAsync(string ip, CancellationToken cancellationToken)
    {
        if (executable is null)
        {
            return null;
        }

        var result = await processes.RunAsync(executable, ["whois", "--json", ip], WhoisTimeout, cancellationToken);
        if (result.TimedOut || result.ExitCode != 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Node", out var node)
                || node.ValueKind != JsonValueKind.Object
                || !node.TryGetProperty("Name", out var name)
                || name.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            List<string> tags = node.TryGetProperty("Tags", out var tagArray) && tagArray.ValueKind == JsonValueKind.Array
                ? [.. tagArray.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!)]
                : [];
            var login = root.TryGetProperty("UserProfile", out var profile) && profile.ValueKind == JsonValueKind.Object
                && profile.TryGetProperty("LoginName", out var loginName) && loginName.ValueKind == JsonValueKind.String
                ? loginName.GetString()
                : null;
            return new WhoisNode(name.GetString()!, tags, login);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Parsed <c>tailscale serve status --json</c>; null when tailscale is absent, fails, or prints something unparseable. Missing <c>TCP</c>/<c>Web</c>/<c>Handlers</c> mean empty.</summary>
    public async Task<ServeStatus?> ServeStatusAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["serve", "status", "--json"], cancellationToken);
        if (result.TimedOut || result.ExitCode != 0)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var tcp = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("TCP", out var tcpObject) && tcpObject.ValueKind == JsonValueKind.Object)
            {
                tcp.UnionWith(tcpObject.EnumerateObject().Select(p => p.Name));
            }

            var web = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("Web", out var webObject) && webObject.ValueKind == JsonValueKind.Object)
            {
                foreach (var site in webObject.EnumerateObject())
                {
                    string? proxy = null;
                    if (site.Value.ValueKind == JsonValueKind.Object
                        && site.Value.TryGetProperty("Handlers", out var handlers) && handlers.ValueKind == JsonValueKind.Object
                        && handlers.TryGetProperty("/", out var rootHandler) && rootHandler.ValueKind == JsonValueKind.Object
                        && rootHandler.TryGetProperty("Proxy", out var proxyValue) && proxyValue.ValueKind == JsonValueKind.String)
                    {
                        proxy = proxyValue.GetString();
                    }

                    web[site.Name] = proxy;
                }
            }

            return new ServeStatus(tcp, web);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>tailscale serve --bg --https=&lt;httpsPort&gt; http://127.0.0.1:&lt;backendPort&gt;</c>.</summary>
    public Task<ProcessResult> ServeAsync(int httpsPort, int backendPort, CancellationToken cancellationToken) =>
        RunAsync(["serve", "--bg", $"--https={httpsPort.ToString(CultureInfo.InvariantCulture)}", $"http://127.0.0.1:{backendPort.ToString(CultureInfo.InvariantCulture)}"], cancellationToken);

    /// <summary><c>tailscale serve --https=&lt;httpsPort&gt; off</c>: drops every handler on that port, so only call it for a port cfgdash published.</summary>
    public Task<ProcessResult> ServeOffAsync(int httpsPort, CancellationToken cancellationToken) =>
        RunAsync(["serve", $"--https={httpsPort.ToString(CultureInfo.InvariantCulture)}", "off"], cancellationToken);

    private async Task<ProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (executable is null)
        {
            return new ProcessResult(-1, string.Empty, "tailscale is not installed", false);
        }

        try
        {
            return await processes.RunAsync(executable, arguments, ServeTimeout, cancellationToken);
        }
        catch (Win32Exception)
        {
            return new ProcessResult(-1, string.Empty, "tailscale could not be started", false);
        }
    }
}

/// <summary><c>TCP</c> port keys and, per <c>Web</c> key (<c>&lt;dnsName&gt;:&lt;port&gt;</c>), the <c>/</c> handler's <c>Proxy</c> (null when absent).</summary>
internal sealed record ServeStatus(IReadOnlySet<string> TcpPorts, IReadOnlyDictionary<string, string?> Web)
{
    /// <summary>The port has a TCP entry or any Web key ends with <c>:&lt;port&gt;</c> (one per MagicDNS name).</summary>
    public bool UsesPort(int port)
    {
        var key = port.ToString(CultureInfo.InvariantCulture);
        return TcpPorts.Contains(key) || Web.Keys.Any(k => k.EndsWith(":" + key, StringComparison.Ordinal));
    }

    /// <summary>The handler at <c>&lt;dnsName&gt;:&lt;httpsPort&gt;</c> <c>/</c> proxies exactly to cfgdash's backend port.</summary>
    public bool IsOurs(RunTailnet tailnet) =>
        Web.TryGetValue($"{TailnetRules.TrimDot(tailnet.DnsName)}:{tailnet.HttpsPort.ToString(CultureInfo.InvariantCulture)}", out var proxy)
        && proxy == $"http://127.0.0.1:{tailnet.BackendPort.ToString(CultureInfo.InvariantCulture)}";
}

internal static class TailnetRules
{
    private const string GithubTailnetSuffix = ".github";

    /// <summary>First source that yields a value: override file, the untagged self node's user, <c>&lt;name&gt;.github</c> -> <c>&lt;name&gt;@github</c>.</summary>
    public static IReadOnlyList<string> AllowedLogins(MachineEnvironment environment)
    {
        if (environment.AllowedLogins.Count > 0)
        {
            return environment.AllowedLogins;
        }

        var tailnet = environment.Tailnet;
        if (tailnet is null)
        {
            return [];
        }

        if (tailnet.SelfUserLogin is not null)
        {
            return [tailnet.SelfUserLogin];
        }

        var name = tailnet.TailnetName;
        return name.Length > GithubTailnetSuffix.Length && name.EndsWith(GithubTailnetSuffix, StringComparison.OrdinalIgnoreCase)
            ? [name[..^GithubTailnetSuffix.Length] + "@github"]
            : [];
    }

    public static string TrimDot(string name) => name.TrimEnd('.');

    /// <summary>Own tagged device: tagged, inside this tailnet's MagicDNS suffix, and matching <paramref name="allowedTags"/> when set.</summary>
    public static bool IsAllowedTaggedNode(WhoisNode node, string magicDnsSuffix, IReadOnlyList<string> allowedTags) =>
        node.Tags.Count > 0
        && IsInTailnet(node.Name, magicDnsSuffix)
        && (allowedTags.Count == 0 || node.Tags.Any(tag => allowedTags.Contains(tag, StringComparer.Ordinal)));

    /// <summary>The node name ends with this tailnet's MagicDNS suffix (excludes shared-in nodes); false for an empty suffix.</summary>
    public static bool IsInTailnet(string nodeName, string magicDnsSuffix)
    {
        var suffix = TrimDot(magicDnsSuffix).TrimStart('.');
        return suffix.Length > 0 && TrimDot(nodeName).EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Tailnet listener identity: the <c>Tailscale-User-Login</c> header, else a whois of the single <c>X-Forwarded-For</c> IP (cached 60 s per IP, one lookup per IP at a time).</summary>
internal sealed class TailnetIdentity(TailnetListenerOptions options, TailscaleCli tailscale, TimeProvider time)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private readonly Lock gate = new();
    private readonly Dictionary<string, WhoisEntry> cache = new(StringComparer.Ordinal);

    public async Task<bool> IsAllowedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.Headers.TryGetValue("Tailscale-User-Login", out var login))
        {
            return login.Count == 1 && login[0] is { } value && options.AllowedLogins.Contains(value, StringComparer.OrdinalIgnoreCase);
        }

        return await WhoisForwardedAsync(request, cancellationToken) is { } node
            && TailnetRules.IsAllowedTaggedNode(node, options.MagicDnsSuffix, options.AllowedTags);
    }

    /// <summary>Fleet peer identity (fleet-v1): ignores <c>Tailscale-User-Login</c>; the whois node of the single <c>X-Forwarded-For</c> IP must be an allowed tagged node, or an untagged node of this tailnet owned by an allowed login.</summary>
    public async Task<bool> IsAllowedPeerAsync(HttpRequest request, CancellationToken cancellationToken) =>
        await WhoisForwardedAsync(request, cancellationToken) is { } node
        && (TailnetRules.IsAllowedTaggedNode(node, options.MagicDnsSuffix, options.AllowedTags)
            || (node.Tags.Count == 0
                && TailnetRules.IsInTailnet(node.Name, options.MagicDnsSuffix)
                && node.Login is { } login
                && options.AllowedLogins.Contains(login, StringComparer.OrdinalIgnoreCase)));

    private async Task<WhoisNode?> WhoisForwardedAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var forwarded = request.Headers["X-Forwarded-For"];
        if (forwarded.Count != 1 || forwarded[0] is not { } candidate || candidate.Contains(',') || !IPAddress.TryParse(candidate, out var ip))
        {
            return null;
        }

        return await LookupAsync(ip.ToString()).WaitAsync(cancellationToken);
    }

    private Task<WhoisNode?> LookupAsync(string ip)
    {
        lock (gate)
        {
            if (cache.TryGetValue(ip, out var cached) && (cached.Expires is not { } expires || time.GetUtcNow() < expires))
            {
                return cached.Task;
            }

            var entry = new WhoisEntry();
            cache[ip] = entry;
            entry.Task = ResolveAsync(ip, entry);
            return entry.Task;
        }
    }

    private async Task<WhoisNode?> ResolveAsync(string ip, WhoisEntry entry)
    {
        try
        {
            await Task.Yield();
            return await tailscale.WhoisAsync(ip, CancellationToken.None);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            lock (gate)
            {
                entry.Expires = time.GetUtcNow() + CacheFor;
            }
        }
    }

    private sealed class WhoisEntry
    {
        public Task<WhoisNode?> Task = System.Threading.Tasks.Task.FromResult<WhoisNode?>(null);
        public DateTimeOffset? Expires;
    }
}
