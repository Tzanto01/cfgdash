using System.Text.Json;

namespace Cfgdash.Host;

/// <summary>Reads <c>fleet.enabled</c> from cfgdash's <c>config.json</c> (cfgdash-config-v1). Never writes.</summary>
internal static class FleetConfig
{
    /// <summary>True only when the root object has an object <c>fleet</c> whose <c>enabled</c> is JSON <c>true</c>; a missing, unreadable or invalid file is false.</summary>
    public static bool Enabled(string configFile)
    {
        try
        {
            using var stream = new FileStream(configFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("fleet", out var fleet)
                && fleet.ValueKind == JsonValueKind.Object
                && fleet.TryGetProperty("enabled", out var enabled)
                && enabled.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
