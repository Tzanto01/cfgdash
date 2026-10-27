using System.Text.Json.Nodes;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary><c>settings.effective</c>: every merged setting with provenance plus the layer files in precedence order.</summary>
internal sealed class SettingsEffectiveCollector(SettingsLayers settings) : ICollector
{
    private static readonly IReadOnlyList<TableColumn> Columns = [new("layer", "Layer"), new("file", "File"), new("status", "Status")];

    public CollectorManifest Manifest { get; } = new("settings.effective", "Effective settings", "1.0.0", "settings", new Requirements(Paths: [PathKeys.ClaudeConfigDir], TargetDir: true));

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var stack = await settings.LoadAsync(context, cancellationToken);
        var redactor = context.Redactor;
        var rows = stack.Settings.Select(s => SettingsRows.ToKvRow(s, redactor)).ToList();
        var layers = stack.Layers.Select(layer => new TableRow(
            layer.Id,
            new Dictionary<string, JsonNode?>
            {
                ["layer"] = layer.Label,
                ["file"] = FileCell(layer, redactor),
                ["status"] = redactor.RedactText(StatusText(layer)),
            },
            layer.Status switch
            {
                LayerStatus.Error => ["error"],
                LayerStatus.Duplicate => ["duplicate"],
                _ => null,
            },
            layer.FileId)).ToList();

        return new CollectorResult(
            [
                new KvBlock("effective", "Effective settings", rows, redactor.RedactText($"Merged from {stack.Contributing.Count} layers for {PathUtil.ToJson(stack.TargetDir)}")),
                new TableBlock("layers", "Layers (highest precedence first)", Columns, layers),
            ],
            Notes: stack.Notes);
    }

    private static JsonNode? FileCell(SettingsLayer layer, IRedactor redactor) => layer.Id switch
    {
        "managed:hklm" => Cell.Text(@"HKLM\SOFTWARE\Policies\ClaudeCode (Settings)"),
        "managed:hkcu" => Cell.Text(@"HKCU\SOFTWARE\Policies\ClaudeCode (Settings)"),
        _ => layer.Path is null ? null : Cell.File(redactor.RedactText(PathUtil.ToJson(layer.Path)), layer.FileId),
    };

    private static string StatusText(SettingsLayer layer) => layer.Status switch
    {
        LayerStatus.Loaded => "loaded",
        LayerStatus.NotPresent => "not present",
        LayerStatus.Error => "error: " + layer.Detail,
        LayerStatus.Ignored => "ignored: " + layer.Detail,
        LayerStatus.Duplicate => layer.Detail ?? "duplicate",
        _ => "not observable",
    };
}
