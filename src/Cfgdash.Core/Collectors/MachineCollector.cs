using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary><c>overview.machine</c>: hostname, OS, context paths, tools, tailnet and the Claude Code version.</summary>
internal sealed partial class MachineCollector(string overridesFile) : ICollector
{
    public CollectorManifest Manifest { get; } = new("overview.machine", "Machine", "1.0.0", "overview", new Requirements());

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var environment = context.Environment;
        var redactor = context.Redactor;
        var system = new SourceRef("system");
        var notes = new List<string>();
        var rows = new List<KvRow>
        {
            new("hostname", redactor.RedactText(environment.Hostname), system),
            new("os", redactor.RedactText($"{environment.OsDescription} ({environment.Arch})"), system),
        };

        foreach (var path in environment.Paths)
        {
            rows.Add(new KvRow(
                path.Key,
                path.Value is null ? null : redactor.RedactText(path.Value),
                new SourceRef(path.Source ?? "not found"),
                Flags: path.Value is not null && !path.Exists ? ["missing"] : null));
        }

        var tools = new JsonObject();
        foreach (var (name, toolPath) in environment.Tools.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            tools[name] = redactor.RedactText(PathUtil.ToJson(toolPath));
        }

        rows.Add(new KvRow("tools", tools, new SourceRef("tool probe")));

        if (environment.Tailnet is { BackendState: "Running" } tailnet)
        {
            rows.Add(new KvRow("tailnet", redactor.RedactText(tailnet.DnsName), new SourceRef("tailscale status")));
        }

        if (environment.Tools.TryGetValue("claude", out var claude))
        {
            var source = new SourceRef("claude --version");
            string? failure;
            try
            {
                var result = await context.Processes.RunAsync(claude, ["--version"], TimeSpan.FromSeconds(5), cancellationToken);
                var match = VersionRegex().Match(result.StandardOutput);
                if (!result.TimedOut && result.ExitCode == 0 && match.Success)
                {
                    rows.Add(new KvRow("claudeCodeVersion", match.Value, source));
                    failure = null;
                }
                else
                {
                    failure = result.TimedOut ? "timed out" : $"exit code {result.ExitCode}";
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failure = e.GetType().Name + ": " + e.Message.ReplaceLineEndings(" ");
            }

            if (failure is not null)
            {
                rows.Add(new KvRow("claudeCodeVersion", null, source, Flags: ["error"]));
                notes.Add(redactor.RedactText("claude --version failed: " + failure));
            }
        }
        else
        {
            notes.Add("claude not installed");
        }

        if (File.Exists(overridesFile))
        {
            rows.Add(new KvRow("overridesFile", redactor.RedactText(PathUtil.ToJson(overridesFile)), new SourceRef("cfgdash")));
        }

        return new CollectorResult([new KvBlock("machine", "Machine", rows)], Notes: notes.Count > 0 ? notes : null);
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+")]
    private static partial Regex VersionRegex();
}
