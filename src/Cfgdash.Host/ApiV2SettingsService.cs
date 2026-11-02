using System.Text.Json.Nodes;
using Cfgdash.Core.Config;

namespace Cfgdash.Host;

/// <summary>GET and PUT of cfgdash's own settings (cfgdash-config-v1); the OS start-at-login entry changes before the file.</summary>
internal sealed class CfgdashSettingsService(CfgdashConfig config, IAutostart autostart)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>The GET shape: <c>values</c>, <c>startAtLogin</c> (configured, actual, detail), <c>paths</c> and <c>configError</c> when config.json exists but cannot be read or parsed.</summary>
    public async Task<JsonObject> GetAsync(CancellationToken ct)
    {
        var settings = config.Read();
        var state = await autostart.GetAsync(ct);
        var startAtLogin = new JsonObject { ["configured"] = settings.StartAtLogin, ["actual"] = state.Actual };
        if (state.Detail is not null)
        {
            startAtLogin["detail"] = state.Detail;
        }

        var body = new JsonObject
        {
            ["values"] = settings.ToJson(),
            ["startAtLogin"] = startAtLogin,
            ["paths"] = new JsonObject { ["config"] = Slash(config.FilePath), ["overrides"] = Slash(config.OverridesPath) },
        };
        switch (config.CheckWritable())
        {
            case "parse-error":
                body["configError"] = "config.json is not valid JSON";
                break;
            case "write-failed":
                body["configError"] = "config.json cannot be read";
                break;
        }

        return body;
    }

    /// <summary>Validates and applies a partial <c>values</c> object; returns the HTTP status and body.</summary>
    public async Task<(int Status, JsonObject Body)> UpdateAsync(JsonNode? partial, string listener, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (config.CheckWritable() is { } blocked)
            {
                return blocked == "unsafe-path" ? Failure(blocked) : Failure("parse-error");
            }

            if (config.Validate(partial) is { } invalid)
            {
                return (400, InvalidSetting(invalid.Key, invalid.Reason));
            }

            var values = (JsonObject)partial!;

            if (values["startAtLogin"] is JsonValue value && value.TryGetValue<bool>(out var on)
                && await autostart.SetAsync(on, ct) is { } reason)
            {
                return (500, new JsonObject { ["error"] = "autostart-failed", ["reason"] = reason });
            }

            return config.Write(values, listener) is { } failure ? Failure(failure) : (200, await GetAsync(ct));
        }
        finally
        {
            gate.Release();
        }
    }

    private static (int Status, JsonObject Body) Failure(string reason) =>
        reason == "parse-error" ? (409, new JsonObject { ["error"] = "config-unreadable" }) : (500, new JsonObject { ["error"] = "write-failed", ["reason"] = reason });

    internal static JsonObject InvalidSetting(string? key, string reason) =>
        new() { ["error"] = "invalid-setting", ["key"] = key, ["reason"] = reason };

    private static string Slash(string path) => Path.DirectorySeparatorChar == '\\' ? path.Replace('\\', '/') : path;
}
