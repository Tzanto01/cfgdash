using System.Text.Json.Nodes;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

/// <summary>Managed (file), local, project and user layers that overlap on every merge rule.</summary>
public sealed class SettingsMergeFixture : IDisposable
{
    public SettingsMergeFixture()
    {
        var target = Tree.Dir("proj");
        var config = Tree.Dir("config/.claude");
        Managed = Tree.Write("managed/managed-settings.json", """{ "availableModels": ["haiku"] }""");
        Local = Tree.Write("proj/.claude/settings.local.json", """
            { "model": "opus", "permissions": { "allow": ["Bash(npm test *)", "Read(./docs/**)"] }, "env": { "A": "local" },
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "a" } ] } ] } }
            """);
        Project = Tree.Write("proj/.claude/settings.json", """
            { "model": "sonnet", "permissions": { "allow": ["Read(./docs/**)", "Edit"] }, "fallbackModel": { "model": "sonnet" } }
            """);
        User = Tree.Write("config/.claude/settings.json", """
            { "model": "haiku", "env": { "A": "user", "B": "user" }, "availableModels": ["opus", "sonnet"],
              "fallbackModel": { "model": "haiku", "retries": 2 },
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "a" } ] }, { "hooks": [ { "type": "command", "command": "b" } ] } ] } }
            """);
        var layers = new SettingsLayers(new PlatformLocations(Tree.Path("managed"), null, _ => null));
        Stack = layers.LoadAsync(TestContexts.Create(TestContexts.WithConfigDir(Tree, config), target), CancellationToken.None).GetAwaiter().GetResult();
    }

    public TempTree Tree { get; } = new();

    public string Managed { get; }

    public string Local { get; }

    public string Project { get; }

    public string User { get; }

    internal SettingsStack Stack { get; }

    public void Dispose() => Tree.Dispose();
}

public sealed class SettingsLayersTests(SettingsMergeFixture fixture) : IClassFixture<SettingsMergeFixture>
{
    private static async Task<SettingsStack> LoadAsync(
        TempTree tree,
        string target,
        string configDir,
        string? managedDir = null,
        string? ignoredDir = null,
        Func<string, string?>? registry = null)
    {
        var layers = new SettingsLayers(new PlatformLocations(managedDir, ignoredDir, registry ?? (_ => null)));
        return await layers.LoadAsync(TestContexts.Create(TestContexts.WithConfigDir(tree, configDir), target), CancellationToken.None);
    }

    private static (string Value, string Label, string? FileId, ContributionStatus Status) View(SettingContribution c) =>
        (c.Value!.ToJsonString(), c.Layer.Label, c.Layer.FileId, c.Status);

    private EffectiveSetting Get(string key) => fixture.Stack.Get(key) ?? throw new InvalidOperationException("missing " + key);

    [Fact]
    public void Scalar_highest_layer_wins_and_lower_layers_are_overridden()
    {
        var model = Get("model");

        Assert.Equal("opus", model.Value!.GetValue<string>());
        Assert.Equal(
            [
                ("\"opus\"", "project (local)", FileRegistry.IdFor(fixture.Local), ContributionStatus.Applied),
                ("\"sonnet\"", "project", FileRegistry.IdFor(fixture.Project), ContributionStatus.Overridden),
                ("\"haiku\"", "user", FileRegistry.IdFor(fixture.User), ContributionStatus.Overridden),
            ],
            model.Contributions.Select(View));
        Assert.Contains("overridden", model.Flags);
    }

    [Fact]
    public void Arrays_concatenate_highest_first_without_duplicates_and_keep_each_layer_as_merged()
    {
        var allow = Get("permissions.allow");

        Assert.Equal("""["Bash(npm test *)","Read(./docs/**)","Edit"]""", allow.Value!.ToJsonString());
        Assert.Equal(
            [("project (local)", ContributionStatus.Merged), ("project", ContributionStatus.Merged)],
            allow.Contributions.Select(c => (c.Layer.Label, c.Status)));
        Assert.Contains("trust-gated", allow.Flags);
    }

    [Fact]
    public void Hook_event_arrays_combine_across_layers_and_identical_groups_appear_once()
    {
        var stop = (JsonArray)Get("hooks.Stop").Value!;

        Assert.Equal(["a", "b"], stop.Select(g => g!["hooks"]![0]!["command"]!.GetValue<string>()));
    }

