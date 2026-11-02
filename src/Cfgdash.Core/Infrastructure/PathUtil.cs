using System.Runtime.InteropServices;

namespace Cfgdash.Core.Infrastructure;

internal static class PathUtil
{
    public static StringComparison Comparison { get; } = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string Full(string path)
    {
        var full = Path.GetFullPath(path.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }

    public static string ToJson(string path) => Full(path).Replace('\\', '/');

    /// <summary>A Windows UNC or device path (<c>\\server\share</c>, <c>\\?\</c>, <c>\\.\</c>). Probing one can send the user's credentials to a remote host, so paths taken from file content are never touched when this is true.</summary>
    public static bool IsNetworkPath(string path) => OperatingSystem.IsWindows() && path.Replace('/', '\\').StartsWith(@"\\", StringComparison.Ordinal);

    public static bool PathEquals(string a, string b) => string.Equals(Full(a), Full(b), Comparison);

    public static bool IsUnder(string path, string root)
    {
        var p = Full(path);
        var r = Full(root);
        if (string.Equals(p, r, Comparison))
        {
            return true;
        }

        var prefix = r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, Comparison);
    }

    public static string ExpandHome(string path, string homeDir)
    {
        if (path == "~")
        {
            return homeDir;
        }

        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(homeDir, path[2..])
            : path;
    }

    public static string? FindGitRoot(string dir)
    {
        for (var current = Full(dir); current is not null; current = Path.GetDirectoryName(current))
        {
            var git = Path.Combine(current, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return current;
            }
        }

        return null;
    }

    public const int MaxLinkHops = 40;

    public static string RealPath(string path)
    {
        TryRealPath(path, out var real);
        return real;
    }

    /// <summary>
    /// Resolves every symlink or junction in <paramref name="path"/>. On non-Windows each link is followed one hop at a time (at most <see cref="MaxLinkHops"/>),
    /// links inside link targets included, and <c>..</c> applies to the resolved parent as the kernel does. Links under <c>/proc/</c> are never followed.
    /// False when a link loops, cannot be read, lies under <c>/proc/</c> or the chain is too long (non-Windows), or on Windows when the path or a link target is a UNC or device path
    /// (never probed) or the chain is too long; <paramref name="real"/> is then a best-effort local path, never a refused target, and callers must fail closed.
    /// </summary>
    public static bool TryRealPath(string path, out string real)
    {
        if (OperatingSystem.IsWindows())
        {
            return WindowsRealPath(path, out real);
        }

        var hops = 0;
        var ok = true;
        real = Full(ResolveUnix(Full(path), ref hops, ref ok));
        return ok;
    }

    private static string ResolveUnix(string path, ref int hops, ref bool ok)
    {
        var root = Path.GetPathRoot(path) ?? "/";
        var current = root;
        var segments = path[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (segments[i] == ".")
            {
                continue;
            }

            if (segments[i] == "..")
            {
                current = Path.GetDirectoryName(current) ?? root;
                continue;
            }

            var next = Path.Combine(current, segments[i]);
            string? link;
            try
            {
                link = new FileInfo(next).LinkTarget;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ok = false;
                return Path.Combine([next, .. segments[(i + 1)..]]);
            }

            if (link is null)
            {
                if (!File.Exists(next) && !Directory.Exists(next))
                {
                    return Path.Combine([next, .. segments[(i + 1)..]]);
                }

                current = next;
                continue;
            }

            if (++hops > MaxLinkHops || next.StartsWith("/proc/", StringComparison.Ordinal))
            {
                ok = false;
                return Path.Combine([next, .. segments[(i + 1)..]]);
            }

            current = ResolveUnix(Path.IsPathRooted(link) ? link : Path.Combine(current, link), ref hops, ref ok);
            if (!ok)
            {
                return Path.Combine([current, .. segments[(i + 1)..]]);
            }
        }

        return current;
    }

    private static bool WindowsRealPath(string path, out string real)
    {
        var full = Full(path);
        real = full;
        if (IsDevicePath(path) || IsDevicePath(full))
        {
            return false;
        }

        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        var segments = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var hops = 0;
        for (var i = 0; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);
            var link = LinkTargetOf(next);
            if (link is null)
            {
                if (!Directory.Exists(next) && !File.Exists(next))
                {
                    real = Full(Path.Combine([next, .. segments[(i + 1)..]]));
                    return true;
                }

                current = next;
                continue;
            }

            while (link is not null)
            {
                if (IsDevicePath(link) || ++hops > MaxLinkHops)
                {
                    real = Full(Path.Combine([next, .. segments[(i + 1)..]]));
                    return false;
                }

                next = Full(Path.IsPathRooted(link) ? link : Path.Combine(Path.GetDirectoryName(next) ?? root, link));
                link = LinkTargetOf(next);
            }

            current = next;
        }

        real = Full(current);
        return true;
    }

    private static bool IsDevicePath(string path) => IsNetworkPath(path) || path.Replace('/', '\\').StartsWith(@"\??\", StringComparison.Ordinal);

    private static string? LinkTargetOf(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Windows: expands 8.3 short names (<c>PROGRA~1</c>) to long names through <c>GetLongPathNameW</c>; for a missing path the longest existing ancestor is expanded and the rest re-appended.
    /// Returns <paramref name="path"/> unchanged on other platforms, for UNC or device paths (no filesystem call) and when the call fails.
    /// </summary>
    public static string LongPath(string path)
    {
        if (!OperatingSystem.IsWindows() || IsDevicePath(path))
        {
            return path;
        }

        var existing = path;
        var rest = new List<string>();
        while (!Directory.Exists(existing) && !File.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return path;
            }

            rest.Insert(0, Path.GetFileName(existing));
            existing = parent;
        }

        var size = GetLongPathName(existing, null, 0);
        if (size == 0)
        {
            return path;
        }

        var buffer = new char[size];
        var length = GetLongPathName(existing, buffer, size);
        return length == 0 || length >= size ? path : Path.Combine([new string(buffer, 0, (int)length), .. rest]);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetLongPathNameW")]
    private static extern uint GetLongPathName(string shortPath, [Out] char[]? longPath, uint bufferLength);

    public static IReadOnlyList<string> AncestorsFromRoot(string dir)
    {
        var list = new List<string>();
        for (var current = Full(dir); current is not null; current = Path.GetDirectoryName(current))
        {
            list.Add(current);
        }

        list.Reverse();
        return list;
    }
}
