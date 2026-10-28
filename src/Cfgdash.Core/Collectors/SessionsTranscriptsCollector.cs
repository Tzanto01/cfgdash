using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary>
/// <c>sessions.transcripts</c>: recent Claude Code sessions of this machine from <c>&lt;claudeConfigDir&gt;/projects/*/*.jsonl</c>
/// with tokens, estimated cost and the session-audit signals; subagent transcripts roll into their parent. Rows open a redacted
/// digest (prompts, tool calls, errors, signals; never tool outputs or assistant prose). Writes only its index in the cache dir.
/// </summary>
internal sealed class SessionsTranscriptsCollector(string cacheDir, TimeSpan? indexBudget = null) : ICollector, IDetailProvider
{
    private const string CollectorId = "sessions.transcripts";
    private const int MaxRows = 500;
    private const int MaxDigestChars = 600_000;
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);
    private static readonly TimeSpan Overrun = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly TimeSpan budget = indexBudget ?? TimeSpan.FromSeconds(10);

    public CollectorManifest Manifest { get; } = new(
        CollectorId, "Sessions", "1.0.0", "sessions",
        new Requirements(Paths: [PathKeys.ClaudeConfigDir]));

    private string IndexFile => Path.Combine(cacheDir, "sessions-index.json");

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow - Window;
        var projects = ProjectsDir(context);
        var candidates = projects is null
            ? []
            : new DirectoryInfo(projects).EnumerateDirectories()
                .SelectMany(d => d.EnumerateFiles("*.jsonl"))
                .Where(f => f.LastWriteTimeUtc >= since.UtcDateTime)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

        var sessions = new List<Session>();
        var notes = new List<string>();
        var indexed = 0;
        var unreadable = 0;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var index = SessionIndex.Load(IndexFile);
            var stopwatch = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(budget + Overrun);
            var scanned = 0;
            var found = new ConcurrentBag<Session>();
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(System.Environment.ProcessorCount / 2, 1, 4), CancellationToken = cancellationToken };
            Parallel.ForEach(Partitioner.Create(candidates, EnumerablePartitionerOptions.NoBuffering), options, file =>
            {
                SessionFileSummary? Cached(FileInfo f)
                {
                    lock (index)
                    {
                        return index.Get(f);
                    }
                }

                SessionFileSummary Scan(FileInfo f, bool withSignals)
                {
                    var summary = Summarize(SessionTranscript.Scan(f.FullName, false, deadline.Token), context, withSignals);
                    lock (index)
                    {
                        index.Put(f, summary);
                    }

                    Interlocked.Increment(ref scanned);
                    return summary;
                }

                var subagents = SubagentFiles(file);
                if (Volatile.Read(ref scanned) > 0 && stopwatch.Elapsed > budget && (Cached(file) is null || subagents.Any(s => Cached(s) is null)))
                {
                    return;
                }

                try
                {
                    var parent = Cached(file) ?? Scan(file, true);
                    var children = subagents.Select(s => Cached(s) ?? Scan(s, false)).ToList();
                    found.Add(Assemble(file, parent, children));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref unreadable);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            });

            sessions.AddRange(found);
            indexed = found.Count;
            try
            {
                index.Save(IndexFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                notes.Add("session index not saved: " + e.Message);
            }
        }
        finally
        {
            Gate.Release();
        }

        if (indexed + unreadable < candidates.Count)
        {
            notes.Add($"indexed {indexed} of {candidates.Count} sessions; refresh to continue");
        }

        if (unreadable > 0)
        {
            notes.Add($"{unreadable} transcripts could not be read");
        }

        var company = context.Environment.CompanyPrefixes;
        var inWindow = sessions.Where(s => s.Parent.Last >= since).ToList();
        var excluded = company.Count == 0 ? inWindow.RemoveAll(s => HasCompanySegment(s)) : 0;
        if (excluded > 0)
        {
            notes.Add($"{excluded} sessions under a company folder left out (no company layer on this machine)");
        }

        var shown = inWindow
            .GroupBy(s => s.Id, StringComparer.Ordinal)
            .Select(g => g.MaxBy(s => s.Parent.Last)!)
            .OrderByDescending(s => s.Parent.Last)
            .ToList();
        if (shown.Count > MaxRows)
        {
            notes.Add($"showing the newest {MaxRows} of {shown.Count} sessions");
        }

        return new CollectorResult(
            [Summary(shown, indexed, candidates.Count), SessionTable(shown.Take(MaxRows), company, context.Redactor), SignalTable(shown)],
            Notes: notes.Count > 0 ? notes : null);
    }

    public async Task<DetailContent?> GetDetailAsync(string key, CollectContext context, CancellationToken cancellationToken)
    {
        if (ProjectsDir(context) is not { } projects)
        {
            return null;
        }

        SessionIndex index;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            index = SessionIndex.Load(IndexFile);
        }
        finally
        {
            Gate.Release();
        }

        var entry = index.Entries
            .Where(e => string.Equals(Path.GetFileNameWithoutExtension(e.Path), key, StringComparison.Ordinal)
                && e.Path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
                && Path.GetDirectoryName(Path.GetDirectoryName(e.Path)) is { } root && PathUtil.PathEquals(root, projects)
                && File.Exists(e.Path))
            .MaxBy(e => e.Summary.Last);
        if (entry is null)
        {
            return null;
        }

        var file = new FileInfo(entry.Path);
        var children = SubagentFiles(file).Select(f => index.Entries.FirstOrDefault(e => e.Path == f.FullName)?.Summary).OfType<SessionFileSummary>().ToList();
        var session = Assemble(file, entry.Summary, children);
        var companyPrefixes = context.Environment.CompanyPrefixes;
        if (companyPrefixes.Count == 0 && HasCompanySegment(session))
        {
            return null;
        }

        var scan = SessionTranscript.Scan(entry.Path, true, cancellationToken);
        return new DetailContent("Session " + key, "markdown", Digest(session, scan, IsCompany(session, companyPrefixes), SubagentFiles(file).Count));
    }

    private sealed record Session(string Id, string Slug, SessionFileSummary Parent, int Subagents, TokenCounts Tokens, double? CostUsd, IReadOnlyList<string> Models);

    private static string? ProjectsDir(CollectContext context) =>
        context.Environment.GetPath(PathKeys.ClaudeConfigDir) is { } configDir && Path.Combine(configDir, "projects") is var projects && Directory.Exists(projects)
            ? PathUtil.Full(projects)
            : null;

    private static List<FileInfo> SubagentFiles(FileInfo session)
    {
        var dir = new DirectoryInfo(Path.Combine(session.DirectoryName!, Path.GetFileNameWithoutExtension(session.Name), "subagents"));
        return dir.Exists ? [.. dir.EnumerateFiles("*.jsonl")] : [];
    }

    private static SessionFileSummary Summarize(TranscriptScan scan, CollectContext context, bool withSignals) => new(
        scan.Cwd,
        scan.Started,
        scan.Last,
        scan.Prompts,
        scan.ToolCalls,
        [.. scan.Models],
        scan.Tokens,
        scan.CostUsd,
        scan.Unpriced,
        withSignals ? [.. SessionTranscript.Signals(scan).Select(s => s with { Message = context.Redactor.RedactText(s.Message) })] : []);

    private static Session Assemble(FileInfo file, SessionFileSummary parent, IReadOnlyList<SessionFileSummary> subagents)
    {
        var all = subagents.Prepend(parent).ToList();
        return new Session(
            Path.GetFileNameWithoutExtension(file.Name),
            file.Directory!.Name,
            parent,
            subagents.Count,
            all.Aggregate(TokenCounts.Zero, (sum, s) => sum.Add(s.Tokens)),
            all.Any(s => s.Unpriced) ? null : all.Sum(s => s.CostUsd),
            [.. all.SelectMany(s => s.Models).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    private static bool IsCompany(Session session, IReadOnlyList<string> prefixes) =>
        session.Parent.Cwd is { } cwd && prefixes.Any(p => PathUtil.IsUnder(cwd, p));

    /// <summary>A path segment (or project-slug token) named <c>company</c>; used to leave sessions out where no company layer exists.</summary>
    private static bool HasCompanySegment(Session session) =>
        (session.Parent.Cwd ?? string.Empty).Split('/', '\\').Any(s => s.Equals("company", StringComparison.OrdinalIgnoreCase))
        || session.Slug.Split('-').Any(s => s.Equals("company", StringComparison.OrdinalIgnoreCase));

    private static KvBlock Summary(IReadOnlyList<Session> sessions, int indexed, int candidates)
    {
        var tokens = sessions.Aggregate(TokenCounts.Zero, (sum, s) => sum.Add(s.Tokens));
        var unpriced = sessions.Count(s => s.CostUsd is null);
        var source = new SourceRef("transcripts");
        return new KvBlock("summary", "Summary",
        [
            new KvRow("window", "last 30 days", source),
            new KvRow("listed", sessions.Count, source),
            new KvRow("inputTotal", tokens.Input, source),
            new KvRow("outputTotal", tokens.Output, source),
            new KvRow("cacheReadTotal", tokens.CacheRead, source),
            new KvRow("cacheWriteTotal", tokens.CacheWrite, source),
            new KvRow("estCostUsd", Math.Round(sessions.Sum(s => s.CostUsd ?? 0), 2), new SourceRef($"built-in list prices as of {SessionPrices.AsOf}; estimate")),
            new KvRow("unpricedCount", unpriced, new SourceRef("unknown model")),
            new KvRow("withHardSignals", sessions.Count(s => s.Parent.Signals.Any(g => g.Severity == "HARD")), source),
            new KvRow("indexCoverage", $"{indexed} of {candidates} sessions indexed", new SourceRef("cfgdash cache")),
        ]);
    }

    private static TableBlock SessionTable(IEnumerable<Session> sessions, IReadOnlyList<string> companyPrefixes, IRedactor redactor)
    {
        var rows = sessions.Select(s =>
        {
            var company = IsCompany(s, companyPrefixes);
            var hard = s.Parent.Signals.Any(g => g.Severity == "HARD");
            var flags = new List<string>();
            if (company)
            {
                flags.Add("company");
            }

            if (hard)
            {
                flags.Add("hard");
            }

            return new TableRow(s.Id, new Dictionary<string, JsonNode?>
            {
                ["lastActivity"] = Time(s.Parent.Last),
                ["started"] = Time(s.Parent.Started),
                ["duration"] = Duration(s.Parent),
                ["project"] = s.Parent.Cwd is { } cwd ? redactor.RedactText(cwd.Replace('\\', '/')) : s.Slug,
                ["prompts"] = s.Parent.Prompts,
                ["toolCalls"] = s.Parent.ToolCalls,
                ["subagents"] = s.Subagents,
                ["models"] = string.Join(", ", s.Models),
                ["totalTokens"] = s.Tokens.Total,
                ["inputTokens"] = s.Tokens.Input,
                ["outputTokens"] = s.Tokens.Output,
                ["cacheReadTokens"] = s.Tokens.CacheRead,
                ["cacheWriteTokens"] = s.Tokens.CacheWrite,
                ["estCostUsd"] = s.CostUsd is { } cost ? JsonValue.Create(Math.Round(cost, 2)) : null,
                ["signals"] = string.Join(", ", s.Parent.Signals.Select(g => $"{g.Code} ({g.Severity})")),
                ["company"] = company,
            }, flags.Count > 0 ? flags : null, DetailId: $"{CollectorId}:{s.Id}");
        }).ToList();

        return new TableBlock("sessions", "Sessions",
        [
            new TableColumn("lastActivity", "Last activity"), new TableColumn("started", "Started"), new TableColumn("duration", "Duration"),
            new TableColumn("project", "Project"), new TableColumn("prompts", "Prompts"), new TableColumn("toolCalls", "Tool calls"),
            new TableColumn("subagents", "Subagents"), new TableColumn("models", "Models"), new TableColumn("totalTokens", "Tokens"),
            new TableColumn("inputTokens", "Input"), new TableColumn("outputTokens", "Output"), new TableColumn("cacheReadTokens", "Cache read"),
            new TableColumn("cacheWriteTokens", "Cache write"), new TableColumn("estCostUsd", "Est. cost (USD)"), new TableColumn("signals", "Signals"),
            new TableColumn("company", "Company"),
        ],
        rows,
        "Newest first. Tokens and cost include subagents; cost is an estimate from built-in list prices and blank for unknown models.");
    }

    private static TableBlock SignalTable(IReadOnlyList<Session> sessions)
    {
        var rows = sessions
            .SelectMany(s => s.Parent.Signals.Select(g => (g.Code, g.Severity)).Distinct())
            .GroupBy(g => g)
            .OrderBy(g => g.Key.Severity == "HARD" ? 0 : 1).ThenByDescending(g => g.Count()).ThenBy(g => g.Key.Code, StringComparer.Ordinal)
            .Select(g => new TableRow($"{g.Key.Code}-{g.Key.Severity}".ToLowerInvariant(), new Dictionary<string, JsonNode?>
            {
                ["code"] = g.Key.Code,
                ["severity"] = g.Key.Severity,
                ["sessions"] = g.Count(),
            }))
            .ToList();
        return new TableBlock("signals", "Signals",
            [new TableColumn("code", "Code"), new TableColumn("severity", "Severity"), new TableColumn("sessions", "Sessions")],
            rows,
            "Mechanical signals from the session-audit rubric.");
    }

    private static string Digest(Session session, TranscriptScan scan, bool company, int subagents)
    {
        var p = session.Parent;
        var sb = new StringBuilder();
        sb.Append("# Session ").AppendLine(session.Id).AppendLine();
        sb.Append("- Project: ").AppendLine((p.Cwd ?? session.Slug).Replace('\\', '/'));
        sb.Append("- Started: ").Append(Time(scan.Started) ?? "-").Append(", last activity: ").Append(Time(scan.Last) ?? "-").Append(", duration: ").AppendLine(Duration(p));
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Prompts: {scan.Prompts}, tool calls: {scan.ToolCalls}, subagents: {subagents}");
        sb.Append("- Models: ").AppendLine(session.Models.Count > 0 ? string.Join(", ", session.Models) : "-");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Tokens (incl. subagents): input {session.Tokens.Input:N0}, output {session.Tokens.Output:N0}, cache read {session.Tokens.CacheRead:N0}, cache write {session.Tokens.CacheWrite:N0}");
        sb.Append("- Est. cost: ").AppendLine(session.CostUsd is { } cost
            ? string.Create(CultureInfo.InvariantCulture, $"${cost:0.00} (list prices as of {SessionPrices.AsOf})")
            : "unknown model");
        if (company)
        {
            sb.AppendLine("- Company session");
        }

        sb.AppendLine().AppendLine("## Signals").AppendLine();
        var signals = SessionTranscript.Signals(scan);
        if (signals.Count == 0)
        {
            sb.AppendLine("None.");
        }

        foreach (var signal in signals)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {signal.Severity} {signal.Code}: {signal.Message}");
        }

        sb.AppendLine().AppendLine("## Events").AppendLine();
        for (var i = 0; i < scan.Events.Count; i++)
        {
            if (sb.Length > MaxDigestChars)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- ({scan.Events.Count - i} more events not shown)");
                break;
            }

            var e = scan.Events[i];
            var when = e.Time is { } t ? t.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "--";
            var label = e.Kind switch { 'U' => "Prompt", 'E' => "Error", _ => "Tool" };
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {when} {label}: {e.Text}");
        }

        return sb.ToString();
    }

    private static string? Time(DateTimeOffset? value) => value is { } v ? IsoTime.Format(v.ToLocalTime()) : null;

    private static string Duration(SessionFileSummary s)
    {
        if (s.Started is not { } start || s.Last is not { } last)
        {
            return "-";
        }

        var span = last - start;
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours}h {span.Minutes:00}m")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(span.Minutes, 0)}m");
    }
}
