using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cfgdash.Core.Redaction;

namespace Cfgdash.Core.Fleet;

/// <summary>One side of a snapshot diff (fleet-v1 "Diff").</summary>
public sealed record DiffSide(string? Machine, DateTimeOffset? CollectedAt, string? Dir);

/// <summary>Row counts over all emitted collectors.</summary>
public sealed record DiffCounts(int Changed, int OnlyLeft, int OnlyRight);

/// <summary>A collector's state string on each side; null when the side has no entry.</summary>
public sealed record DiffStates(string? Left, string? Right);

/// <summary>One differing item; <paramref name="Change"/> is <c>changed</c>, <c>onlyLeft</c> or <c>onlyRight</c>.</summary>
public sealed record DiffRow(string BlockId, string ItemId, string Change, JsonNode? Left, JsonNode? Right);

/// <summary>The differing rows of one collector.</summary>
public sealed record CollectorDiff(string Id, DiffStates State, IReadOnlyList<DiffRow> Rows);

/// <summary>The fleet-v1 diff answer.</summary>
public sealed record SnapshotDiffReport(DiffSide Left, DiffSide Right, DiffCounts Counts, IReadOnlyList<CollectorDiff> Collectors);

/// <summary>Compares two snapshots as JSON (any cfgdash version) on redacted values with context paths replaced by their keys.</summary>
public static class SnapshotDiff
{
    private static readonly Regex DriveRoot = new(@"^[A-Za-z]:\z", RegexOptions.CultureInvariant);

    /// <summary>Diffs <paramref name="left"/> (the local snapshot) against <paramref name="right"/> (an export or peer snapshot). Never mutates either.</summary>
    public static SnapshotDiffReport Compare(JsonObject left, JsonObject right) => Compare(left, right, new Redactor());