    [Fact]
    public void FallbackModel_is_replaced_whole_by_the_highest_layer()
    {
        var fallback = Get("fallbackModel");

        Assert.Equal("""{"model":"sonnet"}""", fallback.Value!.ToJsonString());
        Assert.Equal([ContributionStatus.Applied, ContributionStatus.Overridden], fallback.Contributions.Select(c => c.Status));
        Assert.DoesNotContain(fixture.Stack.Settings, s => s.Key.StartsWith("fallbackModel.", StringComparison.Ordinal));
    }

    [Fact]
    public void Managed_availableModels_is_used_as_is()
    {
        var models = Get("availableModels");

        Assert.Equal("""["haiku"]""", models.Value!.ToJsonString());
        Assert.Equal(
            [("managed (file)", ContributionStatus.Applied), ("user", ContributionStatus.Overridden)],
            models.Contributions.Select(c => (c.Layer.Label, c.Status)));
    }

    [Fact]
    public void Env_follows_precedence_per_variable_and_project_values_are_trust_gated()
    {
        Assert.Equal(("local", "project (local)"), (Get("env.A").Value!.GetValue<string>(), Get("env.A").Contributions[0].Layer.Label));
        Assert.Equal("user", Get("env.B").Value!.GetValue<string>());
        Assert.Contains("trust-gated", Get("env.A").Flags);
        Assert.DoesNotContain("trust-gated", Get("env.B").Flags);
        Assert.Contains(fixture.Stack.Notes, n => n.Contains("trust", StringComparison.Ordinal));
    }

    [Fact]
    public void Layers_are_listed_highest_first_with_command_line_not_observable()
    {
        Assert.Equal(
            ["managed:file:managed-settings.json", "cli", "local", "project", "user"],
            fixture.Stack.Layers.Select(l => l.Id));
        Assert.Equal(LayerStatus.NotObservable, fixture.Stack.Layers[1].Status);
        Assert.Contains("Command-line flags are not observable", fixture.Stack.Notes);
    }

    [Fact]
    public async Task Managed_sources_first_with_keys_wins_and_empty_or_lower_sources_are_ignored()
    {
        using var tree = new TempTree();
        var config = tree.Dir("config");
        tree.Write("config/remote-settings.json", "{}");
        tree.Write("managed/managed-settings.json", """{ "model": "from-file" }""");
        var registry = new Dictionary<string, string> { ["HKLM"] = """{ "model": "from-hklm" }""", ["HKCU"] = """{ "model": "from-hkcu" }""" };

        var stack = await LoadAsync(tree, tree.Dir("proj"), config, tree.Path("managed"), registry: registry.GetValueOrDefault);

        var managed = stack.Layers.Where(l => l.Scope == "managed").ToDictionary(l => l.Id);
        Assert.Equal(LayerStatus.Ignored, managed["managed:server"].Status);
        Assert.Equal(LayerStatus.Loaded, managed["managed:hklm"].Status);
        Assert.Equal((LayerStatus.Ignored, "lower-ranked managed source"), (managed["managed:file:managed-settings.json"].Status, managed["managed:file:managed-settings.json"].Detail));
        Assert.Equal(LayerStatus.Ignored, managed["managed:hkcu"].Status);
        var model = stack.Get("model")!;
        Assert.Equal(("from-hklm", "managed (HKLM)"), (model.Value!.GetValue<string>(), Assert.Single(model.Contributions).Layer.Label));
    }

    [Fact]
    public async Task Later_managed_drop_ins_override_earlier_files()
    {
        using var tree = new TempTree();
        tree.Write("managed/managed-settings.json", """{ "model": "main", "effortLevel": "low" }""");
        tree.Write("managed/managed-settings.d/20-b.json", """{ "model": "b" }""");
        tree.Write("managed/managed-settings.d/10-a.json", """{ "model": "a" }""");

        var stack = await LoadAsync(tree, tree.Dir("proj"), tree.Dir("config"), tree.Path("managed"));

        var model = stack.Get("model")!;
        Assert.Equal("b", model.Value!.GetValue<string>());
        Assert.Equal(
            [("managed:file:d/20-b.json", ContributionStatus.Applied), ("managed:file:d/10-a.json", ContributionStatus.Overridden), ("managed:file:managed-settings.json", ContributionStatus.Overridden)],
            model.Contributions.Select(c => (c.Layer.Id, c.Status)));
        Assert.Equal("low", stack.Get("effortLevel")!.Value!.GetValue<string>());
    }

