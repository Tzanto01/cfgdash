using System.Text.Json.Nodes;
using Cfgdash.Core.Redaction;

namespace Cfgdash.Core.Tests.Edits;

public sealed class EditSettingsReadTests : IDisposable
{
    private const string Secret = "synthetic-secret-value-1";
    private static readonly string TokenLike = "sk-ant-" + new string('a', 30);

    private readonly EditFixture f = new();

    public void Dispose() => f.Dispose();

    private static Task<JsonObject> Get(EditFixture fixture) => fixture.Service.GetSettingsAsync(fixture.Project, CancellationToken.None);

    private static string[] Strings(JsonNode? array) => [.. array!.AsArray().Select(n => EditFixture.Text(n))];

    private static JsonObject RowOf(JsonObject body, string key) =>
        EditFixture.Row(body, key) ?? throw new InvalidOperationException("No row " + key);

    [Fact]
    public async Task Values_per_scope_and_the_effective_value_follow_scope_precedence()
    {
        f.WriteUser("""{"model":"haiku","verbose":true,"permissions":{"allow":["Read"]}}""");
        f.WriteProject("""{"model":"sonnet","permissions":{"allow":["Edit"]}}""");

        var body = await Get(f);

        var model = RowOf(body, "model");
        Assert.Equal(new[] { "user", "project", "local" }, Strings(model["scopes"]));
        var values = model["values"]!.AsObject();
        Assert.Equal("haiku", EditFixture.Text(values["user"]));
        Assert.Equal("sonnet", EditFixture.Text(values["project"]));
        Assert.True(values.ContainsKey("local"));
        Assert.Null(values["local"]);
        Assert.Equal("sonnet", EditFixture.Text(model["effective"]));
        Assert.Equal("project", EditFixture.Text(model["effectiveSource"]));
        Assert.True((bool?)model["editable"] ?? false);
        Assert.False(model.ContainsKey("reason"));
        Assert.NotNull(RowOf(body, "effortLevel")["options"]);

        var verbose = RowOf(body, "verbose");
        Assert.True((bool?)verbose["effective"] ?? false);
        Assert.Equal("user", EditFixture.Text(verbose["effectiveSource"]));
        Assert.True(verbose["values"]!.AsObject().ContainsKey("project"));
        Assert.Null(verbose["values"]!["project"]);
        Assert.False(verbose.ContainsKey("options"));

        var allow = RowOf(body, "permissions.allow");
        Assert.Equal(new[] { "Edit", "Read" }, Strings(allow["effective"]).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("merged", EditFixture.Text(allow["effectiveSource"]));
    }

    [Fact]
    public async Task A_setting_that_is_set_nowhere_has_explicit_null_values_and_effective()
    {
        var body = await Get(f);

        var theme = RowOf(body, "theme");
        Assert.True(theme.ContainsKey("effective"));
        Assert.Null(theme["effective"]);
        Assert.True(theme.ContainsKey("effectiveSource"));
        Assert.Null(theme["effectiveSource"]);
        foreach (var (scope, value) in theme["values"]!.AsObject())
        {
            Assert.Contains(scope, new[] { "user", "project", "local" });
            Assert.Null(value);
        }

        Assert.Equal(3, theme["values"]!.AsObject().Count);
    }

    [Fact]
    public async Task Env_and_plugin_rows_exist_once_per_present_name()
    {
        f.WriteUser("""{"env":{"A_VAR":"1"},"enabledPlugins":{"tool@mkt":true}}""");
        f.WriteProject("""{"env":{"B_VAR":"2"}}""");

        var body = await Get(f);

        var a = RowOf(body, "env.A_VAR");
        Assert.Equal("env", EditFixture.CategoryOf(body, "env.A_VAR"));
        Assert.Equal("1", EditFixture.Text(a["values"]!["user"]));
        Assert.Null(a["values"]!["project"]);
        Assert.Equal("1", EditFixture.Text(a["effective"]));
        Assert.Equal("user", EditFixture.Text(a["effectiveSource"]));
        Assert.Equal("2", EditFixture.Text(RowOf(body, "env.B_VAR")["values"]!["project"]));
        Assert.Equal("plugins", EditFixture.CategoryOf(body, "enabledPlugins.tool@mkt"));
        Assert.True((bool?)RowOf(body, "enabledPlugins.tool@mkt")["values"]!["user"] ?? false);
        Assert.Null(EditFixture.Row(body, "env.*"));
        Assert.Null(EditFixture.Row(body, "env.C_VAR"));
    }

    [Fact]
    public async Task Keys_outside_the_catalog_are_listed_read_only_under_other()
    {
        f.WriteUser("""{"zzCustom":3}""");

        var body = await Get(f);

        var row = RowOf(body, "zzCustom");
        Assert.Equal("other", EditFixture.CategoryOf(body, "zzCustom"));
        Assert.False((bool?)row["editable"] ?? true);
        Assert.Equal("not-in-catalog", EditFixture.Text(row["reason"]));
        Assert.Equal(3, (int?)row["values"]!["user"]);
    }

    [Fact]
    public async Task Secret_and_token_like_values_are_redacted_and_secret_names_are_not_editable()
    {
        f.WriteUser(new JsonObject
        {
            ["env"] = new JsonObject
            {
                ["API_TOKEN"] = Secret,
                ["GREETING"] = TokenLike,
                ["PLAIN_VAR"] = "visible-value",
                ["LD_PRELOAD"] = "visible-lib",
            },
            ["enabledPlugins"] = new JsonObject { ["token-tool@mkt"] = true, ["ok-tool@mkt"] = true },
            ["mySecretThing"] = Secret,
            ["customNote"] = TokenLike,
            ["model"] = "sonnet",
        }.ToJsonString());

        var body = await Get(f);

        var token = RowOf(body, "env.API_TOKEN");
        Assert.Equal("[redacted]", EditFixture.Text(token["values"]!["user"]));
        Assert.Equal("[redacted]", EditFixture.Text(token["effective"]));
        Assert.False((bool?)token["editable"] ?? true);
        Assert.Equal("secret", EditFixture.Text(token["reason"]));
        var greeting = RowOf(body, "env.GREETING");
        Assert.Equal("[redacted]", EditFixture.Text(greeting["values"]!["user"]));
        Assert.Equal("[redacted]", EditFixture.Text(greeting["effective"]));
        Assert.True((bool?)greeting["editable"] ?? false);
        Assert.Equal("visible-value", EditFixture.Text(RowOf(body, "env.PLAIN_VAR")["values"]!["user"]));
        Assert.False((bool?)RowOf(body, "env.LD_PRELOAD")["editable"] ?? true);
        Assert.False((bool?)RowOf(body, "enabledPlugins.token-tool@mkt")["editable"] ?? true);
        Assert.True((bool?)RowOf(body, "enabledPlugins.ok-tool@mkt")["editable"] ?? false);
        Assert.Equal("[redacted]", EditFixture.Text(RowOf(body, "mySecretThing")["values"]!["user"]));
        Assert.Equal("[redacted]", EditFixture.Text(RowOf(body, "customNote")["values"]!["user"]));
        var text = body.ToJsonString();
        Assert.DoesNotContain(Secret, text);
        Assert.DoesNotContain(TokenLike, text);
    }

    [Fact]
    public async Task Every_row_with_a_secret_key_segment_is_not_editable()
    {
        f.WriteUser("""{"env":{"MY_SECRET":"a","DB_PASSWORD":"b","ok_name":"c"},"enabledPlugins":{"auth-tool@mkt":true}}""");
        var redactor = new Redactor();

        var body = await Get(f);

        var wronglyEditable = new List<string>();
        foreach (var category in body["categories"]!.AsArray())
        {
            foreach (var node in category!["settings"]!.AsArray())
            {
                var row = node!.AsObject();
                var key = EditFixture.Text(row["key"]);
                var prefix = new[] { "env.", "enabledPlugins." }.FirstOrDefault(p => key.StartsWith(p, StringComparison.Ordinal));
                var segments = prefix is null ? key.Split('.') : new[] { prefix[..^1], key[prefix.Length..] };
                if (segments.Any(redactor.IsSecretName) && ((bool?)row["editable"] ?? true))
                {
                    wronglyEditable.Add(key);
                }
            }
        }

        Assert.Empty(wronglyEditable);
        Assert.False((bool?)RowOf(body, "env.MY_SECRET")["editable"] ?? true);
        Assert.False((bool?)RowOf(body, "env.DB_PASSWORD")["editable"] ?? true);
        Assert.True((bool?)RowOf(body, "env.ok_name")["editable"] ?? false);
    }

    [Fact]
    public async Task Files_list_the_scope_files_with_existence_and_git_tracking_off_without_git()
    {
        f.WriteUser("{}\n");

        var body = await Get(f);

        var files = body["files"]!.AsArray();
        Assert.Equal(new[] { "user", "project", "local" }, files.Select(x => EditFixture.Text(x!["scope"])).ToArray());
        Assert.Equal(EditFixture.Real(f.UserSettings), EditFixture.Text(files[0]!["path"]));
        Assert.Equal(EditFixture.Real(f.ProjectSettings), EditFixture.Text(files[1]!["path"]));
        Assert.Equal(EditFixture.Real(f.Tree.Path("proj/.claude/settings.local.json")), EditFixture.Text(files[2]!["path"]));
        Assert.Equal(new[] { true, false, false }, files.Select(x => (bool?)x!["exists"] ?? false).ToArray());
        Assert.All(files, x => Assert.False((bool?)x!["gitTracked"] ?? true));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("?? .claude/settings.json\n", false)]
    [InlineData("!! .claude/settings.json\n", false)]
    public async Task A_file_counts_as_git_tracked_unless_git_reports_it_untracked_or_ignored(string status, bool tracked)
    {
        using var g = new EditFixture(
            processes: new StubProcessRunner(status),
            tools: new Dictionary<string, string> { ["git"] = "git" });
        g.Tree.Dir("proj/.git");
        g.WriteProject("{}\n");

        var body = await Get(g);

        var project = body["files"]!.AsArray().First(x => EditFixture.Text(x!["scope"]) == "project")!;
        Assert.Equal(tracked, (bool?)project["gitTracked"] ?? false);
    }

    [Theory]
    [InlineData("""{"configAutoPull": true}""", null, true, "machine", true)]
    [InlineData("""{"configAutoPull": true}""", "0", false, "env", true)]
    [InlineData(null, null, false, "default", null)]
    public async Task Auto_pull_row_reports_the_machine_value_and_which_source_wins(string? machineJson, string? variable, bool effective, string source, bool? machineValue)
    {
        using var g = new EditFixture(configRepo: true);
        if (machineJson is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(g.MachineJson)!);
            File.WriteAllText(g.MachineJson, machineJson);
        }

        if (variable is not null)
        {
            g.Reader.Process["CC_CONFIG_AUTOPULL"] = variable;
        }

        var body = await Get(g);

        var row = RowOf(body, "configAutoPull");
        Assert.Equal("config", EditFixture.CategoryOf(body, "configAutoPull"));
        Assert.Equal(new[] { "machine" }, Strings(row["scopes"]));
        Assert.Contains("CC_CONFIG_AUTOPULL", EditFixture.Text(row["description"]));
        Assert.True(row["values"]!.AsObject().ContainsKey("machine"));
        Assert.Equal(machineValue, (bool?)row["values"]!["machine"]);
        Assert.Equal(effective, (bool?)row["effective"] ?? false);
        Assert.Equal(source, EditFixture.Text(row["effectiveSource"]));
        Assert.True((bool?)row["editable"] ?? false);
        Assert.Contains("machine", body["files"]!.AsArray().Select(x => EditFixture.Text(x!["scope"])));
    }

    [Fact]
    public async Task Auto_pull_row_is_read_only_without_a_config_repo()
    {
        var body = await Get(f);

        var row = RowOf(body, "configAutoPull");
        Assert.False((bool?)row["editable"] ?? true);
        Assert.Null(row["effective"]);
        Assert.DoesNotContain("machine", body["files"]!.AsArray().Select(x => EditFixture.Text(x!["scope"])));
    }
}
