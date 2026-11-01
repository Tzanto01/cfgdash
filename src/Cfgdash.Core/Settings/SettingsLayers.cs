using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Settings;

internal enum LayerStatus { Loaded, NotPresent, Error, Ignored, Duplicate, NotObservable }

/// <summary>
/// One settings source row. <see cref="Root"/> is the parsed, UNREDACTED object: consumers redact before emitting.
/// <see cref="Detail"/> holds the error text, "duplicate of &lt;label&gt;", the ignore reason or a note.
/// </summary>
internal sealed record SettingsLayer(string Id, string Scope, string Label, string? Path, string? FileId, LayerStatus Status, JsonObject? Root, string? Detail)
{
    /// <summary>Scopes of lower layers that are the same physical file and were skipped as duplicates of this one.</summary>
    public IReadOnlyList<string> ExtraScopes { get; init; } = [];
}

internal sealed record SettingContribution(JsonNode? Value, SettingsLayer Layer, ContributionStatus Status, bool IgnoredScope = false);

/// <summary>A merged leaf. <see cref="Key"/> is the dotted display form of <see cref="Segments"/>.</summary>
internal sealed record EffectiveSetting(string Key, JsonNode? Value, IReadOnlyList<SettingContribution> Contributions, IReadOnlyList<string> Flags)
{
    public IReadOnlyList<string> Segments { get; init; } = [];
}

/// <summary>The settings layers and merged leaves for one target dir (docs/contracts/load-and-merge-v1.md).</summary>
internal sealed class SettingsStack(string targetDir, string projectRoot, IReadOnlyList<SettingsLayer> layers, IReadOnlyList<EffectiveSetting> settings, IReadOnlyList<string> notes)
{
    private readonly Dictionary<string, EffectiveSetting> byKey = settings.ToDictionary(s => s.Key, StringComparer.Ordinal);

    public string TargetDir { get; } = targetDir;

    /// <summary>The git root containing <see cref="TargetDir"/>, else <see cref="TargetDir"/>.</summary>
    public string ProjectRoot { get; } = projectRoot;

    /// <summary>All rows, highest precedence first.</summary>
    public IReadOnlyList<SettingsLayer> Layers { get; } = layers;

    /// <summary>Loaded rows that merge, highest precedence first.</summary>
    public IReadOnlyList<SettingsLayer> Contributing { get; } = [.. layers.Where(l => l.Status == LayerStatus.Loaded)];

    /// <summary>Merged leaves, ordered by key (ordinal).</summary>
    public IReadOnlyList<EffectiveSetting> Settings { get; } = settings;

    public IReadOnlyList<string> Notes { get; } = notes;

    public EffectiveSetting? Get(string key) => byKey.GetValueOrDefault(key);

    /// <summary>The setting equal to <paramref name="prefix"/> and every setting nested below it.</summary>
    public IEnumerable<EffectiveSetting> WithPrefix(string prefix) => Settings.Where(s => SettingsMerge.HasPrefix(s.Key, prefix));
}

/// <summary>Loads the settings stack once per <see cref="CollectContext"/> so all collectors of one build agree.</summary>
internal sealed class SettingsLayers(PlatformLocations locations)
{
    private const string LowerRanked = "lower-ranked managed source";

    private readonly ConditionalWeakTable<CollectContext, Lazy<SettingsStack>> cache = new();

    private sealed record Candidate(string Id, string Label, string? Path, string? FileId, Func<(JsonObject? Root, string? Error)> Read);

    public Task<SettingsStack> LoadAsync(CollectContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(cache.GetValue(context, c => new Lazy<SettingsStack>(() => Load(c))).Value);
    }

    private SettingsStack Load(CollectContext context)
    {
        var target = PathUtil.Full(context.TargetDir);
        var projectRoot = PathUtil.FindGitRoot(target) ?? target;
        var configDir = context.Environment.GetPath(PathKeys.ClaudeConfigDir);
        var notes = new List<string> { "Command-line flags are not observable", "Object merge is not documented; merged per leaf key" };
        if (context.Environment.Os == OsFamily.Macos)
        {
            notes.Add("macOS managed preferences (plist domain com.anthropic.claudecode) are not read");
        }

        var layers = Managed(context, configDir);
        layers.Add(new SettingsLayer("cli", "cli", "command line", null, null, LayerStatus.NotObservable, null, null));
        var seen = new Dictionary<string, string>(PathUtil.Comparer);
        layers.Add(FileLayer(context, "local", "project (local)", Path.Combine(projectRoot, ".claude", "settings.local.json"), seen));
        layers.Add(FileLayer(context, "project", "project", Path.Combine(target, ".claude", "settings.json"), seen));
        layers.Add(configDir is null
            ? new SettingsLayer("user", "user", "user", null, null, LayerStatus.NotPresent, null, "no claudeConfigDir")
            : FileLayer(context, "user", "user", Path.Combine(configDir, "settings.json"), seen));
        foreach (var duplicate in layers.Where(l => l.Status == LayerStatus.Duplicate).ToList())
        {
            var real = PathUtil.RealPath(duplicate.Path!);
            var index = layers.FindIndex(l => l.Status == LayerStatus.Loaded && l.Path is not null && PathUtil.PathEquals(PathUtil.RealPath(l.Path), real));
            if (index >= 0)
            {
                layers[index] = layers[index] with { ExtraScopes = [.. layers[index].ExtraScopes, duplicate.Scope] };
            }
        }

        var merged = SettingsMerge.Merge([.. layers.Where(l => l.Status == LayerStatus.Loaded)], notes);
        return new SettingsStack(target, projectRoot, layers, merged, notes);
    }

