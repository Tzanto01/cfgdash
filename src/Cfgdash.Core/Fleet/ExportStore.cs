using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Cfgdash.Core.Fleet;

/// <summary>One export file in the exports folder (fleet-v1 Phase A).</summary>
public sealed record ExportInfo(string Id, string? Machine, DateTimeOffset? CollectedAt, string? Dir);

/// <summary>Lists and loads the snapshot exports written by <c>cfgdash export</c>. Only regular <c>*.json</c> files directly in the folder count.</summary>
public static class ExportStore
{
    /// <summary>Largest export file that is read.</summary>
    public const long MaxBytes = 16 * 1024 * 1024;

    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant);

    /// <summary>True when <paramref name="id"/> matches <c>^[A-Za-z0-9._-]+$</c>.</summary>
    public static bool IsValidId(string? id) => !string.IsNullOrEmpty(id) && IdPattern.IsMatch(id);

    /// <summary>The exports in <paramref name="exportsDir"/>, newest first; unreadable, oversized or malformed files are skipped.</summary>
    public static IReadOnlyList<ExportInfo> List(string exportsDir)
    {
        var exports = new List<ExportInfo>();
        foreach (var (id, file) in Enumerate(exportsDir))
        {
            if (file.Length > MaxBytes)
            {
                continue;
            }

            try
            {
                using var stream = file.OpenRead();
                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var machine = Property(document.RootElement, "machine");
                var collectedAt = Text(machine, "collectedAt") is { } text
                    && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : (DateTimeOffset?)null;
                exports.Add(new ExportInfo(id, Text(machine, "hostname"), collectedAt, Text(Property(document.RootElement, "context"), "targetDir")));
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return
        [
            .. exports
                .OrderBy(e => e.CollectedAt is null)
                .ThenByDescending(e => e.CollectedAt)
                .ThenByDescending(e => e.Id, StringComparer.Ordinal),
        ];
    }

    /// <summary>The export with <paramref name="id"/>, or null when the id is invalid or no such file exists. Throws <see cref="InvalidDataException"/> when the file is too large or not a snapshot.</summary>
    public static JsonObject? Load(string exportsDir, string id)
    {
        if (!IsValidId(id) || Enumerate(exportsDir).FirstOrDefault(e => e.Id == id).File is not { } file)
        {
            return null;
        }

        if (file.Length > MaxBytes)
        {
            throw new InvalidDataException($"export {id} is larger than {MaxBytes} bytes");
        }

        JsonNode? node;
        try
        {
            using var stream = file.OpenRead();
            node = JsonNode.Parse(stream, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"export {id} is not valid JSON", e);
        }

        if (node is not JsonObject snapshot || snapshot["machine"] is not JsonObject || snapshot["sections"] is not JsonArray)
        {
            throw new InvalidDataException($"export {id} is not a cfgdash snapshot");
        }

        return snapshot;
    }

    private static IEnumerable<(string Id, FileInfo File)> Enumerate(string exportsDir)
    {
        var dir = new DirectoryInfo(exportsDir);
        if (!dir.Exists)
        {
            yield break;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in dir.EnumerateFiles("*.json", options))
        {
            if (file.LinkTarget is null && file.Name.EndsWith(".json", StringComparison.Ordinal) && file.Name[..^".json".Length] is var id && IsValidId(id))
            {
                yield return (id, file);
            }
        }
    }

    private static JsonElement? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value) ? value : null;

    private static string? Text(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
