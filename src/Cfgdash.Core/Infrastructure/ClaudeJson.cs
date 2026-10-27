using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cfgdash.Core.Infrastructure;

internal static class ClaudeJson
{
    public static string Locate(string? claudeConfigDir, string? claudeConfigDirSource, string homeDir) =>
        claudeConfigDir is not null && claudeConfigDirSource is not null && !claudeConfigDirSource.StartsWith("default:", StringComparison.Ordinal)
            ? Path.Combine(claudeConfigDir, ".claude.json")
            : Path.Combine(homeDir, ".claude.json");

    public static ClaudeJsonData? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonObject? userServers = null;
            var projects = new List<ClaudeJsonProject>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("mcpServers"))
                {
                    userServers = AsObject(property.Value);
                }
                else if (property.NameEquals("projects") && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var project in property.Value.EnumerateObject())
                    {
                        JsonObject? servers = null;
                        if (project.Value.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var field in project.Value.EnumerateObject())
                            {
                                if (field.NameEquals("mcpServers"))
                                {
                                    servers = AsObject(field.Value);
                                }
                            }
                        }

                        projects.Add(new ClaudeJsonProject(project.Name, servers));
                    }
                }
            }

            return new ClaudeJsonData(userServers, projects);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static JsonObject? AsObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(element.GetRawText()) as JsonObject;
        }
        catch (Exception e) when (e is ArgumentException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}

internal sealed record ClaudeJsonData(JsonObject? UserMcpServers, IReadOnlyList<ClaudeJsonProject> Projects);

/// <summary>Keys may repeat with different case or separators.</summary>
internal sealed record ClaudeJsonProject(string Path, JsonObject? McpServers);
