using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Context;

/// <summary>One line of a context section: a kv row, a table row or a tree node (<see cref="Depth"/> is its tree depth).</summary>
public sealed record ContextItem(string Block, string Label, string? Value, string? Source, int Depth = 0);

/// <summary>One collector's part of the report; <see cref="State"/> is the collector state, <see cref="Reason"/> why it is not active.</summary>
public sealed record ContextSection(string Id, string CollectorId, string Title, string State, string? Reason, IReadOnlyList<ContextItem> Items);

/// <summary>
/// What Claude Code loads for a dir, from an already redacted snapshot (no file reads): instruction chain, effective settings with
/// their source, agents, skills, hooks and MCP servers. Every string is redacted again.
/// </summary>
public sealed record ContextReport(string Dir, IReadOnlyList<ContextSection> Sections)
{
    public const int MaxTitleLength = 200;
    public const int MaxIdLength = 200;

    private static readonly (string Id, string CollectorId, string Title)[] Parts =
    [
        ("instructions", "instructions.chain", "Instruction chain"),
        ("settings", "settings.effective", "Effective settings"),
        ("agents", "agents.definitions", "Agents"),
        ("skills", "skills.catalog", "Skills"),
        ("hooks", "hooks.registered", "Hooks"),
        ("mcp", "mcp.servers", "MCP servers"),
    ];

    private static readonly Redactor Redactor = new();
    private static readonly JsonSerializerOptions Indented = new(SnapshotJson.Options) { WriteIndented = true };

    public static ContextReport Build(HarnessSnapshot snapshot)
    {
        var sections = new List<ContextSection>();
        foreach (var (id, collectorId, title) in Parts)
        {
            var status = snapshot.Collectors.FirstOrDefault(c => c.Id == collectorId);
            var items = new List<ContextItem>();
            foreach (var block in snapshot.Sections.Where(s => s.CollectorId == collectorId).SelectMany(s => s.Blocks))
            {
                items.AddRange(Items(block));
            }

            var state = status is null ? "missing" : JsonNamingPolicy.CamelCase.ConvertName(status.State.ToString());
            sections.Add(new ContextSection(id, collectorId, title, state, status?.Reason is { } reason ? R(reason) : null, items));
        }

        return new ContextReport(R(snapshot.Context.TargetDir), sections);
    }

    /// <summary>Indented JSON of the report.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Indented);

