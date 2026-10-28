using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;
using Microsoft.Data.Sqlite;

namespace Cfgdash.Core.Collectors;

/// <summary>
/// <c>engram.memory</c>: projects, scopes, types, topic keys, counts and review dates from <c>&lt;engramDataDir&gt;/engram.db</c>, with an observation's content as a detail.
/// The database is opened with <c>Mode=ReadOnly</c>, no pooling and parameterized queries only; the schema is checked at runtime through <c>pragma_table_info</c>.
/// Soft-deleted observations are excluded. Projects whose name starts with <c>company</c> appear only on a machine with a company layer, otherwise they are excluded everywhere and counted in a note.
/// </summary>
internal sealed class EngramMemoryCollector : ICollector, IDetailProvider
{
    public const string Id = "engram.memory";
    public const string DatabaseFile = "engram.db";
    public const int MaxRows = 500;

    private const string IsCompany = "(ltrim(lower(coalesce(project, '')), char(32, 9, 10, 11, 12, 13)) LIKE 'company%')";
    private static readonly string[] CoreColumns = ["id", "type", "title", "content", "project", "created_at"];

    public CollectorManifest Manifest { get; } = new(
        Id, "Engram memory", "1.0.0", "memory",
        new Requirements(Paths: [PathKeys.EngramDataDir]));

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var (db, schema, reason) = await OpenAsync(context.Environment, cancellationToken);
        if (db is null || schema is null)
        {
            return CollectorResult.Unavailable(context.Redactor.RedactText(reason!));
        }

