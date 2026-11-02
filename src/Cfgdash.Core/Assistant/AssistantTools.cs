using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Edits;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Assistant;

/// <summary>The MCP tool results of the config assistant (docs/contracts/assistant-v1.md), always computed on an <see cref="AssistantView"/>.</summary>
public static class AssistantTools
{
    public const int MaxSnapshotBytes = 256 * 1024;
    public const int MaxFileBytes = 64 * 1024;
    public const int MinQueryLength = 2;

    /// <summary>
    /// The filtered snapshot, optionally narrowed to one view and/or collector, with <c>files</c> limited to those the kept sections
    /// reference. Over 256 KiB whole sections are left out and <c>truncated</c> is true.
    /// </summary>
    public static JsonObject GetSnapshot(AssistantView view, string? viewId, string? collectorId)
    {
        var snapshot = view.Snapshot;
        bool Wanted(string sectionView, string id) => (viewId is null || sectionView == viewId) && (collectorId is null || id == collectorId);

        var refs = snapshot.Files.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var collectors = snapshot.Collectors.Where(c => Wanted(c.View, c.Id)).ToList();
        long size = Bytes(snapshot with { Collectors = collectors, Sections = [], Files = [] }) + 32;
        var sections = new List<Section>();
        var files = new List<FileRef>();
        var fileIds = new HashSet<string>(StringComparer.Ordinal);
        var truncated = false;
        foreach (var section in snapshot.Sections.Where(s => Wanted(s.View, s.CollectorId)))
        {
            var added = section.Blocks.SelectMany(AssistantView.FileIds).Distinct(StringComparer.Ordinal)
                .Where(id => !fileIds.Contains(id) && refs.ContainsKey(id)).Select(id => refs[id]).ToList();
            var cost = Bytes(section) + 1 + added.Sum(f => Bytes(f) + 1);
            if (size + cost > MaxSnapshotBytes)
            {
                truncated = true;
                continue;
            }

            size += cost;
            sections.Add(section);
            files.AddRange(added);
            fileIds.UnionWith(added.Select(f => f.Id));
        }

        var body = (JsonObject)JsonSerializer.SerializeToNode(snapshot with { Collectors = collectors, Sections = sections, Files = files }, SnapshotJson.Options)!;
        body["truncated"] = truncated;
        return body;
    }

    /// <summary>The health-v1 findings of the filtered snapshot.</summary>
    public static JsonObject GetFindings(AssistantView view) =>
        (JsonObject)JsonSerializer.SerializeToNode(CfgdashCore.Findings(view.Snapshot), SnapshotJson.Options)!;

    /// <summary>
    /// The redacted content of a file in the view, capped at 64 KiB of UTF-8 (cut on a character boundary) and wrapped in the
    /// session's <c>&lt;context-<paramref name="nonce"/>&gt;</c> fence. Null for an id outside the view.
    /// </summary>
    public static async Task<JsonObject?> GetFileAsync(AssistantView view, IFileContentReader reader, string fileId, string nonce, CancellationToken cancellationToken)
    {
        if (!view.Files.TryGet(fileId, out var file))
        {
            return null;
        }

        var content = await reader.ReadAsync(file, cancellationToken);
        var text = content.Content;
        var truncated = content.Truncated;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxFileBytes)
        {
            var end = MaxFileBytes;
            while (end > 0 && (bytes[end] & 0xC0) == 0x80)
            {
                end--;
            }

            text = Encoding.UTF8.GetString(bytes, 0, end);
            truncated = true;
        }

        return new JsonObject
        {
            ["fileId"] = content.Id,
            ["path"] = content.Path,
            ["kind"] = content.Kind,
            ["contentType"] = content.ContentType,
            ["content"] = $"<context-{nonce}>\n{text}\n</context-{nonce}>",
            ["truncated"] = truncated,
        };
    }

    /// <summary>The api-v1 search over the view only; <c>{ "error": "query-too-short" }</c> below 2 characters.</summary>
    public static JsonObject Search(AssistantView view, ISnapshotSearch search, IFileContentReader reader, string q, CancellationToken cancellationToken)
    {
        if (q.Trim().Length < MinQueryLength)
        {
            return new JsonObject { ["error"] = "query-too-short" };
        }

        return (JsonObject)JsonSerializer.SerializeToNode(search.Search(view.Snapshot, view.Files, reader, q, cancellationToken), SnapshotJson.Options)!;
    }

    /// <summary>
    /// What the model sees of a proposal: <c>{ proposalId, expiresAt, files: [ { path, scope, diff } ] }</c>, never <c>confirm</c>.
    /// Any other outcome (<c>denied</c>, <c>invalid-change</c>) is its body unchanged. Diff lines naming a company-layer path
    /// keep only their <c>+</c>/<c>-</c>/space marker followed by <c>[company path hidden]</c>.
    /// </summary>
    public static JsonObject ProposalForModel(EditResult result, AssistantView view)
    {
        if (result.Status != EditStatus.Ok)
        {
            return (JsonObject)result.Body.DeepClone();
        }

        var files = result.Body["files"] as JsonArray ?? [];
        return new JsonObject
        {
            ["proposalId"] = result.Body["id"]?.DeepClone(),
            ["expiresAt"] = result.Body["expiresAt"]?.DeepClone(),
            ["files"] = new JsonArray([.. files.OfType<JsonObject>().Select(f => (JsonNode)new JsonObject
            {
                ["path"] = f["path"]?.DeepClone(),
                ["scope"] = f["scope"]?.DeepClone(),
                ["diff"] = f["diff"] is JsonValue diff && diff.TryGetValue<string>(out var text) ? HideCompanyLines(text, view) : f["diff"]?.DeepClone(),
            })]),
        };
    }

    private static string HideCompanyLines(string diff, AssistantView view) =>
        string.Join('\n', diff.Split('\n').Select(line => view.MentionsCompanyPath(line) ? line[..1] + "[company path hidden]" : line));

    private static long Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, SnapshotJson.Options).LongLength;
}
