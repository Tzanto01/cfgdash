using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cfgdash.Core.Edits;
using Cfgdash.Core.Infrastructure;

namespace Cfgdash.Core.Config;

/// <summary>cfgdash's own settings (docs/contracts/cfgdash-config-v1.md).</summary>
public sealed record CfgdashSettings(
    bool StartAtLogin,
    string Theme,
    string? DefaultDir,
    bool Tailnet,
    bool FleetEnabled,
    string AssistantModel,
    string AssistantEffort,
    double AssistantMaxBudgetUsd)
{
    /// <summary>The values a missing file or key reads as.</summary>
    public static CfgdashSettings Defaults { get; } = new(false, "system", null, false, false, "sonnet", "medium", 2.0);

    /// <summary>The nested <c>values</c> shape of the API; <c>defaultDir</c> is an explicit null when unset.</summary>
    public JsonObject ToJson() => new()
    {
        ["startAtLogin"] = StartAtLogin,
        ["theme"] = Theme,
        ["defaultDir"] = DefaultDir,
        ["tailnet"] = Tailnet,
        ["fleet"] = new JsonObject { ["enabled"] = FleetEnabled },
        ["assistant"] = new JsonObject { ["model"] = AssistantModel, ["effort"] = AssistantEffort, ["maxBudgetUsd"] = AssistantMaxBudgetUsd },
    };
}

/// <summary>
/// Reads, validates and writes <c>config.json</c> in <paramref name="configDir"/> (atomic replace, unknown keys kept) and audits
/// each write to <c>&lt;cacheDir&gt;/edits/audit.jsonl</c>. <c>overrides.json</c> is never written.
/// </summary>
public sealed partial class CfgdashConfig(string configDir, string cacheDir, TimeProvider time)
{
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly string[] TopLeaves = ["startAtLogin", "theme", "defaultDir", "tailnet"];
    private static readonly string[] NestedLeaves = ["fleet.enabled", "assistant.model", "assistant.effort", "assistant.maxBudgetUsd"];

    /// <summary>The settings file, <c>&lt;configDir&gt;/config.json</c>.</summary>
    public string FilePath => Path.Combine(configDir, "config.json");

    /// <summary>The hand-edited overrides file, <c>&lt;configDir&gt;/overrides.json</c>.</summary>
    public string OverridesPath => Path.Combine(configDir, "overrides.json");

    /// <summary>The current settings: defaults for a missing or unparseable file, and per key for a wrong type or value or a <c>defaultDir</c> that no longer exists.</summary>
    public CfgdashSettings Read()
    {
        var defaults = CfgdashSettings.Defaults;
        JsonObject? root;
        try
        {
            root = File.Exists(FilePath) ? JsonEdit.Parse(File.ReadAllBytes(FilePath), "\n", out _) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            root = null;
        }

        if (root is null)
        {
            return defaults;
        }

        return new CfgdashSettings(
            TryLeaf(root, "startAtLogin", out var startAtLogin) ? startAtLogin!.GetValue<bool>() : defaults.StartAtLogin,
            TryLeaf(root, "theme", out var theme) ? theme!.GetValue<string>() : defaults.Theme,
            TryLeaf(root, "defaultDir", out var defaultDir) && defaultDir is not null ? PathUtil.ToJson(defaultDir.GetValue<string>()) : defaults.DefaultDir,
            TryLeaf(root, "tailnet", out var tailnet) ? tailnet!.GetValue<bool>() : defaults.Tailnet,
            TryLeaf(root, "fleet.enabled", out var fleet) ? fleet!.GetValue<bool>() : defaults.FleetEnabled,
            TryLeaf(root, "assistant.model", out var model) ? model!.GetValue<string>() : defaults.AssistantModel,
            TryLeaf(root, "assistant.effort", out var effort) ? effort!.GetValue<string>() : defaults.AssistantEffort,
            TryLeaf(root, "assistant.maxBudgetUsd", out var budget) && budget!.AsValue().TryGetValue<double>(out var usd) ? usd : defaults.AssistantMaxBudgetUsd);
    }

    /// <summary>
    /// Null when <paramref name="partial"/> is a valid non-empty partial <c>values</c> object, else the dotted key (null for the body)
    /// and <c>bad-request</c>, <c>unknown-key</c>, <c>bad-type</c> or <c>bad-value</c>.
    /// </summary>
    public (string? Key, string Reason)? Validate(JsonNode? partial)
    {
        try
        {
            if (partial is not JsonObject { Count: > 0 } root)
            {
                return (null, "bad-request");
            }

            foreach (var (name, node) in root)
            {
                if (name is "fleet" or "assistant")
                {
                    if (node is not JsonObject nested)
                    {
                        return (name, "bad-type");
                    }

                    if (nested.Count == 0)
                    {
                        return (name, "bad-request");
                    }

                    foreach (var (child, value) in nested)
                    {
                        var key = name + "." + child;
                        if (!NestedLeaves.Contains(key, StringComparer.Ordinal))
                        {
                            return (key, "unknown-key");
                        }

                        if (LeafError(key, value) is { } nestedError)
                        {
                            return (key, nestedError);
                        }
                    }
                }
                else if (!TopLeaves.Contains(name, StringComparer.Ordinal))
                {
                    return (name, "unknown-key");
                }
                else if (LeafError(name, node) is { } error)
                {
                    return (name, error);
                }
            }

            return null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return (null, "bad-request");
        }
    }

