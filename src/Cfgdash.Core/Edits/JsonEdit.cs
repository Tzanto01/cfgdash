using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cfgdash.Core.Edits;

/// <summary>Strict parse, in-place set/unset and the canonical writer of edited settings files.</summary>
internal static class JsonEdit
{
    private static readonly JsonDocumentOptions Strict = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, AllowDuplicateProperties = false };
    private static readonly JsonSerializerOptions Lf = Writer("\n");
    private static readonly JsonSerializerOptions CrLf = Writer("\r\n");

    /// <summary>The root object of <paramref name="bytes"/> (BOM tolerated) and its canonical text; null when not strict JSON or not an object.</summary>
    public static JsonObject? Parse(byte[] bytes, string newLine, out string canonical)
    {
        canonical = string.Empty;
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith("﻿"u8))
        {
            json = json[3..];
        }

        try
        {
            if (JsonNode.Parse(json, documentOptions: Strict) is not JsonObject root)
            {
                return null;
            }

            canonical = Serialize(root, newLine);
            return root;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Two-space indent, relaxed escaping, <paramref name="newLine"/> line breaks and a trailing newline.</summary>
    public static string Serialize(JsonNode root, string newLine) => root.ToJsonString(newLine == "\r\n" ? CrLf : Lf) + newLine;

    /// <summary>Assigns in place (key order kept, new keys appended), creating missing objects. Null on success, else <c>not-an-object</c>.</summary>
    public static string? Set(JsonObject root, IReadOnlyList<string> segments, JsonNode? value)
    {
        var current = root;
        for (var i = 0; i < segments.Count - 1; i++)
        {
            if (!current.TryGetPropertyValue(segments[i], out var child))
            {
                child = new JsonObject();
                current[segments[i]] = child;
            }

            if (child is not JsonObject next)
            {
                return "not-an-object";
            }

            current = next;
        }

        current[segments[^1]] = value?.DeepClone();
        return null;
    }

    /// <summary>Removes the leaf, then every object on the path it leaves empty (never the root). Null on success, else <c>not-an-object</c>.</summary>
    public static string? Unset(JsonObject root, IReadOnlyList<string> segments)
    {
        var path = new List<JsonObject> { root };
        for (var i = 0; i < segments.Count - 1; i++)
        {
            if (!path[^1].TryGetPropertyValue(segments[i], out var child))
            {
                return null;
            }

            if (child is not JsonObject next)
            {
                return "not-an-object";
            }

            path.Add(next);
        }

        if (!path[^1].Remove(segments[^1]))
        {
            return null;
        }

        for (var i = path.Count - 1; i > 0 && path[i].Count == 0; i--)
        {
            path[i - 1].Remove(segments[i - 1]);
        }

        return null;
    }

    private static JsonSerializerOptions Writer(string newLine) =>
        new() { WriteIndented = true, IndentSize = 2, NewLine = newLine, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
