using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Edits;

internal sealed partial class EditService
{
    private const string AutoPullNote = " CC_CONFIG_AUTOPULL in the environment wins over this setting.";
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(5);

    public async Task<JsonObject> GetSettingsAsync(string dir, CancellationToken cancellationToken)
    {
        var target = PathUtil.Full(dir);
        var context = new CollectContext
        {
            Environment = environment,
            TargetDir = target,
            Files = new FileRegistry(environment.CompanyPrefixes),
            Redactor = redactor,
            Processes = processes,
        };
        var stack = await new SettingsLayers(locations).LoadAsync(context, cancellationToken);

        var rows = new List<(string Category, string MergedKey, JsonObject Row)>();
        foreach (var definition in SettingsCatalogData.Settings)
        {
            if (definition.Key == EditCatalog.AutoPullKey)
            {
                rows.Add((definition.Category, definition.Key, AutoPullRow(definition, target)));
            }
            else if (EditCatalog.IsTemplate(definition))
            {
                var top = definition.Key[..^2];
                var names = stack.Contributing
                    .SelectMany(l => l.Root![top] is JsonObject obj ? obj.Select(p => p.Key) : Enumerable.Empty<string>())
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal);
                foreach (var name in names)
                {
                    if (EditCatalog.Resolve(top + "." + name) is { } resolved)
                    {
                        rows.Add((definition.Category, SettingsMerge.Key(resolved.Segments), Row(resolved.Definition, top + "." + name, name, resolved.Segments, stack)));
                    }
                }
            }
            else
            {
                var segments = definition.Key.Split('.');
                rows.Add((definition.Category, SettingsMerge.Key(segments), Row(definition, definition.Key, definition.Label, segments, stack)));
            }
        }

        var covered = rows.Select(r => r.MergedKey).ToList();
        rows.AddRange(stack.Settings
            .Where(s => !covered.Any(key => SettingsMerge.HasPrefix(s.Key, key)))
            .Select(s => ("other", s.Key, OtherRow(s))));

        var labels = SettingsCatalogData.Categories.ToDictionary(c => c.Id, c => c.Label, StringComparer.Ordinal);
        labels.TryAdd("other", "Other");
        var categories = new JsonArray();
        foreach (var id in SettingsCatalogData.Categories.Select(c => c.Id).Concat(rows.Select(r => r.Category)).Distinct(StringComparer.Ordinal))
        {
            var settings = rows.Where(r => r.Category == id).Select(r => r.Row).ToList();
            if (settings.Count > 0)
            {
                categories.Add(new JsonObject { ["id"] = id, ["label"] = labels.GetValueOrDefault(id, id), ["settings"] = Nodes(settings) });
            }
        }

        var files = new JsonArray();
        foreach (var scope in EditTargets.Scopes)
        {
            if (targets.Resolve(scope, target, out _) is { } file)
            {
                var exists = File.Exists(file.Real);
                files.Add(new JsonObject
                {
                    ["scope"] = scope,
                    ["path"] = PathUtil.ToJson(file.Real),
                    ["exists"] = exists,
                    ["gitTracked"] = exists && await GitTrackedAsync(file.Real, cancellationToken),
                });
            }
        }

