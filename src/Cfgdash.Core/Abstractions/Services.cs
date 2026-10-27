using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core;

/// <summary>Builds snapshots. Implemented by the core runner; the host serves its output.</summary>
public interface ISnapshotSource
{
    Task<SnapshotResult> BuildAsync(string targetDir, CancellationToken cancellationToken);
}

public sealed record SnapshotResult(HarnessSnapshot Snapshot, IFileCatalog Files);

/// <summary>Files a snapshot registered. Only these can be opened or searched.</summary>
public interface IFileCatalog
{
    IReadOnlyList<RegisteredFile> All { get; }

    bool TryGet(string id, [MaybeNullWhen(false)] out RegisteredFile file);
}

public interface IFileRegistry : IFileCatalog
{
    /// <summary>Registers an existing file for viewing; returns null for never-read files (credentials, keys, .env, .claude.json).</summary>
    FileRef? Register(string path, string kind, bool? company = null);
}

public sealed record RegisteredFile(FileRef Ref, string FullPath);

public interface IFileContentReader
{
    /// <summary>Reads a registered file for display: JSON re-serialized after redaction, text after value redaction, capped at 512 KiB.</summary>
    Task<FileContent> ReadAsync(RegisteredFile file, CancellationToken cancellationToken);
}

/// <summary><c>ContentType</c> is <c>markdown</c>, <c>json</c> or <c>text</c>.</summary>
public sealed record FileContent(string Id, string Path, string Kind, string ContentType, string Content, bool Truncated);

public interface ISnapshotSearch
{
    SearchResponse Search(HarnessSnapshot snapshot, IFileCatalog files, IFileContentReader reader, string query, CancellationToken cancellationToken);
}

/// <summary><c>Kind</c> is <c>row</c>, <c>node</c> or <c>file</c>.</summary>
public sealed record SearchHit(string Kind, string View, string CollectorId, string BlockId, string? ItemId, string? FileId, int? Line, string Snippet);

public sealed record SearchResponse(string Query, IReadOnlyList<SearchHit> Results, bool Truncated);

public interface IRedactor
{
    bool IsSecretName(string name);

    string RedactText(string value);

    /// <summary>Returns a redacted deep copy; <paramref name="redactAllValues"/> redacts every leaf (MCP headers).</summary>
    JsonNode? RedactJson(JsonNode? node, bool redactAllValues = false);
}

/// <summary>Reads environment variables by exact allowlisted name only. Never enumerates.</summary>
public interface IEnvironmentReader
{
    string? Get(string name);

    /// <summary>Windows User or Machine scope value by exact name; null elsewhere.</summary>
    string? GetScoped(string name, EnvironmentVariableTarget target);
}

public interface IProcessRunner
{
    /// <summary>Runs an executable without a shell. Arguments are passed as a list. Kills the process tree on timeout.</summary>
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? workingDirectory = null, string? standardInput = null);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Discovered machine facts. Data only; discovery lives in the core.</summary>
public sealed record MachineEnvironment(
    OsFamily Os,
    string OsDescription,
    string Arch,
    string Hostname,
    string HomeDir,
    IReadOnlyList<ContextPath> Paths,
    IReadOnlyDictionary<string, string> Tools,
    TailnetStatus? Tailnet,
    IReadOnlyList<string> CompanyPrefixes,
    IReadOnlyList<string> KnownProjectDirs,
    IReadOnlyList<string> AllowedLogins,
    IReadOnlyList<string> AllowedTags)
{
    public string? GetPath(string key) =>
        Paths.FirstOrDefault(p => p.Key == key && p.Exists)?.Value;

    public bool HasTool(string tool) => Tools.ContainsKey(tool);
}

/// <summary>From <c>tailscale status --json</c>. <c>SelfUserLogin</c> is null for tagged nodes.</summary>
public sealed record TailnetStatus(
    string BackendState,
    string DnsName,
    string TailnetName,
    string MagicDnsSuffix,
    bool MagicDnsEnabled,
    IReadOnlyList<string> CertDomains,
    IReadOnlyList<string> Tags,
    string? SelfUserLogin);