        await using (db)
        {
            return await CollectAsync(db, schema, context, cancellationToken);
        }
    }

    public async Task<DetailContent?> GetDetailAsync(string key, CollectContext context, CancellationToken cancellationToken)
    {
        if (!long.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || context.Environment.GetPath(PathKeys.EngramDataDir) is null)
        {
            return null;
        }

        var (db, schema, _) = await OpenAsync(context.Environment, cancellationToken);
        if (db is null || schema is null)
        {
            return null;
        }

        await using (db)
        {
            var o = schema.Observations;
            using var command = db.CreateCommand();
            command.CommandText =
                $"SELECT title, content, type, project, created_at, {o.Or("updated_at")}, {o.Or("scope")}, {o.Or("topic_key")}, {o.Or("review_after")}, {o.Or("revision_count")} " +
                $"FROM observations WHERE id = $id AND {Where(schema, context.Environment)}";
            command.Parameters.AddWithValue("$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var title = Str(reader, 0) ?? string.Empty;
            var markdown = new StringBuilder();
            void Field(string label, string? value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    markdown.Append("- **").Append(label).Append(":** ").Append(value).Append('\n');
                }
            }

            Field("ID", id.ToString(CultureInfo.InvariantCulture));
            Field("Project", Str(reader, 3));
            Field("Scope", Str(reader, 6));
            Field("Type", Str(reader, 2));
            Field("Topic key", Str(reader, 7));
            Field("Created", Time(Str(reader, 4)));
            Field("Updated", Time(Str(reader, 5)));
            Field("Review after", Time(Str(reader, 8)));
            Field("Revisions", Str(reader, 9));
            markdown.Append("\n---\n\n").Append(Str(reader, 1));
            return new DetailContent(title, "markdown", markdown.ToString());
        }
    }

    private static async Task<CollectorResult> CollectAsync(SqliteConnection db, Schema schema, CollectContext context, CancellationToken ct)
    {
        var redactor = context.Redactor;
        var showCompany = ShowCompany(context.Environment);
        var where = Where(schema, context.Environment);
        var o = schema.Observations;
        var activity = o.Has("updated_at") ? "coalesce(updated_at, created_at)" : "created_at";
        JsonNode? Text(string? value) => value is null ? null : redactor.RedactText(value);
        IReadOnlyList<string>? CompanyFlag(string? project) => IsCompanyProject(project) ? ["company"] : null;

        var observations = await CountAsync(db, "observations", o.Has("deleted_at") ? "deleted_at IS NULL" : "1", showCompany, ct);
        (long Visible, long Company)? sessions = schema.Sessions.Has("project") ? await CountAsync(db, "sessions", "1", showCompany, ct) : null;
        (long Visible, long Company)? prompts = schema.Prompts.Has("project") ? await CountAsync(db, "user_prompts", "1", showCompany, ct) : null;

        var file = Path.Combine(DataDir(context.Environment), DatabaseFile);
        var lastWrite = new[] { file, file + "-wal" }.Where(File.Exists).Max(File.GetLastWriteTimeUtc);
        var source = new SourceRef(DatabaseFile);
        var summary = new KvBlock("summary", "Engram database",
        [
            new("dbSizeBytes", new FileInfo(file).Length, source),
            new("lastWrite", IsoTime.Format(new DateTimeOffset(lastWrite)), source),
            new("userVersion", await ScalarAsync(db, "PRAGMA user_version", ct), source),
            new("observations", observations.Visible, source),
            new("sessions", sessions?.Visible, source),
            new("prompts", prompts?.Visible, source),
        ]);

        var projects = new Dictionary<string, ProjectRow>(StringComparer.Ordinal);
        ProjectRow Project(string? name) => projects.TryGetValue(name ?? string.Empty, out var row) ? row : projects[name ?? string.Empty] = new ProjectRow(name);
        var scopes = o.Has("scope") ? "sum(scope = 'project'), sum(scope = 'personal'), sum(scope = 'global')" : "NULL, NULL, NULL";
        await foreach (var r in QueryAsync(db, $"SELECT project, count(*), {scopes}, max({activity}) FROM observations WHERE {where} GROUP BY project", ct))
        {
            var row = Project(Str(r, 0));
            row.Observations = r.GetInt64(1);
            (row.ScopeProject, row.ScopePersonal, row.ScopeGlobal) = (Long(r, 2), Long(r, 3), Long(r, 4));
            row.LastActivity = Latest(row.LastActivity, Str(r, 5));
        }

        if (sessions is not null)
        {
            var sessionWhere = showCompany ? "1" : "NOT " + IsCompany;
            await foreach (var r in QueryAsync(db, $"SELECT project, count(*), {schema.Sessions.Or("started_at")} FROM sessions WHERE {sessionWhere} GROUP BY project", ct))
            {
                var row = Project(Str(r, 0));
                row.Sessions = r.GetInt64(1);
                row.LastActivity = Latest(row.LastActivity, Str(r, 2));
            }
        }

        var projectRows = projects.Values
            .OrderByDescending(p => p.LastActivity, StringComparer.Ordinal)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => new TableRow(p.Name ?? "(none)", new Dictionary<string, JsonNode?>
            {
                ["project"] = Text(p.Name ?? "(none)"),
                ["observations"] = p.Observations,
                ["sessions"] = p.Sessions,
                ["lastActivity"] = Time(p.LastActivity),
                ["project-scope"] = p.ScopeProject,
                ["personal-scope"] = p.ScopePersonal,
                ["global-scope"] = p.ScopeGlobal,
            }, CompanyFlag(p.Name)))
            .ToList();

        var observationRows = new List<TableRow>();
        await foreach (var r in QueryAsync(db,
            $"SELECT id, created_at, {o.Or("updated_at")}, project, {o.Or("scope")}, type, title, {o.Or("topic_key")}, {o.Or("review_after")} " +
            $"FROM observations WHERE {where} ORDER BY {activity} DESC, id DESC LIMIT {MaxRows}", ct))
        {
            var id = r.GetInt64(0).ToString(CultureInfo.InvariantCulture);
            var cells = new Dictionary<string, JsonNode?>
            {
                ["id"] = r.GetInt64(0),
                ["created"] = Time(Str(r, 1)),
                ["updated"] = Time(Str(r, 2)),
                ["project"] = Text(Str(r, 3)),
                ["scope"] = Text(Str(r, 4)),
                ["type"] = Text(Str(r, 5)),
                ["title"] = Text(Str(r, 6)),
                ["topicKey"] = Text(Str(r, 7)),
            };
            if (o.Has("review_after"))
            {
                cells["reviewAfter"] = Time(Str(r, 8));
            }

            observationRows.Add(new TableRow(id, cells, CompanyFlag(Str(r, 3)), DetailId: $"{Id}:{id}"));
        }

        List<TableColumn> observationColumns =
        [
            new("id", "ID"), new("updated", "Updated"), new("created", "Created"), new("project", "Project"), new("scope", "Scope"),
            new("type", "Type"), new("title", "Title"), new("topicKey", "Topic key"),
        ];
        if (o.Has("review_after"))
        {
            observationColumns.Add(new("reviewAfter", "Review after"));
        }

        var blocks = new List<Block>
        {
            summary,
            new TableBlock("projects", "Projects",
            [
                new("project", "Project"), new("observations", "Observations"), new("sessions", "Sessions"), new("lastActivity", "Last activity"),
                new("project-scope", "Project scope"), new("personal-scope", "Personal scope"), new("global-scope", "Global scope"),
            ], projectRows),
            new TableBlock("observations", "Observations", observationColumns, observationRows, "Newest first. Open a row for its content."),
        };

        var notes = new List<string>();
        if (observations.Visible > MaxRows)
        {
            notes.Add($"showing the newest {MaxRows} of {observations.Visible} observations");
        }

        if (o.Has("topic_key"))
        {
            var topicRows = new List<TableRow>();
            await foreach (var r in QueryAsync(db,
                $"SELECT topic_key, project, count(*), max({activity}) FROM observations WHERE {where} AND topic_key IS NOT NULL AND topic_key <> '' " +
                $"GROUP BY topic_key, project ORDER BY 4 DESC, 1 LIMIT {MaxRows + 1}", ct))
            {
                topicRows.Add(new TableRow($"{Str(r, 1)}|{Str(r, 0)}", new Dictionary<string, JsonNode?>
                {
                    ["topicKey"] = Text(Str(r, 0)),
                    ["project"] = Text(Str(r, 1)),
                    ["observations"] = r.GetInt64(2),
                    ["lastActivity"] = Time(Str(r, 3)),
                }, CompanyFlag(Str(r, 1))));
            }

            if (topicRows.Count > MaxRows)
            {
                topicRows.RemoveAt(MaxRows);
                notes.Add($"showing the {MaxRows} most recent topic keys");
            }

            blocks.Add(new TableBlock("topics", "Topic keys",
                [new("topicKey", "Topic key"), new("project", "Project"), new("observations", "Observations"), new("lastActivity", "Last activity")], topicRows));
        }

        var hidden = (Observations: observations.Company, Sessions: sessions?.Company ?? 0, Prompts: prompts?.Company ?? 0);
        if (!showCompany && hidden != (0, 0, 0))
        {
            notes.Add($"{hidden.Observations} company observations, {hidden.Sessions} sessions and {hidden.Prompts} prompts hidden: this machine has no company layer");
        }

        return new CollectorResult(blocks, Notes: notes.Count > 0 ? notes : null);
    }

    private static bool ShowCompany(MachineEnvironment environment) => environment.CompanyPrefixes.Count > 0;

    private static bool IsCompanyProject(string? project) =>
        project is not null && project.TrimStart(' ', '\t', '\n', '\v', '\f', '\r').StartsWith("company", StringComparison.OrdinalIgnoreCase);

    private static string DataDir(MachineEnvironment environment) => PathUtil.Full(environment.GetPath(PathKeys.EngramDataDir)!);

    /// <summary>The observation filter: not soft-deleted and, without a company layer, not in a company project.</summary>
    private static string Where(Schema schema, MachineEnvironment environment) =>
        (schema.Observations.Has("deleted_at") ? "deleted_at IS NULL" : "1") + (ShowCompany(environment) ? string.Empty : " AND NOT " + IsCompany);

    private static async Task<(SqliteConnection? Db, Schema? Schema, string? Reason)> OpenAsync(MachineEnvironment environment, CancellationToken ct)
    {
        var path = Path.Combine(DataDir(environment), DatabaseFile);
        if (PathUtil.IsNetworkPath(path))
        {
            return (null, null, "the engram data dir is a network path");
        }

        if (!File.Exists(path))
        {
            return (null, null, $"no {DatabaseFile} in the engram data dir");
        }

        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        try
        {
            await db.OpenAsync(ct);
            var schema = new Schema(await ColumnsAsync(db, "observations", ct), await ColumnsAsync(db, "sessions", ct), await ColumnsAsync(db, "user_prompts", ct));
            if (CoreColumns.All(schema.Observations.Has))
            {
                return (db, schema, null);
            }

            await db.DisposeAsync();
            return (null, null, "unsupported engram schema");
        }
        catch (SqliteException e)
        {
            await db.DisposeAsync();
            return (null, null, $"{DatabaseFile} is unreadable: {e.Message.Split('\n')[0].Trim()}");
        }
    }

    private static async Task<Columns> ColumnsAsync(SqliteConnection db, string table, CancellationToken ct)
    {
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table)";
        command.Parameters.AddWithValue("$table", table);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return new Columns(names);
    }

    private static async Task<(long Visible, long Company)> CountAsync(SqliteConnection db, string table, string filter, bool showCompany, CancellationToken ct)
    {
        await foreach (var r in QueryAsync(db, $"SELECT count(*), coalesce(sum({IsCompany}), 0) FROM {table} WHERE {filter}", ct))
        {
            var (total, company) = (r.GetInt64(0), r.GetInt64(1));
            return showCompany ? (total, 0) : (total - company, company);
        }

        return (0, 0);
    }

    private static async Task<long?> ScalarAsync(SqliteConnection db, string sql, CancellationToken ct)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(ct) is long value ? value : null;
    }

    private static async IAsyncEnumerable<SqliteDataReader> QueryAsync(SqliteConnection db, string sql, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            yield return reader;
        }
    }

    private static string? Str(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private static long? Long(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? Latest(string? a, string? b) => string.CompareOrdinal(a, b) >= 0 ? a : b;

    /// <summary>Engram stores UTC <c>yyyy-MM-dd HH:mm:ss</c>; returned as ISO 8601 UTC, other values unchanged.</summary>
    internal static string? Time(string? value) =>
        DateTime.TryParseExact(value, ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ssK"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : value;

    private sealed record Schema(Columns Observations, Columns Sessions, Columns Prompts);

    private sealed record Columns(HashSet<string> Names)
    {
        public bool Has(string column) => Names.Contains(column);

        /// <summary>The column when present, else <c>NULL</c>; only ever called with fixed column names.</summary>
        public string Or(string column) => Has(column) ? column : "NULL";
    }

    private sealed class ProjectRow(string? name)
    {
        public string? Name { get; } = name;

        public long? Observations { get; set; }

        public long? Sessions { get; set; }

        public long? ScopeProject { get; set; }

        public long? ScopePersonal { get; set; }

        public long? ScopeGlobal { get; set; }

        public string? LastActivity { get; set; }
    }
}
