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

    public static string RealPath(string path)
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
                current = target is null ? next : Full(target.FullName);
            }
            catch (IOException)
            {
                current = next;
            }
            catch (UnauthorizedAccessException)
            {
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
