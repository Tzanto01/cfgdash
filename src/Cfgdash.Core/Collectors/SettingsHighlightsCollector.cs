using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary><c>settings.highlights</c>: permission rules per source, model and display settings, auto mode strings.</summary>
internal sealed class SettingsHighlightsCollector(SettingsLayers settings) : ICollector
{
    private static readonly string[] HighlightKeys =
    [
        "permissions.defaultMode", "model", "effortLevel", "fallbackModel", "availableModels", "modelSettings", "statusLine",
        "outputStyle", "agent", "enabledPlugins", "extraKnownMarketplaces",
    ];

    private static readonly string[] PermissionKinds = ["allow", "ask", "deny"];

    public CollectorManifest Manifest { get; } = new("settings.highlights", "Permissions and model", "1.0.0", "mcp-permissions", new Requirements(Paths: [PathKeys.ClaudeConfigDir], TargetDir: true));

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var stack = await settings.LoadAsync(context, cancellationToken);
        var redactor = context.Redactor;
        var managedRulesOnly = stack.Get("allowManagedPermissionRulesOnly")?.Value is JsonValue only && only.GetValueKind() == JsonValueKind.True;
        var permissions = new List<TableRow>();
        var automode = new List<TableRow>();
        foreach (var layer in stack.Contributing)
        {
            if (layer.Root!["permissions"] is JsonObject rules)
            {
                foreach (var kind in PermissionKinds)
                {
                    if (rules[kind] is not JsonArray list)
                    {
                        continue;
                    }

                    var status = managedRulesOnly && layer.Scope != "managed" ? "ignored (managed rules only)" : "applied";
                    for (var i = 0; i < list.Count; i++)
                    {
                        var rule = list[i] is JsonValue v && v.TryGetValue<string>(out var text) ? text : list[i]?.ToJsonString() ?? "null";
                        permissions.Add(Row($"{layer.Id}:{kind}:{i}", layer, ("kind", kind), ("rule", redactor.RedactText(rule)), ("status", status)));
                    }
                }
            }

            if (layer.Root["autoMode"] is JsonObject auto)
            {
                var ignored = layer.Scope is not ("user" or "managed");
                foreach (var (kind, node) in auto)
                {
                    if (node is not JsonArray list)
                    {
                        continue;
                    }

                    for (var i = 0; i < list.Count; i++)
                    {
                        if (list[i] is JsonValue v && v.TryGetValue<string>(out var text))
                        {
                            var row = Row($"{layer.Id}:{kind}:{i}", layer, ("kind", kind), ("value", redactor.RedactText(text)), ("status", ignored ? "ignored (scope)" : "applied"));
                            automode.Add(ignored ? row with { Flags = ["ignored-scope"] } : row);
                        }
                    }
                }
            }
        }

        var highlights = stack.Settings
            .Where(s => HighlightKeys.Any(k => SettingsMerge.HasPrefix(s.Key, k)))
            .Select(s => SettingsRows.ToKvRow(s, redactor))
            .ToList();

        return new CollectorResult(
        [
            new TableBlock("permissions", "Permission rules", [new("kind", "Kind"), new("rule", "Rule"), new("source", "Source"), new("status", "Status")], permissions, "Deny rules from any scope are evaluated before allow rules"),
            new KvBlock("highlights", "Model, display and plugins", highlights),
            new TableBlock("automode", "Auto mode", [new("kind", "Kind"), new("value", "Value"), new("source", "Source"), new("status", "Status")], automode),
        ]);
    }

    private static TableRow Row(string id, SettingsLayer layer, params (string Key, string Value)[] cells)
    {
        var map = cells.ToDictionary(c => c.Key, c => (JsonNode?)JsonValue.Create(c.Value));
        map["source"] = Cell.File(layer.Label, layer.FileId);
        return new TableRow(id, map, FileId: layer.FileId);
    }
}
