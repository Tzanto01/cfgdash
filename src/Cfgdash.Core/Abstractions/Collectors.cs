using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core;

public enum OsFamily { Windows, Linux, Macos }

public enum CollectorState { Active, NotPresent, Unavailable }

/// <summary>Path keys a collector can require. Values come from <see cref="MachineEnvironment.Paths"/>.</summary>
public static class PathKeys
{
    public const string WorkspaceRoot = "workspaceRoot";
    public const string ClaudeConfigDir = "claudeConfigDir";
    public const string EngramDataDir = "engramDataDir";
    public const string ConfigRepo = "configRepo";
    public const string CompanyLayer = "companyLayer";
}

/// <summary>Evaluated by the runner in this order: OS, paths, tools. See docs/contracts/collector-v1.md.</summary>
public sealed record Requirements(
    IReadOnlyList<OsFamily>? Os = null,
    IReadOnlyList<string>? Tools = null,
    IReadOnlyList<string>? Paths = null,
    bool TargetDir = false);

public sealed record CollectorManifest(string Id, string Title, string Version, string View, Requirements Requirements);

public interface ICollector
{
    CollectorManifest Manifest { get; }

    Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken);
}

public sealed record CollectorResult(IReadOnlyList<Block> Blocks, string? UnavailableReason = null, IReadOnlyList<string>? Notes = null)
{
    public static CollectorResult Unavailable(string reason) => new([], reason);
}

/// <summary>
/// Optional on a collector whose rows or nodes carry a <c>detailId</c> of the form <c>&lt;collectorId&gt;:&lt;key&gt;</c>.
/// Resolves the key on demand; returns null for an unknown key. The runner redacts, caps and times out the result.
/// </summary>
public interface IDetailProvider
{
    Task<DetailContent?> GetDetailAsync(string key, CollectContext context, CancellationToken cancellationToken);
}

/// <summary><c>ContentType</c> is <c>markdown</c>, <c>json</c> or <c>text</c>.</summary>
public sealed record DetailContent(string Title, string ContentType, string Content);

public sealed class CollectContext
{
    public required MachineEnvironment Environment { get; init; }

    public required string TargetDir { get; init; }

    public required IFileRegistry Files { get; init; }

    public required IRedactor Redactor { get; init; }

    public required IProcessRunner Processes { get; init; }
}
