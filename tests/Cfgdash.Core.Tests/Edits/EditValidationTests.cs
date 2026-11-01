using System.Text.Json.Nodes;
using Cfgdash.Core.Edits;

namespace Cfgdash.Core.Tests.Edits;

public sealed class EditValidationTests : IDisposable
{
    private readonly EditFixture f = new();

    public void Dispose() => f.Dispose();

    [Theory]
    [InlineData("""{"key":"model","scope":"user","value":"x"}""", null, "bad-request")]
    [InlineData("null", null, "bad-request")]
    [InlineData("[]", null, "bad-request")]
    [InlineData("""["model"]""", null, "bad-request")]
    [InlineData("""[{"key":5,"scope":"user","value":"x"}]""", null, "bad-request")]
    [InlineData("""[{"key":"model","scope":"user","value":"x","unset":true}]""", "model", "bad-request")]
    [InlineData("""[{"key":"model","scope":"user"}]""", "model", "bad-request")]
    [InlineData("""[{"key":"model","scope":"user","unset":false}]""", "model", "bad-request")]
    [InlineData("""[{"key":"model","scope":"user","value":"x","extra":1}]""", "model", "bad-request")]
    [InlineData("""[{"key":"nope","scope":"user","value":"x"}]""", "nope", "unknown-key")]
    [InlineData("""[{"key":"env.9LIVES","scope":"user","value":"x"}]""", "env.9LIVES", "unknown-key")]
    [InlineData("""[{"key":"env.MY_TOKEN","scope":"user","value":"x"}]""", "env.MY_TOKEN", "not-editable")]
    [InlineData("""[{"key":"env.LD_PRELOAD","scope":"user","value":"x"}]""", "env.LD_PRELOAD", "not-editable")]
    [InlineData("""[{"key":"env.PATH","scope":"user","value":"x"}]""", "env.PATH", "not-editable")]
    [InlineData("""[{"key":"enabledPlugins.token-tool@mkt","scope":"user","value":true}]""", "enabledPlugins.token-tool@mkt", "not-editable")]
    [InlineData("""[{"key":"model","scope":"machine","value":"x"}]""", "model", "bad-scope")]
    [InlineData("""[{"key":"model","scope":"bogus","value":"x"}]""", "model", "bad-scope")]
    [InlineData("""[{"key":"askUserQuestionTimeout","scope":"project","value":"5m"}]""", "askUserQuestionTimeout", "bad-scope")]
    [InlineData("""[{"key":"configAutoPull","scope":"user","value":true}]""", "configAutoPull", "bad-scope")]
    [InlineData("""[{"key":"model","scope":"user","value":5}]""", "model", "bad-type")]
    [InlineData("""[{"key":"model","scope":"user","value":null}]""", "model", "bad-type")]
    [InlineData("""[{"key":"verbose","scope":"user","value":"yes"}]""", "verbose", "bad-type")]
    [InlineData("""[{"key":"cleanupPeriodDays","scope":"user","value":"30"}]""", "cleanupPeriodDays", "bad-type")]
    [InlineData("""[{"key":"permissions.allow","scope":"user","value":"Read"}]""", "permissions.allow", "bad-type")]
    [InlineData("""[{"key":"permissions.allow","scope":"user","value":["Read",1]}]""", "permissions.allow", "bad-type")]
    [InlineData("""[{"key":"effortLevel","scope":"user","value":true}]""", "effortLevel", "bad-type")]
    [InlineData("""[{"key":"effortLevel","scope":"user","value":"ultra"}]""", "effortLevel", "bad-option")]
    [InlineData("""[{"key":"model","scope":"user","value":"a"},{"key":"model","scope":"user","unset":true}]""", "model", "duplicate")]
    public async Task Invalid_changes_are_rejected_with_the_key_and_reason_and_write_nothing(string changesJson, string? key, string reason)
    {
        var result = await f.Service.ProposeAsync(f.Project, JsonNode.Parse(changesJson), CancellationToken.None);

        EditFixture.AssertInvalid(result, key, reason);
        Assert.False(Directory.Exists(f.Cache));
        Assert.False(Directory.Exists(Path.GetDirectoryName(f.ProjectSettings)));
    }

    [Theory]
    [InlineData("hooks")]
    [InlineData("hooks.Stop")]
    [InlineData("mcpServers")]
    [InlineData("apiKeyHelper")]
    [InlineData("awsAuthRefresh")]
    [InlineData("awsCredentialExport")]
    [InlineData("otelHeadersHelper")]
    [InlineData("allowManagedHooksOnly")]
    [InlineData("strictPluginOnlyCustomization")]
    public async Task Keys_that_are_never_editable_are_unknown_keys(string key)
    {
        var result = await f.Propose(EditFixture.Set(key, "user", "x"));

        EditFixture.AssertInvalid(result, key, "unknown-key");
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(101, false)]
    public async Task The_change_list_is_capped_at_100_items(int count, bool accepted)
    {
        var changes = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            changes.Add(EditFixture.Set($"env.VAR_{i}", "user", "1"));
        }

        var result = await f.Service.ProposeAsync(f.Project, changes, CancellationToken.None);

