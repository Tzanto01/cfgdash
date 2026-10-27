using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Settings;

/// <summary>Per-leaf merge of the contributing layers (load-and-merge-v1 "Merge").</summary>
internal static class SettingsMerge
{
    private static readonly JsonSerializerOptions KeyJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string[] Atomic = ["fallbackModel", "modelPicker"];
    private static readonly string[] ManagedOnly = ["allowManagedHooksOnly", "allowManagedMcpServersOnly", "allowManagedPermissionRulesOnly", "strictPluginOnlyCustomization"];
    private static readonly string[] TrustGated = ["permissions.allow", "permissions.additionalDirectories", "extraKnownMarketplaces", "env"];

    /// <summary>True when <paramref name="key"/> equals <paramref name="prefix"/> or is nested below it.</summary>
    public static bool HasPrefix(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.Ordinal) && (key.Length == prefix.Length || key[prefix.Length] is '.' or '[');

    /// <summary>Dotted key; a segment that is empty or contains <c>.</c>, <c>[</c> or <c>"</c> is written <c>["seg"]</c>.</summary>
    public static string Key(IReadOnlyList<string> segments)
    {
        var key = new StringBuilder();
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment.AsSpan().IndexOfAny(".[\"") >= 0)
            {
                key.Append('[').Append(JsonSerializer.Serialize(segment, KeyJson)).Append(']');
            }
            else
            {
                if (key.Length > 0)
                {
                    key.Append('.');
                }

                key.Append(segment);
            }
        }

        return key.ToString();
    }

    public static IReadOnlyList<EffectiveSetting> Merge(IReadOnlyList<SettingsLayer> contributing, List<string> notes)
    {
        var leaves = new Dictionary<string, (IReadOnlyList<string> Segments, List<(JsonNode? Value, SettingsLayer Layer)> Items)>(StringComparer.Ordinal);
        foreach (var layer in contributing)
        {
            Flatten(layer.Root!, [], (segments, value) =>
            {
                var key = Key(segments);
                if (!leaves.TryGetValue(key, out var entry))
                {
                    entry = (segments, []);
                    leaves[key] = entry;
                }

                entry.Items.Add((value, layer));
            });
        }

        var result = new List<EffectiveSetting>();
        var trustGated = false;
        foreach (var (key, (segments, items)) in leaves.OrderBy(l => l.Key, StringComparer.Ordinal))
        {
            var reasons = items.Select(i => IgnoredReason(key, i.Value, i.Layer.Scope)).ToList();
            var valid = items.Where((_, i) => reasons[i] is null).ToList();
            var asArray = valid.Count > 0
                && !Atomic.Contains(key)
                && !(key == "availableModels" && valid[0].Layer.Scope == "managed")
                && valid.All(i => i.Value is JsonArray);

            JsonNode? value = null;
            if (asArray)
            {
                var merged = new JsonArray();
                foreach (var element in valid.SelectMany(i => (JsonArray)i.Value!))
                {
                    if (!merged.Any(m => JsonNode.DeepEquals(m, element)))
                    {
                        merged.Add(element?.DeepClone());
                    }
                }

                value = merged;
            }
            else if (valid.Count > 0)
            {
                value = valid[0].Value?.DeepClone();
            }

            var contributions = new List<SettingContribution>();
            for (var i = 0; i < items.Count; i++)
            {
                var (itemValue, layer) = items[i];
                if (reasons[i] is { } reason)
                {
                    contributions.Add(new SettingContribution(itemValue, layer, ContributionStatus.Overridden, IgnoredScope: true));
                    notes.Add($"{key} from {layer.Label} is ignored: {reason}");
                    continue;
                }

                var status = asArray
                    ? valid.Count == 1 ? ContributionStatus.Applied : ContributionStatus.Merged
                    : ReferenceEquals(layer, valid[0].Layer) ? ContributionStatus.Applied : ContributionStatus.Overridden;
                contributions.Add(new SettingContribution(itemValue, layer, status));
            }

            var flags = new List<string>();
            if (contributions.Any(c => c.Status == ContributionStatus.Overridden))
            {
                flags.Add("overridden");
            }

            if (contributions.Any(c => c.IgnoredScope))
            {
                flags.Add("ignored-scope");
            }

            if (TrustGated.Any(p => HasPrefix(key, p)) && items.Any(i => i.Layer.Scope is "local" or "project"))
            {
                flags.Add("trust-gated");
                trustGated = true;
            }

            result.Add(new EffectiveSetting(key, value, contributions, flags) { Segments = segments });
        }

        if (trustGated)
        {
            notes.Add("Project and local values of permissions.allow, permissions.additionalDirectories, extraKnownMarketplaces and env apply only after the folder is trusted; trust state is not evaluated in v1");
        }

        return result;
    }

    private static void Flatten(JsonObject obj, IReadOnlyList<string> path, Action<IReadOnlyList<string>, JsonNode?> leaf)
    {
        foreach (var (name, value) in obj)
        {
            IReadOnlyList<string> segments = [.. path, name];
            if (value is JsonObject { Count: > 0 } child && !(path.Count == 0 && Atomic.Contains(name)))
            {
                Flatten(child, segments, leaf);
            }
            else
            {
                leaf(segments, value);
            }
        }
    }

    private static string? IgnoredReason(string key, JsonNode? value, string scope)
    {
        if (HasPrefix(key, "autoMode") && scope is not ("user" or "managed"))
        {
            return "autoMode is read only from user or managed settings";
        }

        if (ManagedOnly.Contains(key) && scope != "managed")
        {
            return "read only from managed settings";
        }

        if (key == "permissions.defaultMode" && scope is "local" or "project"
            && value is JsonValue mode && mode.TryGetValue<string>(out var text) && text is "auto" or "bypassPermissions")
        {
            return text + " is not accepted from project or local settings";
        }

        return null;
    }
}
