using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Tomlyn;
using Tomlyn.Model;

namespace Cfgdash.Core.Collectors;

/// <summary>Shared reading and display helpers for the T3, Codex and OpenCode collectors. Values are unredacted until <see cref="Flatten"/> or the caller redacts them.</summary>
internal static partial class ToolConfig
{
    public const int MaxBytes = 4 * 1024 * 1024;

    /// <summary>JSON with comments and trailing commas (JSONC), max 4 MiB, root must be an object.</summary>
    public static bool TryReadJsonc(string path, out JsonObject? root, out string? error)
    {
        root = null;
        if (!TryReadText(path, out var text, out error))
        {
            return false;
        }

        try
        {
            if (JsonNode.Parse(text!, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, AllowDuplicateProperties = false }) is not JsonObject obj)
            {
                error = "root is not a JSON object";
                return false;
            }

            root = obj;
            return true;
        }
        catch (JsonException e)
        {
            error = "invalid JSON: " + FirstLine(e.Message);
            return false;
        }
    }

    /// <summary>TOML, max 4 MiB, converted to JSON (dates as their TOML text). Errors report only the position, never parser text (it can quote source tokens).</summary>
    public static bool TryReadToml(string path, out JsonObject? root, out string? error)
    {
        root = null;
        if (!TryReadText(path, out var text, out error))
        {
            return false;
        }

        try
        {
            root = ToJson(TomlSerializer.Deserialize<TomlTable>(text!) ?? new TomlTable());
            return true;
        }
        catch (TomlException e)
        {
            var position = TomlPosition().Match(e.Message);
            error = position.Success ? $"invalid TOML at line {position.Groups[1].Value}, column {position.Groups[2].Value}" : "invalid TOML";
            return false;
        }
    }

    /// <summary>One kv row per leaf (scalar or array), dotted key. A secret-looking key segment redacts the whole value; otherwise <see cref="IRedactor.RedactJson"/>.</summary>
    public static IEnumerable<KvRow> Flatten(JsonObject? obj, IRedactor redactor, SourceRef source, IReadOnlySet<string>? skipTopLevel = null)
    {
        var rows = new List<KvRow>();
        void Walk(JsonObject node, string prefix, bool secret)
        {
            foreach (var (key, child) in node)
            {
                if (prefix.Length == 0 && skipTopLevel?.Contains(key) == true)
                {
                    continue;
                }

                var path = prefix.Length == 0 ? key : prefix + "." + key;
                var isSecret = secret || redactor.IsSecretName(key);
                if (child is JsonObject inner && !isSecret)
                {
                    Walk(inner, path, false);
                    continue;
                }

                var value = isSecret && child is not null ? JsonValue.Create(Redactor.Marker) : redactor.RedactJson(child);
                var changed = !JsonNode.DeepEquals(value, child);
                rows.Add(new KvRow(redactor.RedactText(path), value, source, changed, changed ? ["redacted"] : null));
            }
        }

        if (obj is not null)
        {
            Walk(obj, string.Empty, false);
        }

        return rows;
    }

    /// <summary><c>k=v; k=v</c> from an already redacted object; null when empty.</summary>
    public static string? Pairs(JsonNode? node) =>
        node is JsonObject { Count: > 0 } obj
            ? string.Join("; ", obj.Select(p => p.Key + "=" + (Text(p.Value) ?? p.Value?.ToJsonString() ?? "null")))
            : null;

    public static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>True when <paramref name="path"/> or its resolved real path lies under any root.</summary>
    public static bool IsUnderAny(string path, IEnumerable<string> roots)
    {
        var real = PathUtil.RealPath(path);
        return roots.Any(root => PathUtil.IsUnder(path, root) || PathUtil.IsUnder(real, root) || PathUtil.IsUnder(real, PathUtil.RealPath(root)));
    }

    internal static JsonObject ToJson(TomlTable table)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in table)
        {
            obj[key] = ToJson(value);
        }

        return obj;
    }

    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        TomlTable table => ToJson(table),
        TomlTableArray tables => new JsonArray([.. tables.Select(t => (JsonNode)ToJson(t))]),
        TomlArray array => new JsonArray([.. array.Select(ToJson)]),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        long number => JsonValue.Create(number),
        double number => double.IsFinite(number) ? JsonValue.Create(number) : JsonValue.Create(number.ToString(CultureInfo.InvariantCulture)),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static bool TryReadText(string path, out string? text, out string? error)
    {
        text = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                error = "file not found";
                return false;
            }

            if (info.Length > MaxBytes)
            {
                error = "file larger than 4 MiB";
                return false;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
            error = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.GetType().Name + ": " + FirstLine(e.Message);
            return false;
        }
    }

    private static string FirstLine(string text) => text.Split('\n')[0].Trim();

    [GeneratedRegex(@"^\((\d+),(\d+)\)")]
    private static partial Regex TomlPosition();
}
