using System.Text.Json.Nodes;
using Cfgdash.Core.Edits;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests.Edits;

public sealed class EditApplyTests : IDisposable
{
    private static readonly string TokenLike = "sk-ant-" + new string('a', 30);

    private readonly EditFixture f = new();

    public void Dispose() => f.Dispose();

    private static string[] Props(JsonNode? node) => [.. node!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Apply_writes_canonical_json_keeping_key_order_appending_new_keys_and_leaving_no_temp_files()
    {
        f.WriteProject("{\n    \"zeta\": 1,\n    \"model\": \"sonnet\"\n}");
        var proposal = await f.Propose(
            EditFixture.Set("model", "project", "opus"),
            EditFixture.Set("verbose", "project", true),
            EditFixture.Set("permissions.allow", "project", new JsonArray("Read")));

        var result = await f.Apply(proposal);

        Assert.Equal(EditStatus.Ok, result.Status);
        Assert.True(result.Wrote);
        Assert.True((bool?)result.Body["applied"] ?? false);
        Assert.Equal(
            "{\n  \"zeta\": 1,\n  \"model\": \"opus\",\n  \"verbose\": true,\n  \"permissions\": {\n    \"allow\": [\n      \"Read\"\n    ]\n  }\n}\n",
            File.ReadAllText(f.ProjectSettings));
        Assert.Equal(new[] { "settings.json" }, EditFixture.Names(Path.GetDirectoryName(f.ProjectSettings)!));
    }

    [Fact]
    public async Task Apply_keeps_crlf_line_endings_of_the_original()
    {
        f.WriteProject("{\r\n  \"model\": \"sonnet\"\r\n}\r\n");
        var proposal = await f.Propose(
            EditFixture.Set("model", "project", "opus"),
            EditFixture.Set("permissions.allow", "project", new JsonArray("Read")));

        await f.Apply(proposal);

        Assert.Equal(
            "{\r\n  \"model\": \"opus\",\r\n  \"permissions\": {\r\n    \"allow\": [\r\n      \"Read\"\r\n    ]\r\n  }\r\n}\r\n",
            File.ReadAllText(f.ProjectSettings));
    }

    [Theory]
    [InlineData("""{"model":"opus","permissions":{"allow":["a"]}}""", "permissions.allow", "{\n  \"model\": \"opus\"\n}\n")]
    [InlineData("""{"sandbox":{"filesystem":{"denyRead":["a"]}},"model":"m"}""", "sandbox.filesystem.denyRead", "{\n  \"model\": \"m\"\n}\n")]
    [InlineData("""{"permissions":{"allow":["a"],"deny":["b"]}}""", "permissions.allow", "{\n  \"permissions\": {\n    \"deny\": [\n      \"b\"\n    ]\n  }\n}\n")]
    [InlineData("""{"env":{"A_VAR":"1"}}""", "env.A_VAR", "{}\n")]
    public async Task Unset_removes_the_key_and_the_objects_it_leaves_empty_but_never_the_root(string existing, string key, string expected)
    {
        f.WriteProject(existing);
        var proposal = await f.Propose(EditFixture.Unset(key, "project"));

        var result = await f.Apply(proposal);

        Assert.Equal(EditStatus.Ok, result.Status);
        Assert.Equal(expected, File.ReadAllText(f.ProjectSettings));
    }

    [Fact]
    public async Task Project_scope_creates_the_claude_settings_file_when_missing()
    {
        var proposal = await f.Propose(EditFixture.Set("verbose", "project", true));

        var result = await f.Apply(proposal);

        Assert.Equal(EditStatus.Ok, result.Status);
        Assert.Equal("{\n  \"verbose\": true\n}\n", File.ReadAllText(f.ProjectSettings));
    }

    [Fact]
    public async Task Local_scope_writes_settings_local_json_at_the_git_root()
    {
        f.Tree.Dir("repo/.git");
        var sub = f.Tree.Dir("repo/sub");
        var proposal = await f.ProposeIn(sub, EditFixture.Set("verbose", "local", true));

        var result = await f.Apply(proposal);

        Assert.Equal(EditStatus.Ok, result.Status);
        Assert.Equal("{\n  \"verbose\": true\n}\n", File.ReadAllText(f.Tree.Path("repo/.claude/settings.local.json")));
        Assert.False(Directory.Exists(f.Tree.Path("repo/sub/.claude")));
    }

    [Fact]
    public async Task Scopes_that_resolve_to_the_same_file_are_merged_into_one_write()
    {
        using var g = new EditFixture(configDir: "proj/.claude");
        g.WriteUser("{\n  \"model\": \"sonnet\"\n}\n");
        var proposal = await g.Propose(EditFixture.Set("model", "user", "opus"), EditFixture.Set("verbose", "project", true));

        var result = await g.Apply(proposal);

        Assert.Single(proposal.Body["files"]!.AsArray());
        Assert.Equal(EditStatus.Ok, result.Status);
        Assert.Equal("{\n  \"model\": \"opus\",\n  \"verbose\": true\n}\n", File.ReadAllText(g.UserSettings));
        var entry = JsonNode.Parse(Assert.Single(File.ReadAllLines(g.AuditPath)))!;
        Assert.Single(entry["files"]!.AsArray());
        Assert.Equal(2, entry["changes"]!.AsArray().Count);
    }

    [Fact]
    public async Task Apply_copies_the_previous_bytes_of_every_existing_file_to_the_backup_dir()
    {
        var userRaw = "{\"model\":\"sonnet\"}";
        var projectRaw = "{\n  \"verbose\": false\n}\n";
        f.WriteUser(userRaw);
        f.WriteProject(projectRaw);
        var proposal = await f.Propose(EditFixture.Set("model", "user", "opus"), EditFixture.Set("verbose", "project", true));

        var result = await f.Apply(proposal);

        var backupDir = Path.Combine(f.Cache, "edits", "backups", EditFixture.Text(result.Body["auditId"]));
        var backups = Directory.GetFiles(backupDir).Select(p => File.ReadAllText(p)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { userRaw, projectRaw }.Order(StringComparer.Ordinal).ToArray(), backups);
    }

    [Fact]
    public async Task Apply_appends_one_audit_line_with_hashes_and_change_actions_but_no_values()
    {
        f.WriteUser("{\"verbose\":false}");
        var userBefore = File.ReadAllBytes(f.UserSettings);
        var proposal = await f.Propose(EditFixture.Unset("verbose", "user"), EditFixture.Set("env.OTHER_VAR", "project", TokenLike));

        var result = await f.Apply(proposal);

        var line = Assert.Single(File.ReadAllLines(f.AuditPath));
        var entry = JsonNode.Parse(line)!.AsObject();
        Assert.Equal(EditFixture.Text(result.Body["auditId"]), EditFixture.Text(entry["auditId"]));
        Assert.Equal("2026-11-01T12:00:00+00:00", EditFixture.Text(entry["at"]));
        Assert.Equal("ui", EditFixture.Text(entry["listener"]));
        var files = entry["files"]!.AsArray();
        Assert.Equal(2, files.Count);
        Assert.Equal(EditFixture.Real(f.UserSettings), EditFixture.Text(files[0]!["path"]));
        Assert.Equal(EditFixture.Sha256(userBefore), EditFixture.Text(files[0]!["sha256Before"]));
        Assert.Equal(EditFixture.Sha256File(f.UserSettings), EditFixture.Text(files[0]!["sha256After"]));
        Assert.Equal(EditFixture.Real(f.ProjectSettings), EditFixture.Text(files[1]!["path"]));
        Assert.True(files[1]!.AsObject().ContainsKey("sha256Before"));
        Assert.Null(files[1]!["sha256Before"]);
        Assert.Equal(EditFixture.Sha256File(f.ProjectSettings), EditFixture.Text(files[1]!["sha256After"]));
        var changes = entry["changes"]!.AsArray();
        Assert.Equal(2, changes.Count);
        Assert.Equal(new[] { "action", "key", "scope" }, Props(changes[0]));
        Assert.Equal(new[] { "action", "key", "scope" }, Props(changes[1]));
        Assert.Equal(("verbose", "user", "unset"), (EditFixture.Text(changes[0]!["key"]), EditFixture.Text(changes[0]!["scope"]), EditFixture.Text(changes[0]!["action"])));
        Assert.Equal(("env.OTHER_VAR", "project", "set"), (EditFixture.Text(changes[1]!["key"]), EditFixture.Text(changes[1]!["scope"]), EditFixture.Text(changes[1]!["action"])));
        Assert.DoesNotContain(TokenLike, line);
        Assert.Contains(TokenLike, File.ReadAllText(f.ProjectSettings));
    }

    [LinuxFact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Cache_artifacts_are_private_and_an_existing_target_keeps_its_mode()
    {
        var shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        var privateDir = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var privateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        f.WriteProject("{}\n");
        File.SetUnixFileMode(f.ProjectSettings, shared);
        var proposal = await f.Propose(EditFixture.Set("verbose", "project", true), EditFixture.Set("model", "user", "opus"));

        var result = await f.Apply(proposal);

        Assert.Equal(EditStatus.Ok, result.Status);
        Assert.Equal(shared, File.GetUnixFileMode(f.ProjectSettings));
        Assert.Equal(privateFile, File.GetUnixFileMode(f.UserSettings));
        var edits = Path.Combine(f.Cache, "edits");
        var backupDir = Path.Combine(edits, "backups", EditFixture.Text(result.Body["auditId"]));
        foreach (var dir in new[] { edits, Path.Combine(edits, "backups"), backupDir })
        {
            Assert.Equal(privateDir, File.GetUnixFileMode(dir));
        }

        var backup = Assert.Single(Directory.GetFiles(backupDir));
        Assert.Equal(privateFile, File.GetUnixFileMode(backup));
        Assert.Equal(privateFile, File.GetUnixFileMode(f.AuditPath));
    }
}
