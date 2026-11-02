using System.Text.Json.Nodes;
using Cfgdash.Core.Edits;

namespace Cfgdash.Core.Tests.Edits;

public sealed class EditAssistantPolicyTests : IDisposable
{
    private readonly EditFixture f = new();

    public void Dispose() => f.Dispose();

    private Task<EditResult> Assistant(JsonNode change) =>
        f.Service.ProposeForAssistantAsync(f.Project, EditFixture.Changes(change), "session-1", CancellationToken.None);

    private static void AssertDenied(EditResult result, string key)
    {
        Assert.Equal(EditStatus.Denied, result.Status);
        Assert.False(result.Wrote);
        Assert.Equal(2, result.Body.Count);
        Assert.Equal(key, (string?)result.Body["denied"]);
        Assert.Equal("assistant-policy", (string?)result.Body["reason"]);
    }

    public static IEnumerable<object[]> TightenOnlyKeys() =>
        SettingsCatalogData.AssistantPolicies.Where(p => p.Value.Kind == AssistantKind.TightenOnly).Select(p => new object[] { p.Key });

    [Fact]
    public async Task A_free_key_is_accepted_in_project_scope()
    {
        var result = await Assistant(EditFixture.Set("model", "project", "opus"));

        Assert.Equal(EditStatus.Ok, result.Status);
        Assert.False(File.Exists(f.ProjectSettings));
    }

    [Fact]
    public async Task A_free_key_that_is_project_only_is_denied_in_user_scope()
    {
        AssertDenied(await Assistant(EditFixture.Set("model", "user", "opus")), "model");
    }

    [Fact]
    public async Task A_free_key_with_user_scope_allowed_is_accepted_in_user_scope()
    {
        var result = await Assistant(EditFixture.Set("theme", "user", "dark"));

        Assert.Equal(EditStatus.Ok, result.Status);
    }

    [Theory]
    [InlineData("sandbox.enabled", true, true)]
    [InlineData("sandbox.enabled", false, false)]
    [InlineData("sandbox.allowUnsandboxedCommands", false, true)]
    [InlineData("sandbox.allowUnsandboxedCommands", true, false)]
    public async Task A_boolean_tighten_only_key_accepts_only_the_safe_value(string key, bool value, bool accepted)
    {
        var result = await Assistant(EditFixture.Set(key, "project", value));

        if (accepted)
        {
            Assert.Equal(EditStatus.Ok, result.Status);
        }
        else
        {
            AssertDenied(result, key);
        }
    }

    [Fact]
    public async Task A_disable_key_accepts_disable()
    {
        var result = await Assistant(EditFixture.Set("disableAutoMode", "project", "disable"));

        Assert.Equal(EditStatus.Ok, result.Status);
    }

    [Fact]
    public async Task A_subset_list_accepts_removing_an_item_and_denies_adding_one()
    {
        f.WriteProject("""{"permissions":{"allow":["Read","Grep"]}}""");

        var removed = await Assistant(EditFixture.Set("permissions.allow", "project", new JsonArray("Read")));
        var added = await Assistant(EditFixture.Set("permissions.allow", "project", new JsonArray("Read", "Grep", "Bash")));

        Assert.Equal(EditStatus.Ok, removed.Status);
        AssertDenied(added, "permissions.allow");
    }

    [Fact]
    public async Task A_subset_list_cannot_be_filled_when_the_file_does_not_exist()
    {
        AssertDenied(await Assistant(EditFixture.Set("permissions.allow", "project", new JsonArray("Bash"))), "permissions.allow");
    }

    [Fact]
    public async Task A_superset_list_accepts_adding_an_item_and_denies_removing_one()
    {
        f.WriteProject("""{"permissions":{"deny":["Read","Grep"]}}""");

        var added = await Assistant(EditFixture.Set("permissions.deny", "project", new JsonArray("Read", "Grep", "Bash")));
        var removed = await Assistant(EditFixture.Set("permissions.deny", "project", new JsonArray("Read")));

        Assert.Equal(EditStatus.Ok, added.Status);
        AssertDenied(removed, "permissions.deny");
    }

    [Theory]
    [MemberData(nameof(TightenOnlyKeys))]
    public async Task Every_tighten_only_key_denies_unset(string key)
    {
        f.WriteUser("{}");

        var result = await f.Service.ProposeForAssistantAsync(f.Project, EditFixture.Changes(EditFixture.Unset(key, "user")), "session-1", CancellationToken.None);

        AssertDenied(result, key);
    }

    [Theory]
    [InlineData("advisorModel", "project", "opus")]
    [InlineData("env.EXAMPLE_VAR", "project", "1")]
    [InlineData("permissions.defaultMode", "project", "plan")]
    [InlineData("skipDangerousModePermissionPrompt", "user", true)]
    [InlineData("disableAllHooks", "project", true)]
    [InlineData("enableAllProjectMcpServers", "project", true)]
    [InlineData("language", "project", "german")]
    [InlineData("forceLoginMethod", "project", "console")]
    [InlineData("forceLoginGatewayUrl", "user", "https://gateway.example")]
    [InlineData("cleanupPeriodDays", "project", 30)]
    [InlineData("enabledPlugins.p@m", "project", true)]
    public async Task Unlisted_and_always_deny_keys_answer_denied(string key, string scope, object value)
    {
        JsonNode node = value switch { bool b => b, int i => JsonNode.Parse(i.ToString())!, _ => (string)value };

        AssertDenied(await Assistant(EditFixture.Set(key, scope, node)), key);
    }

    [Theory]
    [InlineData("claudeMdExcludes", "project")]
    [InlineData("autoMode.allow", "user")]
    public async Task Always_deny_list_keys_answer_denied(string key, string scope)
    {
        AssertDenied(await Assistant(EditFixture.Set(key, scope, new JsonArray("x"))), key);
    }

    [Fact]
    public void Every_policy_key_exists_in_the_catalog_and_every_other_catalog_key_resolves_to_deny()
    {
        var catalogKeys = SettingsCatalogData.Settings.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        var missing = SettingsCatalogData.AssistantPolicies.Keys.Where(k => !catalogKeys.Contains(k)).ToList();
        Assert.Empty(missing);
        foreach (var setting in SettingsCatalogData.Settings.Where(s => !SettingsCatalogData.AssistantPolicies.ContainsKey(s.Key)))
        {
            Assert.Null(setting.Assistant);
            Assert.True(EditCatalog.AssistantDenied(setting, "project", null, JsonValue.Create(true), false), setting.Key);
        }

        Assert.All(SettingsCatalogData.Settings.Where(s => SettingsCatalogData.AssistantPolicies.ContainsKey(s.Key)),
            s => Assert.Equal(SettingsCatalogData.AssistantPolicies[s.Key], s.Assistant));
    }
}
