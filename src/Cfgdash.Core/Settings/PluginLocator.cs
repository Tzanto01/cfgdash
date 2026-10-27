using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Infrastructure;

namespace Cfgdash.Core.Settings;

internal sealed record PluginRoot(string Key, string Name, string Marketplace, string Dir, string? Version);

/// <summary>Enabled plugins and their install dirs from <c>&lt;config dir&gt;/plugins/installed_plugins.json</c>.</summary>
internal static class PluginLocator
{
    public const string CacheDirNote = "A CLAUDE_CODE_PLUGIN_CACHE_DIR override is not read; plugins are located under <config dir>/plugins";

    /// <summary>Enabled = effective <c>enabledPlugins.&lt;name@marketplace&gt;</c> is <c>true</c>. Install preference: local, project (matching project path), user.</summary>
    public static IReadOnlyList<PluginRoot> Locate(SettingsStack settings, MachineEnvironment environment)
    {
        var configDir = environment.GetPath(PathKeys.ClaudeConfigDir);
        var enabled = settings.WithPrefix("enabledPlugins")
            .Where(s => s.Segments.Count == 2 && s.Value is JsonValue v && v.GetValueKind() == JsonValueKind.True)
            .Select(s => s.Segments[1])
            .Order(StringComparer.Ordinal)
            .ToList();
        if (configDir is null || enabled.Count == 0
            || !HarnessFiles.TryReadStrictJson(Path.Combine(configDir, "plugins", "installed_plugins.json"), out var root, out _)
            || root!["plugins"] is not JsonObject plugins)
        {
            return [];
        }

        var result = new List<PluginRoot>();
        foreach (var key in enabled)
        {
            var installs = plugins[key] switch
            {
                JsonArray array => array.OfType<JsonObject>().ToList(),
                JsonObject single => [single],
                _ => [],
            };
            var install = installs
                .Select(i => (Install: i, Rank: Rank(i, settings), Dir: Text(i["installPath"])))
                .Where(i => i.Rank >= 0 && i.Dir is not null && Directory.Exists(i.Dir))
                .OrderBy(i => i.Rank)
                .FirstOrDefault();
            if (install.Install is null)
            {
                continue;
            }

            var at = key.LastIndexOf('@');
            result.Add(new PluginRoot(
                key,
                at > 0 ? key[..at] : key,
                at > 0 ? key[(at + 1)..] : string.Empty,
                PathUtil.Full(install.Dir!),
                Text(install.Install["version"])));
        }

        return result;
    }

    private static int Rank(JsonObject install, SettingsStack settings)
    {
        var projectPath = Text(install["projectPath"]);
        var matches = projectPath is not null && (PathUtil.PathEquals(projectPath, settings.ProjectRoot) || PathUtil.PathEquals(projectPath, settings.TargetDir));
        return Text(install["scope"]) switch
        {
            "local" when matches => 0,
            "project" when matches => 1,
            "user" => 2,
            _ => -1,
        };
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