        if (accepted)
        {
            Assert.Equal(EditStatus.Ok, result.Status);
        }
        else
        {
            EditFixture.AssertInvalid(result, null, "bad-request");
        }
    }

    [Theory]
    [InlineData("verbose", "true")]
    [InlineData("cleanupPeriodDays", "30")]
    [InlineData("cleanupPeriodDays", "2.5")]
    [InlineData("model", "\"opus\"")]
    [InlineData("effortLevel", "\"high\"")]
    [InlineData("permissions.allow", """["Read","Edit"]""")]
    [InlineData("permissions.allow", "[]")]
    [InlineData("env.GREETING", "\"hello\"")]
    [InlineData("enabledPlugins.tool@mkt", "false")]
    public async Task Values_matching_the_catalog_type_are_accepted(string key, string valueJson)
    {
        var result = await f.Propose(EditFixture.Set(key, "user", JsonNode.Parse(valueJson)));

        Assert.Equal(EditStatus.Ok, result.Status);
    }

    [Theory]
    [InlineData("user", "bypassPermissions", true)]
    [InlineData("user", "auto", true)]
    [InlineData("project", "bypassPermissions", false)]
    [InlineData("local", "auto", false)]
    [InlineData("project", "plan", true)]
    [InlineData("local", "acceptEdits", true)]
    public async Task Permission_modes_that_project_settings_may_not_grant_are_accepted_in_user_scope_only(string scope, string mode, bool accepted)
    {
        var result = await f.Propose(EditFixture.Set("permissions.defaultMode", scope, mode));

        if (accepted)
        {
            Assert.Equal(EditStatus.Ok, result.Status);
        }
        else
        {
            EditFixture.AssertInvalid(result, "permissions.defaultMode", "bad-option");
        }
    }

    [Fact]
    public async Task The_same_key_may_be_changed_in_different_scopes()
    {
        var result = await f.Propose(EditFixture.Set("model", "user", "opus"), EditFixture.Set("model", "project", "sonnet"));

        Assert.Equal(EditStatus.Ok, result.Status);
        Assert.Equal(2, result.Body["files"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("model", "user", "\"x\"")]
    [InlineData("configAutoPull", "machine", "true")]
    public async Task Scopes_without_a_location_are_unavailable(string key, string scope, string valueJson)
    {
        using var g = new EditFixture(userScope: false, configRepo: false);

        var result = await g.Propose(EditFixture.Set(key, scope, JsonNode.Parse(valueJson)));

        EditFixture.AssertInvalid(result, key, "scope-unavailable");
        Assert.False(Directory.Exists(g.Cache));
    }

    [Theory]
    [InlineData("{ // note\n  \"a\": 1\n}")]
    [InlineData("{ \"a\": /* note */ 1 }")]
    [InlineData("{\"a\":1,}")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("[1]")]
    [InlineData("\"text\"")]
    public async Task A_target_file_that_is_not_a_strict_json_object_is_not_edited(string content)
    {
        var path = f.WriteProject(content);

        var result = await f.Propose(EditFixture.Set("model", "project", "opus"));

        EditFixture.AssertInvalid(result, "model", "parse-error");
        Assert.Equal(content, File.ReadAllText(path));
        Assert.False(Directory.Exists(f.Cache));
    }

    [Fact]
    public async Task A_target_file_over_4_MiB_is_not_edited()
    {
        var content = "{\"a\":\"" + new string('x', 4 * 1024 * 1024) + "\"}";
        var path = f.WriteProject(content);

        var result = await f.Propose(EditFixture.Set("model", "project", "opus"));

        EditFixture.AssertInvalid(result, "model", "parse-error");
        Assert.Equal(content.Length, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_key_path_through_a_non_object_value_is_refused(bool unset)
    {
        var path = f.WriteProject("""{"permissions": "x"}""");
        var change = unset
            ? EditFixture.Unset("permissions.allow", "project")
            : EditFixture.Set("permissions.allow", "project", new JsonArray("Read"));

        var result = await f.Propose(change);

        EditFixture.AssertInvalid(result, "permissions.allow", "not-an-object");
        Assert.Equal("""{"permissions": "x"}""", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("{\n  \"model\": \"opus\"\n}\n", """{"key":"model","scope":"project","value":"opus"}""")]
    [InlineData(null, """{"key":"verbose","scope":"project","unset":true}""")]
    [InlineData("{\n  \"model\": \"opus\"\n}\n", """{"key":"verbose","scope":"project","unset":true}""")]
    public async Task Changes_that_leave_every_file_as_it_is_report_no_change(string? existing, string changeJson)
    {
        if (existing is not null)
        {
            f.WriteProject(existing);
        }

        var result = await f.Propose(JsonNode.Parse(changeJson));

        EditFixture.AssertInvalid(result, null, "no-change");
        Assert.Equal(existing is not null, File.Exists(f.ProjectSettings));
        Assert.False(Directory.Exists(f.Cache));
    }

    [Fact]
    public async Task One_invalid_change_rejects_the_whole_proposal_and_nothing_is_written()
    {
        var path = f.WriteProject("{\n  \"model\": \"sonnet\"\n}\n");
        var before = File.ReadAllBytes(path);

        var result = await f.Propose(
            EditFixture.Set("model", "project", "opus"),
            EditFixture.Set("verbose", "user", true),
            EditFixture.Set("effortLevel", "project", "ultra"));

        EditFixture.AssertInvalid(result, "effortLevel", "bad-option");
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(f.UserSettings));
        Assert.False(Directory.Exists(f.Cache));
    }
}
