using System.Text.Json.Nodes;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class SessionsTranscriptsCollectorTests : IDisposable
{
    private const string SessionA = "0b0e5f7a-0000-4000-8000-00000000000a";
    private const string SessionB = "0b0e5f7a-0000-4000-8000-00000000000b";
    private const string SessionC = "0b0e5f7a-0000-4000-8000-00000000000c";
    private const string ToolOutput = "TOOL-OUTPUT-MARKER";
    private const string Prose = "ASSISTANT-PROSE-MARKER";

    private readonly TempTree tree = new();
    private readonly DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-3);
    private readonly string configDir;
    private readonly string cacheDir;
    private readonly string cwd;

    public SessionsTranscriptsCollectorTests()
    {
        configDir = tree.Dir("claude");
        cacheDir = tree.Dir("cache");
        cwd = tree.Dir("work/app");
    }

    public void Dispose() => tree.Dispose();

    [Fact]
    public async Task Sessions_roll_up_subagents_dedupe_repeated_usage_and_price_known_models_only()
    {
        WriteSession(SessionA, StandardSession(cwd, "claude-opus-5-5"));
        WriteSubagent(SessionA, "agent-a1", [Assistant(start.AddMinutes(5), "msg-sub", "claude-opus-5-5", Usage(10_000, 5_000, 0, 0), Text(Prose))]);
        WriteSession(SessionB, [Prompt(start, cwd, "hello"), Assistant(start.AddMinutes(1), "msg-b", "claude-future-9", Usage(1_000, 1_000, 0, 0), Text(Prose))]);

        var section = await BuildSectionAsync();

        var rows = Table(section, "sessions").Rows;
        Assert.Equal(2, rows.Count);
        var a = rows.Single(r => r.Id == SessionA);
        Assert.Equal($"sessions.transcripts:{SessionA}", a.DetailId);
        Assert.Equal(1, Int(a, "subagents"));
        Assert.Equal((110_000L, 55_000L, 1_000_000L, 200_000L), (Long(a, "inputTokens"), Long(a, "outputTokens"), Long(a, "cacheReadTokens"), Long(a, "cacheWriteTokens")));
        Assert.Equal(2.74, a.Cells["estCostUsd"]!.GetValue<double>());
        Assert.Equal(1, Int(a, "prompts"));
        Assert.Equal(4, Int(a, "toolCalls"));
        Assert.Contains("FAILURE_LOOP (HARD)", a.Cells["signals"]!.GetValue<string>());
        Assert.Null(rows.Single(r => r.Id == SessionB).Cells["estCostUsd"]);

        var summary = Kv(section).Rows.ToDictionary(r => r.Key, r => r.Value);
        Assert.Equal(2, summary["listed"]!.GetValue<int>());
        Assert.Equal(111_000L, summary["inputTotal"]!.GetValue<long>());
        Assert.Equal(1, summary["unpricedCount"]!.GetValue<int>());
        Assert.Equal(1, summary["withHardSignals"]!.GetValue<int>());
        Assert.Contains(Table(section, "signals").Rows, r => r.Cells["code"]!.GetValue<string>() == "FAILURE_LOOP" && r.Cells["sessions"]!.GetValue<int>() == 1);
    }

    [Fact]
    public async Task Digest_holds_prompts_tool_calls_errors_and_signals_but_no_tool_output_prose_or_secrets()
    {
        WriteSession(SessionA, StandardSession(cwd, "claude-opus-5-5"));
        await BuildSectionAsync();
        var reader = new DetailReader(Environment(), [Collector()], new Redactor(), new FakeProcessRunner());

        var detail = await reader.ReadAsync(tree.Root, $"sessions.transcripts:{SessionA}", CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal("markdown", detail.ContentType);
        Assert.Contains("Prompt: fix the build", detail.Content);
        Assert.Contains("Tool: PowerShell: dotnet build", detail.Content);
        Assert.Contains("Error: Exit code 1 build failed", detail.Content);
        Assert.Contains("HARD FAILURE_LOOP", detail.Content);
        Assert.DoesNotContain(ToolOutput, detail.Content);
        Assert.DoesNotContain(Prose, detail.Content);
        Assert.DoesNotContain(Secret, detail.Content);
    }

    [Fact]
    public async Task Detail_keys_resolve_only_to_indexed_sessions_never_to_paths()
    {
        WriteSession(SessionA, StandardSession(cwd, "claude-opus-5-5"));
        WriteSubagent(SessionA, "agent-a1", [Prompt(start, cwd, "subagent prompt")]);
        await BuildSectionAsync();
        var collector = Collector();
        var context = TestContexts.Create(Environment(), tree.Root);

        Assert.NotNull(await collector.GetDetailAsync(SessionA, context, CancellationToken.None));
        Assert.Null(await collector.GetDetailAsync(SessionC, context, CancellationToken.None));
        Assert.Null(await collector.GetDetailAsync("..", context, CancellationToken.None));
        Assert.Null(await collector.GetDetailAsync("agent-a1", context, CancellationToken.None));
    }

    [Fact]
    public async Task Index_is_reused_for_unchanged_files_holds_no_transcript_text_and_survives_corruption()
    {
        var path = WriteSession(SessionA, StandardSession(cwd, "claude-opus-5-5"));
        await BuildSectionAsync();
        var indexText = File.ReadAllText(Path.Combine(cacheDir, "sessions-index.json"));
        Assert.DoesNotContain("fix the build", indexText);
        Assert.DoesNotContain(ToolOutput, indexText);
        Assert.DoesNotContain(Prose, indexText);
        Assert.DoesNotContain(Secret, indexText);

        var written = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, new string(' ', (int)new FileInfo(path).Length));
        File.SetLastWriteTimeUtc(path, written);
        var cached = Table(await BuildSectionAsync(), "sessions").Rows.Single();
        Assert.Equal(1, Int(cached, "prompts"));

        File.WriteAllText(Path.Combine(cacheDir, "sessions-index.json"), "{ not json");
        var rebuilt = await BuildAsync(Collector());
        Assert.Equal(CollectorState.Active, Status(rebuilt).State);
        Assert.Empty(Table(Section(rebuilt), "sessions").Rows);
    }

    [Fact]
    public async Task A_build_that_scans_nothing_leaves_the_index_alone()
    {
        WriteSession(SessionA, StandardSession(cwd, "claude-opus-5-5"));
        await BuildSectionAsync();
        var indexFile = Path.Combine(cacheDir, "sessions-index.json");
        var before = File.ReadAllText(indexFile);
        var emptyConfig = TestEnvironments.Create(tree, paths: [TestEnvironments.ContextPath(PathKeys.ClaudeConfigDir, tree.Dir("empty-claude"))]);

        await BuildAsync(Collector(), emptyConfig);

        Assert.Equal(before, File.ReadAllText(indexFile));
    }

    [Fact]
    public async Task A_spent_budget_indexes_one_new_session_per_build_and_says_so()
    {
        WriteSession(SessionA, StandardSession(cwd, "claude-opus-5-5"));
        WriteSession(SessionB, StandardSession(cwd, "claude-opus-5-5"));
        WriteSession(SessionC, StandardSession(cwd, "claude-opus-5-5"));
        var collector = Collector(TimeSpan.Zero);

        var first = await BuildAsync(collector);
        var second = await BuildAsync(collector);

        Assert.Single(Table(Section(first), "sessions").Rows);
        Assert.Contains("indexed 1 of 3 sessions; refresh to continue", Status(first).Notes!);
        Assert.Equal(2, Table(Section(second), "sessions").Rows.Count);
    }

    [Fact]
    public async Task Sessions_whose_last_record_is_older_than_30_days_are_left_out()
    {
        WriteSession(SessionA, StandardSession(cwd, "claude-opus-5-5"));
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        WriteSession(SessionB, [Prompt(old, cwd, "old work")]);
        var stalePath = WriteSession(SessionC, [Prompt(old, cwd, "old but touched")]);
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow);
        File.SetLastWriteTimeUtc(SessionPath(SessionB), old.UtcDateTime);

        var rows = Table(await BuildSectionAsync(), "sessions").Rows;

        Assert.Equal([SessionA], rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Company_sessions_are_flagged_with_a_company_layer_and_left_out_without_one()
    {
        var companyDir = tree.Dir("work/company/app");
        WriteSession(SessionA, StandardSession(cwd, "claude-opus-5-5"));
        WriteSession(SessionB, [Prompt(start, companyDir, "company work")]);
        WriteSession(SessionC, [Prompt(start, cwd, "start outside"), Prompt(start.AddMinutes(1), companyDir, "then inside")]);

        var without = await BuildAsync(Collector());
        var with = await BuildAsync(Collector(), Environment([tree.Dir("work/company")]));

        Assert.Equal([SessionA], Table(Section(without), "sessions").Rows.Select(r => r.Id));
        Assert.Contains("2 sessions under a company folder left out (no company layer on this machine)", Status(without).Notes!);
        Assert.Null(await Collector().GetDetailAsync(SessionC, TestContexts.Create(Environment(), tree.Root), CancellationToken.None));
        var flagged = Table(Section(with), "sessions").Rows.Single(r => r.Id == SessionB);
        Assert.True(flagged.Cells["company"]!.GetValue<bool>());
        Assert.Contains("company", flagged.Flags!);
    }

    [Fact]
    public async Task Digest_redacts_before_cutting_and_skips_compact_summaries_and_tagged_blocks()
    {
        WriteSession(SessionA,
        [
            Prompt(start, cwd, "start"),
            ToolUse(start, "m1", "t1", "Bash", new JsonObject { ["command"] = new string('x', 120) + " " + Secret }),
            ToolUse(start, "m2", "t2", "Bash", new JsonObject { ["command"] = "export API_TOKEN=\"" + new string('q', 5000) + "\"" }),
            new JsonObject
            {
                ["type"] = "user",
                ["timestamp"] = Iso(start),
                ["isCompactSummary"] = true,
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = "This session is being continued. " + Prose },
            },
            new JsonObject
            {
                ["type"] = "user",
                ["timestamp"] = Iso(start),
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = new JsonArray(Text("look here"), Text("<ide_selection>" + ToolOutput + "</ide_selection>")) },
            },
        ]);
        var section = await BuildSectionAsync();
        var reader = new DetailReader(Environment(), [Collector()], new Redactor(), new FakeProcessRunner());

        var detail = await reader.ReadAsync(tree.Root, $"sessions.transcripts:{SessionA}", CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal(2, Int(Table(section, "sessions").Rows.Single(), "prompts"));
        Assert.Contains("Prompt: look here", detail.Content);
        Assert.DoesNotContain("gh" + "p_", detail.Content);
        Assert.DoesNotContain("qqqq", detail.Content);
        Assert.DoesNotContain(Prose, detail.Content);
        Assert.DoesNotContain(ToolOutput, detail.Content);
    }

    [Fact]
    public void Transcript_lines_survive_crlf_long_lines_lone_surrogates_and_a_truncated_or_unterminated_tail()
    {
        var bigText = new string('y', 100_000);
        var path = tree.Write("edge.jsonl",
            Line(Prompt(start, cwd, "first")) + "\r\n"
            + "\n"
            + Line(Assistant(start.AddMinutes(1), "msg-big", "claude-opus-5-5", Usage(7, 3, 0, 0), Text(bigText))) + "\n"
            + "{\"type\":\"user\",\"timestamp\":\"" + Iso(start.AddMinutes(2)) + "\",\"message\":{\"role\":\"user\",\"content\":\"broken \\ud83d\"}}\n"
            + "{\"type\":\"user\",\"timestamp\":\"" + Iso(start.AddMinutes(3)) + "\",\"message\":{\"role\":\n"
            + Line(Prompt(start.AddMinutes(4), cwd, "last")));

        var scan = SessionTranscript.Scan(path, new Redactor(), false, CancellationToken.None);

        Assert.Equal(2, scan.Prompts);
        Assert.Equal((7L, 3L), (scan.Tokens.Input, scan.Tokens.Output));
        Assert.Equal(start.AddMinutes(4), scan.Last);
    }

    [Fact]
    public void Failure_loops_count_repeated_failures_but_not_permission_denials_and_reviews_repeat_only_without_new_work()
    {
        var path = tree.Write("signals.jsonl", string.Join('\n',
        [
            Line(Prompt(start, cwd, "review this")),
            Line(ToolUse(start, "m1", "t1", "Edit", new JsonObject { ["file_path"] = "src/app.cs" })),
            Line(ToolUse(start, "m2", "t2", "Agent", new JsonObject { ["subagent_type"] = "momus", ["description"] = "review" })),
            Line(ToolUse(start, "m3", "t3", "Agent", new JsonObject { ["subagent_type"] = "momus", ["description"] = "review" })),
            Line(ToolUse(start, "m4", "t4", "Agent", new JsonObject { ["subagent_type"] = "momus", ["description"] = "review" })),
            Line(Result(start, "t5", "Permission for this action was denied", true)),
            Line(Result(start, "t6", "Permission for this action was denied", true)),
            Line(Result(start, "t7", "Permission for this action was denied", true)),
            Line(Result(start, "t7b", "Permission for this action was denied", true)),
            Line(Result(start, "t8", "PreToolUse:Bash hook error: [guard.ps1]: deny 41", true)),
            Line(Result(start, "t9", "PreToolUse:Bash hook error: [other.ps1]: deny 42", true)),
            Line(Result(start, "t10", "PreToolUse:Bash hook error: [third.ps1]: deny 43", true)),
        ]));

        var signals = SessionTranscript.Signals(SessionTranscript.Scan(path, new Redactor(), false, CancellationToken.None));

        Assert.Equal(["FAILURE_LOOP", "GUARDRAIL_REPEAT", "REVIEW_REPEAT"], signals.Select(s => s.Code).Order(StringComparer.Ordinal));
        Assert.StartsWith("Same failure repeated 3x ('pretooluse:bash hook error: deny #')", signals.Single(s => s.Code == "FAILURE_LOOP").Message);
    }

    [Fact]
    public void Timeouts_wiring_fanout_missed_gate_noise_and_token_spikes_are_detected()
    {
        var lines = new List<JsonObject>
        {
            Prompt(start, cwd, "build it"),
            Assistant(start, "m0", "claude-opus-5-5", Usage(13_000_000, 0, 0, 0), Text(Prose)),
            ToolUse(start, "m1", "t1", "Edit", new JsonObject { ["file_path"] = "web/app.ts" }),
            ToolUse(start, "m2", "a1", "Agent", new JsonObject { ["subagent_type"] = "explorer" }),
            ToolUse(start, "m3", "a2", "Agent", new JsonObject { ["subagent_type"] = "explorer" }),
            Result(start, "a2", "Blocked: explorer not allowed here", true),
            ToolUse(start, "m4", "r1", "Read", new JsonObject { ["file_path"] = "a.txt" }),
            ToolUse(start, "m5", "r2", "Read", new JsonObject { ["file_path"] = "b.txt" }),
            ToolUse(start, "m6", "r3", "Read", new JsonObject { ["file_path"] = "c.txt" }),
            ToolUse(start, "m7", "a3", "Agent", new JsonObject { ["subagent_type"] = "explorer" }),
            ToolUse(start, "m8", "r4", "Read", new JsonObject { ["file_path"] = "d.txt" }),
            ToolUse(start, "m9", "a4", "Agent", new JsonObject { ["subagent_type"] = "explorer" }),
            Result(start, "x1", "Ripgrep search timed out", true),
            Result(start, "x2", "No agent named helper", true),
        };
        lines.AddRange(Enumerable.Range(0, 5).Select(i => Result(start, $"b{i}", "mcp__Claude_Browser__screenshot timed out", true)));
        lines.AddRange(new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel" }.Select(w => Result(start, w, "unexpected failure " + w, true)));
        var path = tree.Write("signals.jsonl", string.Join('\n', lines.Select(Line)));

        var signals = SessionTranscript.Signals(SessionTranscript.Scan(path, new Redactor(), false, CancellationToken.None));

        Assert.Equal(
            ["BROWSER_TIMEOUT:HARD", "FAILURE_LOOP:HARD", "MISSED_GATE:SOFT", "NOISY:SOFT", "SEARCH_TIMEOUT:HARD", "SERIAL_FANOUT:SOFT", "TOKEN_SPIKE:HARD", "WIRING_BROKEN:HARD"],
            signals.Select(s => $"{s.Code}:{s.Severity}").Order(StringComparer.Ordinal));
        Assert.StartsWith("3x @explorer", signals.Single(s => s.Code == "SERIAL_FANOUT").Message);
    }

    private static string Secret => "gh" + "p_" + new string('a', 36);

    private SessionsTranscriptsCollector Collector(TimeSpan? budget = null) => new(cacheDir, budget);

    private MachineEnvironment Environment(IReadOnlyList<string>? companyPrefixes = null) =>
        TestEnvironments.Create(tree, paths: [TestEnvironments.ContextPath(PathKeys.ClaudeConfigDir, configDir)], companyPrefixes: companyPrefixes);

    private Task<SnapshotResult> BuildAsync(SessionsTranscriptsCollector collector, MachineEnvironment? environment = null) =>
        new SnapshotBuilder(environment ?? Environment(), [collector], new Redactor(), new FakeProcessRunner(), "test").BuildAsync(tree.Root, CancellationToken.None);

    private async Task<Section> BuildSectionAsync() => Section(await BuildAsync(Collector()));

    private static Section Section(SnapshotResult result) => result.Snapshot.Sections.Single(s => s.CollectorId == "sessions.transcripts");

    private static CollectorStatus Status(SnapshotResult result) => result.Snapshot.Collectors.Single(c => c.Id == "sessions.transcripts");

    private static TableBlock Table(Section section, string id) => section.Blocks.OfType<TableBlock>().Single(b => b.Id == id);

    private static KvBlock Kv(Section section) => section.Blocks.OfType<KvBlock>().Single();

    private static int Int(TableRow row, string key) => row.Cells[key]!.GetValue<int>();

    private static long Long(TableRow row, string key) => row.Cells[key]!.GetValue<long>();

    private string SessionPath(string id) => tree.Path($"claude/projects/work-app/{id}.jsonl");

    private string WriteSession(string id, IEnumerable<JsonObject> records) =>
        tree.Write($"claude/projects/work-app/{id}.jsonl", string.Join('\n', records.Select(Line)) + "\n");

    private void WriteSubagent(string id, string agent, IEnumerable<JsonObject> records) =>
        tree.Write($"claude/projects/work-app/{id}/subagents/{agent}.jsonl", string.Join('\n', records.Select(Line)) + "\n");

    private IEnumerable<JsonObject> StandardSession(string dir, string model) =>
    [
        new JsonObject { ["type"] = "queue-operation", ["timestamp"] = Iso(start) },
        Prompt(start, dir, "fix the build " + Secret),
        Assistant(start.AddMinutes(1), "msg-1", model, Usage(100_000, 50_000, 1_000_000, 200_000), Text(Prose)),
        Assistant(start.AddMinutes(1), "msg-1", model, Usage(100_000, 50_000, 1_000_000, 200_000), ToolBlock("t1", "PowerShell", new JsonObject { ["command"] = "dotnet build" })),
        Result(start.AddMinutes(2), "t1", "Exit code 1 build failed at line 12", true),
        ToolUse(start.AddMinutes(3), "msg-2", "t2", "PowerShell", new JsonObject { ["command"] = "dotnet build" }),
        Result(start.AddMinutes(3), "t2", "Exit code 1 build failed at line 13", true),
        ToolUse(start.AddMinutes(4), "msg-3", "t3", "PowerShell", new JsonObject { ["command"] = "dotnet build" }),
        Result(start.AddMinutes(4), "t3", "Exit code 1 build failed at line 14", true),
        ToolUse(start.AddMinutes(5), "msg-4", "t4", "Read", new JsonObject { ["file_path"] = "src/app.txt" }),
        Result(start.AddMinutes(5), "t4", ToolOutput + " " + Secret, false),
    ];

    private static string Line(JsonObject record) => record.ToJsonString();

    private static string Iso(DateTimeOffset time) => time.ToString("O");

    private static JsonObject Prompt(DateTimeOffset time, string dir, string text) => new()
    {
        ["type"] = "user",
        ["timestamp"] = Iso(time),
        ["cwd"] = dir,
        ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
    };

    private static JsonObject Usage(long input, long output, long cacheRead, long cacheWrite) => new()
    {
        ["input_tokens"] = input,
        ["output_tokens"] = output,
        ["cache_read_input_tokens"] = cacheRead,
        ["cache_creation_input_tokens"] = cacheWrite,
    };

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject ToolBlock(string id, string name, JsonObject input) => new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input };

    private static JsonObject Assistant(DateTimeOffset time, string messageId, string model, JsonObject usage, JsonObject block) => new()
    {
        ["type"] = "assistant",
        ["timestamp"] = Iso(time),
        ["message"] = new JsonObject { ["id"] = messageId, ["model"] = model, ["role"] = "assistant", ["content"] = new JsonArray(block), ["usage"] = usage },
    };

    private static JsonObject ToolUse(DateTimeOffset time, string messageId, string toolId, string name, JsonObject input) =>
        Assistant(time, messageId, "claude-opus-5-5", Usage(0, 0, 0, 0), ToolBlock(toolId, name, input));

    private static JsonObject Result(DateTimeOffset time, string toolId, string text, bool isError) => new()
    {
        ["type"] = "user",
        ["timestamp"] = Iso(time),
        ["toolUseResult"] = new JsonObject { ["stdout"] = text },
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = toolId, ["content"] = text, ["is_error"] = isError }),
        },
    };
}
