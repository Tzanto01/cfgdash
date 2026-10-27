using System.Text.Json.Nodes;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Search;

/// <summary>Case-insensitive substring search over block values and the redacted content of registered files (api-v1).</summary>
internal sealed class SnapshotSearch : ISnapshotSearch
{
    public const int MaxHits = 200;
    private const int SnippetLength = 160;

    public SearchResponse Search(HarnessSnapshot snapshot, IFileCatalog files, IFileContentReader reader, string query, CancellationToken cancellationToken)
    {
        var q = query.Trim();
        if (q.Length < 2)
        {
            return new SearchResponse(q, [], false);
        }

        var hits = new List<SearchHit>();
        var owners = new Dictionary<string, SearchHit>(StringComparer.Ordinal);

        bool Add(SearchHit hit)
        {
            if (hits.Count >= MaxHits)
            {
                return false;
            }

            hits.Add(hit);
            return true;
        }

        void Own(string? fileId, Section section, Block block, string? itemId)
        {
            if (fileId is not null)
            {
                owners.TryAdd(fileId, new SearchHit("file", section.View, section.CollectorId, block.Id, itemId, fileId, null, string.Empty));
            }
        }

        foreach (var section in snapshot.Sections)
        {
            foreach (var block in section.Blocks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (block)
                {
                    case KvBlock kv:
                        foreach (var row in kv.Rows)
                        {
                            Own(row.Source?.FileId, section, block, row.Key);
                            foreach (var contribution in row.Contributions ?? [])
                            {
                                Own(contribution.Source.FileId, section, block, row.Key);
                            }

                            var text = row.Key + " " + AsText(row.Value);
                            if (Matches(text, q) && !Add(new SearchHit("row", section.View, section.CollectorId, block.Id, row.Key, row.Source?.FileId, null, Snippet(text, q))))
                            {
                                return new SearchResponse(q, hits, true);
                            }
                        }

                        break;
                    case TableBlock table:
                        foreach (var row in table.Rows)
                        {
                            Own(row.FileId, section, block, row.Id);
                            foreach (var cell in row.Cells.Values)
                            {
                                if (cell is JsonObject obj && obj["fileId"] is JsonValue id && id.TryGetValue<string>(out var cellFileId))
                                {
                                    Own(cellFileId, section, block, row.Id);
                                }
                            }

                            var text = string.Join(" ", table.Columns.Select(c => row.Cells.TryGetValue(c.Key, out var cell) ? CellText(cell) : string.Empty));
                            if (Matches(text, q) && !Add(new SearchHit("row", section.View, section.CollectorId, block.Id, row.Id, row.FileId, null, Snippet(text, q))))
                            {
                                return new SearchResponse(q, hits, true);
                            }
                        }

                        break;
                    case TreeBlock tree:
                        if (!SearchNodes(tree.Nodes, section, block))
                        {
                            return new SearchResponse(q, hits, true);
                        }

                        break;
                }
            }
        }

        bool SearchNodes(IReadOnlyList<TreeNode> nodes, Section section, Block block)
        {
            foreach (var node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Own(node.FileId, section, block, node.Id);
                var metaText = node.Meta is null
                    ? string.Empty
                    : string.Join(" ", node.Meta.Values.Select(v => v is JsonValue value && value.TryGetValue<string>(out var s) ? s : null).Where(s => s is not null));
                var text = node.Label + " " + metaText;
                if (Matches(text, q) && !Add(new SearchHit("node", section.View, section.CollectorId, block.Id, node.Id, node.FileId, null, Snippet(text, q))))
                {
                    return false;
                }

                if (!SearchNodes(node.Children, section, block))
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var file in files.All.OrderBy(f => f.Ref.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string content;
            try
            {
                content = reader.ReadAsync(file, cancellationToken).GetAwaiter().GetResult().Content;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            owners.TryGetValue(file.Ref.Id, out var owner);
            var lines = content.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (!Matches(lines[i], q))
                {
                    continue;
                }

                var hit = new SearchHit("file", owner?.View ?? "files", owner?.CollectorId ?? string.Empty, owner?.BlockId ?? string.Empty, owner?.ItemId, file.Ref.Id, i + 1, Snippet(lines[i].TrimEnd('\r'), q));
                if (!Add(hit))
                {
                    return new SearchResponse(q, hits, true);
                }
            }
        }

        return new SearchResponse(q, hits, false);
    }

    private static bool Matches(string text, string query) => text.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string AsText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "null";

    private static string CellText(JsonNode? cell) => cell switch
    {
        JsonObject obj => obj["text"] is JsonValue t && t.TryGetValue<string>(out var s) ? s : string.Empty,
        null => string.Empty,
        _ => AsText(cell),
    };

    private static string Snippet(string text, string query)
    {
        var line = text.Replace('\r', ' ').Replace('\n', ' ');
        if (line.Length <= SnippetLength)
        {
            return line;
        }

        var index = Math.Max(0, line.IndexOf(query, StringComparison.OrdinalIgnoreCase));
        var start = Math.Clamp(index - (SnippetLength - query.Length) / 2, 0, line.Length - SnippetLength);
        return line.Substring(start, SnippetLength);
    }
}
