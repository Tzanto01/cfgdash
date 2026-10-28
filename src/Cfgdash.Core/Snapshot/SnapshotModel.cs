using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Cfgdash.Core.Snapshot;

/// <summary>Snapshot schema v1, see docs/contracts/snapshot-v1.md.</summary>
public sealed record HarnessSnapshot(
    int SchemaVersion,
    MachineInfo Machine,
    SnapshotContext Context,
    IReadOnlyList<CollectorStatus> Collectors,
    IReadOnlyList<Section> Sections,
    IReadOnlyList<FileRef> Files)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed record MachineInfo(string Hostname, TailnetInfo? Tailnet, OsInfo Os, DateTimeOffset CollectedAt, string CfgdashVersion);

public sealed record TailnetInfo(string DnsName, string TailnetName, IReadOnlyList<string> Tags);

public sealed record OsInfo(OsFamily Family, string Description, string Arch);

public sealed record SnapshotContext(string TargetDir, IReadOnlyList<ContextPath> Paths);

public sealed record ContextPath(string Key, string? Value, string? Source, bool Exists);

public sealed record CollectorStatus(
    string Id,
    string Title,
    string Version,
    string View,
    CollectorState State,
    string? Reason,
    Requirements Requirements,
    long DurationMs,
    IReadOnlyList<string>? Notes = null);

public sealed record Section(string CollectorId, string View, string Title, IReadOnlyList<Block> Blocks);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(KvBlock), "kv")]
[JsonDerivedType(typeof(TableBlock), "table")]
[JsonDerivedType(typeof(TreeBlock), "tree")]
public abstract record Block(string Id, string Title, string? Description);

public sealed record KvBlock(string Id, string Title, IReadOnlyList<KvRow> Rows, string? Description = null)
    : Block(Id, Title, Description);

public sealed record KvRow(
    string Key,
    JsonNode? Value,
    SourceRef? Source,
    bool Redacted = false,
    IReadOnlyList<string>? Flags = null,
    IReadOnlyList<Contribution>? Contributions = null);

public sealed record SourceRef(string Label, string? FileId = null);

public sealed record Contribution(JsonNode? Value, SourceRef Source, ContributionStatus Status);

public enum ContributionStatus { Applied, Overridden, Merged }

public sealed record TableBlock(string Id, string Title, IReadOnlyList<TableColumn> Columns, IReadOnlyList<TableRow> Rows, string? Description = null)
    : Block(Id, Title, Description);

public sealed record TableColumn(string Key, string Label);

/// <summary>A cell is a string, number, boolean, null, or an object <c>{ "text", "fileId"? }</c>.</summary>
public sealed record TableRow(string Id, IReadOnlyDictionary<string, JsonNode?> Cells, IReadOnlyList<string>? Flags = null, string? FileId = null, string? DetailId = null);

public sealed record TreeBlock(string Id, string Title, IReadOnlyList<TreeNode> Nodes, TreeSummary? Summary = null, string? Description = null)
    : Block(Id, Title, Description);

public sealed record TreeNode(
    string Id,
    string Label,
    IReadOnlyList<TreeNode> Children,
    string? FileId = null,
    IReadOnlyList<string>? Badges = null,
    IReadOnlyDictionary<string, JsonNode?>? Meta = null,
    string? DetailId = null);

public sealed record TreeSummary(int Files, long SizeBytes, long Tokens);

public sealed record FileRef(string Id, string Path, string Kind, long SizeBytes, long Tokens, bool Company);