    /// <summary>
    /// Merges a partial that passed <see cref="Validate"/> into the file and audits the write (no values are logged).
    /// Null on success, else <c>unsafe-path</c>, <c>parse-error</c> (the existing file is kept) or <c>write-failed</c>.
    /// </summary>
    /// <summary>Null when <see cref="Write"/> can merge into the file, else <c>unsafe-path</c>, <c>parse-error</c> or <c>write-failed</c>. Nothing is written.</summary>
    public string? CheckWritable()
    {
        if (!IsSafePath(configDir))
        {
            return "unsafe-path";
        }

        try
        {
            return !File.Exists(FilePath) || JsonEdit.Parse(File.ReadAllBytes(FilePath), "\n", out _) is not null ? null : "parse-error";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "write-failed";
        }
    }

    public string? Write(JsonObject partial, string listener)
    {
        if (!IsSafePath(configDir))
        {
            return "unsafe-path";
        }

        string? temp = null;
        string? sha256Before;
        byte[] after;
        var keys = new List<string>();
        try
        {
            var raw = File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;
            var newLine = raw is not null && raw.AsSpan().IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
            var root = raw is null ? new JsonObject() : JsonEdit.Parse(raw, newLine, out _);
            if (root is null)
            {
                return "parse-error";
            }

            foreach (var (name, node) in partial)
            {
                if (name is "fleet" or "assistant")
                {
                    if (root[name] is not JsonObject)
                    {
                        root[name] = new JsonObject();
                    }

                    foreach (var (child, value) in node!.AsObject())
                    {
                        Set(root, [name, child], value, keys);
                    }
                }
                else
                {
                    Set(root, [name], name == "defaultDir" && node is not null ? PathUtil.ToJson(node.GetValue<string>()) : node, keys);
                }
            }

            sha256Before = raw is null ? null : Convert.ToHexStringLower(SHA256.HashData(raw));
            after = Encoding.UTF8.GetBytes(JsonEdit.Serialize(root, newLine));
            if (!Directory.Exists(configDir))
            {
                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(configDir);
                }
                else
                {
                    Directory.CreateDirectory(configDir, PrivateDirectoryMode);
                }
            }

            temp = Path.Combine(configDir, $".config.json.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.cfgdash.tmp");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFileMode;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(after);
                stream.Flush(true);
            }

            File.Move(temp, FilePath, overwrite: true);
            temp = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "write-failed";
        }
        finally
        {
            if (temp is not null)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        var now = time.GetUtcNow();
        var entry = new JsonObject
        {
            ["auditId"] = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)),
            ["at"] = IsoTime.Format(now),
            ["listener"] = listener,
            ["kind"] = "cfgdash-settings",
            ["files"] = new JsonArray(new JsonObject
            {
                ["path"] = PathUtil.ToJson(FilePath),
                ["sha256Before"] = sha256Before,
                ["sha256After"] = Convert.ToHexStringLower(SHA256.HashData(after)),
            }),
            ["changes"] = new JsonArray([.. keys.Select(k => (JsonNode)new JsonObject { ["key"] = k, ["scope"] = "cfgdash", ["action"] = "set" })]),
        };
        try
        {
            EditService.AppendAudit(cacheDir, entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static void Set(JsonObject root, string[] segments, JsonNode? value, List<string> keys)
    {
        JsonEdit.Set(root, segments, value);
        keys.Add(string.Join('.', segments));
    }

    private static bool TryLeaf(JsonObject root, string key, out JsonNode? value)
    {
        value = null;
        JsonNode? node = root;
        foreach (var segment in key.Split('.'))
        {
            if (node is not JsonObject parent || !parent.TryGetPropertyValue(segment, out node))
            {
                return false;
            }
        }

        value = node;
        return LeafError(key, node) is null;
    }

    private static string? LeafError(string key, JsonNode? value)
    {
        var kind = value?.GetValueKind() ?? JsonValueKind.Null;
        switch (key)
        {
            case "startAtLogin" or "tailnet" or "fleet.enabled":
                return kind is JsonValueKind.True or JsonValueKind.False ? null : "bad-type";
            case "theme":
                return kind != JsonValueKind.String ? "bad-type" : value!.GetValue<string>() is "system" or "light" or "dark" ? null : "bad-value";
            case "assistant.effort":
                return kind != JsonValueKind.String ? "bad-type" : value!.GetValue<string>() is "low" or "medium" or "high" or "xhigh" or "max" ? null : "bad-value";
            case "assistant.model":
                return kind != JsonValueKind.String ? "bad-type" : ModelPattern().IsMatch(value!.GetValue<string>()) ? null : "bad-value";
            case "assistant.maxBudgetUsd":
                return kind != JsonValueKind.Number ? "bad-type"
                    : value!.AsValue().TryGetValue<double>(out var usd) && double.IsFinite(usd) && usd is >= 0.1 and <= 50 ? null : "bad-value";
            case "defaultDir":
                return kind == JsonValueKind.Null ? null
                    : kind != JsonValueKind.String ? "bad-type"
                    : IsSafePath(value!.GetValue<string>()) && Directory.Exists(value!.GetValue<string>()) ? null : "bad-value";
            default:
                return "unknown-key";
        }
    }

    /// <summary>Non-empty, fully qualified and, on Windows, not UNC or device; checked before any filesystem call.</summary>
    private static bool IsSafePath(string path) =>
        path.Length > 0
        && !(OperatingSystem.IsWindows() && path[0] is '\\' or '/')
        && Path.IsPathFullyQualified(path);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\[\]/@-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ModelPattern();
}
