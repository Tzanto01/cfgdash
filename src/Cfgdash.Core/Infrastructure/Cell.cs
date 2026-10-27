using System.Text.Json.Nodes;

namespace Cfgdash.Core.Infrastructure;

internal static class Cell
{
    public static JsonNode? Text(string? text) => text is null ? null : JsonValue.Create(text);

    public static JsonNode? File(string text, string? fileId) =>
        fileId is null ? JsonValue.Create(text) : new JsonObject { ["text"] = text, ["fileId"] = fileId };
}
