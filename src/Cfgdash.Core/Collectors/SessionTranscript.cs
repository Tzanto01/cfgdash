using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Cfgdash.Core.Collectors;

/// <summary>Assistant token counts, deduplicated by message id.</summary>
internal sealed record TokenCounts(long Input, long Output, long CacheRead, long CacheWrite)
{
    public static readonly TokenCounts Zero = new(0, 0, 0, 0);

    [JsonIgnore]
    public long Total => Input + Output + CacheRead + CacheWrite;

    public TokenCounts Add(TokenCounts other) =>
        new(Input + other.Input, Output + other.Output, CacheRead + other.CacheRead, CacheWrite + other.CacheWrite);
}

/// <summary>A mechanical audit signal; codes and thresholds follow the session-audit skill's <c>Get-SessionSignals</c>.</summary>
internal sealed record SessionSignal(string Severity, string Code, string Message);

/// <summary>One timeline entry: <c>U</c> user prompt, <c>A</c> tool call, <c>E</c> error result. <c>Text</c> is only kept for a digest.</summary>
internal sealed class TranscriptEvent(char kind, DateTimeOffset? time, string? text, string? tool = null, string? agentType = null)
{
    public char Kind { get; } = kind;

    public DateTimeOffset? Time { get; } = time;

    public string? Text { get; } = text;

    public string? Tool { get; } = tool;

    public string? AgentType { get; } = agentType;

    public bool Blocked { get; set; }

    public bool IsAgent => Kind == 'A' && Tool is "Agent" or "Task";
}

/// <summary>What one transcript file yields. Never holds tool outputs or assistant prose.</summary>
internal sealed class TranscriptScan
{
    public string? Cwd { get; set; }

    public DateTimeOffset? Started { get; set; }

    public DateTimeOffset? Last { get; set; }

    public int Prompts { get; set; }

    public int ToolCalls { get; set; }

    public SortedSet<string> Models { get; } = new(StringComparer.Ordinal);

    public TokenCounts Tokens { get; set; } = TokenCounts.Zero;

    public double CostUsd { get; set; }

    public bool Unpriced { get; set; }

    public List<string> Errors { get; } = [];

    public List<TranscriptEvent> Events { get; } = [];

    public List<string> Skills { get; } = [];

    public List<string> Agents { get; } = [];

    public bool EditedCode { get; set; }
}

/// <summary>Streams a Claude Code transcript (<c>.jsonl</c>) line by line and ports the mechanical signals of the session-audit skill (<c>SessionAudit.psm1</c>).</summary>
internal static partial class SessionTranscript
{
    public const int PromptChars = 2000;
    public const int ToolChars = 140;
    private const int ErrorChars = 200;
    private const double TokenRateSpikeMhr = 25;
    private const long TokenSpikeFloor = 12_000_000;

    private static readonly string[] ReviewAgents = ["momus", "council", "code-reviewer", "security-reviewer", "oracle", "healthcare-reviewer"];
    private static readonly string[] WorkflowSkills = ["coding-workflow", "feature", "feature-team", "bugfix", "refactor"];

    /// <summary>Reads one transcript. With <paramref name="digest"/> the events keep their (unredacted) text for the caller to redact.</summary>
    public static TranscriptScan Scan(string path, bool digest, CancellationToken cancellationToken)
    {
        var scan = new TranscriptScan();
        var usageById = new Dictionary<string, MessageUsage>(StringComparer.Ordinal);
        var usageWithoutId = new List<MessageUsage>();
        var agentUses = new Dictionary<string, TranscriptEvent>(StringComparer.Ordinal);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        foreach (var line in Lines(stream))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Header(line.Span) is not { } header)
            {
                continue;
            }

            if (header.Time is { } time)
            {
                scan.Started ??= time;
                scan.Last = time;
            }

            if (scan.Cwd is null && header.Cwd is { Length: > 0 } cwd)
            {
                scan.Cwd = cwd;
            }