    /// <summary>A plain-text rendering, one line per item.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        text.Append("Context for ").Append(Dir).Append('\n');
        foreach (var section in Sections)
        {
            text.Append('\n').Append("## ").Append(section.Title).Append(" (").Append(section.State);
            text.Append(section.Reason is null ? ")" : ": " + section.Reason + ")").Append('\n');
            foreach (var item in section.Items)
            {
                text.Append(' ', 2 + 2 * item.Depth).Append(item.Label);
                if (item.Value is { Length: > 0 })
                {
                    text.Append(" = ").Append(item.Value);
                }

                if (item.Source is { Length: > 0 })
                {
                    text.Append("  [").Append(item.Source).Append(']');
                }

                text.Append('\n');
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// The short title of a context reference in <paramref name="snapshot"/> (the assistant view), redacted and at most 200 chars.
    /// <paramref name="item"/> holds exactly the string ids of <paramref name="kind"/> (and optionally <c>kind</c>): <c>row</c>
    /// <c>collectorId</c>, <c>blockId</c>, <c>itemId</c>; <c>file</c> <c>fileId</c>; <c>finding</c> <c>findingId</c>, each at most
    /// 200 chars. Null for an unknown kind, a bad field or an id not in the snapshot.
    /// </summary>
    public static string? Titles(HarnessSnapshot snapshot, string kind, JsonObject item)
    {
        string[] fields = kind switch
        {
            "row" => ["collectorId", "blockId", "itemId"],
            "file" => ["fileId"],
            "finding" => ["findingId"],
            _ => [],
        };
        if (fields.Length == 0 || item.Any(p => p.Key != "kind" && !fields.Contains(p.Key, StringComparer.Ordinal)))
        {
            return null;
        }

        var ids = new string[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            if (item[fields[i]] is not JsonValue value || value.GetValueKind() != JsonValueKind.String || value.GetValue<string>() is not { Length: > 0 and <= MaxIdLength } id)
            {
                return null;
            }

            ids[i] = id;
        }

        var title = kind switch
        {
            "row" => RowTitle(snapshot, ids[0], ids[1], ids[2]),
            "file" => snapshot.Files.FirstOrDefault(f => f.Id == ids[0]) is { } file ? "File " + file.Path : null,
            _ => CfgdashCore.Findings(snapshot).Findings.FirstOrDefault(f => f.Id == ids[0]) is { } finding ? "Finding: " + finding.Title : null,
        };
        if (title is null)
        {
            return null;
        }

        title = R(title.Replace('\r', ' ').Replace('\n', ' '));
        return title.Length > MaxTitleLength ? title[..MaxTitleLength] : title;
    }

    private static string? RowTitle(HarnessSnapshot snapshot, string collectorId, string blockId, string itemId)
    {
        foreach (var section in snapshot.Sections.Where(s => s.CollectorId == collectorId))
        {
            foreach (var block in section.Blocks.Where(b => b.Id == blockId))
            {
                string? label = block switch
                {
                    KvBlock kv => kv.Rows.FirstOrDefault(r => r.Key == itemId)?.Key,
                    TableBlock table => table.Rows.FirstOrDefault(r => r.Id == itemId) is { } row ? RowLabel(table, row) : null,
                    TreeBlock tree => FindNode(tree.Nodes, itemId)?.Label,
                    _ => null,
                };
                if (label is not null)
                {
                    return section.Title + " / " + block.Title + " / " + label;
                }
            }
        }

        return null;
    }

    private static TreeNode? FindNode(IReadOnlyList<TreeNode> nodes, string id)
    {
        foreach (var node in nodes)
        {
            if (node.Id == id)
            {
                return node;
            }

            if (FindNode(node.Children, id) is { } child)
            {
                return child;
            }
        }

        return null;
    }

    private static IEnumerable<ContextItem> Items(Block block) => block switch
    {
        KvBlock kv => kv.Rows.Select(r => new ContextItem(block.Id, R(r.Key), R(Text(r.Value)), r.Source is null ? null : R(r.Source.Label))),
        TableBlock table => table.Rows.Select(r => new ContextItem(block.Id, R(RowLabel(table, r)),
            R(string.Join("; ", table.Columns.Skip(1).Select(c => (c.Label, Text: r.Cells.TryGetValue(c.Key, out var cell) ? CellText(cell) : null))
                .Where(c => c.Text is { Length: > 0 }).Select(c => c.Label + ": " + c.Text))),
            null)),
        TreeBlock tree => Nodes(block.Id, tree.Nodes, 0),
        _ => [],
    };

    private static IEnumerable<ContextItem> Nodes(string blockId, IReadOnlyList<TreeNode> nodes, int depth) =>
        nodes.SelectMany(n => Nodes(blockId, n.Children, depth + 1).Prepend(new ContextItem(blockId, R(n.Label), null, null, depth)));

    private static string RowLabel(TableBlock table, TableRow row) =>
        table.Columns.Count > 0 && row.Cells.TryGetValue(table.Columns[0].Key, out var cell) && CellText(cell) is { Length: > 0 } text ? text : row.Id;

    private static string? CellText(JsonNode? cell) => cell switch
    {
        JsonObject obj => obj["text"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : null,
        _ => Text(cell),
    };

    private static string? Text(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var s) => s,
        _ => node.ToJsonString(),
    };

    [return: NotNullIfNotNull(nameof(text))]
    private static string? R(string? text) => text is null ? null : Redactor.RedactText(text);
}
