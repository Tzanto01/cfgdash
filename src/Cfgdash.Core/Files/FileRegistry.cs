using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Files;

/// <summary>Files registered by one snapshot build. Thread-safe. Refuses credential files.</summary>
internal sealed class FileRegistry(IReadOnlyList<string> companyPrefixes) : IFileRegistry
{
    private const int MaxTokenReadBytes = 2 * 1024 * 1024;

    private static readonly string[] NeverReadExtensions =
    [
        ".pem", ".key", ".pfx", ".p12", ".kdbx", ".ppk", ".gpg", ".asc", ".jks", ".keystore", ".p8", ".tfstate", ".tfvars", ".tfstate.backup",
    ];

    private static readonly string[] NeverReadPrefixes = ["id_rsa", "id_ecdsa", "id_ed25519", ".claude.json"];

    private static readonly string[] NeverReadNames =
    [
        ".credentials.json", ".env", ".netrc", "_netrc", ".git-credentials", ".npmrc", ".pypirc", ".pgpass", ".envrc", "credentials", "credentials.json",
    ];

    private static readonly string[] NeverReadSegments = [".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker"];
    private static readonly string[] NeverReadConfigDirs = ["gh", "gcloud"];
    private static readonly string[] NeverReadRoots = ["/proc/", "/sys/", "/dev/"];

    private readonly Lock gate = new();
    private readonly Dictionary<string, RegisteredFile> files = new(StringComparer.Ordinal);

    public IReadOnlyList<RegisteredFile> All
    {
        get
        {
            lock (gate)
            {
                return [.. files.Values];
            }
        }
    }

    public bool TryGet(string id, [MaybeNullWhen(false)] out RegisteredFile file)
    {
        lock (gate)
        {
            return files.TryGetValue(id, out file);
        }
    }

    public FileRef? Register(string path, string kind, bool? company = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var full = PathUtil.Full(path);
        if (IsNeverRead(full) || !File.Exists(full))
        {
            return null;
        }

        var id = IdFor(full);
        lock (gate)
        {
            if (files.TryGetValue(id, out var existing))
            {
                return existing.Ref;
            }
        }

        var size = new FileInfo(full).Length;
        var text = size <= MaxTokenReadBytes ? HarnessFiles.ReadText(full, MaxTokenReadBytes) : null;
        var chars = text?.Length ?? size;
        var real = PathUtil.RealPath(full);
        var isCompany = company ?? companyPrefixes.Any(prefix => PathUtil.IsUnder(full, prefix) || PathUtil.IsUnder(real, prefix));
        var registered = new RegisteredFile(new FileRef(id, PathUtil.ToJson(full), kind, size, HarnessFiles.Tokens(chars), isCompany), full);
        lock (gate)
        {
            if (!files.TryAdd(id, registered))
            {
                return files[id].Ref;
            }
        }

        return registered.Ref;
    }

    /// <summary>First 16 lowercase hex chars of SHA-256 over the JSON form of the full path (lower-cased on Windows).</summary>
    public static string IdFor(string path)
    {
        var normalized = PathUtil.ToJson(path);
        if (OperatingSystem.IsWindows())
        {
            normalized = normalized.ToLowerInvariant();
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    /// <summary>True for credential files by name or path segment, checked on the path and on its resolved real path, and for network and pseudo-filesystem paths.</summary>
    public static bool IsNeverRead(string path)
    {
        var full = PathUtil.Full(path);
        if (PathUtil.IsNetworkPath(full) || (OperatingSystem.IsWindows() && full.IndexOf(':', (Path.GetPathRoot(full) ?? string.Empty).Length) >= 0))
        {
            return true;
        }

        return IsNeverReadName(full) || IsNeverReadName(PathUtil.RealPath(full));
    }

    private static bool IsNeverReadName(string full)
    {
        var name = Path.GetFileName(full);
        var comparison = StringComparison.OrdinalIgnoreCase;
        if ((!OperatingSystem.IsWindows() && NeverReadRoots.Any(r => full.StartsWith(r, StringComparison.Ordinal)))
            || NeverReadNames.Any(n => name.Equals(n, comparison))
            || name.StartsWith(".env.", comparison)
            || NeverReadPrefixes.Any(p => name.StartsWith(p, comparison))
            || NeverReadExtensions.Any(e => name.EndsWith(e, comparison)))
        {
            return true;
        }

        var segments = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s => NeverReadSegments.Any(n => s.Equals(n, comparison)))
            || segments.Skip(1).Where((s, i) => segments[i].Equals(".config", comparison) && NeverReadConfigDirs.Any(n => s.Equals(n, comparison))).Any();
    }
}
