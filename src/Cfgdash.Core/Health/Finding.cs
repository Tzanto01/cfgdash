namespace Cfgdash.Core.Health;

/// <summary>Finding severity, see docs/contracts/health-v1.md.</summary>
public enum FindingSeverity { Critical, Warning, Info }

/// <summary>Where a finding lives in the snapshot. <paramref name="BlockId"/> is the raw block id; item, key and file are optional.</summary>
public sealed record FindingEvidence(string CollectorId, string? BlockId = null, string? ItemId = null, string? Key = null, string? FileId = null);

/// <summary>One deterministic health finding. <paramref name="Id"/> is stable: <c>&lt;rule&gt;:&lt;collectorId&gt;[:&lt;blockId&gt;:&lt;itemId or key&gt;]</c>; <paramref name="Detail"/> is redacted.</summary>
public sealed record Finding(string Id, string Rule, FindingSeverity Severity, string Title, string Detail, FindingEvidence Evidence, string View);

/// <summary>Number of findings per severity.</summary>
public sealed record FindingCounts(int Critical, int Warning, int Info);

/// <summary>The findings for one snapshot of one target dir, sorted by severity then id.</summary>
public sealed record FindingsReport(string Dir, DateTimeOffset CollectedAt, FindingCounts Counts, IReadOnlyList<Finding> Findings);
