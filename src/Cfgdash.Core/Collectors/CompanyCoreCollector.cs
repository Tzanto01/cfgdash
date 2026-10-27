using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary>
/// <c>agents.company-core</c>: per company agent type the exact text <c>Get-AgentComposedContext</c> returns, computed by
/// dot-sourcing the config repo's <c>agent-core.ps1</c> in one pwsh run (the same call as <c>subagent-inject.ps1</c>).
/// </summary>
internal sealed partial class CompanyCoreCollector : ICollector
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(18);

    public CollectorManifest Manifest { get; } = new(
        "agents.company-core", "Company agent cores", "1.0.0", "agents",
        new Requirements(Tools: ["pwsh"], Paths: [PathKeys.ConfigRepo, PathKeys.CompanyLayer]));

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var environment = context.Environment;
        var redactor = context.Redactor;
        var configRepo = PathUtil.Full(environment.GetPath(PathKeys.ConfigRepo)!);
        var types = AgentTypes(environment.CompanyPrefixes);
        var valid = types.Where(t => ValidType().IsMatch(t)).ToList();

        var results = new Dictionary<string, (string State, string? Context)>(StringComparer.Ordinal);
        var prefixCount = 0;
        if (valid.Count > 0)
        {
            var core = PathUtil.RealPath(Path.Combine(configRepo, ".claude", "hooks", "lib", "agent-core.ps1"));
            var pathsFile = PathUtil.Full(Path.Combine(configRepo, ".claude", "hooks", "company-paths.psd1"));
            var run = await context.Processes.RunAsync(
                environment.Tools["pwsh"], ["-NoProfile", "-NonInteractive", "-Command", "-"], Timeout, cancellationToken, standardInput: Script(core, pathsFile, valid));
            if (run.TimedOut)
            {
                return CollectorResult.Unavailable("pwsh timed out after 18 s");
            }

            if (run.ExitCode != 0)
            {
                var first = run.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit code {run.ExitCode}";
                return CollectorResult.Unavailable(redactor.RedactText("pwsh failed: " + first));
            }

            if (!TryParse(run.StandardOutput, results, out prefixCount))
            {
                return CollectorResult.Unavailable("unexpected output from agent-core.ps1");
            }
        }

        var nodes = new List<TreeNode>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal) { ["Missing"] = 0, ["Empty"] = 0, ["Invalid"] = 0 };
        foreach (var type in types)
        {
            var (state, text) = ValidType().IsMatch(type) ? results.GetValueOrDefault(type, ("Missing", null)) : ("Invalid", null);
            if (counts.ContainsKey(state))
            {
                counts[state]++;
            }

            var coreFile = environment.CompanyPrefixes.Select(p => Path.Combine(p, ".claude", "custom", "agents", type, "core.md")).FirstOrDefault(File.Exists);
            var file = coreFile is null ? null : context.Files.Register(coreFile, "agent", company: true);
            var meta = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["state"] = state,
                ["chars"] = text?.Length ?? 0,
                ["tokens"] = HarnessFiles.Tokens(text?.Length ?? 0),
            };
            if (text is not null)
            {
                meta["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                meta["text"] = redactor.RedactText(text);
            }

            nodes.Add(new TreeNode(type, redactor.RedactText(type), [], file?.Id, [state.ToLowerInvariant(), "company"], meta));
        }

        return new CollectorResult(
            [new TreeBlock("cores", "Composed agent cores", nodes)],
            Notes:
            [
                $"Missing: {counts["Missing"]}, Empty: {counts["Empty"]}, Invalid: {counts["Invalid"]}",
                $"Derived company prefixes: {prefixCount}",
                "Text is the exact Get-AgentComposedContext result; the snapshot redacts secret-looking values",
            ]);
    }

    private static List<string> AgentTypes(IReadOnlyList<string> prefixes)
    {
        var types = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var prefix in prefixes)
        {
            var dir = Path.Combine(prefix, ".claude", "custom", "agents");
            try
            {
                if (Directory.Exists(dir))
                {
                    types.UnionWith(Directory.EnumerateDirectories(dir).Where(d => File.Exists(Path.Combine(d, "core.md"))).Select(d => Path.GetFileName(d)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return [.. types];
    }

    private static string Script(string core, string pathsFile, IReadOnlyList<string> types)
    {
        static string Decode(string value) => $"[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}'))";
        var list = string.Join(", ", types.Select(t => $"'{t}'"));
        return string.Join("; ",
            "$ErrorActionPreference = 'Stop'",
            $"$core = {Decode(core)}",
            $"$pathsFile = {Decode(pathsFile)}",
            ". $core",
            "$prefixes = @(Get-CompanyPrefixes -PathsFile $pathsFile -DerivedOnly)",
            $"$list = @(foreach ($t in @({list})) {{ $r = Get-AgentComposedContext -Prefixes $prefixes -AgentType $t; [ordered]@{{ type = $t; state = [string]$r.State; context = $r.Context }} }})",
            "$json = ConvertTo-Json -InputObject ([ordered]@{ prefixes = @($prefixes); agents = @($list) }) -Depth 5 -Compress",
            "[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))");
    }

    private static bool TryParse(string stdout, Dictionary<string, (string State, string? Context)> results, out int prefixCount)
    {
        prefixCount = 0;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(stdout.Trim()));
            if (JsonNode.Parse(json) is not JsonObject root)
            {
                return false;
            }

            prefixCount = root["prefixes"] switch { JsonArray a => a.Count, null => 0, _ => 1 };
            var agents = root["agents"] switch { JsonArray a => [.. a], JsonObject o => [o], _ => new List<JsonNode?>() };
            foreach (var agent in agents)
            {
                if (agent is not JsonObject item || Text(item["type"]) is not { } type || Text(item["state"]) is not { } state)
                {
                    return false;
                }

                results[type] = (state, Text(item["context"]));
            }

            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    [GeneratedRegex(@"^[A-Za-z0-9_:-]+\z")]
    internal static partial Regex ValidType();
}
