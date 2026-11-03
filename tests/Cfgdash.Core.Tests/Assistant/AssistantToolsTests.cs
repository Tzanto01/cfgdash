using System.Text;
using System.Text.Json.Nodes;
using Cfgdash.Core.Assistant;
using Cfgdash.Core.Edits;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Search;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Edits;
using static Cfgdash.Core.Tests.Assistant.AssistantViewFixture;

namespace Cfgdash.Core.Tests.Assistant;

public sealed class AssistantToolsTests : IDisposable
{
    private const string Nonce = "n0nce";
    private readonly AssistantViewFixture f = new();

    public void Dispose() => f.Dispose();

    private sealed class StubReader(string content, bool truncated = false) : IFileContentReader
    {
        public Task<FileContent> ReadAsync(RegisteredFile file, CancellationToken cancellationToken) =>
            Task.FromResult(new FileContent(file.Ref.Id, file.Ref.Path, file.Ref.Kind, "text", content, truncated));
    }

    private sealed class ThrowingReader : IFileContentReader
    {
        public Task<FileContent> ReadAsync(RegisteredFile file, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("reader must not be called");
    }

    [Fact]
    public async Task GetFile_answers_null_for_dropped_company_and_unknown_ids()
    {
        var view = f.View(false);

        foreach (var id in new[] { f.Memory.Id, f.CoreOnly.Id, f.CompanyRowFile.Id, f.CompanyCell.Id, f.Orphan.Id, "0000000000000000" })
        {
            Assert.Null(await AssistantTools.GetFileAsync(view, new ThrowingReader(), id, Nonce, CancellationToken.None));
        }
    }

    [Fact]
    public async Task GetFile_wraps_a_file_in_the_session_fence()
    {
        var view = f.View(false);

        var result = await AssistantTools.GetFileAsync(view, new StubReader("hello"), f.Plain.Id, Nonce, CancellationToken.None);

        Assert.Equal(f.Plain.Id, (string?)result!["fileId"]);
        Assert.Equal($"<context-{Nonce}>\nhello\n</context-{Nonce}>", (string?)result["content"]);
        Assert.False((bool)result["truncated"]!);
    }

    [Fact]
    public async Task GetFile_cuts_content_over_64_KiB_on_a_character_boundary()
    {
        var view = f.View(false);
        var content = new string('a', AssistantTools.MaxFileBytes - 1) + "étail";

        var result = await AssistantTools.GetFileAsync(view, new StubReader(content), f.Plain.Id, Nonce, CancellationToken.None);

        var text = (string)result!["content"]!;
        var inner = text[$"<context-{Nonce}>\n".Length..^$"\n</context-{Nonce}>".Length];
        Assert.True((bool)result["truncated"]!);
        Assert.Equal(new string('a', AssistantTools.MaxFileBytes - 1), inner);
        Assert.DoesNotContain('�', inner);
        Assert.True(Encoding.UTF8.GetByteCount(inner) <= AssistantTools.MaxFileBytes);
    }

    [Fact]
    public async Task GetFile_keeps_a_multibyte_character_that_ends_exactly_at_the_limit()
    {
        var view = f.View(false);
        var content = new string('a', AssistantTools.MaxFileBytes - 2) + "é" + "more";

        var result = await AssistantTools.GetFileAsync(view, new StubReader(content), f.Plain.Id, Nonce, CancellationToken.None);

        var text = (string)result!["content"]!;
        Assert.Contains("aé\n</context-", text);
        Assert.DoesNotContain("more", text);
    }

    [Fact]
    public async Task GetFile_passes_through_the_readers_own_truncation()
    {
        var result = await AssistantTools.GetFileAsync(f.View(false), new StubReader("short", truncated: true), f.Plain.Id, Nonce, CancellationToken.None);

        Assert.True((bool)result!["truncated"]!);
    }