        return new JsonObject { ["categories"] = categories, ["files"] = files };
    }

    private JsonObject Row(SettingDefinition definition, string key, string label, IReadOnlyList<string> segments, SettingsStack stack)
    {
        var values = new JsonObject();
        foreach (var scope in definition.Scopes)
        {
            var layer = stack.Contributing.FirstOrDefault(l => l.Scope == scope || l.ExtraScopes.Contains(scope));
            values[scope] = layer is null ? null : Leaf(layer.Root!, segments);
        }

        var effective = stack.Get(SettingsMerge.Key(segments));
        var reason = EditCatalog.NotEditableReason(segments, redactor);
        return SettingRow(key, label, definition.Description, definition.Type, definition.Options, definition.Scopes, values,
            effective is null ? null : Redacted(effective.Value, segments), Source(effective), reason);
    }

    private JsonObject AutoPullRow(SettingDefinition definition, string dir)
    {
        var values = new JsonObject { ["machine"] = null };
        JsonNode? effective = null;
        string? source = null;
        var file = targets.Resolve("machine", dir, out var reason);
        if (file is not null && environment.GetPath(PathKeys.ConfigRepo) is { } repo)
        {
            if (HarnessFiles.TryReadStrictJson(file.Real, out var root, out _))
            {
                values["machine"] = Leaf(root!, [EditCatalog.AutoPullKey]);
            }

            var (on, from) = ConfigRepoCollector.AutoPull(PathUtil.Full(repo), environmentReader.Get(ConfigRepoCollector.AutoPullVariable));
            effective = on;
            source = from == ConfigRepoCollector.AutoPullVariable ? "env" : from == "default" ? "default" : "machine";
        }

        return SettingRow(definition.Key, definition.Label, definition.Description + AutoPullNote, definition.Type, definition.Options, definition.Scopes, values, effective, source, reason);
    }

    private JsonObject OtherRow(EffectiveSetting setting)
    {
        var values = new JsonObject();
        foreach (var contribution in setting.Contributions)
        {
            foreach (var scope in contribution.Layer.ExtraScopes.Prepend(contribution.Layer.Scope))
            {
                if (!values.ContainsKey(scope))
                {
                    values[scope] = Redacted(contribution.Value, setting.Segments);
                }
            }
        }

        var sample = setting.Value ?? setting.Contributions.FirstOrDefault()?.Value;
        var type = sample is JsonArray list
            ? list.All(i => i?.GetValueKind() == JsonValueKind.String) ? "stringList" : "json"
            : sample?.GetValueKind() switch
            {
                JsonValueKind.True or JsonValueKind.False => "bool",
                JsonValueKind.Number => "number",
                JsonValueKind.String => "string",
                _ => "json",
            };
        return SettingRow(setting.Key, setting.Key, string.Empty, type, null, [.. values.Select(v => v.Key)], values,
            Redacted(setting.Value, setting.Segments), Source(setting), "not-in-catalog");
    }

    private static JsonObject SettingRow(string key, string label, string description, string type, IReadOnlyList<string>? options, IReadOnlyList<string> scopes,
        JsonObject values, JsonNode? effective, string? effectiveSource, string? reason)
    {
        var row = new JsonObject { ["key"] = key, ["label"] = label, ["description"] = description, ["type"] = type };
        if (options is not null)
        {
            row["options"] = Nodes(options.Select(o => JsonValue.Create(o)));
        }

        row["scopes"] = Nodes(scopes.Select(s => JsonValue.Create(s)));
        row["values"] = values;
        row["effective"] = effective;
        row["effectiveSource"] = effectiveSource;
        row["editable"] = reason is null;
        if (reason is not null)
        {
            row["reason"] = reason;
        }

        return row;
    }

    /// <summary>The leaf at <paramref name="segments"/>; a present secret segment yields the marker, anything else passes through <c>RedactJson</c>.</summary>
    private JsonNode? Leaf(JsonObject root, IReadOnlyList<string> segments)
    {
        JsonNode? node = root;
        foreach (var segment in segments)
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(segment, out var child))
            {
                return null;
            }

            if (redactor.IsSecretName(segment))
            {
                return JsonValue.Create(Redactor.Marker);
            }

            node = child;
        }

        return redactor.RedactJson(node);
    }

    private JsonNode? Redacted(JsonNode? value, IReadOnlyList<string> segments) =>
        value is not null && segments.Any(redactor.IsSecretName) ? JsonValue.Create(Redactor.Marker) : redactor.RedactJson(value);

    private static string? Source(EffectiveSetting? setting) =>
        setting is null ? null
        : setting.Contributions.Any(c => c.Status == ContributionStatus.Merged) ? "merged"
        : setting.Contributions.FirstOrDefault(c => c.Status == ContributionStatus.Applied)?.Layer.Scope;

    private async Task<bool> GitTrackedAsync(string path, CancellationToken cancellationToken)
    {
        if (!environment.Tools.TryGetValue("git", out var git) || Path.GetDirectoryName(path) is not { } dir || PathUtil.FindGitRoot(dir) is not { } root)
        {
            return false;
        }

        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var result = await processes.RunAsync(git,
            ["--no-optional-locks", "--literal-pathspecs", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=", "--git-dir=" + Path.Combine(root, ".git"), "--work-tree=" + root,
                "status", "--porcelain=v1", "--ignored", "--", relative],
            GitTimeout, cancellationToken);
        return !result.TimedOut && result.ExitCode == 0
            && !result.StandardOutput.StartsWith("??", StringComparison.Ordinal) && !result.StandardOutput.StartsWith("!!", StringComparison.Ordinal);
    }
}
