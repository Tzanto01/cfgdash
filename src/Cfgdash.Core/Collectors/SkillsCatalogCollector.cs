using System.Text.Json.Nodes;
using Cfgdash.Core.Catalog;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary><c>skills.catalog</c>: skills, slash commands and output styles with scope, link target and status.</summary>
internal sealed class SkillsCatalogCollector(SettingsLayers settings, PlatformLocations locations) : ICollector
{
    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new("name", "Name"), new("description", "Description"), new("scope", "Scope"), new("file", "File"),
        new("exposedVia", "Exposed via"), new("status", "Status"),
    ];

    public CollectorManifest Manifest { get; } = new("skills.catalog", "Skills, commands and output styles", "1.0.0", "skills", new Requirements(Paths: [PathKeys.ClaudeConfigDir], TargetDir: true));

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var stack = await settings.LoadAsync(context, cancellationToken);
        var outputStyle = stack.Get("outputStyle")?.Value is JsonValue v && v.TryGetValue<string>(out var style) ? style : "Default";
        var notes = new List<string> { "Same-name command priority (user over project) is not documented" };
        if (PluginLocator.Locate(stack, context.Environment).Count > 0)
        {
            notes.Add(PluginLocator.CacheDirNote);
        }

        return new CollectorResult(
        [
            new TableBlock("skills", "Skills", Columns, Rows(context, HarnessCatalog.Skills(context, stack, locations), _ => null)),
            new TableBlock("commands", "Slash commands", Columns, Rows(context, HarnessCatalog.Commands(context, stack, locations), _ => null)),
            new TableBlock("output-styles", "Output styles", Columns, Rows(context, HarnessCatalog.OutputStyles(context, stack, locations), e => e.Name == outputStyle)),
        ],
        Notes: notes);
    }

    private static List<TableRow> Rows(CollectContext context, IReadOnlyList<CatalogEntry> entries, Func<CatalogEntry, bool?> selected)
    {
        var redactor = context.Redactor;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<TableRow>();
        foreach (var entry in entries)
        {
            var file = entry.Path is null ? null : context.Files.Register(entry.Path, entry.Kind);
            var flags = new List<string>();
            if (entry.Overridden)
            {
                flags.Add("overridden");
            }

            if (entry.LinkTarget is not null)
            {
                flags.Add("link");
            }

            if (entry.Error is not null)
            {
                flags.Add("error");
            }

            if (file?.Company == true)
            {
                flags.Add("company");
            }

            if (entry.Path is null)
            {
                flags.Add("built-in");
            }

            var exposed = entry.Kind == "skill" ? Path.GetDirectoryName(entry.Path!)! : entry.Path;
            var status = entry.Overridden ? "overridden" : selected(entry) switch { null or true => "active", false => null };
            var description = CatalogText.Description(entry.Fields?["description"]) ?? (entry.Error is null ? null : "error: " + entry.Error);
            var id = $"{entry.Scope}:{entry.Name}";
            rows.Add(new TableRow(
                ids.Add(id) ? id : $"{id}@{file?.Id ?? FileRegistry.IdFor(entry.Path!)}",
                new Dictionary<string, JsonNode?>
                {
                    ["name"] = redactor.RedactText(entry.Name),
                    ["description"] = description is null ? null : redactor.RedactText(description),
                    ["scope"] = entry.Scope,
                    ["file"] = entry.Path is null ? null : Cell.File(redactor.RedactText(PathUtil.ToJson(entry.Path)), file?.Id),
                    ["exposedVia"] = entry.LinkTarget is null ? null : redactor.RedactText($"{PathUtil.ToJson(exposed!)} -> {entry.LinkTarget}"),
                    ["status"] = status,
                },
                flags.Count > 0 ? flags : null,
                file?.Id));
        }

        return rows;
    }
}