            var needed = header.Type == "assistant"
                || (header.Type == "user" && (!header.HasToolResult || line.Span.IndexOf("\"is_error\":true"u8) >= 0 || line.Span.IndexOf("\"is_error\": true"u8) >= 0));
            if (!needed)
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                ReadRecord(document.RootElement, header.Type!, header.Time, scan, digest, usageById, usageWithoutId, agentUses);
            }
        }

        var tokens = TokenCounts.Zero;
        foreach (var usage in usageById.Values.Concat(usageWithoutId))
        {
            if (usage.Total == 0)
            {
                continue;
            }

            if (usage.Model is { } model && model != "<synthetic>")
            {
                scan.Models.Add(model);
            }

            tokens = tokens.Add(new TokenCounts(usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite));
            if (SessionPrices.Cost(usage) is { } cost)
            {
                scan.CostUsd += cost;
            }
            else
            {
                scan.Unpriced = true;
            }
        }

        scan.Tokens = tokens;
        return scan;
    }

    /// <summary>Top-level <c>type</c>, <c>timestamp</c>, <c>cwd</c> and whether a <c>toolUseResult</c> is present, read without building a document; null for a line that is not a JSON object.</summary>
    private static RecordHeader? Header(ReadOnlySpan<byte> line)
    {
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            string? type = null, cwd = null;
            DateTimeOffset? time = null;
            var hasToolResult = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.ValueTextEquals("type"u8) ? 1 : reader.ValueTextEquals("timestamp"u8) ? 2 : reader.ValueTextEquals("cwd"u8) ? 3 : reader.ValueTextEquals("toolUseResult"u8) ? 4 : 0;
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                {
                    switch (name)
                    {
                        case 1:
                            type = reader.GetString();
                            break;
                        case 2 when reader.TryGetDateTimeOffset(out var t):
                            time = t;
                            break;
                        case 3:
                            cwd = reader.GetString();
                            break;
                    }
                }

                hasToolResult |= name == 4;
                reader.Skip();
            }

            return new RecordHeader(type, time, cwd, hasToolResult);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Lines of a UTF-8 stream without decoding; each slice is valid until the next one is requested.</summary>
    private static IEnumerable<ReadOnlyMemory<byte>> Lines(Stream stream)
    {
        var buffer = new byte[1 << 16];
        int start = 0, end = 0;
        while (true)
        {
            var newline = buffer.AsSpan(start, end - start).IndexOf((byte)'\n');
            if (newline >= 0)
            {
                yield return buffer.AsMemory(start, newline);
                start += newline + 1;
                continue;
            }

            if (start > 0)
            {
                Buffer.BlockCopy(buffer, start, buffer, 0, end - start);
                end -= start;
                start = 0;
            }

            if (end == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            var read = stream.Read(buffer, end, buffer.Length - end);
            if (read == 0)
            {
                if (end > 0)
                {
                    yield return buffer.AsMemory(0, end);
                }

                yield break;
            }

            end += read;
        }
    }

    private sealed record RecordHeader(string? Type, DateTimeOffset? Time, string? Cwd, bool HasToolResult);

    private static void ReadRecord(
        JsonElement root,
        string type,
        DateTimeOffset? time,
        TranscriptScan scan,
        bool digest,
        Dictionary<string, MessageUsage> usageById,
        List<MessageUsage> usageWithoutId,
        Dictionary<string, TranscriptEvent> agentUses)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (type == "assistant" && message.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            var cacheCreation = usage.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object ? cc : default;
            var record = new MessageUsage(
                String(message, "model"),
                Long(usage, "input_tokens"),
                Long(usage, "output_tokens"),
                Long(usage, "cache_read_input_tokens"),
                Long(usage, "cache_creation_input_tokens"),
                cacheCreation.ValueKind == JsonValueKind.Object ? Long(cacheCreation, "ephemeral_1h_input_tokens") : 0,
                String(usage, "speed") == "fast");
            if (String(message, "id") is { } id)
            {
                usageById[id] = usageById.TryGetValue(id, out var seen) ? seen.Merge(record) : record;
            }
            else
            {
                usageWithoutId.Add(record);
            }
        }

        message.TryGetProperty("content", out var content);
        if (type == "user" && !(root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True))
        {
            var text = content.ValueKind == JsonValueKind.String ? content.GetString() : JoinText(content);
            if (!string.IsNullOrEmpty(text) && !TagStart().IsMatch(text))
            {
                scan.Prompts++;
                scan.Events.Add(new TranscriptEvent('U', time, digest ? Limit(Clear(text), PromptChars) : null));
            }
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            switch (String(block, "type"))
            {
                case "tool_use":
                    ReadToolUse(block, time, scan, digest, agentUses);
                    break;
                case "tool_result" when block.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True:
                    var result = block.TryGetProperty("content", out var resultContent) && resultContent.ValueKind == JsonValueKind.String
                        ? resultContent.GetString() ?? string.Empty
                        : JoinText(resultContent);
                    var clean = Limit(Clear(result), ErrorChars);
                    scan.Errors.Add(clean);
                    scan.Events.Add(new TranscriptEvent('E', time, digest ? clean : null));
                    if (String(block, "tool_use_id") is { } useId && agentUses.TryGetValue(useId, out var agentEvent)
                        && !agentEvent.Blocked && AgentBlocked().IsMatch(clean))
                    {
                        agentEvent.Blocked = true;
                        if (agentEvent.AgentType is { } blockedType)
                        {
                            scan.Agents.Remove(blockedType);
                        }
                    }

                    break;
            }
        }
    }

    private static void ReadToolUse(JsonElement block, DateTimeOffset? time, TranscriptScan scan, bool digest, Dictionary<string, TranscriptEvent> agentUses)
    {
        var name = String(block, "name") ?? string.Empty;
        var input = block.TryGetProperty("input", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        string? In(string key) => input.ValueKind == JsonValueKind.Object ? String(input, key) : null;

        scan.ToolCalls++;
        string? agentType = null;
        if (name == "Skill" && In("skill") is { } skill)
        {
            scan.Skills.Add(skill);
        }

        if (name is "Agent" or "Task")
        {
            agentType = In("subagent_type");
            if (agentType is not null)
            {
                scan.Agents.Add(agentType);
            }
        }

        if (name is "Edit" or "Write" && In("file_path") is { } file && CodeFile().IsMatch(file))
        {
            scan.EditedCode = true;
        }

        var ev = new TranscriptEvent('A', time, digest ? Limit(ToolSummary(name, In), ToolChars) : null, name, agentType);
        scan.Events.Add(ev);
        if (name is "Agent" or "Task" && String(block, "id") is { } id)
        {
            agentUses[id] = ev;
        }
    }

    /// <summary>One-line summary of a tool call (<c>Get-ToolSummary</c>): the tool name and at most a path, pattern, command or description.</summary>
    private static string ToolSummary(string name, Func<string, string?> input) => name switch
    {
        "Skill" => $"Skill({input("skill")})",
        "Agent" or "Task" => $"Agent({input("subagent_type")}): {Limit(Clear(input("description")), 80)}",
        "SendMessage" => $"SendMessage(->{input("to") ?? input("agent_id")})",
        "PowerShell" or "Bash" => $"{name}: {Limit(Clear(input("command")), 110)}",
        "Read" or "Edit" or "Write" => $"{name}({input("file_path")})",
        "Grep" => $"Grep(/{input("pattern")}/ path={input("path")})",
        "Glob" => $"Glob({input("pattern")} path={input("path")})",
        _ => name,
    };

    /// <summary>Mechanical HARD/SOFT signals of one session (<c>Get-SessionSignals</c>, same codes and thresholds).</summary>
    public static IReadOnlyList<SessionSignal> Signals(TranscriptScan s)
    {
        var signals = new List<SessionSignal>();

        var total = s.Tokens.Total;
        if (total > TokenSpikeFloor)
        {
            var hours = s.Started is { } first && s.Last is { } last ? Math.Max((last - first).TotalHours, 0.1) : 0.1;
            var rate = total / 1e6 / hours;
            if (rate > TokenRateSpikeMhr)
            {
                var cacheReadPct = Math.Round(100.0 * s.Tokens.CacheRead / total);
                signals.Add(new("HARD", "TOKEN_SPIKE", string.Format(CultureInfo.InvariantCulture,
                    "{0:0.#} M tokens in {1:N1}h = {2:N0} M/hr ({3}% cache_read): hot burn; check over-long agent runs, broad in-context discovery, or concurrent heavy pods.",
                    Math.Round(total / 1e6, 1), hours, rate, cacheReadPct)));
            }
        }

        var timeouts = s.Errors.Count(e => SearchTimeout().IsMatch(e) && !BrowserTool().IsMatch(e));
        if (timeouts > 0)
        {
            signals.Add(new("HARD", "SEARCH_TIMEOUT", $"{timeouts} search/command timeout(s): likely a search not scoped to a sub-repo."));
        }

        var browserTimeouts = s.Errors.Count(e => TimedOut().IsMatch(e) && BrowserTool().IsMatch(e));
        if (browserTimeouts > 0)
        {
            signals.Add(new(browserTimeouts >= 5 ? "HARD" : "SOFT", "BROWSER_TIMEOUT", $"{browserTimeouts} browser automation timeout(s): browser pane likely hidden; stop-loss should have fired after 2."));
        }

        if (s.Errors.Count > 0)
        {
            var loop = s.Errors
                .Where(e => !Permission().IsMatch(e) && !Infrastructure().IsMatch(e))
                .Select(ErrorSignature)
                .OfType<string>()
                .GroupBy(sig => sig, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.Key, Count: g.Count()))
                .OrderByDescending(g => g.Count)
                .FirstOrDefault();
            if (loop.Count >= 3)
            {
                signals.Add(new("HARD", "FAILURE_LOOP", $"Same failure repeated {loop.Count}x ('{loop.Key}'): stop-loss should have fired after 2."));
            }

            var denials = s.Errors.Count(e => Permission().IsMatch(e));
            if (denials >= 3)
            {
                signals.Add(new("SOFT", "GUARDRAIL_REPEAT", $"{denials} permission/approval denials or user declines: repeated blocked attempts or a noisy approval flow."));
            }
        }

        var wiring = s.Errors.Count(e => Wiring().IsMatch(e));
        if (wiring > 0)
        {
            signals.Add(new("HARD", "WIRING_BROKEN", $"{wiring} 'No agent named' error(s): SendMessage/Gate-2 wiring failure."));
        }

        foreach (var group in s.Agents.GroupBy(a => a, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 3))
        {
            var type = group.Key;
            var positions = new List<int>();
            for (var i = 0; i < s.Events.Count; i++)
            {
                if (s.Events[i] is { IsAgent: true, Blocked: false } e && string.Equals(e.AgentType, type, StringComparison.OrdinalIgnoreCase))
                {
                    positions.Add(i);
                }
            }

            if (positions.Count < 3)
            {
                continue;
            }

            if (ReviewAgents.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                int reReviews = 0;
                bool userTurnSince = true, builderSince = true;
                foreach (var e in s.Events)
                {
                    if (e.Kind == 'U')
                    {
                        userTurnSince = true;
                        continue;
                    }

                    if (e is not { IsAgent: true, Blocked: false })
                    {
                        continue;
                    }

                    if (e.AgentType is { } t && (t.Equals("implementer", StringComparison.OrdinalIgnoreCase) || t.Equals("designer", StringComparison.OrdinalIgnoreCase)))
                    {
                        builderSince = true;
                    }

                    if (string.Equals(e.AgentType, type, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!userTurnSince && !builderSince)
                        {
                            reReviews++;
                        }

                        userTurnSince = false;
                        builderSince = false;
                    }
                }

                if (reReviews >= 2)
                {
                    signals.Add(new("SOFT", "REVIEW_REPEAT", $"@{type} re-spawned {reReviews}x with no new user turn or builder spawn between (total {group.Count()}x): likely a re-review loop or cold-respawn on one change-set."));
                }
            }
            else if (positions[^1] - positions[0] >= positions.Count * 2)
            {
                signals.Add(new("SOFT", "SERIAL_FANOUT", $"{group.Count()}x @{type} spawned spread across the run: likely serial, should batch into one parallel message."));
            }
        }

        if (s.EditedCode)
        {
            var hadReview = s.Agents.Any(a => a.Equals("momus", StringComparison.OrdinalIgnoreCase) || a.Equals("council", StringComparison.OrdinalIgnoreCase));
            var hadWorkflow = s.Skills.Any(k => WorkflowSkills.Contains(k, StringComparer.OrdinalIgnoreCase));
            if (!hadReview && !hadWorkflow)
            {
                signals.Add(new("SOFT", "MISSED_GATE", "Edited code but no Gate-4 (@momus/@council) and no coding-workflow skill: verify this was genuinely trivial."));
            }
        }

        if (s.Errors.Count > 15)
        {
            signals.Add(new("SOFT", "NOISY", $"{s.Errors.Count} error results: noisy run, worth a look."));
        }

        return signals;
    }

    /// <summary>Normalizes an error so the same failure groups across echoed headers (<c>Get-ErrorSignature</c>); null when too thin.</summary>
    internal static string? ErrorSignature(string text)
    {
        var t = Clear(text);
        t = HookPrefix().Replace(t, "hook error: ");
        t = EchoedHeader().Replace(t, " ");
        t = ExitCode().Replace(t, " ");
        t = Digits().Replace(t, "#");
        t = Whitespace().Replace(t, " ").Trim().ToLowerInvariant();
        return t.Length < 8 ? null : Limit(t, 60);
    }

    /// <summary>Strips ANSI escapes and collapses whitespace (<c>Clear-Text</c>).</summary>
    internal static string Clear(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : Whitespace().Replace(Ansi().Replace(text, string.Empty), " ").Trim();

    internal static string Limit(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static string JoinText(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && String(item, "type") == "text" && String(item, "text") is { } text)
            {
                parts.Add(text);
            }
        }

        return string.Join(' ', parts);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Long(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : 0;

    [GeneratedRegex(@"^\s*<", RegexOptions.CultureInvariant)]
    private static partial Regex TagStart();

    [GeneratedRegex(@"\.(cs|ts|tsx|js|jsx|py|go|java|sql|scss|css|html|razor|cshtml)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeFile();

    [GeneratedRegex(@"PreToolUse:Agent hook error|Blocked:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AgentBlocked();

    [GeneratedRegex(@"Ripgrep search timed out|\b(Grep|Glob|search)\b.*timed out|Command timed out", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchTimeout();

    [GeneratedRegex(@"Preview automation|Preview snapshot|Screenshot timed out|javascript_tool timed out|get_page_text timed out|Browser pane|mcp__Claude_Browser__|mcp__t3-code__preview_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BrowserTool();

    [GeneratedRegex(@"timed out", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimedOut();

    [GeneratedRegex(@"requires approval|doesn't want to proceed|permission for this action was denied|user rejected", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Permission();

    [GeneratedRegex(@"temporarily unavailable|auto mode is unavailable|overloaded|rate.?limit|\b429\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Infrastructure();

    [GeneratedRegex(@"No agent named|is not currently running", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Wiring();

    [GeneratedRegex(@"hook error: \[[^\]]*\]:\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HookPrefix();

    [GeneratedRegex(@"=+[^=]*=+", RegexOptions.CultureInvariant)]
    private static partial Regex EchoedHeader();

    [GeneratedRegex(@"Exit code \d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExitCode();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\x1b\[[0-9;?]*[A-Za-z]", RegexOptions.CultureInvariant)]
    private static partial Regex Ansi();
}
