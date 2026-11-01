using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;
using Microsoft.Data.Sqlite;

namespace Cfgdash.Core.Collectors;

/// <summary>
/// <c>t3.settings</c>: T3 Code server runtime facts, <c>settings.json</c>, providers, models and projects from <c>~/.t3/userdata</c>.
/// Reads only <c>settings.json</c>, the listed keys of <c>server-runtime.json</c> and the <c>projection_projects</c> table of <c>statev2.sqlite</c>
/// (read-only, parameterized, opened <c>immutable</c> when SQLite would otherwise create <c>-wal</c>/<c>-shm</c> side files).
/// Nothing else in the userdata dir is enumerated, opened or registered.
/// </summary>
internal sealed class T3SettingsCollector : ICollector
{
    public const string Id = "t3.settings";
    public const int MaxRows = 500;

    private const string StateFile = "statev2.sqlite";
    private const string ProjectsTable = "projection_projects";

    private static readonly string[] NeverOpenedNames = ["secrets", "clerk-tokens.json", "anonymous-id", "environment-id", "logs", "attachments"];
    private static readonly string[] NeverOpenedPrefixes = ["connection-catalog", "server-owner.sqlite"];
    private static readonly string[] RuntimeKeys = ["serverVersion", "port", "origin", "startedAt", "serviceManaged", "pid"];
    private static readonly string[] RequiredColumns = ["project_id", "title", "workspace_root"];
    private static readonly HashSet<string> TableKeys = new(StringComparer.Ordinal) { "providerInstances", "favorites", "providerModelPreferences" };

    private static readonly IReadOnlyList<TableColumn> ProviderColumns =
    [
        new("instance", "Instance"), new("driver", "Driver"), new("displayName", "Display name"), new("enabled", "Enabled"),
        new("config", "Config"), new("hiddenModels", "Hidden models"),
    ];

    private static readonly IReadOnlyList<TableColumn> ModelColumns = [new("provider", "Provider"), new("model", "Model"), new("status", "Status")];

    private static readonly IReadOnlyList<TableColumn> ProjectColumns =
    [
        new("project", "Project"), new("path", "Path"), new("defaultModel", "Default model"), new("envMode", "Env mode"), new("autoPull", "Auto pull"),
        new("scripts", "Scripts"), new("pinned", "Pinned"), new("archived", "Archived"), new("updated", "Updated"),
    ];

    public CollectorManifest Manifest { get; } = new(Id, "T3 Code", "1.0.0", "t3", new Requirements());

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var dir = Path.Combine(context.Environment.HomeDir, ".t3", "userdata");
        if (PathUtil.IsNetworkPath(dir))
        {
            return CollectorResult.NotPresent("the T3 userdata dir (~/.t3/userdata) is a network path");
        }

        if (!Directory.Exists(dir))
        {
            return CollectorResult.NotPresent("no T3 userdata dir (~/.t3/userdata)");
        }

        var redactor = context.Redactor;
        var root = PathUtil.RealPath(dir);
        var notes = new List<string>();
        var blocks = new List<Block>();

        bool CanOpen(string path)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            if (Allowed(path, root))
            {
                return true;
            }

