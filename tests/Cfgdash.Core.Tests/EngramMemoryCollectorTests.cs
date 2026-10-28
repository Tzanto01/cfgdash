using System.Security.Cryptography;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Cfgdash.Core.Tests;

public sealed class EngramMemoryCollectorTests : IDisposable
{
    private const string FullSchema = """
        CREATE TABLE sessions (id TEXT PRIMARY KEY, project TEXT NOT NULL, directory TEXT NOT NULL, started_at TEXT NOT NULL DEFAULT (datetime('now')), ended_at TEXT, summary TEXT);
        CREATE TABLE observations (
          id INTEGER PRIMARY KEY AUTOINCREMENT, sync_id TEXT, session_id TEXT NOT NULL REFERENCES sessions(id), type TEXT NOT NULL, title TEXT NOT NULL,
          content TEXT NOT NULL, tool_name TEXT, project TEXT, scope TEXT NOT NULL DEFAULT 'project', topic_key TEXT, normalized_hash TEXT,
          revision_count INTEGER NOT NULL DEFAULT 1, duplicate_count INTEGER NOT NULL DEFAULT 1, last_seen_at TEXT, pinned BOOLEAN NOT NULL DEFAULT 0,
          created_at TEXT NOT NULL DEFAULT (datetime('now')), updated_at TEXT NOT NULL DEFAULT (datetime('now')), deleted_at TEXT, review_after TEXT);
        CREATE TABLE user_prompts (id INTEGER PRIMARY KEY AUTOINCREMENT, sync_id TEXT, session_id TEXT NOT NULL REFERENCES sessions(id), content TEXT NOT NULL, project TEXT, created_at TEXT NOT NULL DEFAULT (datetime('now')));
        """;

    private readonly TempTree tree = new();
    private readonly EngramMemoryCollector collector = new();

    public void Dispose() => tree.Dispose();

    private string DataDir => tree.Dir("home/user/.engram");

    private string DbPath => Path.Combine(DataDir, "engram.db");

