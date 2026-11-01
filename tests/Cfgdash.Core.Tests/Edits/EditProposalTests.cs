using System.Text.Json.Nodes;
using Cfgdash.Core.Edits;

namespace Cfgdash.Core.Tests.Edits;

public sealed class EditProposalTests : IDisposable
{
    private const string Secret = "synthetic-secret-value-1";
    private static readonly string TokenLike = "sk-ant-" + new string('a', 30);

    private readonly EditFixture f = new();

    public void Dispose() => f.Dispose();

    private static string Diff(EditResult proposal, int index = 0) => EditFixture.Text(proposal.Body["files"]![index]!["diff"]);

    [Fact]
    public async Task Proposal_describes_each_file_with_scope_hash_and_diff_and_writes_nothing()
    {
        f.WriteUser("{\n  \"model\": \"sonnet\"\n}\n");
        var userBytes = File.ReadAllBytes(f.UserSettings);

        var result = await f.Propose(EditFixture.Set("model", "user", "opus"), EditFixture.Set("verbose", "project", true));

        Assert.Equal(EditStatus.Ok, result.Status);
        Assert.False(result.Wrote);
        Assert.Matches("^[0-9a-f]{32}$", EditFixture.Text(result.Body["id"]));
        Assert.Equal(f.Time.Now.AddMinutes(10), DateTimeOffset.Parse(EditFixture.Text(result.Body["expiresAt"])));
        Assert.Matches("^[0-9a-f]{64}$", EditFixture.Text(result.Body["confirm"]));
        var files = result.Body["files"]!.AsArray();
        Assert.Equal(2, files.Count);
        Assert.Equal(EditFixture.Real(f.UserSettings), EditFixture.Text(files[0]!["path"]));
        Assert.Equal("user", EditFixture.Text(files[0]!["scope"]));
        Assert.Equal(EditFixture.Sha256(userBytes), EditFixture.Text(files[0]!["sha256Before"]));
        Assert.Equal(EditFixture.Real(f.ProjectSettings), EditFixture.Text(files[1]!["path"]));
        Assert.Equal("project", EditFixture.Text(files[1]!["scope"]));
        Assert.True(files[1]!.AsObject().ContainsKey("sha256Before"));
        Assert.Null(files[1]!["sha256Before"]);
        Assert.Equal(userBytes, File.ReadAllBytes(f.UserSettings));
        Assert.False(Directory.Exists(Path.GetDirectoryName(f.ProjectSettings)));
        Assert.False(Directory.Exists(f.Cache));
    }

    [Fact]
    public async Task Confirm_is_stable_for_the_same_outcome_and_changes_with_the_outcome_or_the_file()
    {
        var first = await f.Propose(EditFixture.Set("model", "project", "opus"));
        var again = await f.Propose(EditFixture.Set("model", "project", "opus"));
        var otherValue = await f.Propose(EditFixture.Set("model", "project", "haiku"));
        f.WriteProject("{\n  \"verbose\": true\n}\n");
        var otherFile = await f.Propose(EditFixture.Set("model", "project", "opus"));

        var confirm = EditFixture.Text(first.Body["confirm"]);
        Assert.Equal(confirm, EditFixture.Text(again.Body["confirm"]));
        Assert.NotEqual(EditFixture.Text(first.Body["id"]), EditFixture.Text(again.Body["id"]));
        Assert.NotEqual(confirm, EditFixture.Text(otherValue.Body["confirm"]));
        Assert.NotEqual(confirm, EditFixture.Text(otherFile.Body["confirm"]));
    }

    [Fact]
    public async Task A_new_file_diffs_against_dev_null()
    {
        var result = await f.Propose(EditFixture.Set("model", "project", "opus"));

        var name = EditFixture.Real(f.ProjectSettings).TrimStart('/');
        Assert.Equal(
            $"--- /dev/null\n+++ b/{name}\n@@ -0,0 +1,3 @@\n+{{\n+  \"model\": \"opus\"\n+}}\n",
            Diff(result));
    }

    [Fact]
    public async Task A_canonical_file_diffs_as_a_unified_hunk_without_the_reformat_marker()
    {
        f.WriteProject("{\n  \"model\": \"sonnet\",\n  \"verbose\": false\n}\n");

        var result = await f.Propose(EditFixture.Set("model", "project", "opus"));

        var name = EditFixture.Real(f.ProjectSettings).TrimStart('/');
        Assert.Equal(
            $"--- a/{name}\n+++ b/{name}\n@@ -1,4 +1,4 @@\n {{\n-  \"model\": \"sonnet\",\n+  \"model\": \"opus\",\n   \"verbose\": false\n }}\n",
            Diff(result));
    }

    [Fact]
    public async Task A_file_whose_formatting_differs_gets_the_reformat_marker_and_a_semantic_diff()
    {
        f.WriteProject("{\"model\":\"sonnet\"}");

        var result = await f.Propose(EditFixture.Set("model", "project", "opus"));

        var name = EditFixture.Real(f.ProjectSettings).TrimStart('/');
        Assert.Equal(
            $"{UnifiedDiff.ReformatMarker}\n--- a/{name}\n+++ b/{name}\n@@ -1,3 +1,3 @@\n {{\n-  \"model\": \"sonnet\"\n+  \"model\": \"opus\"\n }}\n",
            Diff(result));
    }

    [Fact]
    public async Task A_canonical_file_with_crlf_line_endings_has_no_marker_and_an_lf_only_diff()
    {
        f.WriteProject("{\r\n  \"model\": \"sonnet\"\r\n}\r\n");

        var result = await f.Propose(EditFixture.Set("model", "project", "opus"));

        var diff = Diff(result);
        Assert.False(diff.StartsWith(UnifiedDiff.ReformatMarker, StringComparison.Ordinal));
        Assert.DoesNotContain("\r", diff);
        Assert.Contains("-  \"model\": \"sonnet\"\n+  \"model\": \"opus\"\n", diff);
    }

    [Fact]
    public async Task Secrets_and_token_like_values_never_appear_in_the_proposal()
    {
        f.WriteUser(new JsonObject
        {
            ["env"] = new JsonObject { ["API_TOKEN"] = Secret, ["GREETING"] = TokenLike },
            ["model"] = "sonnet",
        }.ToJsonString());

        var result = await f.Propose(EditFixture.Set("model", "user", "opus"), EditFixture.Set("env.OTHER_VAR", "user", TokenLike));

        var diff = Diff(result);
        Assert.Contains("\"API_TOKEN\": \"[redacted]\"", diff);
        Assert.Contains("\"GREETING\": \"[redacted]\"", diff);
        Assert.Contains("\"OTHER_VAR\": \"[redacted]\"", diff);
        var body = result.Body.ToJsonString();
        Assert.DoesNotContain(Secret, body);
        Assert.DoesNotContain(TokenLike, body);
    }
}
