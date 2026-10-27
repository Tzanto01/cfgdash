using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Environment;

/// <summary>Inputs of discovery that differ per machine; tests pass synthetic values.</summary>
internal sealed record DiscoveryOptions(OsFamily Os, string HomeDir, string OverridesFile, string? ProgramFilesDir)
{
    public static DiscoveryOptions Default(IEnvironmentReader env) => new(
        OperatingSystem.IsWindows() ? OsFamily.Windows : OperatingSystem.IsMacOS() ? OsFamily.Macos : OsFamily.Linux,
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        CfgdashPaths.OverridesFile(env),
        OperatingSystem.IsWindows() ? System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles) : null);
}

/// <summary>Discovers the machine facts described in docs/contracts/collector-v1.md ("MachineEnvironment").</summary>
internal static partial class MachineEnvironmentDiscovery
{
    private const string OverrideSource = "override-file";
    private static readonly string AgentCoreMarker = Path.Combine(".claude", "hooks", "lib", "agent-core.ps1");

    public static async Task<MachineEnvironment> DiscoverAsync(IEnvironmentReader env, IProcessRunner processes, DiscoveryOptions options, CancellationToken ct)
    {
        var overrides = ReadOverrides(options.OverridesFile);
        string? Expand(string? path) => string.IsNullOrWhiteSpace(path) ? null : PathUtil.Full(PathUtil.ExpandHome(path, options.HomeDir));

        (string? Value, string? Source) FromEnv(string name)
        {
            var value = env.Get(name);
            if (value is not null)
            {
                return (value, "env:" + name);
            }

            if (options.Os == OsFamily.Windows)
            {
                if ((value = env.GetScoped(name, EnvironmentVariableTarget.User)) is not null)
                {
                    return (value, "env-user:" + name);
                }

                if ((value = env.GetScoped(name, EnvironmentVariableTarget.Machine)) is not null)
                {
                    return (value, "env-machine:" + name);
                }
            }

            return (null, null);
        }

        (string? Value, string? Source) Override(string key) =>
            overrides.Paths.TryGetValue(key, out var value) ? (Expand(value), OverrideSource) : (null, null);

        var configDir = Override(PathKeys.ClaudeConfigDir);
        if (configDir.Value is null)
        {
            var fromEnv = FromEnv("CLAUDE_CONFIG_DIR");
            configDir = fromEnv.Value is not null
                ? (Expand(fromEnv.Value), fromEnv.Source)
                : (PathUtil.Full(Path.Combine(options.HomeDir, ".claude")), "default:home");
        }

        var workspaceEnv = FromEnv("WORKSPACE_ROOT");
        var workspaceEnvDir = Expand(workspaceEnv.Value);

        var configRepo = Override(PathKeys.ConfigRepo);
        if (configRepo.Value is null)
        {
            var parent = configDir.Value is null ? null : Path.GetDirectoryName(configDir.Value);
            if (parent is not null && File.Exists(Path.Combine(parent, AgentCoreMarker)))
            {
                configRepo = (PathUtil.Full(parent), "derived:claudeConfigDir-parent");
            }
            else if (workspaceEnvDir is not null && File.Exists(Path.Combine(workspaceEnvDir, "config", AgentCoreMarker)))
            {
                configRepo = (PathUtil.Full(Path.Combine(workspaceEnvDir, "config")), "derived:workspaceRoot-config");
            }
        }

        var workspace = Override(PathKeys.WorkspaceRoot);
        if (workspace.Value is null)
        {
            if (workspaceEnvDir is not null && Directory.Exists(workspaceEnvDir))
            {
                workspace = (workspaceEnvDir, workspaceEnv.Source);
            }
            else if (configRepo.Value is not null && Path.GetDirectoryName(configRepo.Value) is { } repoParent)
            {
                workspace = (PathUtil.Full(repoParent), "derived:configRepo-parent");
            }
        }

        var engram = Override(PathKeys.EngramDataDir);
        if (engram.Value is null)
        {
            var fromEnv = FromEnv("ENGRAM_DATA_DIR");
            engram = (Expand(fromEnv.Value), fromEnv.Value is null ? null : fromEnv.Source);
        }

        var companyPrefixes = new List<string>();
        var company = Override(PathKeys.CompanyLayer);
        if (company.Value is not null && Directory.Exists(company.Value))
        {
            companyPrefixes.Add(PathUtil.ToJson(company.Value));
        }

        if (configRepo.Value is not null)
        {
            foreach (var raw in ReadCompanyPrefixes(Path.Combine(configRepo.Value, ".claude", "hooks", "company-paths.psd1")))
            {
                var entry = raw.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar);
                var resolved = Path.IsPathRooted(entry) ? entry : workspace.Value is null ? null : Path.Combine(workspace.Value, entry);
                if (resolved is not null && Directory.Exists(resolved) && !companyPrefixes.Any(p => PathUtil.PathEquals(p, resolved)))
                {
                    companyPrefixes.Add(PathUtil.ToJson(resolved));
                }
            }

            if (company.Value is null && companyPrefixes.Count > 0)
            {
                company = (companyPrefixes[0], "file:company-paths.psd1");
            }
        }

        var paths = new List<ContextPath>
        {
            Context(PathKeys.WorkspaceRoot, workspace),
            Context(PathKeys.ClaudeConfigDir, configDir),
            Context(PathKeys.EngramDataDir, engram),
            Context(PathKeys.ConfigRepo, configRepo),
            Context(PathKeys.CompanyLayer, company),
        };