    [Fact]
    public void GetSnapshot_over_the_cap_drops_whole_sections_and_sets_truncated()
    {
        var filler = new string('x', 1000);
        var sections = Enumerable.Range(0, 10).Select(i => new Section($"c{i}", "v", $"S{i}",
            [new KvBlock("kv", "KV", [.. Enumerable.Range(0, 40).Select(r => Kv($"k{r}", value: filler))])])).ToList();
        var view = f.Create(Snap(sections), new FileRegistry([]), f.OutsideDir);

        var body = AssistantTools.GetSnapshot(view, null, null);

        var kept = body["sections"]!.AsArray().Select(s => (string)s!["collectorId"]!).ToArray();
        Assert.True((bool)body["truncated"]!);
        Assert.InRange(kept.Length, 1, 9);
        Assert.Equal(sections.Take(kept.Length).Select(s => s.CollectorId), kept);
        Assert.All(body["sections"]!.AsArray(), s => Assert.Equal(40, s!["blocks"]![0]!["rows"]!.AsArray().Count));
        Assert.True(Encoding.UTF8.GetByteCount(body.ToJsonString()) <= AssistantTools.MaxSnapshotBytes);
    }

    [Fact]
    public void GetSnapshot_within_the_limit_is_not_truncated_and_lists_only_referenced_files()
    {
        var body = AssistantTools.GetSnapshot(f.View(false), null, null);

        Assert.False((bool)body["truncated"]!);
        Assert.Equal(new[] { f.Plain.Id, f.Plain2.Id }.Order().ToArray(), body["files"]!.AsArray().Select(x => (string)x!["id"]!).Order().ToArray());
        Assert.DoesNotContain(body["sections"]!.AsArray(), s => (string?)s!["collectorId"] == "engram.memory");
    }

    [Fact]
    public void GetSnapshot_narrows_to_one_view()
    {
        var body = AssistantTools.GetSnapshot(f.View(false), "agents", null);

        Assert.Equal(["agents.definitions"], body["sections"]!.AsArray().Select(s => (string)s!["collectorId"]!).ToArray());
    }

    [Theory]
    [InlineData("a")]
    [InlineData("  a ")]
    [InlineData("")]
    public void Search_with_a_query_under_two_characters_answers_query_too_short(string query)
    {
        var result = AssistantTools.Search(f.View(false), new SnapshotSearch(), new ThrowingReader(), query, CancellationToken.None);

        Assert.Equal("""{"error":"query-too-short"}""", result.ToJsonString());
    }

    [Fact]
    public async Task GetFile_and_search_hide_lines_naming_a_company_path_and_keep_the_rest()
    {
        var view = f.View(false);
        var companyLine = "args = [\"" + PathUtil.ToJson(Path.Combine(f.CompanyPrefix, "mcp", "guard.js")) + "\"]";
        var reader = new StubReader($"model = \"sonnet-guard\"\n{companyLine}");

        var file = await AssistantTools.GetFileAsync(view, reader, f.Plain.Id, Nonce, CancellationToken.None);
        var hits = AssistantTools.Search(view, new SnapshotSearch(), reader, "guard", CancellationToken.None).ToJsonString();

        Assert.Equal($"<context-{Nonce}>\nmodel = \"sonnet-guard\"\n[company path hidden]\n</context-{Nonce}>", (string?)file!["content"]);
        Assert.Contains("sonnet-guard", hits);
        Assert.DoesNotContain("guard.js", hits);
    }

    [Fact]
    public void Search_never_returns_hits_from_dropped_sections_or_company_rows()
    {
        var view = f.View(false);
        var reader = new FileContentReader(new Redactor());

        var excluded = AssistantTools.Search(view, new SnapshotSearch(), reader, SecretWord, CancellationToken.None);
        var company = AssistantTools.Search(view, new SnapshotSearch(), reader, "company-source", CancellationToken.None);
        var control = AssistantTools.Search(view, new SnapshotSearch(), reader, "plain", CancellationToken.None);

        Assert.Empty(excluded["results"]!.AsArray());
        Assert.Empty(company["results"]!.AsArray());
        Assert.NotEmpty(control["results"]!.AsArray());
    }

