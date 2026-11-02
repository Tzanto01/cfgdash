using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cfgdash.Core.Assistant;

/// <summary>
/// <paramref name="Ok"/> when the session is contained. <paramref name="PluginsOnly"/> when only non-empty <c>plugins</c> failed;
/// <paramref name="PluginIds"/> are then their <c>source</c> (else <c>name</c>) values to disable for the one retry.
/// </summary>
public sealed record InitVerdict(bool Ok, bool PluginsOnly, IReadOnlyList<string> PluginIds, string? Reason);

/// <summary>The fail-closed containment check of the first <c>system</c>/<c>init</c> stream-json event (assistant-v1).</summary>
public static class InitCheck
{
    private const int MaxReasonLength = 500;

    public static InitVerdict Evaluate(JsonObject init)
    {
        var failures = new List<string>();
        var tools = Strings(init["tools"]);
        if (tools is null || tools.Count != AssistantLaunch.ToolNames.Count || !tools.ToHashSet(StringComparer.Ordinal).SetEquals(AssistantLaunch.ToolNames))
        {
            failures.Add("tools " + Describe(tools));
        }

        var servers = Servers(init["mcp_servers"]);
        if (servers is not [("cfgdash", _)])
        {
            failures.Add("mcp_servers " + (servers is null ? "unreadable" : "[" + string.Join(", ", servers.Select(s => s.Name + ":" + (s.Status ?? "?"))) + "]"));
        }

        foreach (var field in new[] { "agents", "skills", "slash_commands" })
        {
            if (init[field] is not JsonArray { Count: 0 })
            {
                failures.Add(field + " " + (init[field] is JsonArray list ? "count " + list.Count : "unreadable"));
            }
        }

        var pluginIds = new List<string>();
        var pluginsReadable = init["plugins"] is JsonArray;
        if (init["plugins"] is JsonArray plugins)
        {
            foreach (var plugin in plugins)
            {
                if (plugin is JsonObject obj && (Text(obj["source"]) ?? Text(obj["name"])) is { Length: > 0 } id)
                {
                    pluginIds.Add(id);
                }
                else
                {
                    pluginsReadable = false;
                }
            }
        }

        if (failures.Count == 0 && pluginsReadable && pluginIds.Count == 0)
        {
            return new InitVerdict(true, false, [], null);
        }

        var pluginsOnly = failures.Count == 0 && pluginsReadable;
        if (!pluginsReadable || pluginIds.Count > 0)
        {
            failures.Add("plugins " + (pluginsReadable ? "[" + string.Join(", ", pluginIds) + "]" : "unreadable"));
        }

        var reason = string.Join("; ", failures);
        return new InitVerdict(false, pluginsOnly, pluginsOnly ? pluginIds : [], reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason);
    }

    private static List<string>? Strings(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var items = array.Select(Text).ToList();
        return items.Any(i => i is null) ? null : [.. items.OfType<string>()];
    }

    private static List<(string Name, string? Status)>? Servers(JsonNode? node)
    {
        if (node is not JsonArray array || array.Any(s => s is not JsonObject obj || Text(obj["name"]) is null))
        {
            return null;
        }

        return [.. array.Select(s => (Text(s!["name"])!, Text(s!["status"])))];
    }

    private static string Describe(List<string>? items) => items is null ? "unreadable" : "[" + string.Join(", ", items) + "]";

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
