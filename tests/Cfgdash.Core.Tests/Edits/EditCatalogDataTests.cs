using System.Globalization;
using System.Text.Json.Nodes;
using Cfgdash.Core.Edits;

namespace Cfgdash.Core.Tests.Edits;

public sealed class EditCatalogDataTests
{
    private static readonly string[] Types = ["bool", "enum", "string", "number", "stringList"];
    private static readonly string[] Scopes = ["user", "project", "local", "machine"];
    private static readonly string[] NeverEditable =
    [
        "hooks", "mcpServers", "apiKeyHelper", "awsAuthRefresh", "awsCredentialExport", "otelHeadersHelper",
        "allowManagedHooksOnly", "allowManagedMcpServersOnly", "allowManagedPermissionRulesOnly", "strictPluginOnlyCustomization",
    ];

    [Fact]
    public void Every_entry_has_a_known_category_a_doc_source_a_valid_type_and_valid_scopes()
    {
        var categories = SettingsCatalogData.Categories.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var s in SettingsCatalogData.Settings)
        {
            if (!categories.Contains(s.Category))
            {
                problems.Add($"{s.Key}: unknown category {s.Category}");
            }

            if (!s.Doc.StartsWith(SettingsCatalogData.Source + "#", StringComparison.Ordinal) && s.Doc != "docs/contracts/edits-v1.md")
            {
                problems.Add($"{s.Key}: doc {s.Doc}");
            }

            if (!DateOnly.TryParseExact(s.DocRead, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                problems.Add($"{s.Key}: docRead '{s.DocRead}'");
            }

            if (!Types.Contains(s.Type))
            {
                problems.Add($"{s.Key}: type {s.Type}");
            }

            if (s.Type == "enum" && s.Options is not { Count: > 0 })
            {
                problems.Add($"{s.Key}: enum without options");
            }

            if (s.UserOnlyOptions is not null && s.UserOnlyOptions.Any(o => s.Options?.Contains(o) != true))
            {
                problems.Add($"{s.Key}: user-only option outside options");
            }

            if (s.Scopes.Count == 0 || s.Scopes.Any(x => !Scopes.Contains(x)))
            {
                problems.Add($"{s.Key}: scopes");
            }

            if (s.Scopes.Contains("machine") && s.Key != "configAutoPull")
            {
                problems.Add($"{s.Key}: machine scope");
            }
        }

        Assert.Empty(problems);
        Assert.Equal(SettingsCatalogData.Settings.Count, SettingsCatalogData.Settings.Select(s => s.Key).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Hooks_mcp_servers_helper_commands_and_managed_only_keys_are_not_in_the_catalog()
    {
        var offenders = SettingsCatalogData.Settings
            .Where(s => NeverEditable.Any(n => s.Key == n || s.Key.StartsWith(n + ".", StringComparison.Ordinal)))
            .Select(s => s.Key);

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task Every_catalog_entry_can_be_proposed_with_a_sample_value_in_each_of_its_scopes()
    {
        using var f = new EditFixture(configRepo: true);
        var problems = new List<string>();
        foreach (var s in SettingsCatalogData.Settings)
        {
            var key = s.Key switch
            {
                "env.*" => "env.SAMPLE_VAR",
                "enabledPlugins.*" => "enabledPlugins.tool@mkt",
                _ => s.Key,
            };
            foreach (var scope in s.Scopes)
            {
                JsonNode? value = s.Type switch
                {
                    "bool" => true,
                    "number" => JsonValue.Create(s.Min ?? 1),
                    "stringList" => new JsonArray("x"),
                    "enum" => s.Options!.First(o => scope == "user" || s.UserOnlyOptions?.Contains(o) != true),
                    _ => "x",
                };
                var result = await f.Propose(EditFixture.Set(key, scope, value));
                if (result.Status != EditStatus.Ok)
                {
                    problems.Add($"{key} in {scope}: {result.Body.ToJsonString()}");
                }
            }
        }

        Assert.Empty(problems);
    }
}