    private static SettingsLayer FileLayer(CollectContext context, string id, string label, string path, Dictionary<string, string> seen)
    {
        if (!File.Exists(path))
        {
            return new SettingsLayer(id, id, label, path, null, LayerStatus.NotPresent, null, null);
        }

        if (FileRegistry.IsNeverRead(path))
        {
            return new SettingsLayer(id, id, label, path, null, LayerStatus.Error, null, "not opened");
        }

        var fileId = context.Files.Register(path, "settings")?.Id;
        var real = PathUtil.RealPath(path);
        if (seen.TryGetValue(real, out var original))
        {
            return new SettingsLayer(id, id, label, path, fileId, LayerStatus.Duplicate, null, "duplicate of " + original);
        }

        seen[real] = label;
        var (root, error) = ReadFile(path);
        return new SettingsLayer(id, id, label, path, fileId, error is null ? LayerStatus.Loaded : LayerStatus.Error, root, error);
    }

    private List<SettingsLayer> Managed(CollectContext context, string? configDir)
    {
        var sources = new List<IReadOnlyList<Candidate>>();
        Candidate FileCandidate(string id, string path) =>
            new(id, id == "managed:server" ? "managed (server)" : "managed (file)", path, context.Files.Register(path, "settings")?.Id, () => FileRegistry.IsNeverRead(path) ? (null, "not opened") : ReadFile(path));
        Candidate? RegistryCandidate(string hive) =>
            locations.ReadPolicyRegistry(hive) is { } text ? new Candidate("managed:" + hive.ToLowerInvariant(), $"managed ({hive})", null, null, () => ParseText(text)) : null;

        if (configDir is not null && Path.Combine(configDir, "remote-settings.json") is var remote && File.Exists(remote))
        {
            sources.Add([FileCandidate("managed:server", remote)]);
        }

        if (RegistryCandidate("HKLM") is { } hklm)
        {
            sources.Add([hklm]);
        }

        if (locations.ManagedDir is { } managedDir)
        {
            var files = new List<Candidate>();
            var main = Path.Combine(managedDir, "managed-settings.json");
            if (File.Exists(main))
            {
                files.Add(FileCandidate("managed:file:managed-settings.json", main));
            }

            var dropIns = Path.Combine(managedDir, "managed-settings.d");
            if (Directory.Exists(dropIns))
            {
                files.AddRange(Directory.GetFiles(dropIns, "*.json")
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                    .Select(f => FileCandidate("managed:file:d/" + Path.GetFileName(f), f)));
            }

            if (files.Count > 0)
            {
                files.Reverse();
                sources.Add(files);
            }
        }

        if (RegistryCandidate("HKCU") is { } hkcu)
        {
            sources.Add([hkcu]);
        }

        var rows = new List<SettingsLayer>();
        var won = false;
        foreach (var source in sources)
        {
            if (won)
            {
                rows.AddRange(source.Select(c => Row(c, LayerStatus.Ignored, null, LowerRanked)));
                continue;
            }

            var parsed = source.Select(c => (Candidate: c, Result: c.Read())).ToList();
            won = parsed.Any(p => p.Result.Root is { Count: > 0 });
            rows.AddRange(parsed.Select(p =>
                p.Result.Error is { } error ? Row(p.Candidate, LayerStatus.Error, null, error)
                : won ? Row(p.Candidate, LayerStatus.Loaded, p.Result.Root, null)
                : Row(p.Candidate, LayerStatus.Ignored, null, "no settings keys")));
        }

        if (rows.Count == 0)
        {
            rows.Add(new SettingsLayer("managed", "managed", "managed", null, null, LayerStatus.NotPresent, null, null));
        }

        if (locations.IgnoredManagedDir is { } ignoredDir && Path.Combine(ignoredDir, "managed-settings.json") is var ignored && File.Exists(ignored))
        {
            rows.Add(new SettingsLayer("managed:programdata", "managed", "managed (ProgramData)", ignored, null, LayerStatus.Ignored, null, "not read by Claude Code"));
        }

        return rows;
    }

    private static SettingsLayer Row(Candidate c, LayerStatus status, JsonObject? root, string? detail) =>
        new(c.Id, "managed", c.Label, c.Path, c.FileId, status, root, detail);

    private static (JsonObject? Root, string? Error) ReadFile(string path) =>
        HarnessFiles.TryReadStrictJson(path, out var root, out var error) ? (root, null) : (null, error);

    private static (JsonObject? Root, string? Error) ParseText(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow }) is JsonObject root
                ? (root, null)
                : (null, "root is not a JSON object");
        }
        catch (JsonException e)
        {
            return (null, "invalid JSON: " + e.Message.Split('\n')[0].Trim());
        }
    }
}