    [Fact]
    public async Task ProposalForModel_never_contains_confirm_and_keeps_id_expiry_path_scope_and_diff()
    {
        using var edits = new EditFixture();
        var proposal = await edits.Service.ProposeForAssistantAsync(edits.Project, EditFixture.Changes(EditFixture.Set("model", "project", "opus")), "s1", false, CancellationToken.None);

        var shown = AssistantTools.ProposalForModel(proposal, f.View(false));

        Assert.DoesNotContain("confirm", shown.ToJsonString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(EditFixture.Text(proposal.Body["confirm"]), shown.ToJsonString());
        Assert.Equal(EditFixture.Text(proposal.Body["id"]), (string?)shown["proposalId"]);
        Assert.Equal(EditFixture.Text(proposal.Body["expiresAt"]), (string?)shown["expiresAt"]);
        var file = Assert.Single(shown["files"]!.AsArray())!.AsObject();
        Assert.Equal(["path", "scope", "diff"], file.Select(p => p.Key).ToArray());
        Assert.Contains("opus", (string?)file["diff"]);
    }

    [Fact]
    public void ProposalForModel_passes_denied_and_invalid_bodies_through_unchanged()
    {
        var denied = new EditResult(EditStatus.Denied, new JsonObject { ["denied"] = "model", ["reason"] = "assistant-policy" });
        var invalid = new EditResult(EditStatus.InvalidChange, new JsonObject { ["error"] = "invalid-change", ["key"] = "model", ["reason"] = "quota" });

        Assert.Equal(denied.Body.ToJsonString(), AssistantTools.ProposalForModel(denied, f.View(false)).ToJsonString());
        Assert.Equal(invalid.Body.ToJsonString(), AssistantTools.ProposalForModel(invalid, f.View(false)).ToJsonString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProposalForModel_hides_diff_lines_naming_a_company_path_outside_the_company_layer(bool inCompany)
    {
        var companyLine = "     \"Read(" + PathUtil.ToJson(Path.Combine(f.CompanyPrefix, "**")) + ")\",";
        var diff = $"@@ -1,3 +1,3 @@\n{companyLine}\n-  \"model\": \"sonnet\"\n+  \"model\": \"opus\"";
        var result = new EditResult(EditStatus.Ok, new JsonObject
        {
            ["id"] = "p1",
            ["expiresAt"] = "2026-01-02T03:14:05Z",
            ["confirm"] = "c",
            ["files"] = new JsonArray(new JsonObject { ["path"] = "/home/user/.claude/settings.json", ["scope"] = "user", ["diff"] = diff }),
        });

        var shown = (string?)AssistantTools.ProposalForModel(result, f.View(inCompany))["files"]![0]!["diff"];

        Assert.Equal(inCompany ? diff : "@@ -1,3 +1,3 @@\n [company path hidden]\n-  \"model\": \"sonnet\"\n+  \"model\": \"opus\"", shown);
    }

    [Fact]
    public void ProposalForModel_hides_a_removed_or_added_markdown_line_that_starts_with_the_company_folder()
    {
        var folder = Path.GetFileName(f.CompanyPrefix.TrimEnd('/', '\\'));
        var diff = $"@@ -1,3 +1,3 @@\n # Layout\n-{folder}/   work repos\n+{folder}/   all repos\n keep";
        var result = new EditResult(EditStatus.Ok, new JsonObject
        {
            ["id"] = "p1",
            ["expiresAt"] = "2026-01-02T03:14:05Z",
            ["confirm"] = "c",
            ["files"] = new JsonArray(new JsonObject { ["path"] = "/home/user/ws/AGENTS.md", ["scope"] = "file", ["diff"] = diff }),
        });

        var shown = (string?)AssistantTools.ProposalForModel(result, f.View(false))["files"]![0]!["diff"];

        Assert.Equal("@@ -1,3 +1,3 @@\n # Layout\n-[company path hidden]\n+[company path hidden]\n keep", shown);
    }
}