    [Fact]
    public async Task ProgramData_managed_file_is_shown_ignored_and_never_parsed()
    {
        using var tree = new TempTree();
        tree.Write("programdata/managed-settings.json", "{ not json");

        var stack = await LoadAsync(tree, tree.Dir("proj"), tree.Dir("config"), tree.Path("managed"), tree.Path("programdata"));

        var ignored = Assert.Single(stack.Layers, l => l.Id == "managed:programdata");
        Assert.Equal((LayerStatus.Ignored, "not read by Claude Code"), (ignored.Status, ignored.Detail));
        Assert.Equal(LayerStatus.NotPresent, Assert.Single(stack.Layers, l => l.Id == "managed").Status);
        Assert.Empty(stack.Settings);
    }

    [Fact]
    public async Task A_layer_that_fails_strict_parsing_is_an_error_and_the_others_still_merge()
    {
        using var tree = new TempTree();
        var target = tree.Dir("proj");
        tree.Write("proj/.claude/settings.json", """{ "model": "opus", }""");
        tree.Write("config/settings.json", """{ "model": "sonnet" }""");

        var stack = await LoadAsync(tree, target, tree.Path("config"));

        var project = Assert.Single(stack.Layers, l => l.Id == "project");
        Assert.Equal(LayerStatus.Error, project.Status);
        Assert.StartsWith("invalid JSON", project.Detail);
        Assert.Equal("user", Assert.Single(stack.Contributing).Label);
        Assert.Equal("sonnet", stack.Get("model")!.Value!.GetValue<string>());
    }

    [Fact]
    public async Task The_same_physical_file_in_two_layers_is_a_duplicate_and_merges_once()
    {
        using var tree = new TempTree();
        var repo = tree.Dir("config");
        tree.Write("config/.claude/settings.json", """{ "permissions": { "allow": ["Read"] } }""");

        var stack = await LoadAsync(tree, repo, tree.Path("config/.claude"));

        var user = Assert.Single(stack.Layers, l => l.Id == "user");
        Assert.Equal((LayerStatus.Duplicate, "duplicate of project"), (user.Status, user.Detail));
        var allow = stack.Get("permissions.allow")!;
        Assert.Equal("""["Read"]""", allow.Value!.ToJsonString());
        Assert.Equal(("project", ContributionStatus.Applied), (Assert.Single(allow.Contributions).Layer.Label, allow.Contributions[0].Status));
    }

    [Fact]
    public async Task Local_settings_are_read_at_the_git_root_and_project_settings_only_in_the_target_dir()
    {
        using var tree = new TempTree();
        tree.Dir("repo/.git");
        var local = tree.Write("repo/.claude/settings.local.json", """{ "model": "opus" }""");
        tree.Write("repo/.claude/settings.json", """{ "effortLevel": "high" }""");
        var target = tree.Dir("repo/sub");

        var stack = await LoadAsync(tree, target, tree.Dir("config"));

        Assert.True(PathUtil.PathEquals(tree.Path("repo"), stack.ProjectRoot));
        var localLayer = Assert.Single(stack.Layers, l => l.Id == "local");
        Assert.Equal(LayerStatus.Loaded, localLayer.Status);
        Assert.True(PathUtil.PathEquals(local, localLayer.Path!));
        Assert.Equal(LayerStatus.NotPresent, Assert.Single(stack.Layers, l => l.Id == "project").Status);
        Assert.Null(stack.Get("effortLevel"));
    }

    [Fact]
    public async Task Keys_outside_their_allowed_scopes_are_ignored_with_a_note()
    {
        using var tree = new TempTree();
        var target = tree.Dir("proj");
        tree.Write("proj/.claude/settings.json", """
            { "autoMode": { "allow": ["x"] }, "allowManagedHooksOnly": true, "permissions": { "defaultMode": "bypassPermissions" } }
            """);
        tree.Write("config/settings.json", """{ "autoMode": { "allow": ["y"] }, "permissions": { "defaultMode": "acceptEdits" } }""");

        var stack = await LoadAsync(tree, target, tree.Path("config"));

        var auto = stack.Get("autoMode.allow")!;
        Assert.Equal("""["y"]""", auto.Value!.ToJsonString());
        Assert.Equal([(true, ContributionStatus.Overridden), (false, ContributionStatus.Applied)], auto.Contributions.Select(c => (c.IgnoredScope, c.Status)));
        Assert.Contains("ignored-scope", auto.Flags);
        var hooksOnly = stack.Get("allowManagedHooksOnly")!;
        Assert.Null(hooksOnly.Value);
        Assert.True(Assert.Single(hooksOnly.Contributions).IgnoredScope);
        Assert.Equal("acceptEdits", stack.Get("permissions.defaultMode")!.Value!.GetValue<string>());
        Assert.Equal(3, stack.Notes.Count(n => n.Contains("is ignored", StringComparison.Ordinal)));
    }
}