            notes.Add(redactor.RedactText($"{Path.GetFileName(path)}: skipped, it resolves outside the T3 userdata dir or onto a file cfgdash never opens"));
            return false;
        }

        var runtimePath = Path.Combine(dir, "server-runtime.json");
        if (CanOpen(runtimePath))
        {
            if (ToolConfig.TryReadJsonc(runtimePath, out var runtime, out var error))
            {
                var source = new SourceRef("server-runtime.json");
                blocks.Add(new KvBlock("runtime", "T3 server",
                    [.. RuntimeKeys.Select(key => new KvRow(key == "serverVersion" ? "version" : key, redactor.RedactJson(runtime![key]), source))]));
            }
            else
            {
                notes.Add(redactor.RedactText($"{PathUtil.ToJson(runtimePath)}: {error}"));
            }
        }

        var settingsPath = Path.Combine(dir, "settings.json");
        JsonObject? settings = null;
        string? settingsId = null;
        if (CanOpen(settingsPath))
        {
            settingsId = context.Files.Register(settingsPath, "settings")?.Id;
            if (!ToolConfig.TryReadJsonc(settingsPath, out settings, out var error))
            {
                notes.Add(redactor.RedactText($"{PathUtil.ToJson(settingsPath)}: {error}"));
            }
        }

        if (settings is not null)
        {
            blocks.Add(new KvBlock("settings", "Settings", [.. ToolConfig.Flatten(settings, redactor, new SourceRef("settings.json", settingsId), TableKeys)]));
            blocks.Add(new TableBlock("providers", "Providers", ProviderColumns, Cap(Providers(settings, redactor, settingsId), "providers", notes)));
            blocks.Add(new TableBlock("models", "Models", ModelColumns, Cap(Models(settings, redactor, settingsId), "models", notes)));
        }

        var dbPath = Path.Combine(dir, StateFile);
        if (!File.Exists(dbPath))
        {
            notes.Add($"no {StateFile}: projects omitted");
        }
        else if (CanOpen(dbPath) && new[] { dbPath + "-wal", dbPath + "-shm" }.All(side => !File.Exists(side) || CanOpen(side))
                 && await ProjectsAsync(dbPath, context, notes, cancellationToken) is { } projects)
        {
            blocks.Add(projects);
        }

        return new CollectorResult(blocks, Notes: notes.Count > 0 ? notes : null);
    }

    private static bool Allowed(string path, string root)
    {
        var real = PathUtil.RealPath(path);
        if (FileRegistry.IsNeverRead(path) || PathUtil.PathEquals(real, root) || !PathUtil.IsUnder(real, root))
        {
            return false;
        }

        return !Path.GetRelativePath(root, real)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => NeverOpenedNames.Any(n => segment.Equals(n, StringComparison.OrdinalIgnoreCase))
                || NeverOpenedPrefixes.Any(p => segment.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<TableRow> Cap(List<TableRow> rows, string table, List<string> notes)
    {
        if (rows.Count > MaxRows)
        {
            notes.Add($"showing the first {MaxRows} of {rows.Count} {table}");
            rows.RemoveRange(MaxRows, rows.Count - MaxRows);
        }

        return rows;
    }

    private static List<TableRow> Providers(JsonObject settings, IRedactor redactor, string? fileId)
    {
        var preferences = settings["providerModelPreferences"] as JsonObject;
        var rows = new List<TableRow>();
        JsonNode? Str(JsonNode? node) => ToolConfig.Text(node) is { } text ? redactor.RedactText(text) : null;
        foreach (var (key, node) in settings["providerInstances"] as JsonObject ?? new JsonObject())
        {
            var entry = node as JsonObject;
            var id = redactor.RedactText(key);
            var enabled = entry?["enabled"] is JsonValue flag && flag.TryGetValue<bool>(out var on) ? on : (bool?)null;
            var config = ToolConfig.Pairs(redactor.RedactJson(entry?["config"]));
            var hidden = (preferences?[key] as JsonObject)?["hiddenModels"] is JsonArray models ? models.Count : (int?)null;
            rows.Add(new TableRow(id, new Dictionary<string, JsonNode?>
            {
                ["instance"] = id,
                ["driver"] = Str(entry?["driver"]),
                ["displayName"] = Str(entry?["displayName"]),
                ["enabled"] = enabled,
                ["config"] = config is null ? null : redactor.RedactText(config),
                ["hiddenModels"] = hidden,
            }, FileId: fileId));
        }

        return rows;
    }

    private static List<TableRow> Models(JsonObject settings, IRedactor redactor, string? fileId)
    {
        var rows = new List<TableRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? provider, string? model, string status)
        {
            if (model is null)
            {
                return;
            }

            var id = redactor.RedactText($"{status}:{provider}:{model}");
            if (seen.Add(id))
            {
                rows.Add(new TableRow(id, new Dictionary<string, JsonNode?>
                {
                    ["provider"] = provider is null ? null : redactor.RedactText(provider),
                    ["model"] = redactor.RedactText(model),
                    ["status"] = status,
                }, FileId: fileId));
            }
        }

        foreach (var favorite in settings["favorites"] as JsonArray ?? new JsonArray())
        {
            if (favorite is JsonObject entry)
            {
                Add(ToolConfig.Text(entry["provider"]), ToolConfig.Text(entry["model"]), "favorite");
            }
        }

        foreach (var (provider, preference) in settings["providerModelPreferences"] as JsonObject ?? new JsonObject())
        {
            foreach (var model in (preference as JsonObject)?["hiddenModels"] as JsonArray ?? new JsonArray())
            {
                Add(provider, ToolConfig.Text(model), "hidden");
            }
        }

        return rows;
    }

    private static async Task<TableBlock?> ProjectsAsync(string path, CollectContext context, List<string> notes, CancellationToken ct)
    {
        var redactor = context.Redactor;
        try
        {
            var header = new byte[20];
            int read;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
            }

            var immutable = read == header.Length && header[18] == 2 && header[19] == 2 && !(File.Exists(path + "-wal") && File.Exists(path + "-shm"));
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = immutable ? new Uri(path).AbsoluteUri + "?immutable=1" : path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5,
            }.ToString());
            await db.OpenAsync(ct);

            var columns = await ColumnsAsync(db, ct);
            if (!RequiredColumns.All(columns.Contains))
            {
                notes.Add("unsupported T3 state schema: projects omitted");
                return null;
            }

            string Or(string column) => columns.Contains(column) ? column : "NULL";
            using var command = db.CreateCommand();
            command.CommandText =
                $"SELECT project_id, title, workspace_root, {Or("default_model_selection_json")}, {Or("default_thread_env_mode")}, {Or("auto_pull")}, " +
                $"{Or("scripts_json")}, {Or("pinned_at")}, {Or("archived_at")}, {Or("updated_at")} FROM {ProjectsTable} " +
                $"WHERE {(columns.Contains("deleted_at") ? "deleted_at IS NULL" : "1")} " +
                $"ORDER BY {(columns.Contains("updated_at") ? "updated_at" : "title")} DESC LIMIT {MaxRows + 1}";
            await using var reader = await command.ExecuteReaderAsync(ct);

            string? S(int ordinal) => reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
            JsonNode? R(string? value) => value is null ? null : redactor.RedactText(value);
            bool? Set(string column, int ordinal) => columns.Contains(column) ? !reader.IsDBNull(ordinal) : null;

            var rows = new List<TableRow>();
            while (await reader.ReadAsync(ct))
            {
                if (rows.Count == MaxRows)
                {
                    notes.Add($"showing the {MaxRows} most recently updated projects");
                    break;
                }

                var workspace = S(2);
                var rooted = workspace is not null && workspace.IndexOf('\0') < 0 && Path.IsPathRooted(workspace);
                IReadOnlyList<string>? flags = rooted && context.Environment.CompanyPrefixes.Any(prefix => PathUtil.IsUnder(workspace!, prefix)) ? ["company"] : null;
                rows.Add(new TableRow(redactor.RedactText(S(0) ?? string.Empty), new Dictionary<string, JsonNode?>
                {
                    ["project"] = R(S(1)),
                    ["path"] = R(rooted ? PathUtil.ToJson(workspace!) : workspace),
                    ["defaultModel"] = R(DefaultModel(S(3))),
                    ["envMode"] = R(S(4)),
                    ["autoPull"] = reader.IsDBNull(5) ? (bool?)null : reader.GetInt64(5) != 0,
                    ["scripts"] = ParseJson(S(6)) is JsonArray scripts ? scripts.Count : (int?)null,
                    ["pinned"] = Set("pinned_at", 7),
                    ["archived"] = Set("archived_at", 8),
                    ["updated"] = R(S(9)),
                }, flags));
            }

            return new TableBlock("projects", "Projects", ProjectColumns, rows);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            notes.Add(redactor.RedactText($"{StateFile} is unreadable: {e.Message.Split('\n')[0].Trim()}"));
            return null;
        }
    }

    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnection db, CancellationToken ct)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        command.Parameters.AddWithValue("$table", ProjectsTable);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string? DefaultModel(string? json)
    {
        if (ParseJson(json) is not JsonObject selection || ToolConfig.Text(selection["model"]) is not { } model)
        {
            return null;
        }

        return (ToolConfig.Text(selection["provider"]) ?? ToolConfig.Text(selection["instanceId"])) is { } provider ? provider + "/" + model : model;
    }

    private static JsonNode? ParseJson(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
