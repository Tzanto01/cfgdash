using System.Text.Json.Nodes;
using Cfgdash.Core.Catalog;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary><c>agents.definitions</c>: sub-agent definitions by priority (managed, project, user, plugin) with their frontmatter.</summary>
internal sealed class AgentDefinitionsCollector(SettingsLayers settings, PlatformLocations locations) : ICollector
{
    private static readonly string[] OtherKeys = ["disallowedTools", "permissionMode", "effort", "isolation", "skills", "mcpServers"];

    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new("name", "Name"), new("scope", "Scope"), new("model", "Model"), new("tools", "Tools"), new("hooks", "Hooks"),
        new("description", "Description"), new("other", "Other"), new("companyCore", "Company core"), new("file", "File"),
    ];

    public CollectorManifest Manifest { get; } = new("agents.definitions", "Agent definitions", "1.0.0", "agents", new Requirements(Paths: [PathKeys.ClaudeConfigDir], TargetDir: true));

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var stack = await settings.LoadAsync(context, cancellationToken);
        var redactor = context.Redactor;
        var prefixes = context.Environment.CompanyPrefixes;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<TableRow>();
        foreach (var agent in HarnessCatalog.Agents(context, stack, locations))
        {
            var file = context.Files.Register(agent.Path!, "agent");
            var fields = agent.Fields;
            var other = OtherKeys.Where(k => fields?[k] is not null).Select(k => $"{k}: {CatalogText.Value(fields![k])}").ToList();
            if (agent.Error is not null)
            {
                other.Insert(0, "error: " + agent.Error);
            }

            var flags = new List<string>();
            if (agent.Overridden)
            {
                flags.Add("overridden");
            }

            if (agent.Error is not null)
            {
                flags.Add("error");
            }

            if (file?.Company == true)
            {
                flags.Add("company");
            }

            if (agent.LinkTarget is not null)
            {
                flags.Add("link");
            }

            var id = $"{agent.Scope}:{agent.Name}";
            rows.Add(new TableRow(
                ids.Add(id) ? id : $"{id}@{file?.Id ?? FileRegistry.IdFor(agent.Path!)}",
                new Dictionary<string, JsonNode?>
                {
                    ["name"] = redactor.RedactText(agent.Name),
                    ["scope"] = agent.Scope,
                    ["model"] = Redacted(redactor, CatalogText.Value(fields?["model"])),
                    ["tools"] = Redacted(redactor, CatalogText.Value(fields?["tools"])),
                    ["hooks"] = Redacted(redactor, HookSummary(fields?["hooks"])),
                    ["description"] = Redacted(redactor, CatalogText.Description(fields?["description"])),
                    ["other"] = other.Count > 0 ? redactor.RedactText(string.Join("; ", other)) : null,
                    ["companyCore"] = prefixes.Any(p => File.Exists(Path.Combine(p, ".claude", "custom", "agents", agent.Name, "core.md"))),
                    ["file"] = Cell.File(redactor.RedactText(PathUtil.ToJson(agent.Path!)), file?.Id),
                },
                flags.Count > 0 ? flags : null,
                file?.Id));
        }

        return new CollectorResult(
            [new TableBlock("agents", "Agents", Columns, rows)],
            Notes: ["Agents passed with --agents are not observable", "Plugin agents' frontmatter hooks are ignored"]);
    }

    private static JsonNode? Redacted(IRedactor redactor, string? text) => text is null ? null : redactor.RedactText(text);

    private static string? HookSummary(JsonNode? hooks)
    {
        if (hooks is not JsonObject events || events.Count == 0)
        {
            return null;
        }

        return string.Join(", ", events.Select(e =>
        {
            var count = (e.Value as JsonArray)?.OfType<JsonObject>().Sum(g => (g["hooks"] as JsonArray)?.Count ?? 0) ?? 0;
            return $"{e.Key} ({count})";
        }));
    }
}
