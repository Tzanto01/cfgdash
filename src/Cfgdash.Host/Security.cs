using System.Buffers.Text;
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

    public bool Verify(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var hash = Hash(presented);
        try
        {
            return CryptographicOperations.FixedTimeEquals(hash, expected);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(hash);
        }
    }

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

/// <summary>Node facts from <c>tailscale whois --json</c>. <c>Name</c> keeps tailscale's trailing dot.</summary>
internal sealed record WhoisNode(string Name, IReadOnlyList<string> Tags);

/// <summary>The tailscale CLI calls cfgdash makes, through <see cref="IProcessRunner"/>.</summary>
internal sealed class TailscaleCli(IProcessRunner processes, string? executable)
{
    private static readonly TimeSpan WhoisTimeout = TimeSpan.FromSeconds(10);

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
            return new WhoisNode(name.GetString()!, tags);
        }
        catch (JsonException)
        {
            return null;
        }
    }
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
    public static bool IsAllowedTaggedNode(WhoisNode node, string magicDnsSuffix, IReadOnlyList<string> allowedTags)
    {
        var suffix = TrimDot(magicDnsSuffix).TrimStart('.');
        return suffix.Length > 0
            && node.Tags.Count > 0
            && TrimDot(node.Name).EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase)
            && (allowedTags.Count == 0 || node.Tags.Any(tag => allowedTags.Contains(tag, StringComparer.Ordinal)));
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

        var forwarded = request.Headers["X-Forwarded-For"];
        if (forwarded.Count != 1 || forwarded[0] is not { } candidate || candidate.Contains(',') || !IPAddress.TryParse(candidate, out var ip))
        {
            return false;
        }

        return await LookupAsync(ip.ToString()).WaitAsync(cancellationToken);
    }

    private Task<bool> LookupAsync(string ip)
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

    private async Task<bool> ResolveAsync(string ip, WhoisEntry entry)
    {
        try
        {
            await Task.Yield();
            var node = await tailscale.WhoisAsync(ip, CancellationToken.None);
            return node is not null && TailnetRules.IsAllowedTaggedNode(node, options.MagicDnsSuffix, options.AllowedTags);
        }
        catch (Exception)
        {
            return false;
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
        public Task<bool> Task = System.Threading.Tasks.Task.FromResult(false);
        public DateTimeOffset? Expires;
    }
}
