using System.Text;
using System.Text.Json.Nodes;

namespace Cfgdash.Core.Tests.Edits;

public sealed class EditAuditTests : IDisposable
{
    private readonly EditFixture f = new();

    public void Dispose() => f.Dispose();

    private static string Line(string id) =>
        $$"""{"auditId":"{{id}}","at":"2026-11-01T12:00:00+00:00","listener":"ui","files":[],"changes":[]}""";

    private void SeedAudit(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(f.AuditPath)!);
        File.WriteAllText(f.AuditPath, content);
    }

    private string[] Ids(int limit) =>
        [.. f.Service.ReadAudit(limit)["entries"]!.AsArray().Select(e => EditFixture.Text(e!["auditId"]))];

    [Fact]
    public void Missing_audit_log_yields_no_entries()
    {
        Assert.Empty(f.Service.ReadAudit(50)["entries"]!.AsArray());
    }

    [Theory]
    [InlineData(2, "id-5,id-4")]
    [InlineData(0, "id-5")]
    [InlineData(-3, "id-5")]
    [InlineData(50, "id-5,id-4,id-3,id-2,id-1")]
    public void Entries_are_newest_first_and_unparseable_lines_are_skipped(int limit, string expected)
    {
        SeedAudit(string.Join("\n", Line("id-1"), "not json", Line("id-2"), string.Empty, """{"auditId":"no-files"}""", Line("id-3"), "[]", Line("id-4"), Line("id-5")) + "\n");

        Assert.Equal(expected.Split(','), Ids(limit));
    }

    [Fact]
    public void The_limit_is_capped_at_500()
    {
        SeedAudit(string.Concat(Enumerable.Range(0, 600).Select(i => Line($"n-{i}") + "\n")));

        var ids = Ids(100_000);

        Assert.Equal(500, ids.Length);
        Assert.Equal("n-599", ids[0]);
        Assert.Equal("n-100", ids[^1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task New_entries_are_appended_without_touching_earlier_bytes_and_start_on_their_own_line(bool endsWithNewline)
    {
        var existing = Line("id-1") + (endsWithNewline ? "\n" : string.Empty);
        SeedAudit(existing);
        var existingBytes = File.ReadAllBytes(f.AuditPath);
        f.WriteProject("{}\n");
        var proposal = await f.Propose(EditFixture.Set("verbose", "project", true));

        var result = await f.Apply(proposal);

        var after = File.ReadAllBytes(f.AuditPath);
        Assert.Equal(existingBytes, after[..existingBytes.Length]);
        var lines = Encoding.UTF8.GetString(after).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(EditFixture.Text(result.Body["auditId"]), EditFixture.Text(JsonNode.Parse(lines[1])!["auditId"]));
        Assert.Equal(new[] { EditFixture.Text(result.Body["auditId"]), "id-1" }, Ids(10));
    }
}
