namespace Cfgdash.Core.Environment;

/// <summary>Finds the tools collectors depend on: PATH search (PATHEXT on Windows) plus known install dirs.</summary>
internal static class ToolProbe
{
    public static readonly IReadOnlyList<string> Tools = ["git", "pwsh", "claude", "tailscale", "schtasks", "systemctl", "crontab", "gh"];

    public static IReadOnlyDictionary<string, string> Probe(IEnvironmentReader env, OsFamily os, string homeDir, string? programFilesDir)
    {
        var dirs = (env.Get("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Trim('"'))
            .Where(Path.IsPathFullyQualified)
            .ToList();
        IReadOnlyList<string> extensions = os == OsFamily.Windows
            ? (env.Get("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [string.Empty];

        var known = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Known(string tool, string path) => (known.TryGetValue(tool, out var list) ? list : known[tool] = []).Add(path);
        if (os == OsFamily.Windows && programFilesDir is not null)
        {
            Known("tailscale", Path.Combine(programFilesDir, "Tailscale", "tailscale.exe"));
            Known("pwsh", Path.Combine(programFilesDir, "PowerShell", "7", "pwsh.exe"));
        }

        Known("claude", Path.Combine(homeDir, ".local", "bin", os == OsFamily.Windows ? "claude.exe" : "claude"));

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var tool in Tools)
        {
            var hit = Find(tool, dirs, extensions) ?? known.GetValueOrDefault(tool)?.FirstOrDefault(File.Exists);
            if (hit is not null)
            {
                found[tool] = hit;
            }
        }

        return found;
    }

    private static string? Find(string tool, IReadOnlyList<string> dirs, IReadOnlyList<string> extensions)
    {
        foreach (var dir in dirs)
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir, tool + extension.ToLowerInvariant());
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }
}
