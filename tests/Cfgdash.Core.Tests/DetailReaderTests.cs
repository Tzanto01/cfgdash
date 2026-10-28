using System.Text;
using System.Text.Json.Nodes;
using Cfgdash.Core.Files;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class DetailReaderTests(TempTree tree) : IClassFixture<TempTree>
{
    private const string R = "[redacted]";

    private sealed class ProvidingCollector(Func<string, DetailContent?> detail) : ICollector, IDetailProvider
    {
        public CollectorManifest Manifest { get; } = new("jobs", "Jobs", "1.0.0", "jobs", new Requirements());

        public Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken) => Task.FromResult(new CollectorResult([]));

        public Task<DetailContent?> GetDetailAsync(string key, CollectContext context, CancellationToken cancellationToken) => Task.FromResult(detail(key));
    }

    private Task<DetailResponse?> ReadAsync(string detailId, Func<string, DetailContent?> detail) =>
        new DetailReader(TestEnvironments.Create(tree), [new ProvidingCollector(detail)], new Redactor(), new FakeProcessRunner())
            .ReadAsync(tree.Root, detailId, CancellationToken.None);

    [Fact]
    public async Task Secrets_in_the_title_text_and_json_content_are_redacted()
    {
        var token = "gh" + "p_" + new string('a', 36);

        var text = await ReadAsync("jobs:run-1", _ => new DetailContent("Run " + token, "text", "log line " + token));
        var json = await ReadAsync("jobs:run-2", _ => new DetailContent("Run", "json", "{ \"env\": { \"API_TOKEN\": \"abc\" }, \"model\": \"opus\" }"));

        Assert.NotNull(text);
        Assert.Equal(("jobs:run-1", "Run " + R, "text", "log line " + R), (text.Id, text.Title, text.ContentType, text.Content));
        Assert.NotNull(json);
        Assert.Equal("json", json.ContentType);
        var parsed = JsonNode.Parse(json.Content)!;
        Assert.Equal(R, parsed["env"]!["API_TOKEN"]!.GetValue<string>());
        Assert.Equal("opus", parsed["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Content_over_512_KiB_is_cut_at_a_line_and_marked_truncated()
    {
        var line = new string('x', 1023) + "\n";
        var content = string.Concat(Enumerable.Repeat(line, 600));

        var detail = await ReadAsync("jobs:big", _ => new DetailContent("Big", "markdown", content));

        Assert.NotNull(detail);
        Assert.True(detail.Truncated);
        Assert.Equal(FileContentReader.MaxBytes, Encoding.UTF8.GetByteCount(detail.Content));
        Assert.EndsWith("\n", detail.Content);
    }

    [Fact]
    public async Task Unknown_collector_and_unknown_key_resolve_to_null()
    {
        var unknownCollector = await ReadAsync("other:run-1", _ => new DetailContent("t", "text", "c"));
        var unknownKey = await ReadAsync("jobs:missing", _ => null);

        Assert.Null(unknownCollector);
        Assert.Null(unknownKey);
    }
}
