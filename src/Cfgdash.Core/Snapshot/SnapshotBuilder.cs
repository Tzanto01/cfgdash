using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;

namespace Cfgdash.Core.Snapshot;

/// <summary>The collector runner (docs/contracts/collector-v1.md "Runner rules").</summary>
internal sealed class SnapshotBuilder(
    MachineEnvironment environment,
    IReadOnlyList<ICollector> collectors,
    IRedactor redactor,
    IProcessRunner processes,
    string cfgdashVersion,
    TimeSpan? collectorTimeout = null) : ISnapshotSource
{
    private const int MaxReasonLength = 300;
    private static readonly Regex DetailKey = new(@"^[A-Za-z0-9._-]{1,128}\z", RegexOptions.CultureInvariant);
    private readonly TimeSpan timeout = collectorTimeout ?? TimeSpan.FromSeconds(20);

    public async Task<SnapshotResult> BuildAsync(string targetDir, CancellationToken cancellationToken)
    {
        var target = PathUtil.Full(targetDir);
        var registry = new FileRegistry(environment.CompanyPrefixes);
        var context = new CollectContext
        {
            Environment = environment,
            TargetDir = target,
            Files = registry,
            Redactor = redactor,
            Processes = processes,
        };

        var runs = collectors.Select(c => RunAsync(c, context, cancellationToken)).ToList();
        var outcomes = await Task.WhenAll(runs);

        var detailIds = outcomes.SelectMany(o => o.DetailIds ?? []).ToHashSet(StringComparer.Ordinal);
        var statuses = outcomes.Select(o => o.Status).ToList();
        var sections = outcomes.Where(o => o.Section is not null).Select(o => o.Section!).ToList();
        var tailnet = environment.Tailnet is { BackendState: "Running" } t ? new TailnetInfo(t.DnsName, t.TailnetName, t.Tags) : null;
        var machine = new MachineInfo(environment.Hostname, tailnet, new OsInfo(environment.Os, environment.OsDescription, environment.Arch), DateTimeOffset.UtcNow, cfgdashVersion);
        var files = registry.All.Select(f => f.Ref).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        var snapshot = new HarnessSnapshot(HarnessSnapshot.CurrentSchemaVersion, machine, new SnapshotContext(PathUtil.ToJson(target), environment.Paths), statuses, sections, files);
        return new SnapshotResult(snapshot, registry, detailIds);
    }

    /// <summary>True when <paramref name="key"/> matches <c>^[A-Za-z0-9._-]{1,128}$</c>.</summary>
    internal static bool IsValidDetailKey(string key) => DetailKey.IsMatch(key);

    private sealed record Outcome(CollectorStatus Status, Section? Section, IReadOnlyCollection<string>? DetailIds = null);

    private async Task<Outcome> RunAsync(ICollector collector, CollectContext context, CancellationToken cancellationToken)
    {
        var manifest = collector.Manifest;
        Outcome Inactive(CollectorState state, string reason, long ms, IReadOnlyList<string>? notes = null) =>
            new(new CollectorStatus(manifest.Id, manifest.Title, manifest.Version, manifest.View, state, redactor.RedactText(reason), manifest.Requirements, ms, RedactNotes(notes)), null);

        var requirementFailure = CheckRequirements(manifest.Requirements);
        if (requirementFailure is { } failure)
        {
            return Inactive(failure.State, failure.Reason, 0);
        }

        var stopwatch = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var work = Task.Run(() => collector.CollectAsync(context, cts.Token), cts.Token);

        CollectorResult result;
        try
        {
            result = await work.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException) when (!work.IsCompleted)
        {
            _ = work.ContinueWith(static t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return Inactive(CollectorState.Unavailable, $"timed out after {FormatSeconds(timeout)}", stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && cts.IsCancellationRequested)
        {
            return Inactive(CollectorState.Unavailable, $"timed out after {FormatSeconds(timeout)}", stopwatch.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Inactive(CollectorState.Unavailable, OneLine(e), stopwatch.ElapsedMilliseconds);
        }

        var ms = stopwatch.ElapsedMilliseconds;
        try
        {
            if (result.UnavailableReason is { } reason)
            {
                return Inactive(CollectorState.Unavailable, reason, ms, result.Notes);
            }

            var detailIds = new HashSet<string>(StringComparer.Ordinal);
            var prefix = manifest.Id + ":";
            string? KeepDetail(string? id)
            {
                if (id is null || !id.StartsWith(prefix, StringComparison.Ordinal) || !IsValidDetailKey(id[prefix.Length..]))
                {
                    return null;
                }

                detailIds.Add(id);
                return id;
            }

            var blocks = result.Blocks.Select(b => RedactBlock(b, KeepDetail)).ToList();
            return new Outcome(
                new CollectorStatus(manifest.Id, manifest.Title, manifest.Version, manifest.View, CollectorState.Active, null, manifest.Requirements, ms, RedactNotes(result.Notes)),
                new Section(manifest.Id, manifest.View, manifest.Title, blocks),
                detailIds);
        }
        catch (Exception e)
        {
            return Inactive(CollectorState.Unavailable, OneLine(e), ms);
        }
    }

    private (CollectorState State, string Reason)? CheckRequirements(Requirements requirements)
    {
        if (requirements.Os is { Count: > 0 } os && !os.Contains(environment.Os))
        {
            return (CollectorState.NotPresent, os.Count switch
            {
                1 => OsName(os[0]) + " only",
                _ => string.Join(", ", os.Take(os.Count - 1).Select(OsName)) + " and " + OsName(os[^1]) + " only",
            });
        }

        foreach (var key in requirements.Paths ?? [])
        {
            if (environment.GetPath(key) is null)
            {
                return (CollectorState.NotPresent, $"no {key} on this machine");
            }
        }

        foreach (var tool in requirements.Tools ?? [])
        {
            if (!environment.HasTool(tool))
            {
                return (CollectorState.Unavailable, tool + " not installed");
            }
        }

        return null;
    }

    private static string OsName(OsFamily os) => os switch
    {
        OsFamily.Windows => "Windows",
        OsFamily.Linux => "Linux",
        _ => "macOS",
    };

    private static string FormatSeconds(TimeSpan span) =>
        span.TotalSeconds >= 1 && span.TotalSeconds % 1 == 0 ? $"{span.TotalSeconds:0} s" : $"{span.TotalMilliseconds:0} ms";

    private string OneLine(Exception e)
    {
        var text = redactor.RedactText(e.GetType().Name + ": " + e.Message.ReplaceLineEndings(" ").Trim());
        return text.Length > MaxReasonLength ? text[..MaxReasonLength] : text;
    }

    private IReadOnlyList<string>? RedactNotes(IReadOnlyList<string>? notes) =>
        notes is null ? null : [.. notes.Select(redactor.RedactText)];

    private Block RedactBlock(Block block, Func<string?, string?> keepDetail) => block switch
    {
        KvBlock kv => kv with { Rows = [.. kv.Rows.Select(RedactRow)] },
        TableBlock table => table with
        {
            Rows = [.. table.Rows.Select(row => row with { Cells = row.Cells.ToDictionary(c => c.Key, c => RedactCell(c.Value)), DetailId = keepDetail(row.DetailId) })],
        },
        TreeBlock tree => tree with { Nodes = RedactNodes(tree.Nodes, keepDetail) },
        _ => block,
    };

    private KvRow RedactRow(KvRow row)
    {
        var changed = false;
        JsonNode? Redact(JsonNode? value)
        {
            var redacted = LastSegmentIsSecret(row.Key) ? JsonValue.Create(Redactor.Marker) : redactor.RedactJson(value);
            changed |= !JsonNode.DeepEquals(redacted, value);
            return redacted;
        }

        var value = Redact(row.Value);
        var contributions = row.Contributions is null
            ? null
            : row.Contributions.Select(c => c with { Value = Redact(c.Value) }).ToList();
        if (!changed)
        {
            return row;
        }

        var flags = row.Flags?.Contains("redacted") == true ? row.Flags : [.. row.Flags ?? [], "redacted"];
        return row with { Value = value, Contributions = contributions, Redacted = true, Flags = flags };
    }

    private bool LastSegmentIsSecret(string key)
    {
        var dot = key.LastIndexOf('.');
        return redactor.IsSecretName(dot >= 0 ? key[(dot + 1)..] : key);
    }

    private JsonNode? RedactCell(JsonNode? cell) => cell switch
    {
        JsonObject obj when obj["text"] is JsonValue text && text.TryGetValue<string>(out var s) => CopyWithText(obj, redactor.RedactText(s)),
        JsonValue value when value.TryGetValue<string>(out var s) => JsonValue.Create(redactor.RedactText(s)),
        _ => cell?.DeepClone(),
    };

    private static JsonObject CopyWithText(JsonObject obj, string text)
    {
        var copy = (JsonObject)obj.DeepClone();
        copy["text"] = text;
        return copy;
    }

    private IReadOnlyList<TreeNode> RedactNodes(IReadOnlyList<TreeNode> nodes, Func<string?, string?> keepDetail) =>
    [
        .. nodes.Select(node => node with
        {
            Label = redactor.RedactText(node.Label),
            Children = RedactNodes(node.Children, keepDetail),
            DetailId = keepDetail(node.DetailId),
            Meta = node.Meta?.ToDictionary(m => m.Key, m => m.Value is JsonValue v && v.TryGetValue<string>(out var s) ? JsonValue.Create(redactor.RedactText(s)) : m.Value?.DeepClone()),
        }),
    ];
}
