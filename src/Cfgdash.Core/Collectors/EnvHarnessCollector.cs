using System.Text.Json.Nodes;
using Cfgdash.Core.Environment;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary><c>env.harness</c>: merged settings <c>env</c> with provenance and the allowlisted variables per scope.</summary>
internal sealed class EnvHarnessCollector(SettingsLayers settings, IEnvironmentReader environmentReader) : ICollector
{
    public CollectorManifest Manifest { get; } = new("env.harness", "Environment", "1.0.0", "env", new Requirements());

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var stack = await settings.LoadAsync(context, cancellationToken);
        var redactor = context.Redactor;
        var windows = context.Environment.Os == OsFamily.Windows;
        var settingsEnv = stack.WithPrefix("env").Select(s => SettingsRows.ToKvRow(s, redactor)).ToList();

        List<TableColumn> columns = [new("name", "Name"), new("kind", "Kind"), new("process", "Process")];
        if (windows)
        {
            columns.AddRange([new("user", "User"), new("machine", "Machine")]);
        }

        var rows = new List<TableRow>();
        foreach (var (names, presenceOnly) in new[] { (HarnessEnvAllowlist.Harness, false), (HarnessEnvAllowlist.PresenceOnly, true) })
        {
            foreach (var name in names)
            {
                JsonNode? Show(string? raw) =>
                    raw is null ? null
                    : presenceOnly ? "set"
                    : redactor.IsSecretName(name) ? Redactor.Marker
                    : redactor.RedactText(raw);

                var cells = new Dictionary<string, JsonNode?>
                {
                    ["name"] = name,
                    ["kind"] = presenceOnly ? "presence only" : "harness",
                    ["process"] = Show(environmentReader.Get(name)),
                };
                if (windows)
                {
                    cells["user"] = Show(environmentReader.GetScoped(name, EnvironmentVariableTarget.User));
                    cells["machine"] = Show(environmentReader.GetScoped(name, EnvironmentVariableTarget.Machine));
                }

                rows.Add(new TableRow(name, cells));
            }
        }

        return new CollectorResult(
            [
                new KvBlock("settings-env", "Settings env", settingsEnv),
                new TableBlock("variables", "Environment variables", columns, rows),
            ],
            Notes: ["Runtime probe variables are never displayed"]);
    }
}