    internal static SnapshotDiffReport Compare(JsonObject left, JsonObject right, IRedactor redactor)
    {
        var l = Read(left, redactor);
        var r = Read(right, redactor);
        var collectors = new List<CollectorDiff>();
        int changed = 0, onlyLeft = 0, onlyRight = 0;
        foreach (var id in l.Ids.Union(r.Ids, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var leftItems = l.Items.GetValueOrDefault(id) ?? [];
            var rightItems = r.Items.GetValueOrDefault(id) ?? [];
            var rows = new List<DiffRow>();
            foreach (var (key, value) in leftItems)
            {
                if (!rightItems.TryGetValue(key, out var other))
                {
                    rows.Add(new DiffRow(key.Block, key.Item, "onlyLeft", value, null));
                    onlyLeft++;
                }
                else if (!JsonNode.DeepEquals(value, other))
                {
                    rows.Add(new DiffRow(key.Block, key.Item, "changed", value, other));
                    changed++;
                }
            }

            foreach (var (key, value) in rightItems.Where(p => !leftItems.ContainsKey(p.Key)))
            {
                rows.Add(new DiffRow(key.Block, key.Item, "onlyRight", null, value));
                onlyRight++;
            }

            var state = new DiffStates(l.States.GetValueOrDefault(id), r.States.GetValueOrDefault(id));
            if (rows.Count > 0 || state.Left != state.Right)
            {
                collectors.Add(new CollectorDiff(id, state, [.. rows.OrderBy(x => x.BlockId, StringComparer.Ordinal).ThenBy(x => x.ItemId, StringComparer.Ordinal)]));
            }
        }

        return new SnapshotDiffReport(l.Side, r.Side, new DiffCounts(changed, onlyLeft, onlyRight), collectors);
    }

    private sealed record SideData(
        DiffSide Side,
        HashSet<string> Ids,
        Dictionary<string, string?> States,
        Dictionary<string, Dictionary<(string Block, string Item), JsonNode?>> Items);

    private static SideData Read(JsonObject snapshot, IRedactor redactor)
    {
        var machine = snapshot["machine"] as JsonObject;
        var context = snapshot["context"] as JsonObject;
        var collectedAt = Str(machine?["collectedAt"]) is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : (DateTimeOffset?)null;
        var side = new DiffSide(Str(machine?["hostname"]) is { } hostname ? redactor.RedactText(hostname) : null, collectedAt, Str(context?["targetDir"]) is { } targetDir ? redactor.RedactText(targetDir) : null);

        var windows = string.Equals(Str((machine?["os"] as JsonObject)?["family"]), "windows", StringComparison.OrdinalIgnoreCase);
        var candidates = new List<(string Key, string Value)>();
        void AddCandidate(string? key, string? value)
        {
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
            {
                return;
            }

            var normalized = redactor.RedactText(value).Replace('\\', '/').TrimEnd('/');
            if (normalized.Length > 0 && !DriveRoot.IsMatch(normalized))
            {
                candidates.Add((key, normalized));
            }
        }

        foreach (var path in Objects(context?["paths"]))
        {
            AddCandidate(Str(path["key"]), Str(path["value"]));
        }

        AddCandidate("targetDir", side.Dir);
        var ordered = candidates.OrderByDescending(c => c.Value.Length).ToList();
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string Normalize(string value)
        {
            var text = redactor.RedactText(value).Replace('\\', '/');
            foreach (var (key, candidate) in ordered)
            {
                text = Substitute(text, candidate, $"<{key}>", comparison);
            }

            return text;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var states = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var collector in Objects(snapshot["collectors"]))
        {
            if (Str(collector["id"]) is { } id && ids.Add(id))
            {
                states[id] = Str(collector["state"]);
            }
        }

        var items = new Dictionary<string, Dictionary<(string Block, string Item), JsonNode?>>(StringComparer.Ordinal);
        foreach (var section in Objects(snapshot["sections"]))
        {
            if (Str(section["collectorId"]) is not { } collectorId)
            {
                continue;
            }

            ids.Add(collectorId);
            if (!items.TryGetValue(collectorId, out var collectorItems))
            {
                items[collectorId] = collectorItems = [];
            }

            foreach (var block in Objects(section["blocks"]))
            {
                if (Str(block["id"]) is not { } blockId)
                {
                    continue;
                }

                void Put(string? itemId, Func<JsonNode?> value)
                {
                    if (itemId is not null && (redactor.RedactText(blockId), redactor.RedactText(itemId)) is var key && !collectorItems.ContainsKey(key))
                    {
                        collectorItems[key] = Clean(value(), Normalize);
                    }
                }

                switch (Str(block["type"]))
                {
                    case "kv":
                        foreach (var row in Objects(block["rows"]))
                        {
                            Put(Str(row["key"]), () => Str(row["key"]) is { } key && redactor.IsSecretName(key) ? JsonValue.Create(Redactor.Marker) : redactor.RedactJson(row["value"]));
                        }

                        break;
                    case "table":
                        foreach (var row in Objects(block["rows"]))
                        {
                            var secretRow = Str(row["id"]) is { } rowId && redactor.IsSecretName(rowId);
                            Put(Str(row["id"]), () => row["cells"] is JsonObject cells
                                ? new JsonObject(cells.Select(c => KeyValuePair.Create(c.Key, c.Value is JsonObject cell ? Clean(cell["text"], s => secretRow ? Redactor.Marker : s) : Clean(c.Value, s => secretRow ? Redactor.Marker : s))))
                                : null);
                        }

                        break;
                    case "tree":
                        void Walk(JsonNode? nodes)
                        {
                            foreach (var node in Objects(nodes))
                            {
                                Put(Str(node["id"]), () => new JsonObject { ["label"] = Clean(node["label"], s => s), ["badges"] = Clean(node["badges"], s => s) });
                                Walk(node["children"]);
                            }
                        }

                        Walk(block["nodes"]);
                        break;
                }
            }
        }

        return new SideData(side, ids, states, items);
    }

    private static string Substitute(string text, string value, string replacement, StringComparison comparison)
    {
        var result = new StringBuilder();
        var start = 0;
        var search = 0;
        while (search <= text.Length - value.Length && text.IndexOf(value, search, comparison) is var index && index >= 0)
        {
            var end = index + value.Length;
            if (end == text.Length || text[end] == '/' || !(char.IsLetterOrDigit(text[end]) || text[end] is '.' or '_' or '-'))
            {
                result.Append(text, start, index - start).Append(replacement);
                start = search = end;
            }
            else
            {
                search = index + 1;
            }
        }

        return start == 0 ? text : result.Append(text, start, text.Length - start).ToString();
    }

    private static JsonNode? Clean(JsonNode? node, Func<string, string> map) => node switch
    {
        null => null,
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Clean(p.Value, map)))),
        JsonArray array => new JsonArray([.. array.Select(item => Clean(item, map))]),
        JsonValue value when value.TryGetValue<string>(out var s) => JsonValue.Create(map(s)),
        _ => node.DeepClone(),
    };

    private static IEnumerable<JsonObject> Objects(JsonNode? node) => node is JsonArray array ? array.OfType<JsonObject>() : [];

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
