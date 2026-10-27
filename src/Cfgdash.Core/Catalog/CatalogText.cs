using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cfgdash.Core.Catalog;

/// <summary>Display text for frontmatter values (unredacted; callers redact).</summary>
internal static partial class CatalogText
{
    private const int MaxDescription = 300;

    /// <summary>Strings raw, string lists joined with ", ", anything else as compact JSON; null when absent.</summary>
    public static string? Value(JsonNode? node) => node switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonArray array => string.Join(", ", array.Select(item => Value(item) ?? "null")),
        _ => node.ToJsonString(),
    };

    /// <summary>Whitespace collapsed, max 300 chars.</summary>
    public static string? Description(JsonNode? node)
    {
        if (Value(node) is not { } text)
        {
            return null;
        }

        var collapsed = Whitespace().Replace(text, " ").Trim();
        return collapsed.Length > MaxDescription ? collapsed[..MaxDescription] : collapsed;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
