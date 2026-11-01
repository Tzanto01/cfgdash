using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class UsageMetricsCollectorTests : IDisposable
{
    private const string UserInfoEndpoint = "http://user:" + "pw@metrics.example:8428/opentelemetry/v1/metrics";
    private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone("Test/PlusTwo", TimeSpan.FromHours(2), "Test/PlusTwo", "Test/PlusTwo");
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 10, 30, 0, TimeSpan.Zero);
    private static readonly double LocalTodayFirstHour = new DateTimeOffset(2026, 10, 14, 23, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    private static readonly double LocalYesterdayLastHour = new DateTimeOffset(2026, 10, 14, 22, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    private static readonly double TenDaysAgo = LocalTodayFirstHour - (10 * 86400);

    private readonly TempTree tree = new();

    public void Dispose() => tree.Dispose();

    private sealed class FixedTime(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    /// <summary>A synthetic Prometheus API: answers by path and by which metric the query names; records every request.</summary>
    private sealed class FakeStore(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public string[] Names { get; set; } = [];

        public string[] Labels { get; set; } = [];

        public Dictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);

        public IEnumerable<string> Queries => Requests.Select(r => Uri.UnescapeDataString(r.RequestUri!.Query));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (respond is not null)
            {
                return Task.FromResult(respond(request));
            }

            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            string data;
            if (path == "/api/v1/labels")
            {
                data = JsonSerializer.Serialize(Labels);
            }
            else if (query.Contains("count by (__name__)", StringComparison.Ordinal))
            {
                data = Vector(Names.Select(n => (new Dictionary<string, string> { ["__name__"] = n }, 1.0)));
            }
            else
            {
                var key = (path.EndsWith("query_range", StringComparison.Ordinal) ? "range:" : "instant:")
                    + (query.Contains(" or ", StringComparison.Ordinal) ? (query.Contains("project", StringComparison.Ordinal) ? "project" : "machine")
                    : query.Contains("cost", StringComparison.Ordinal) ? "cost"
                    : query.Contains("token", StringComparison.Ordinal) ? "token"
                    : query.Contains("session", StringComparison.Ordinal) ? "session" : "active");
                data = Answers.GetValueOrDefault(key) ?? """{ "resultType": "vector", "result": [] }""";
            }

            return Task.FromResult(Json($$"""{ "status": "success", "data": {{data}} }"""));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string Vector(IEnumerable<(Dictionary<string, string> Labels, double Value)> series) =>
        JsonSerializer.Serialize(new { resultType = "vector", result = series.Select(s => new { metric = s.Labels, value = new object[] { Now.ToUnixTimeSeconds(), s.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) } }) });

    private static string Matrix(params (Dictionary<string, string> Labels, (double Time, double Value)[] Samples)[] series) =>
        JsonSerializer.Serialize(new { resultType = "matrix", result = series.Select(s => new { metric = s.Labels, values = s.Samples.Select(p => new object[] { p.Time, p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }) }) });

    private static Dictionary<string, string> L(params string[] pairs) =>
        Enumerable.Range(0, pairs.Length / 2).ToDictionary(i => pairs[2 * i], i => pairs[(2 * i) + 1]);

    private async Task<CollectorResult> CollectAsync(HttpMessageHandler handler, string? settingsEnv = """{ "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT": "http://metrics.example:8428/opentelemetry/v1/metrics" }""", FakeEnvironmentReader? env = null)
    {
        var config = tree.Dir("config");
        tree.Write("config/settings.json", settingsEnv is null ? "{}" : $$"""{ "env": {{settingsEnv}} }""");
        var collector = new UsageMetricsCollector(
            new SettingsLayers(new PlatformLocations(null, null, _ => null)), env ?? new FakeEnvironmentReader(), tree.Path("cfgdash/overrides.json"), handler, new FixedTime(Now, PlusTwo));
        return await collector.CollectAsync(TestContexts.Create(TestContexts.WithConfigDir(tree, config), tree.Dir("proj")), CancellationToken.None);
    }

    private static FakeStore FullStore(string[] names)
    {
        var store = new FakeStore { Names = names, Labels = ["__name__", "model", "type", "host.name", "project", "session.id", "user.account_uuid"] };
        store.Answers["range:cost"] = Matrix(
            (L("g", "claude-opus"), [(LocalTodayFirstHour, 1.5), (LocalYesterdayLastHour, 2.0), (TenDaysAgo, 4.0)]),
            (L("g", "claude-haiku"), [(LocalTodayFirstHour, 0.25)]));
        store.Answers["range:token"] = Matrix(
            (L("g", "claude-opus", "t", "input"), [(LocalTodayFirstHour, 1000)]),
            (L("g", "claude-opus", "t", "cacheRead"), [(LocalTodayFirstHour, 5000)]),
            (L("g", "claude-opus", "t", "cacheCreation"), [(LocalTodayFirstHour, 200)]),
            (L("g", "claude-haiku", "t", "output"), [(LocalYesterdayLastHour, 300)]));
        store.Answers["range:session"] = Matrix((L(), [(LocalTodayFirstHour, 2), (LocalYesterdayLastHour, 1)]));
        store.Answers["instant:active"] = Vector([(L(), 7200)]);
        store.Answers["instant:machine"] = Vector([(L("g", "devbox", "m", "cost"), 7.75), (L("g", "devbox", "m", "tokens"), 6500), (L("g", "devbox", "m", "sessions"), 3)]);
        store.Answers["instant:project"] = Vector([(L("g", "cfgdash", "m", "cost"), 5), (L("g", "website", "m", "cost"), 2.75), (L("g", "website", "m", "sessions"), 1)]);
        return store;
    }

    private static T Block<T>(CollectorResult result, string id) where T : Block => result.Blocks.OfType<T>().Single(b => b.Id == id);

    private static JsonNode? Kv(CollectorResult result, string key) => Block<KvBlock>(result, "totals").Rows.Single(r => r.Key == key).Value;

    private static JsonNode? Cell(TableBlock table, string rowId, string column) => table.Rows.Single(r => r.Id == rowId).Cells[column];

    public static TheoryData<string[]> NamingSchemes => new()
    {
        { ["claude_code.cost.usage", "claude_code.token.usage", "claude_code.session.count", "claude_code.active_time.total", "claude_code.commit.count"] },
        { ["claude_code_cost_usage_USD_total", "claude_code_token_usage_tokens_total", "claude_code_session_count_total", "claude_code_active_time_seconds_total", "claude_code_cost_usage_by_team"] },
    };

    [Theory]
    [MemberData(nameof(NamingSchemes))]
    public async Task Discovered_metric_names_feed_totals_and_tables_with_days_in_the_local_time_zone(string[] names)
    {
        var store = FullStore(names);

        var result = await CollectAsync(store);

        Assert.Null(result.UnavailableReason);
        Assert.Equal(["totals", "by-day", "by-model", "by-machine", "by-project"], result.Blocks.Select(b => b.Id));
        Assert.Equal(1.75, Kv(result, "cost-today")!.GetValue<double>());
        Assert.Equal(3.75, Kv(result, "cost-7d")!.GetValue<double>());
        Assert.Equal(7.75, Kv(result, "cost-30d")!.GetValue<double>());
        Assert.Equal(6500, Kv(result, "tokens-30d")!.GetValue<long>());
        Assert.Equal(3, Kv(result, "sessions-30d")!.GetValue<long>());
        Assert.Equal("2.0 h", Kv(result, "active-time-30d")!.GetValue<string>());
        Assert.Equal("metrics.example:8428", Kv(result, "source")!.GetValue<string>());
        Assert.Equal("2026-09-16 to 2026-10-15 (Test/PlusTwo)", Kv(result, "range")!.GetValue<string>());

        var byDay = Block<TableBlock>(result, "by-day");
        Assert.Equal(30, byDay.Rows.Count);
        Assert.Equal(("2026-10-15", "2026-09-16"), (byDay.Rows[0].Id, byDay.Rows[^1].Id));
        Assert.Equal(1.75, Cell(byDay, "2026-10-15", "cost")!.GetValue<double>());
        Assert.Equal(2.0, Cell(byDay, "2026-10-14", "cost")!.GetValue<double>());
        Assert.Equal(4.0, Cell(byDay, "2026-10-05", "cost")!.GetValue<double>());
        Assert.Equal([1000L, 0L, 5000L, 200L, 2L], new[] { "input", "output", "cache-read", "cache-creation", "sessions" }.Select(c => Cell(byDay, "2026-10-15", c)!.GetValue<long>()));
        Assert.Equal(300, Cell(byDay, "2026-10-14", "output")!.GetValue<long>());

        var byModel = Block<TableBlock>(result, "by-model");
        Assert.Equal(["claude-opus", "claude-haiku"], byModel.Rows.Select(r => Cell(byModel, r.Id, "model")!.GetValue<string>()));
        Assert.Equal((7.5, 5000L), (Cell(byModel, "claude-opus", "cost")!.GetValue<double>(), Cell(byModel, "claude-opus", "cache-read")!.GetValue<long>()));

        var byMachine = Block<TableBlock>(result, "by-machine");
        Assert.Equal((7.75, 6500L, 3L), (Cell(byMachine, "devbox", "cost")!.GetValue<double>(), Cell(byMachine, "devbox", "tokens")!.GetValue<long>(), Cell(byMachine, "devbox", "sessions")!.GetValue<long>()));
        var byProject = Block<TableBlock>(result, "by-project");
        Assert.Equal(["cfgdash", "website"], byProject.Rows.Select(r => r.Id));

        Assert.All(names.Take(4), n => Assert.Contains(store.Queries, q => q.Contains(n.Replace(".", "\\\\.", StringComparison.Ordinal), StringComparison.Ordinal)));
        Assert.DoesNotContain(store.Queries, q => q.Contains("commit", StringComparison.Ordinal) || q.Contains("by_team", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Requests_are_bare_gets_to_the_store_origin_within_the_request_budget()
    {
        var store = FullStore(["claude_code.cost.usage", "claude_code.token.usage", "claude_code.session.count", "claude_code.active_time.total"]);

        await CollectAsync(store, $$"""
            {
              "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT": "{{UserInfoEndpoint}}",
              "OTEL_EXPORTER_OTLP_HEADERS": "x-team=blue",
              "OTEL_EXPORTER_OTLP_METRICS_HEADERS": "x-scope=team"
            }
            """);

        Assert.InRange(store.Requests.Count, 1, 10);
        Assert.All(store.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("http://metrics.example:8428", r.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Empty(r.RequestUri.UserInfo);
            Assert.StartsWith("/api/v1/", r.RequestUri.AbsolutePath, StringComparison.Ordinal);
            Assert.Empty(r.Headers);
        });
    }

    [Fact]
    public async Task Id_labels_are_never_grouped_by_and_their_blocks_are_omitted()
    {
        var store = FullStore(["claude_code.cost.usage", "claude_code.token.usage"]);
        store.Labels = ["__name__", "type", "session.id", "user.account_uuid", "user.id", "user.email", "organization.id", "user.repo"];

        var result = await CollectAsync(store);

        Assert.Equal(["totals", "by-day"], result.Blocks.Select(b => b.Id));
        Assert.DoesNotContain(store.Queries.Where(q => !q.Contains("match[]", StringComparison.Ordinal)),
            q => new[] { "session", "account", "user", "email", "organization" }.Any(id => q.Contains(id, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Label_values_are_redacted_in_row_ids_as_well_as_cells()
    {
        const string Token = "tok3n-value-123";
        var store = FullStore(["claude_code.cost.usage", "claude_code.token.usage"]);
        store.Answers["instant:project"] = Vector([(L("g", "https://user:" + Token + "@git.example/x", "m", "cost"), 1), (L("g", "", "m", "cost"), 2)]);

        var result = await CollectAsync(store);

        var byProject = Block<TableBlock>(result, "by-project");
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(byProject, SnapshotJson.Options), StringComparison.Ordinal);
        Assert.All(byProject.Rows, r => Assert.Equal(r.Id, r.Cells["project"]!.GetValue<string>()));
        Assert.Contains(byProject.Rows, r => r.Id == "(none)");
    }

    [Fact]
    public async Task Cumulative_temporality_from_settings_switches_from_summed_deltas_to_increase()
    {
        var delta = FullStore(["claude_code.cost.usage"]);
        var cumulative = FullStore(["claude_code.cost.usage"]);

        await CollectAsync(delta);
        await CollectAsync(cumulative, """{ "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT": "http://metrics.example:8428", "OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE": "cumulative" }""");

        Assert.Contains(delta.Queries, q => q.Contains("sum_over_time(", StringComparison.Ordinal));
        Assert.DoesNotContain(delta.Queries, q => q.Contains("increase(", StringComparison.Ordinal));
        Assert.Contains(cumulative.Queries, q => q.Contains("increase(", StringComparison.Ordinal));
        Assert.DoesNotContain(cumulative.Queries, q => q.Contains("sum_over_time(", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_store_without_claude_code_metrics_is_active_with_the_totals_kv_and_a_note()
    {
        var result = await CollectAsync(new FakeStore { Names = ["go_goroutines"] });

        Assert.Null(result.UnavailableReason);
        var totals = Assert.Single(result.Blocks);
        Assert.Equal(["source", "range"], ((KvBlock)totals).Rows.Select(r => r.Key));
        Assert.Contains("The metrics store holds no claude_code metrics", result.Notes!);
    }

    [Fact]
    public async Task No_endpoint_anywhere_reports_not_configured_without_any_request()
    {
        var store = new FakeStore();

        var result = await CollectAsync(store, settingsEnv: null);

        Assert.Equal("no OpenTelemetry metrics endpoint configured", result.NotPresentReason);
        Assert.Null(result.UnavailableReason);
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task Endpoint_resolution_prefers_the_override_then_settings_then_the_process_environment()
    {
        var env = new FakeEnvironmentReader();
        env.Process["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://process.example:4318";

        var fromProcess = new FakeStore();
        await CollectAsync(fromProcess, settingsEnv: null, env);
        var fromSettings = new FakeStore();
        await CollectAsync(fromSettings, """{ "OTEL_EXPORTER_OTLP_ENDPOINT": "http://settings.example:4318" }""", env);
        tree.Write("cfgdash/overrides.json", """{ "usage": { "metricsUrl": "https://override.example:9443/ignored/path" } }""");
        var fromOverride = new FakeStore();
        await CollectAsync(fromOverride, """{ "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT": "http://settings.example:8428" }""", env);

        Assert.Equal("process.example:4318", fromProcess.Requests[0].RequestUri!.Authority);
        Assert.Equal("settings.example:4318", fromSettings.Requests[0].RequestUri!.Authority);
        Assert.Equal("https://override.example:9443/api/v1/query", fromOverride.Requests[0].RequestUri!.GetLeftPart(UriPartial.Path));
    }

    public static TheoryData<HttpStatusCode, string> FailedAnswers => new()
    {
        { HttpStatusCode.Unauthorized, "metrics store needs auth (not supported)" },
        { HttpStatusCode.Forbidden, "metrics store needs auth (not supported)" },
        { HttpStatusCode.BadGateway, "metrics store at metrics.example:8428 unreachable" },
    };

    [Theory]
    [MemberData(nameof(FailedAnswers))]
    public async Task Non_success_answers_make_the_collector_unavailable_with_one_line(HttpStatusCode status, string reason)
    {
        var result = await CollectAsync(new FakeStore(_ => Json("{}", status)));

        Assert.Equal(reason, result.UnavailableReason);
        Assert.Empty(result.Blocks);
    }

    [Fact]
    public async Task A_connection_failure_is_unreachable()
    {
        var result = await CollectAsync(new FakeStore(_ => throw new HttpRequestException("connection refused")));

        Assert.Equal("metrics store at metrics.example:8428 unreachable", result.UnavailableReason);
    }

    [Fact]
    public async Task Redirects_are_followed_only_within_the_store_origin()
    {
        static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };
        var foreign = new FakeStore(_ => Redirect("http://elsewhere.example/api/v1/query"));
        var same = new FakeStore(r => r.RequestUri!.AbsolutePath == "/vm/api/v1/query"
            ? Json("""{ "status": "success", "data": { "resultType": "vector", "result": [] } }""")
            : Redirect("/vm/api/v1/query"));

        var foreignResult = await CollectAsync(foreign);
        var sameResult = await CollectAsync(same);

        Assert.Equal("metrics store at metrics.example:8428 redirects to another origin (not followed)", foreignResult.UnavailableReason);
        Assert.Single(foreign.Requests);
        Assert.Null(sameResult.UnavailableReason);
        Assert.Equal(["/api/v1/query", "/vm/api/v1/query"], same.Requests.Select(r => r.RequestUri!.AbsolutePath));
    }
}
