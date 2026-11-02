using System.Text.Json.Nodes;
using Cfgdash.Core.Edits;

namespace Cfgdash.Core.Tests.Edits;

public sealed class EditRevertTests : IDisposable
{
    private static readonly string TokenLike = "sk-ant-" + new string('b', 30);

    private readonly EditFixture f = new();

    public void Dispose() => f.Dispose();

    private async Task<string> ApplyAsync(params JsonNode?[] changes)
    {
        var result = await f.Apply(await f.Propose(changes));
        Assert.Equal(EditStatus.Ok, result.Status);
        return EditFixture.Text(result.Body["auditId"]);
    }

    private Task<EditResult> Revert(string id) => f.Service.RevertAsync(id, CancellationToken.None);

    private string BackupDir(string id) => Path.Combine(f.Cache, "edits", "backups", id);

    private void RewriteAudit(Func<string, string> edit) =>
        File.WriteAllText(f.AuditPath, string.Concat(File.ReadAllLines(f.AuditPath).Select(l => edit(l) + "\n")));

    private static void AssertNotFound(EditResult result)
    {
        Assert.Equal(EditStatus.NotFound, result.Status);
        Assert.False(result.Wrote);
    }

    [Fact]
    public async Task Reverting_a_modified_file_restores_the_original_bytes_and_audits_the_revert()
    {
        var original = "{\n    \"zeta\": 1,\n    \"model\": \"sonnet\"\n}";
        f.WriteProject(original);
        var originalBytes = File.ReadAllBytes(f.ProjectSettings);
        var id = await ApplyAsync(EditFixture.Set("model", "project", "opus"));

        var proposal = await Revert(id);

        Assert.Equal(EditStatus.Ok, proposal.Status);
        Assert.NotEqual(originalBytes, File.ReadAllBytes(f.ProjectSettings));
        var result = await f.Apply(proposal);
        Assert.Equal(EditStatus.Ok, result.Status);
        Assert.Equal(originalBytes, File.ReadAllBytes(f.ProjectSettings));
        var entry = JsonNode.Parse(File.ReadAllLines(f.AuditPath)[^1])!;
        Assert.Equal(id, (string?)entry["revertOf"]);
        Assert.Equal("revert", (string?)entry["changes"]![0]!["action"]);
        Assert.Equal(EditFixture.Text(result.Body["auditId"]), (string?)entry["auditId"]);
    }

    [Fact]
    public async Task Reverting_a_file_the_apply_created_deletes_it()
    {
        Assert.False(File.Exists(f.ProjectSettings));
        var id = await ApplyAsync(EditFixture.Set("verbose", "project", true));
        Assert.True(File.Exists(f.ProjectSettings));

        var proposal = await Revert(id);

        Assert.Equal(EditStatus.Ok, proposal.Status);
        var diff = EditFixture.Text(proposal.Body["files"]![0]!["diff"]);
        Assert.Contains("\n+++ /dev/null\n", diff, StringComparison.Ordinal);
        Assert.True(File.Exists(f.ProjectSettings));
        Assert.Equal(EditStatus.Ok, (await f.Apply(proposal)).Status);
        Assert.False(File.Exists(f.ProjectSettings));
    }

    [Fact]
    public async Task A_file_changed_since_the_apply_is_stale_and_yields_no_proposal()
    {
        f.WriteProject("""{ "model": "sonnet" }""");
        var id = await ApplyAsync(EditFixture.Set("model", "project", "opus"));
        File.AppendAllText(f.ProjectSettings, "\n");
        var changed = File.ReadAllBytes(f.ProjectSettings);

        var result = await Revert(id);

        Assert.Equal(EditStatus.Stale, result.Status);
        Assert.False(result.Wrote);
        Assert.False(result.Body.ContainsKey("id"));
        Assert.Equal(changed, File.ReadAllBytes(f.ProjectSettings));
    }

