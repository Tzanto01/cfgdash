using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Health;

/// <summary>The health-v1 rules: deterministic findings over one snapshot, see docs/contracts/health-v1.md.</summary>
internal static class HealthRules
{
    private static readonly Regex ExceptionReason = new(@"^\w*Exception: ", RegexOptions.CultureInvariant);

    public static FindingsReport Evaluate(HarnessSnapshot snapshot, IRedactor redactor)
    {
        var findings = new List<Finding>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        void Add(string id, string rule, FindingSeverity severity, string title, string detail, FindingEvidence evidence, string view)
        {
            if (ids.Add(id))
            {
                findings.Add(new Finding(id, rule, severity, title, redactor.RedactText(detail), evidence, view));
            }
        }

        foreach (var status in snapshot.Collectors.Where(s => s.State == CollectorState.Unavailable))
        {
            var reason = status.Reason ?? string.Empty;
            var failed = reason.Contains("timed out", StringComparison.OrdinalIgnoreCase) || reason.Contains(" failed", StringComparison.Ordinal) || ExceptionReason.IsMatch(reason);
            var rule = failed ? "collector-error" : "collector-unavailable";
            var severity = rule == "collector-unavailable" ? FindingSeverity.Info : FindingSeverity.Warning;
            Add($"{rule}:{status.Id}", rule, severity, rule == "collector-unavailable" ? "Collector unavailable" : "Collector failed",
                $"{status.Title}: {reason}", new FindingEvidence(status.Id), status.View);
        }

        void AddItem(Section section, Block block, IReadOnlyList<string>? flags, string? itemId, string? key, string? fileId, string label, TableRow? row, TreeNode? node)
        {
            foreach (var flag in flags ?? [])
            {
                var kv = row is null && node is null;
                (string Rule, FindingSeverity Severity, string Title, string Detail)? match = flag switch
                {
                    "missing-script" => ("missing-script", FindingSeverity.Critical, "Hook script not found",
                        CellText(row, "script") is { } script
                            ? $"{label} runs {script}, which does not exist."
                            : $"{label} in {block.Title} points at a script that does not exist."),
                    "missing-command" => ("missing-command", FindingSeverity.Warning, "MCP server command not found",
                        CellText(row, "target") is { } target
                            ? $"{label} runs {target}, which is neither an existing absolute path nor on PATH."
                            : $"{label} in {block.Title} has a command that was not found."),
                    "error" when node is not null && section.CollectorId == "instructions.chain" => ("import-error", FindingSeverity.Warning, "Instruction import failed",
                        node.Meta?.GetValueOrDefault("error") is JsonValue error && error.TryGetValue<string>(out var message)
                            ? $"{label}: {message}"
                            : $"{label} could not be loaded."),
                    "error" => ("error", FindingSeverity.Warning, "Entry reports an error",
                        CellText(row, "status") is { } status
                            ? $"{label} in {block.Title}: {status}"
                            : $"{label} in {block.Title} reports an error."),
                    "duplicate" when node is null => ("duplicate", FindingSeverity.Warning, "Duplicate entry",
                        $"{label} in {block.Title} is defined more than once; one entry silently loses."),
                    "overridden" when kv => ("overridden", FindingSeverity.Info, "Setting overridden",
                        $"{label} in {block.Title} is set in more than one layer; the highest-precedence layer applies."),
                    _ => null,
                };

                if (match is { } m)
                {
                    Add($"{m.Rule}:{section.CollectorId}:{block.Id}:{itemId ?? key}", m.Rule, m.Severity, m.Title, m.Detail,
                        new FindingEvidence(section.CollectorId, block.Id, itemId, key, fileId), section.View);
                }
            }
        }

        void AddNodes(Section section, TreeBlock block, IReadOnlyList<TreeNode> nodes)
        {
            foreach (var node in nodes)
            {
                AddItem(section, block, node.Badges, node.Id, null, node.FileId, node.Label, null, node);
                AddNodes(section, block, node.Children);
            }
        }

        foreach (var section in snapshot.Sections)
        {
            foreach (var block in section.Blocks)
            {
                switch (block)
                {
                    case KvBlock kv:
                        foreach (var row in kv.Rows)
                        {
                            AddItem(section, kv, row.Flags, null, row.Key, row.Source?.FileId, row.Key, null, null);
                        }

                        break;
                    case TableBlock table:
                        foreach (var row in table.Rows)
                        {
                            var label = table.Columns.Count > 0 ? CellText(row, table.Columns[0].Key) : null;
                            AddItem(section, table, row.Flags, row.Id, null, row.FileId, label ?? row.Id, row, null);
                        }

                        break;
                    case TreeBlock tree:
                        AddNodes(section, tree, tree.Nodes);
                        break;
                }
            }
        }

        foreach (var f in McpDriftRule.Evaluate(snapshot, redactor).Concat(InstructionsDriftRule.Evaluate(snapshot, redactor)))
        {
            Add(f.Id, f.Rule, f.Severity, f.Title, f.Detail, f.Evidence, f.View);
        }

        var sorted = findings.OrderBy(f => f.Severity).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
        var counts = new FindingCounts(
            sorted.Count(f => f.Severity == FindingSeverity.Critical),
            sorted.Count(f => f.Severity == FindingSeverity.Warning),
            sorted.Count(f => f.Severity == FindingSeverity.Info));
        return new FindingsReport(snapshot.Context.TargetDir, snapshot.Machine.CollectedAt, counts, sorted);
    }

    private static string? CellText(TableRow? row, string key) => row?.Cells.GetValueOrDefault(key) switch
    {
        JsonObject obj => obj["text"] is JsonValue text && text.TryGetValue<string>(out var s) && s.Length > 0 ? s : null,
        JsonValue value => value.TryGetValue<string>(out var s) && s.Length > 0 ? s : null,
        _ => null,
    };
}
