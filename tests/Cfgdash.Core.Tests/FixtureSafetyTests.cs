using System.Text.RegularExpressions;

namespace Cfgdash.Core.Tests;

public sealed partial class FixtureSafetyTests
{
    private static readonly string[] SyntheticTicketParts =
        ["SHA-256", "SHA-1", "UTF-8", "UTF-16", "UTF-32", "ISO-8601", "NET-1", "NET-2", "NET-3"];

    private static readonly string[] SyntheticIpv4Prefixes = ["127.", "0.0.0.0", "192.0.2.", "198.51.100.", "203.0.113."];

    [GeneratedRegex(@"(?<![\w.])(\d{1,3}(?:\.\d{1,3}){3})(?![\w.]*\d)")]
    private static partial Regex Ipv4();

    [GeneratedRegex(@"https?://(?:[^/\s@""']*@)?(?<host>[A-Za-z0-9][A-Za-z0-9.\-]*)|(?<=\w)@(?<host>[A-Za-z0-9][A-Za-z0-9\-]*(?:\.[A-Za-z0-9\-]+)+)")]
    private static partial Regex Hosts();

    [GeneratedRegex(@"(?:Users[\\/]+|/home/)(?<name>[A-Za-z0-9_.\-]+)")]
    private static partial Regex UserPaths();

    [GeneratedRegex(@"\b[A-Z]{2,10}-\d+\b")]
    private static partial Regex TicketIds();

    private static string FindTestsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cfgdash.slnx")))
            {
                return Path.Combine(dir.FullName, "tests", "Cfgdash.Core.Tests");
            }
        }

        throw new InvalidOperationException("cfgdash.slnx not found above " + AppContext.BaseDirectory);
    }

    private static bool IsSyntheticHost(string host)
    {
        var h = host.TrimEnd('.').ToLowerInvariant();
        return h == "localhost" || h == "user" || h == "example.com" || h.EndsWith(".example") || h == "example" || h.EndsWith(".example.com") || h == "example.ts.net" || h.EndsWith(".example.ts.net");
    }

    [Fact]
    public void Committed_test_sources_contain_only_synthetic_ips_hosts_user_paths_and_ticket_ids()
    {
        var dir = FindTestsDir();
        var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != nameof(FixtureSafetyTests) + ".cs")
            .ToList();
        var violations = new List<string>();

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);

            foreach (Match m in Ipv4().Matches(text))
            {
                if (!SyntheticIpv4Prefixes.Any(p => m.Value.StartsWith(p, StringComparison.Ordinal)))
                {
                    violations.Add($"{name}: IPv4 {m.Value}");
                }
            }

            foreach (Match m in Hosts().Matches(text))
            {
                var host = m.Groups["host"].Value;
                if (!IsSyntheticHost(host) && !Ipv4().IsMatch(host))
                {
                    violations.Add($"{name}: host {host}");
                }
            }

            foreach (Match m in UserPaths().Matches(text))
            {
                if (!m.Groups["name"].Value.Equals("user", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{name}: user path {m.Value}");
                }
            }

            foreach (Match m in TicketIds().Matches(text))
            {
                if (!SyntheticTicketParts.Contains(m.Value))
                {
                    violations.Add($"{name}: ticket-like id {m.Value}");
                }
            }
        }

        Assert.NotEmpty(files);
        Assert.Empty(violations.Distinct());
    }
}
