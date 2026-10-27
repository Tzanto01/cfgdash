using System.Text.Json.Nodes;
using Cfgdash.Core.Files;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class FileContentReaderTests(TempTree tree) : IClassFixture<TempTree>
{
    private const string R = "[redacted]";
    private readonly FileContentReader reader = new(new Redactor());

    private RegisteredFile Registered(string relative, string content)
    {
        var registry = new FileRegistry([]);
        var id = registry.Register(tree.Write(relative, content), "other")!.Id;
        registry.TryGet(id, out var file);
        return file!;
    }

    [Fact]
    public async Task Json_is_reserialized_after_key_and_value_redaction()
    {
        var token = "gh" + "p_" + new string('a', 36);
        var file = Registered("json/settings.json", "{ // comment\n \"env\": { \"API_TOKEN\": \"abc\" }, \"note\": \"use " + token + "\", \"model\": \"opus\", }");

        var content = await reader.ReadAsync(file, CancellationToken.None);

        Assert.Equal("json", content.ContentType);
        Assert.False(content.Truncated);
        var parsed = JsonNode.Parse(content.Content)!;
        Assert.Equal(R, parsed["env"]!["API_TOKEN"]!.GetValue<string>());
        Assert.Equal("use " + R, parsed["note"]!.GetValue<string>());
        Assert.Equal("opus", parsed["model"]!.GetValue<string>());
        Assert.Contains("\n", content.Content);
    }

    [Fact]
    public async Task Invalid_json_falls_back_to_redacted_text()
    {
        var token = "gh" + "p_" + new string('b', 36);
        var file = Registered("json/broken.json", "{ \"note\": \"" + token + "\"");

        var content = await reader.ReadAsync(file, CancellationToken.None);

        Assert.Equal("text", content.ContentType);
        Assert.DoesNotContain(token, content.Content);
        Assert.Contains(R, content.Content);
    }

    [Fact]
    public async Task Markdown_and_text_get_value_redaction()
    {
        var url = "https://user" + ":" + "pw12345" + "@" + "host.example";
        var md = await reader.ReadAsync(Registered("text/README.md", "# Title\n" + url), CancellationToken.None);
        var txt = await reader.ReadAsync(Registered("text/run.log", "--token " + "abc" + "def"), CancellationToken.None);

        Assert.Equal("markdown", md.ContentType);
        Assert.Equal("# Title\nhttps://user:" + R + "@host.example", md.Content);
        Assert.Equal("text", txt.ContentType);
        Assert.Equal("--token " + R, txt.Content);
    }

    [Fact]
    public async Task Content_is_capped_at_512_KiB_and_marked_truncated()
    {
        var head = "[\n" + new string(' ', FileContentReader.MaxBytes - 22);
        var file = Registered("big/large.json", head + "gh" + "p_" + new string('a', 36) + "]");

        var content = await reader.ReadAsync(file, CancellationToken.None);

        Assert.True(content.Truncated);
        Assert.Equal("text", content.ContentType);
        Assert.Equal("[\n", content.Content);
    }

    [Fact]
    public async Task Never_read_file_is_refused_even_when_handed_in_directly()
    {
        var path = tree.Write("never/.env", "SECRET=1");
        var forged = new RegisteredFile(new Snapshot.FileRef("0000000000000000", path.Replace('\\', '/'), "other", 8, 2, false), path);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ReadAsync(forged, CancellationToken.None));
    }
}
