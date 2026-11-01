using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cfgdash.Core.Edits;

/// <summary>Resolves change keys against <see cref="SettingsCatalogData"/>, checks editability and value types.</summary>
internal static class EditCatalog
{
    public const string EnvPrefix = "env.";
    public const string PluginPrefix = "enabledPlugins.";
    public const string AutoPullKey = "configAutoPull";

    private const int MaxStringLength = 10_000;
    private const int MaxListItems = 1_000;

    private static readonly Regex EnvName = new("^[A-Za-z_][A-Za-z0-9_]{0,127}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex PluginId = new("^[A-Za-z0-9][A-Za-z0-9._@+-]{0,199}\\z", RegexOptions.CultureInvariant);
    private static readonly (string Prefix, Regex Pattern)[] Templates = [(EnvPrefix, EnvName), (PluginPrefix, PluginId)];
    private static readonly string[] UnsafeEnvPrefixes = ["LD_", "DYLD_", "GIT_CONFIG"];
    private static readonly string[] UnsafeEnvNames =
    [
        "BASH_ENV", "ENV", "NODE_OPTIONS", "NODE_PATH", "PATH", "SHELL", "PYTHONPATH", "PYTHONSTARTUP", "PERL5OPT", "RUBYOPT",
        "JAVA_TOOL_OPTIONS", "DOTNET_STARTUP_HOOKS", "GIT_SSH_COMMAND", "GIT_ASKPASS", "PROMPT_COMMAND", "ZDOTDIR",
    ];
    private static readonly Dictionary<string, SettingDefinition> ByKey = SettingsCatalogData.Settings.ToDictionary(s => s.Key, StringComparer.Ordinal);

    /// <summary>True for the <c>env.*</c> and <c>enabledPlugins.*</c> template entries.</summary>
    public static bool IsTemplate(SettingDefinition definition) => definition.Key.EndsWith(".*", StringComparison.Ordinal);

    /// <summary>The entry and key segments for <paramref name="key"/>; the remainder after a template prefix is one literal segment. Null outside the catalog.</summary>
    public static (SettingDefinition Definition, IReadOnlyList<string> Segments)? Resolve(string key)
    {
        foreach (var (prefix, pattern) in Templates)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                var rest = key[prefix.Length..];
                if (!pattern.IsMatch(rest) || !ByKey.TryGetValue(prefix + "*", out var template))
                {
                    return null;
                }

                string[] segments = [prefix[..^1], rest];
                return (template, segments);
            }
        }

        if (!ByKey.TryGetValue(key, out var definition) || IsTemplate(definition))
        {
            return null;
        }

        return (definition, key.Split('.'));
    }

    /// <summary>Null when editable, else <c>secret</c> (a segment the redactor treats as secret) or <c>unsafe-env</c> (loader and exec variables).</summary>
    public static string? NotEditableReason(IReadOnlyList<string> segments, IRedactor redactor)
    {
        if (segments.Any(redactor.IsSecretName))
        {
            return "secret";
        }

        return segments is ["env", var name]
            && (UnsafeEnvNames.Contains(name, StringComparer.OrdinalIgnoreCase) || UnsafeEnvPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            ? "unsafe-env"
            : null;
    }

    /// <summary>Null when <paramref name="value"/> fits the entry in <paramref name="scope"/>, else <c>bad-type</c> or <c>bad-option</c>.</summary>
    public static string? ValueError(SettingDefinition definition, string scope, JsonNode? value)
    {
        switch (definition.Type)
        {
            case "bool":
                return value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? null : "bad-type";
            case "number":
                if (value is not JsonValue number || number.GetValueKind() != JsonValueKind.Number || !number.TryGetValue<double>(out var d) || !double.IsFinite(d))
                {
                    return "bad-type";
                }

                return d < definition.Min || d > definition.Max || (definition.Integer && d != Math.Floor(d)) ? "out-of-range" : null;
            case "string":
                return IsString(value) ? null : "bad-type";
            case "stringList":
                return value is JsonArray list && list.Count <= MaxListItems && list.All(IsString) ? null : "bad-type";
            case "enum":
                if (!IsString(value))
                {
                    return "bad-type";
                }

                var text = value!.GetValue<string>();
                return definition.Options?.Contains(text, StringComparer.Ordinal) == true
                    && (scope == "user" || definition.UserOnlyOptions?.Contains(text, StringComparer.Ordinal) != true)
                    ? null
                    : "bad-option";
            default:
                return "bad-type";
        }
    }

    private static bool IsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Length <= MaxStringLength;
}