    [Theory]
    [InlineData("20261101T120000Z-deadbeef")]
    [InlineData("../x")]
    [InlineData("")]
    public async Task An_unknown_or_malformed_audit_id_is_not_found(string id)
    {
        f.WriteProject("{ }");
        await ApplyAsync(EditFixture.Set("verbose", "project", true));

        AssertNotFound(await Revert(id));
    }

    [Fact]
    public async Task A_missing_backup_is_not_found()
    {
        f.WriteProject("""{ "model": "sonnet" }""");
        var id = await ApplyAsync(EditFixture.Set("model", "project", "opus"));
        Directory.Delete(BackupDir(id), true);

        AssertNotFound(await Revert(id));
    }

    [Fact]
    public async Task A_backup_that_no_longer_hashes_to_the_recorded_original_is_not_found()
    {
        f.WriteProject("""{ "model": "sonnet" }""");
        var id = await ApplyAsync(EditFixture.Set("model", "project", "opus"));
        File.WriteAllText(Directory.GetFiles(BackupDir(id)).Single(), """{ "model": "evil" }""");

        AssertNotFound(await Revert(id));
    }

    [Fact]
    public async Task An_entry_with_a_kind_such_as_a_settings_write_cannot_be_reverted()
    {
        f.WriteProject("""{ "model": "sonnet" }""");
        var id = await ApplyAsync(EditFixture.Set("model", "project", "opus"));
        RewriteAudit(l => l.Replace("\"listener\":\"ui\"", "\"listener\":\"ui\",\"kind\":\"cfgdash-settings\"", StringComparison.Ordinal));

        AssertNotFound(await Revert(id));
    }

    [Fact]
    public async Task A_tampered_audit_path_outside_the_edit_targets_is_not_found()
    {
        f.WriteProject("""{ "model": "sonnet" }""");
        var id = await ApplyAsync(EditFixture.Set("model", "project", "opus"));
        var evil = f.Tree.Write("proj/evil/settings.json", """{ "model": "opus" }""");
        var evilBytes = File.ReadAllBytes(evil);
        var recorded = EditFixture.Real(f.ProjectSettings);
        RewriteAudit(l => l.Replace(System.Text.Json.JsonSerializer.Serialize(recorded), System.Text.Json.JsonSerializer.Serialize(EditFixture.Real(evil)), StringComparison.Ordinal));
        Assert.Contains("evil", File.ReadAllText(f.AuditPath), StringComparison.Ordinal);

        AssertNotFound(await Revert(id));
        Assert.Equal(evilBytes, File.ReadAllBytes(evil));
    }

    [Fact]
    public async Task Reverting_touches_only_the_files_of_the_original_apply()
    {
        f.WriteProject("""{ "model": "sonnet" }""");
        f.WriteUser("""{ "model": "haiku" }""");
        var id = await ApplyAsync(EditFixture.Set("model", "project", "opus"));
        await ApplyAsync(EditFixture.Set("model", "user", "opus"));
        var userAfter = File.ReadAllBytes(f.UserSettings);

        var proposal = await Revert(id);
        var result = await f.Apply(proposal);

        Assert.Equal(EditStatus.Ok, result.Status);
        Assert.Single(proposal.Body["files"]!.AsArray());
        Assert.Equal(userAfter, File.ReadAllBytes(f.UserSettings));
        Assert.Equal("sonnet", (string?)JsonNode.Parse(File.ReadAllText(f.ProjectSettings))!["model"]);
    }

    [Fact]
    public async Task The_revert_diff_shows_redacted_secrets_only()
    {
        f.WriteProject($$"""{ "model": "sonnet", "env": { "SOME_VAR": "{{TokenLike}}" } }""");
        var id = await ApplyAsync(EditFixture.Set("model", "project", "opus"));

        var proposal = await Revert(id);

        Assert.Equal(EditStatus.Ok, proposal.Status);
        var diff = EditFixture.Text(proposal.Body["files"]![0]!["diff"]);
        Assert.Contains("sonnet", diff, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenLike, diff, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenLike, proposal.Body.ToJsonString(), StringComparison.Ordinal);
    }
}