        var tools = ToolProbe.Probe(env, options.Os, options.HomeDir, options.ProgramFilesDir);
        var tailnet = tools.TryGetValue("tailscale", out var tailscale) ? await ReadTailnetAsync(processes, tailscale, ct) : null;

        var knownProjects = new List<string>();
        var claudeJson = ClaudeJson.Read(ClaudeJson.Locate(configDir.Value, configDir.Source, options.HomeDir));
        foreach (var project in claudeJson?.Projects ?? [])
        {
            if (string.IsNullOrWhiteSpace(project.Path) || !Path.IsPathRooted(project.Path) || !Directory.Exists(project.Path))
            {
                continue;
            }

            var json = PathUtil.ToJson(project.Path);
            if (!knownProjects.Contains(json, PathUtil.Comparer))
            {
                knownProjects.Add(json);
            }
        }

        IReadOnlyList<string> allowedLogins = overrides.AllowedLogins
            ?? (tailnet?.SelfUserLogin is { } login ? [login]
            : tailnet?.TailnetName is { } name && name.EndsWith(".github", StringComparison.OrdinalIgnoreCase) && name.Length > ".github".Length
                ? [name[..^".github".Length] + "@github"]
                : []);

        return new MachineEnvironment(
            options.Os,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            System.Environment.MachineName,
            options.HomeDir,
            paths,
            tools,
            tailnet,
            companyPrefixes,
            knownProjects,
            allowedLogins,
            overrides.AllowedTags ?? []);
    }

    private static ContextPath Context(string key, (string? Value, string? Source) path) =>
        path.Value is null
            ? new ContextPath(key, null, null, false)
            : new ContextPath(key, PathUtil.ToJson(path.Value), path.Source, Directory.Exists(path.Value));

    private sealed record Overrides(IReadOnlyDictionary<string, string> Paths, IReadOnlyList<string>? AllowedLogins, IReadOnlyList<string>? AllowedTags);

    private static Overrides ReadOverrides(string file)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!HarnessFiles.TryReadStrictJson(file, out var root, out _) || root is null)
        {
            return new Overrides(paths, null, null);
        }

        string[] keys = [PathKeys.WorkspaceRoot, PathKeys.ClaudeConfigDir, PathKeys.EngramDataDir, PathKeys.ConfigRepo, PathKeys.CompanyLayer];
        foreach (var key in keys)
        {
            if (root[key]?.GetValueKind() == JsonValueKind.String && root[key]!.GetValue<string>() is { Length: > 0 } value)
            {
                paths[key] = value;
            }
        }

        static IReadOnlyList<string>? StringList(System.Text.Json.Nodes.JsonNode? node) =>
            node is System.Text.Json.Nodes.JsonArray array
                ? [.. array.Where(n => n?.GetValueKind() == JsonValueKind.String).Select(n => n!.GetValue<string>())]
                : null;

        return new Overrides(paths, StringList(root["allowedLogins"]), StringList(root["allowedTags"]));
    }

    private static IReadOnlyList<string> ReadCompanyPrefixes(string psd1)
    {
        var text = HarnessFiles.ReadText(psd1);
        if (text is null)
        {
            return [];
        }

        var list = CompanyListRegex().Match(CommentLineRegex().Replace(text, string.Empty));
        return list.Success
            ? [.. LiteralRegex().Matches(list.Groups[1].Value).Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Where(v => v.Length > 0)]
            : [];
    }

    private static async Task<TailnetStatus?> ReadTailnetAsync(IProcessRunner processes, string tailscale, CancellationToken ct)
    {
        try
        {
            var result = await processes.RunAsync(tailscale, ["status", "--json"], TimeSpan.FromSeconds(5), ct);
            if (result.TimedOut || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return null;
            }

            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            var self = Get(root, "Self");
            var current = Get(root, "CurrentTailnet");
            var tags = Strings(Get(self, "Tags"));
            string? login = null;
            if (tags.Count == 0 && Get(self, "UserID") is { } userId && Get(root, "User") is { ValueKind: JsonValueKind.Object } users)
            {
                var id = userId.ValueKind == JsonValueKind.Number ? userId.GetRawText() : userId.ToString();
                foreach (var user in users.EnumerateObject())
                {
                    if (user.Name == id && Text(Get(user.Value, "LoginName")) is { Length: > 0 } name)
                    {
                        login = name;
                    }
                }
            }

            return new TailnetStatus(
                Text(Get(root, "BackendState")) ?? string.Empty,
                (Text(Get(self, "DNSName")) ?? string.Empty).TrimEnd('.'),
                Text(Get(current, "Name")) ?? string.Empty,
                Text(Get(current, "MagicDNSSuffix")) ?? Text(Get(root, "MagicDNSSuffix")) ?? string.Empty,
                Get(current, "MagicDNSEnabled") is { ValueKind: JsonValueKind.True },
                Strings(Get(root, "CertDomains")),
                tags,
                login);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static JsonElement? Get(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var value) ? value : null;

    private static string? Text(JsonElement? element) => element is { ValueKind: JsonValueKind.String } e ? e.GetString() : null;

    private static IReadOnlyList<string> Strings(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } array
            ? [.. array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];

    [GeneratedRegex(@"CompanyPathPrefixes\s*=\s*@\(([\s\S]*?)\)", RegexOptions.IgnoreCase)]
    private static partial Regex CompanyListRegex();

    [GeneratedRegex(@"'([^']*)'|""([^""]*)""")]
    private static partial Regex LiteralRegex();

    [GeneratedRegex(@"(?m)^\s*#.*$")]
    private static partial Regex CommentLineRegex();
}
