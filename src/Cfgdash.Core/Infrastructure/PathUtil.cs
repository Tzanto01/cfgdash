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
    /// Resolves every symlink or junction in <paramref name="path"/>, including links inside link targets (non-Windows; at most <see cref="MaxLinkHops"/> hops).
    /// False when a link loops, cannot be read or the chain is too long (non-Windows); <paramref name="real"/> is then a best-effort path and callers must fail closed.
    /// </summary>
    public static bool TryRealPath(string path, out string real)
    {
        var hops = 0;
        var ok = true;
        real = Resolve(path, ref hops, ref ok);
        return ok;
    }

    private static string Resolve(string path, ref int hops, ref bool ok)
    {
        var full = Full(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        var segments = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (!info.Exists)
            {
                return Full(Path.Combine([next, .. segments[(i + 1)..]]));
            }

            try
            {
                var target = info.LinkTarget is null ? null : info.ResolveLinkTarget(true);
                if (target is null || OperatingSystem.IsWindows() || IsNetworkPath(target.FullName))
                {
                    current = target is null ? next : Full(target.FullName);
                }
                else if (++hops > MaxLinkHops)
                {
                    ok = false;
                    return Full(Path.Combine([next, .. segments[(i + 1)..]]));
                }
                else
                {
                    current = Resolve(target.FullName, ref hops, ref ok);
                }
            }
            catch (IOException)
            {
                ok &= OperatingSystem.IsWindows();
                current = next;
            }
            catch (UnauthorizedAccessException)
            {
                ok &= OperatingSystem.IsWindows();
                current = next;
            }
        }

        return Full(current);
    }

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
