using System.Text.Json.Nodes;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

/// <summary>One build of settings.effective + settings.highlights over managed (HKLM), project and user layers.</summary>
public sealed class SettingsSnapshotFixture : IDisposable
{
    public SettingsSnapshotFixture()
    {
        Secret = "gh" + "p_" + new string('a', 36);
        var target = Tree.Dir("proj");
        var config = Tree.Dir("config/.claude");
        Project = Tree.Write("proj/.claude/settings.json", """
            { "model": "opus", "permissions": { "allow": ["Edit"] }, "autoMode": { "allow": ["project rule"] } }
            """);
        User = Tree.Write("config/.claude/settings.json", new JsonObject
        {
            ["model"] = "sonnet",
            ["permissions"] = new JsonObject { ["allow"] = new JsonArray("Read"), ["deny"] = new JsonArray("Bash(rm *)") },
            ["autoMode"] = new JsonObject { ["environment"] = new JsonArray("trusted repo") },
            ["env"] = new JsonObject { ["SERVICE_TOKEN"] = Secret, ["OTEL_SERVICE_NAME"] = "svc" },
        }.ToJsonString());
        var hklm = """{ "allowManagedPermissionRulesOnly": true, "permissions": { "deny": ["WebFetch"] } }""";
        var layers = new SettingsLayers(new PlatformLocations(null, null, hive => hive == "HKLM" ? hklm : null));
        var builder = new SnapshotBuilder(
            TestContexts.WithConfigDir(Tree, config),
            [new SettingsEffectiveCollector(layers), new SettingsHighlightsCollector(layers)],
            new Redactor(),
            new FakeProcessRunner(),
            "0.1.0");
        Snapshot = builder.BuildAsync(target, CancellationToken.None).GetAwaiter().GetResult().Snapshot;
    }

    public TempTree Tree { get; } = new();

    public string Secret { get; }

    public string Project { get; }

    public string User { get; }

    public HarnessSnapshot Snapshot { get; }

    public T Block<T>(string collectorId, string blockId)
        where T : Block =>
        Snapshot.Sections.Single(s => s.CollectorId == collectorId).Blocks.OfType<T>().Single(b => b.Id == blockId);

    public void Dispose() => Tree.Dispose();
}

public sealed class SettingsCollectorsTests(SettingsSnapshotFixture fixture) : IClassFixture<SettingsSnapshotFixture>
{
    private static string? Text(JsonNode? cell) => cell is JsonObject obj ? obj["text"]?.GetValue<string>() : cell?.GetValue<string>();

    [Fact]
    public void Effective_rows_carry_source_labels_and_file_ids_per_contribution()
    {
        var model = fixture.Block<KvBlock>("settings.effective", "effective").Rows.Single(r => r.Key == "model");

        Assert.Equal("opus", model.Value!.GetValue<string>());
        Assert.Equal(new SourceRef("project", FileRegistry.IdFor(fixture.Project)), model.Source);
        Assert.Equal(
            [new SourceRef("project", FileRegistry.IdFor(fixture.Project)), new SourceRef("user", FileRegistry.IdFor(fixture.User))],
            model.Contributions!.Select(c => c.Source));
        Assert.Contains(fixture.Snapshot.Files, f => f.Id == FileRegistry.IdFor(fixture.User) && f.Kind == "settings");
    }

    [Fact]
    public void Merged_arrays_name_the_layer_count_as_source()
    {
        var deny = fixture.Block<KvBlock>("settings.effective", "effective").Rows.Single(r => r.Key == "permissions.deny");

        Assert.Equal("""["WebFetch","Bash(rm *)"]""", deny.Value!.ToJsonString());
        Assert.Equal("2 layers", deny.Source!.Label);
        Assert.All(deny.Contributions!, c => Assert.Equal(ContributionStatus.Merged, c.Status));
    }

    [Fact]
    public void Secret_named_setting_is_redacted_in_value_and_contributions()
    {
        var token = fixture.Block<KvBlock>("settings.effective", "effective").Rows.Single(r => r.Key == "env.SERVICE_TOKEN");

        Assert.Equal("[redacted]", token.Value!.GetValue<string>());
        Assert.True(token.Redacted);
        Assert.Contains("redacted", token.Flags!);
        Assert.All(token.Contributions!, c => Assert.Equal("[redacted]", c.Value!.GetValue<string>()));
        Assert.DoesNotContain(fixture.Secret, SnapshotJsonText());
    }

    [Fact]
    public void Layers_table_lists_every_layer_with_its_status_and_file()
    {
        var layers = fixture.Block<TableBlock>("settings.effective", "layers");

        Assert.Equal(
            [
                ("managed:hklm", @"HKLM\SOFTWARE\Policies\ClaudeCode (Settings)", "loaded"),
                ("cli", null, "not observable"),
                ("local", PathUtil.ToJson(fixture.Tree.Path("proj/.claude/settings.local.json")), "not present"),
                ("project", PathUtil.ToJson(fixture.Project), "loaded"),
                ("user", PathUtil.ToJson(fixture.User), "loaded"),
            ],
            layers.Rows.Select(r => (r.Id, Text(r.Cells["file"]), Text(r.Cells["status"]))));
        Assert.Equal(FileRegistry.IdFor(fixture.Project), layers.Rows.Single(r => r.Id == "project").FileId);
    }

    [Fact]
    public void Permission_rules_are_listed_per_source_and_non_managed_rules_are_ignored_when_managed_only()
    {
        var rules = fixture.Block<TableBlock>("settings.highlights", "permissions").Rows
            .Select(r => (r.Id, Text(r.Cells["rule"]), Text(r.Cells["source"]), Text(r.Cells["status"])));

        Assert.Equal(
            [
                ("managed:hklm:deny:0", "WebFetch", "managed (HKLM)", "applied"),
                ("project:allow:0", "Edit", "project", "ignored (managed rules only)"),
                ("user:allow:0", "Read", "user", "ignored (managed rules only)"),
                ("user:deny:0", "Bash(rm *)", "user", "ignored (managed rules only)"),
            ],
            rules);
    }

    [Fact]
    public void Auto_mode_strings_from_project_settings_are_ignored_by_scope()
    {
        var rows = fixture.Block<TableBlock>("settings.highlights", "automode").Rows
            .Select(r => (r.Id, Text(r.Cells["value"]), Text(r.Cells["status"])));

        Assert.Equal(
            [("project:allow:0", "project rule", "ignored (scope)"), ("user:environment:0", "trusted repo", "applied")],
            rows);
    }

    [Fact]
    public void Highlights_keep_model_settings_and_leave_env_to_the_env_view()
    {
        var keys = fixture.Block<KvBlock>("settings.highlights", "highlights").Rows.Select(r => r.Key).ToList();

        Assert.Contains("model", keys);
        Assert.DoesNotContain(keys, k => k.StartsWith("env", StringComparison.Ordinal) || k.StartsWith("permissions.allow", StringComparison.Ordinal));
    }

    private string SnapshotJsonText() => System.Text.Json.JsonSerializer.Serialize(fixture.Snapshot, SnapshotJson.Options);
}
