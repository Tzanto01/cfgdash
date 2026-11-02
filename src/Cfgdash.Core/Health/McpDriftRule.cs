using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Health;

/// <summary>health-v1 <c>mcp-drift</c>: one MCP server name defined with different targets across the present tools.</summary>
internal static class McpDriftRule
{
    private static readonly (string CollectorId, string BlockId, string Tool)[] Tools =
        [("mcp.servers", "servers", "Claude Code"), ("codex.config", "mcp", "Codex"), ("opencode.config", "mcp", "OpenCode")];

    public static IEnumerable<Finding> Evaluate(HarnessSnapshot snapshot, IRedactor redactor)
    {
        var defined = new List<(string CollectorId, string BlockId, string Tool, Dictionary<string, (Section Section, TableRow Row, string? Target)> Servers)>();
        foreach (var (collectorId, blockId, tool) in Tools)
        {
            if (!IsPresent(snapshot, collectorId))
            {
                continue;
            }

            var servers = new Dictionary<string, (Section, TableRow, string?)>(StringComparer.Ordinal);
            foreach (var section in snapshot.Sections.Where(s => s.CollectorId == collectorId))
            {
                foreach (var row in section.Blocks.OfType<TableBlock>().Where(b => b.Id == blockId).SelectMany(b => b.Rows))
                {
                    if (CellText(row, "name") is { } name
                        && (collectorId != "mcp.servers" || CellText(row, "status") == "applied")
                        && !(row.Cells.GetValueOrDefault("enabled") is JsonValue enabled && enabled.GetValueKind() == JsonValueKind.False))
                    {
                        servers.TryAdd(name, (section, row, CellText(row, "target")));
                    }
                }
            }

            defined.Add((collectorId, blockId, tool, servers));
        }

        foreach (var name in defined.SelectMany(d => d.Servers.Keys).Distinct(StringComparer.Ordinal))
        {
            var definers = defined.Where(d => d.Servers.ContainsKey(name)).ToList();
            if (definers.Count < 2 || definers.Select(d => d.Servers[name].Target).Distinct(StringComparer.Ordinal).Count() < 2)
            {
                continue;
            }

            var first = definers[0];
            var (section, row, _) = first.Servers[name];
            var detail = $"{name}: {string.Join(", ", definers.Select(d => $"{d.Tool} runs {d.Servers[name].Target ?? "(none)"}"))}.";
            yield return new Finding(
                $"mcp-drift:{first.CollectorId}:{first.BlockId}:{row.Id}", "mcp-drift", FindingSeverity.Info, "MCP server defined differently",
                redactor.RedactText(detail), new FindingEvidence(first.CollectorId, first.BlockId, row.Id, FileId: row.FileId), section.View);
        }
    }

    internal static bool IsPresent(HarnessSnapshot snapshot, string collectorId) =>
        snapshot.Collectors.Any(c => c.Id == collectorId && c.State == CollectorState.Active);

    internal static string? CellText(TableRow row, string key) => row.Cells.GetValueOrDefault(key) switch
    {
        JsonObject obj => obj["text"] is JsonValue text && text.TryGetValue<string>(out var s) && s.Length > 0 ? s : null,
        JsonValue value => value.TryGetValue<string>(out var s) && s.Length > 0 ? s : null,
        _ => null,
    };
}