    private void Exec(string sql)
    {
        using var db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void SeedFull()
    {
        Exec("PRAGMA journal_mode = WAL;" + FullSchema + """
            INSERT INTO sessions (id, project, directory, started_at) VALUES
              ('s1', 'cfgdash', '/home/user/cfgdash', '2026-10-10 08:00:00'),
              ('s2', 'company/example-app', '/home/user/company', '2026-10-02 08:00:00'),
              ('s3', 'cfgdash', '/home/user/cfgdash', '2026-09-01 08:00:00');
            INSERT INTO observations (id, session_id, type, title, content, project, scope, topic_key, created_at, updated_at, deleted_at, review_after, revision_count) VALUES
              (1, 's1', 'decision', 'Pick sqlite', 'Use **sqlite** read-only.', 'cfgdash', 'project', 'architecture/storage', '2026-10-01 09:00:00', '2026-10-03 09:00:00', NULL, '2027-01-01 00:00:00', 3),
              (2, 's1', 'preference', 'Short answers', 'Keep it short.', 'cfgdash', 'personal', NULL, '2026-10-01 10:00:00', '2026-10-01 10:00:00', NULL, NULL, 1),
              (3, 's1', 'bugfix', 'Old note', 'Gone.', 'cfgdash', 'project', 'architecture/storage', '2026-10-01 11:00:00', '2026-10-01 11:00:00', '2026-10-05 00:00:00', NULL, 1),
              (4, 's2', 'decision', 'Company rule', 'Confidential company content.', 'company/example-app', 'project', 'company/rules', '2026-10-02 09:00:00', '2026-10-04 09:00:00', NULL, NULL, 1),
              (5, 's2', 'decision', 'Other company rule', 'More company content.', char(9) || ' COMPANY/Other', 'global', NULL, '2026-10-02 09:00:00', '2026-10-02 09:00:00', NULL, NULL, 1);
            INSERT INTO user_prompts (session_id, content, project, created_at) VALUES ('s1', 'hi', 'cfgdash', '2026-10-01 08:01:00'), ('s2', 'hi', 'company/example-app', '2026-10-02 08:01:00');
            """);
    }

    private CollectContext Context(bool companyLayer = false) => TestContexts.Create(
        TestEnvironments.Create(tree, paths: [TestEnvironments.ContextPath(PathKeys.EngramDataDir, DataDir)], companyPrefixes: companyLayer ? [tree.Dir("company")] : null),
        tree.Root);

    private static TableBlock Table(CollectorResult result, string id) => result.Blocks.OfType<TableBlock>().Single(b => b.Id == id);

    private static object? Kv(CollectorResult result, string key) =>
        result.Blocks.OfType<KvBlock>().Single().Rows.Single(r => r.Key == key).Value?.GetValue<object>().ToString();

    [Fact]
    public async Task Without_a_company_layer_company_projects_are_absent_from_rows_counts_topics_and_details()
    {
        SeedFull();
        var context = Context();

        var result = await collector.CollectAsync(context, CancellationToken.None);
        var detail = await collector.GetDetailAsync("4", context, CancellationToken.None);

        Assert.Null(result.UnavailableReason);
        Assert.Equal(["1", "2"], Table(result, "observations").Rows.Select(r => r.Id).Order());
        Assert.Equal(["cfgdash"], Table(result, "projects").Rows.Select(r => r.Id));
        Assert.Equal(["cfgdash|architecture/storage"], Table(result, "topics").Rows.Select(r => r.Id));
        Assert.Equal(("2", "2", "1"), (Kv(result, "observations"), Kv(result, "sessions"), Kv(result, "prompts")));
        Assert.Null(detail);
        Assert.Contains("2 company observations, 1 sessions and 1 prompts hidden: this machine has no company layer", result.Notes!);
        Assert.DoesNotContain("company/example-app", System.Text.Json.JsonSerializer.Serialize(result.Blocks, SnapshotJson.Options));
    }

    [Fact]
    public async Task With_a_company_layer_company_memories_appear_flagged_and_open()
    {
        SeedFull();
        var context = Context(companyLayer: true);

        var result = await collector.CollectAsync(context, CancellationToken.None);
        var detail = await collector.GetDetailAsync("4", context, CancellationToken.None);

        var company = Table(result, "observations").Rows.Single(r => r.Id == "4");
        Assert.Equal(["company"], company.Flags);
        Assert.Equal(["company"], Table(result, "projects").Rows.Single(r => r.Id == "company/example-app").Flags);
        Assert.Equal("4", Kv(result, "observations"));
        Assert.Null(result.Notes);
        Assert.Contains("Confidential company content.", detail!.Content);
    }

    [Fact]
    public async Task Soft_deleted_observations_are_excluded_and_newest_comes_first()
    {
        SeedFull();
        var context = Context();

        var result = await collector.CollectAsync(context, CancellationToken.None);
        var deleted = await collector.GetDetailAsync("3", context, CancellationToken.None);

        var rows = Table(result, "observations").Rows;
        Assert.Equal(["1", "2"], rows.Select(r => r.Id));
        Assert.Equal("engram.memory:1", rows[0].DetailId);
        Assert.Equal("2027-01-01T00:00:00Z", rows[0].Cells["reviewAfter"]!.GetValue<string>());
        Assert.Equal(1L, Table(result, "topics").Rows.Single().Cells["observations"]!.GetValue<long>());
        var project = Table(result, "projects").Rows.Single().Cells;
        Assert.Equal((2L, 1L, 1L, 2L), (project["observations"]!.GetValue<long>(), project["project-scope"]!.GetValue<long>(), project["personal-scope"]!.GetValue<long>(), project["sessions"]!.GetValue<long>()));
        Assert.Equal("2026-10-10T08:00:00Z", project["lastActivity"]!.GetValue<string>());
        Assert.Null(deleted);
    }

    [Fact]
    public async Task Detail_shows_metadata_and_full_content_for_a_plain_integer_key_only()
    {
        SeedFull();
        var context = Context();

        var detail = await collector.GetDetailAsync("1", context, CancellationToken.None);
        string[] invalid = ["1 OR 1=1", "-1", "+1", "1.0", "0x1", "999", "99999999999999999999"];
        var rejected = await Task.WhenAll(invalid.Select(k => collector.GetDetailAsync(k, context, CancellationToken.None)));

        Assert.NotNull(detail);
        Assert.Equal(("Pick sqlite", "markdown"), (detail.Title, detail.ContentType));
        Assert.Contains("- **Topic key:** architecture/storage", detail.Content);
        Assert.Contains("- **Review after:** 2027-01-01T00:00:00Z", detail.Content);
        Assert.Contains("- **Revisions:** 3", detail.Content);
        Assert.EndsWith("Use **sqlite** read-only.", detail.Content);
        Assert.All(rejected, Assert.Null);
    }

    [Fact]
    public async Task Reading_never_changes_the_database_file()
    {
        SeedFull();
        var before = SHA256.HashData(File.ReadAllBytes(DbPath));
        var context = Context();

        await collector.CollectAsync(context, CancellationToken.None);
        await collector.GetDetailAsync("1", context, CancellationToken.None);

        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(DbPath)));
    }

    [Fact]
    public async Task Older_schema_without_optional_columns_degrades_to_absent_fields()
    {
        Exec("""
            CREATE TABLE observations (id INTEGER PRIMARY KEY, session_id TEXT, type TEXT, title TEXT, content TEXT, project TEXT, created_at TEXT);
            INSERT INTO observations VALUES (1, 's1', 'decision', 'Old', 'Body', 'cfgdash', '2026-10-01 09:00:00');
            """);
        var context = Context();

        var result = await collector.CollectAsync(context, CancellationToken.None);
        var detail = await collector.GetDetailAsync("1", context, CancellationToken.None);

        Assert.Null(result.UnavailableReason);
        var table = Table(result, "observations");
        Assert.DoesNotContain(table.Columns, c => c.Key == "reviewAfter");
        Assert.DoesNotContain(result.Blocks, b => b.Id == "topics");
        Assert.Equal("2026-10-01T09:00:00Z", table.Rows.Single().Cells["created"]!.GetValue<string>());
        Assert.Null(Kv(result, "sessions"));
        Assert.EndsWith("Body", detail!.Content);
    }

    [Fact]
    public async Task Missing_core_columns_missing_file_and_a_non_database_file_are_unavailable()
    {
        var missing = await collector.CollectAsync(Context(), CancellationToken.None);
        File.WriteAllText(DbPath, "not a database, just text that is long enough to fill a header page........................................");
        var garbage = await collector.CollectAsync(Context(), CancellationToken.None);
        File.Delete(DbPath);
        Exec("CREATE TABLE observations (id INTEGER PRIMARY KEY, title TEXT)");
        var unsupported = await collector.CollectAsync(Context(), CancellationToken.None);

        Assert.Equal("no engram.db in the engram data dir", missing.UnavailableReason);
        Assert.StartsWith("engram.db is unreadable: ", garbage.UnavailableReason);
        Assert.Equal("unsupported engram schema", unsupported.UnavailableReason);
    }

    [Fact]
    public async Task Only_the_newest_500_observations_are_listed_with_a_note()
    {
        Exec(FullSchema + """
            INSERT INTO sessions (id, project, directory) VALUES ('s1', 'cfgdash', '/home/user/cfgdash');
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 501)
            INSERT INTO observations (id, session_id, type, title, content, project, topic_key, created_at, updated_at)
            SELECT i, 's1', 'discovery', 'Note ' || i, 'Body', 'cfgdash', 'topic/' || i, '2026-10-01 00:00:00', printf('2026-10-01 %02d:%02d:00', i / 60, i % 60) FROM n;
            """);

        var result = await collector.CollectAsync(Context(), CancellationToken.None);

        var rows = Table(result, "observations").Rows;
        Assert.Equal(500, rows.Count);
        Assert.Equal("501", rows[0].Id);
        Assert.DoesNotContain(rows, r => r.Id == "1");
        Assert.Contains("showing the newest 500 of 501 observations", result.Notes!);
        Assert.Equal(500, Table(result, "topics").Rows.Count);
        Assert.Contains("showing the 500 most recent topic keys", result.Notes!);
    }

    [Fact]
    public async Task Secrets_in_titles_are_redacted_in_rows()
    {
        var token = "gh" + "p_" + new string('a', 36);
        Exec(FullSchema + "INSERT INTO sessions (id, project, directory) VALUES ('s1', 'cfgdash', '/home/user/cfgdash');" + $"INSERT INTO observations (id, session_id, type, title, content, project) VALUES (1, 's1', 'config', 'Token {token}', 'Body', 'cfgdash');");

        var result = await collector.CollectAsync(Context(), CancellationToken.None);

        Assert.Equal("Token [redacted]", Table(result, "observations").Rows.Single().Cells["title"]!.GetValue<string>());
    }
}
