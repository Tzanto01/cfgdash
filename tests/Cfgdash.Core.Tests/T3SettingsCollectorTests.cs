using System.Security.Cryptography;
using System.Text.Json;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Search;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;
using Microsoft.Data.Sqlite;

namespace Cfgdash.Core.Tests;

public sealed class T3SettingsCollectorTests : IDisposable
{
    private const string Canary = "never-opened-canary-41";
    private const string ThreadCanary = "thread-canary-title-42";
    private const string ScriptCanary = "script-canary-command-43";

    private const string Settings = """
        {
          "idleCompaction": true,
          "theme": "dark",
          "providerInstances": {
            "codex": { "driver": "codex", "displayName": "Codex", "enabled": true, "config": { "binaryPath": "/usr/local/bin/codex" } },
            "claudeAgent": { "driver": "claudeAgent", "displayName": "Claude" }
          },
          "favorites": [ { "provider": "codex", "model": "gpt-5" } ],
          "providerModelPreferences": { "codex": { "hiddenModels": [ "gpt-4o", "o3" ] } }
        }
        """;

    private const string Runtime = $$"""
        { "serverVersion": "0.0.9", "port": 3773, "origin": "http://localhost:3773", "startedAt": "2026-10-30T08:00:00Z", "serviceManaged": false, "pid": 4242, "ownerId": "{{Canary}}" }
        """;

    private const string ProjectsSchema = """
        CREATE TABLE projection_projects (project_id TEXT PRIMARY KEY, title TEXT NOT NULL, workspace_root TEXT NOT NULL, default_model_selection_json TEXT,
          default_thread_env_mode TEXT, auto_pull INTEGER, scripts_json TEXT, created_at TEXT, updated_at TEXT, pinned_at TEXT, archived_at TEXT, deleted_at TEXT);
        CREATE TABLE projection_threads (thread_id TEXT PRIMARY KEY, project_id TEXT, title TEXT);
        """;

    private static readonly string Token = "sk-" + "ant-" + new string('b', 24);
    private static readonly string KeySecret = "planted-" + "api-key-value-5";

    private readonly TempTree tree = new();
    private readonly T3SettingsCollector collector = new();

    public void Dispose() => tree.Dispose();

    private string UserData => tree.Dir("home/user/.t3/userdata");

    private string DbPath => Path.Combine(UserData, "statev2.sqlite");

    private string Seed() => ProjectsSchema + $$"""
        INSERT INTO projection_projects VALUES
          ('p1', 'cfgdash', '{{tree.Path("work/cfgdash")}}', '{"provider":"codex","model":"gpt-5"}', 'local', 1, '[{"name":"test","command":"{{ScriptCanary}}"}]', '2026-10-01T08:00:00Z', '2026-10-20T08:00:00Z', '2026-10-21T08:00:00Z', NULL, NULL),
          ('p2', 'app', '{{tree.Path("company/app")}}', NULL, NULL, 0, NULL, NULL, '2026-10-10T08:00:00Z', NULL, '2026-10-11T08:00:00Z', NULL),
          ('p3', 'gone-project', '{{tree.Path("work/gone")}}', NULL, NULL, NULL, NULL, NULL, '2026-10-30T08:00:00Z', NULL, NULL, '2026-10-31T08:00:00Z');
        INSERT INTO projection_threads VALUES ('t1', 'p1', '{{ThreadCanary}}');
        """;

    private void Exec(string sql)
    {
        using var db = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string WriteUserData(string relative, string content) => tree.Write("home/user/.t3/userdata/" + relative, content);

    private MachineEnvironment Env() => TestEnvironments.Create(tree, companyPrefixes: [tree.Dir("company")]);

    private CollectContext Context() => TestContexts.Create(Env(), tree.Root);

    private Task<SnapshotResult> BuildAsync() =>
        new SnapshotBuilder(Env(), [collector], new Redactor(), new FakeProcessRunner(), "0.1.0").BuildAsync(tree.Root, CancellationToken.None);

    private static TableBlock Table(CollectorResult result, string id) => result.Blocks.OfType<TableBlock>().Single(b => b.Id == id);

    private static KvBlock Kv(CollectorResult result, string id) => result.Blocks.OfType<KvBlock>().Single(b => b.Id == id);

    private static void AssertNoCanary(SnapshotResult result)
    {
        Assert.DoesNotContain(Canary, JsonSerializer.Serialize(result.Snapshot, SnapshotJson.Options));
        Assert.All(result.Files.All, f => Assert.Equal("settings.json", Path.GetFileName(f.FullPath)));
        Assert.Empty(new SnapshotSearch().Search(result.Snapshot, result.Files, new FileContentReader(new Redactor()), Canary, CancellationToken.None).Results);
    }

    [Fact]
    public async Task Without_a_userdata_dir_the_collector_is_not_present()
    {
        tree.Dir("home/user/.t3");

        var result = await collector.CollectAsync(Context(), CancellationToken.None);

        Assert.Equal("no T3 userdata dir (~/.t3/userdata)", result.NotPresentReason);
        Assert.Empty(result.Blocks);
    }

    [Fact]
    public async Task Runtime_settings_providers_models_and_live_projects_are_listed()
    {
        WriteUserData("settings.json", Settings);
        WriteUserData("server-runtime.json", Runtime);
        Exec(Seed());
        var context = Context();

        var result = await collector.CollectAsync(context, CancellationToken.None);

        var settingsFile = context.Files.All.Single();
        Assert.Equal(("settings", tree.Path("home/user/.t3/userdata/settings.json")), (settingsFile.Ref.Kind, settingsFile.FullPath));
        var settings = Kv(result, "settings");
        Assert.Equal(["idleCompaction", "theme"], settings.Rows.Select(r => r.Key));
        Assert.All(settings.Rows, r => Assert.Equal(settingsFile.Ref.Id, r.Source!.FileId));

        var runtime = Kv(result, "runtime");
        Assert.Equal(["version", "port", "origin", "startedAt", "serviceManaged", "pid"], runtime.Rows.Select(r => r.Key));
        Assert.Equal(("0.0.9", 3773), (runtime.Rows[0].Value!.GetValue<string>(), runtime.Rows[1].Value!.GetValue<int>()));
        Assert.Null(runtime.Rows[0].Source!.FileId);

        var providers = Table(result, "providers").Rows;
        Assert.Equal(["codex", "claudeAgent"], providers.Select(r => r.Id));
        var codex = providers[0].Cells;
        Assert.Equal(("codex", "Codex", "binaryPath=/usr/local/bin/codex", 2),
            (codex["driver"]!.GetValue<string>(), codex["displayName"]!.GetValue<string>(), codex["config"]!.GetValue<string>(), codex["hiddenModels"]!.GetValue<int>()));
        Assert.True(codex["enabled"]!.GetValue<bool>());
        Assert.Null(providers[1].Cells["enabled"]);
        Assert.Null(providers[1].Cells["hiddenModels"]);

        Assert.Equal(["favorite:codex:gpt-5", "hidden:codex:gpt-4o", "hidden:codex:o3"], Table(result, "models").Rows.Select(r => r.Id));

        var projects = Table(result, "projects").Rows;
        Assert.Equal(["p1", "p2"], projects.Select(r => r.Id));
        var p1 = projects[0].Cells;
        Assert.Equal(("cfgdash", PathUtil.ToJson(tree.Path("work/cfgdash")), "codex/gpt-5", "local", 1, "2026-10-20T08:00:00Z"),
            (p1["project"]!.GetValue<string>(), p1["path"]!.GetValue<string>(), p1["defaultModel"]!.GetValue<string>(), p1["envMode"]!.GetValue<string>(),
             p1["scripts"]!.GetValue<int>(), p1["updated"]!.GetValue<string>()));
        Assert.True(p1["autoPull"]!.GetValue<bool>());
        Assert.True(p1["pinned"]!.GetValue<bool>());
        Assert.False(p1["archived"]!.GetValue<bool>());
        Assert.Null(projects[0].Flags);
        Assert.Equal(["company"], projects[1].Flags);
        Assert.True(projects[1].Cells["archived"]!.GetValue<bool>());
        Assert.False(projects[1].Cells["autoPull"]!.GetValue<bool>());

        var json = JsonSerializer.Serialize(result.Blocks, SnapshotJson.Options);
        Assert.All(new[] { ThreadCanary, ScriptCanary, "gone-project", Canary }, canary => Assert.DoesNotContain(canary, json));
        Assert.Null(result.Notes);
    }

    [Fact]
    public async Task Secret_keys_and_tokens_in_jsonc_settings_are_redacted()
    {
        WriteUserData("settings.json", $$"""
            {
              // settings with comments and trailing commas
              "note": "deploy with {{Token}}",
              "providerInstances": {
                "claudeAgent": { "driver": "claudeAgent", "config": { "apiKey": "{{KeySecret}}" } },
              },
            }
            """);

        var result = await BuildAsync();

        var json = JsonSerializer.Serialize(result.Snapshot, SnapshotJson.Options);
        Assert.Contains(result.Snapshot.Sections.Single().Blocks, b => b.Id == "providers");
        Assert.Contains("[redacted]", json);
        Assert.DoesNotContain(Token, json);
        Assert.DoesNotContain(KeySecret, json);
        Assert.Contains("no statev2.sqlite: projects omitted", result.Snapshot.Collectors.Single().Notes!);
    }

    [Fact]
    public async Task Never_opened_files_stay_out_of_the_snapshot_the_registry_and_search()
    {
        foreach (var name in new[] { "secrets/x", "clerk-tokens.json", "connection-catalog.json", "server-owner.sqlite", "anonymous-id", "environment-id", "logs/a.log", "attachments/a.txt" })
        {
            WriteUserData(name, $$"""{ "value": "{{Canary}}" }""");
        }

        WriteUserData("server-runtime.json", Runtime);
        WriteUserData("settings.json", Settings);

        var result = await BuildAsync();

        AssertNoCanary(result);
        Assert.Contains(result.Snapshot.Sections.Single().Blocks, b => b.Id == "runtime");
        Assert.Single(result.Files.All);
    }

    [LinuxFact]
    public async Task A_settings_file_linked_onto_a_never_opened_file_is_skipped_with_a_note()
    {
        var target = WriteUserData("secrets/settings.json", $$"""{ "leak": "{{Canary}}" }""");
        File.CreateSymbolicLink(Path.Combine(UserData, "settings.json"), target);

        var result = await BuildAsync();

        AssertNoCanary(result);
        Assert.Empty(result.Files.All);
        Assert.Contains(result.Snapshot.Collectors.Single().Notes!, n => n.StartsWith("settings.json: skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unsupported_projects_schema_omits_only_the_projects_table()
    {
        WriteUserData("settings.json", Settings);
        Exec("CREATE TABLE projection_projects (project_id TEXT PRIMARY KEY, title TEXT)");

        var result = await collector.CollectAsync(Context(), CancellationToken.None);

        Assert.DoesNotContain(result.Blocks, b => b.Id == "projects");
        Assert.Equal(["settings", "providers", "models"], result.Blocks.Select(b => b.Id));
        Assert.Equal(["unsupported T3 state schema: projects omitted"], result.Notes!);
    }

    [Fact]
    public async Task A_wal_mode_database_is_read_without_creating_side_files()
    {
        Exec("PRAGMA journal_mode = WAL;" + Seed());
        Assert.False(File.Exists(DbPath + "-wal"));
        Assert.False(File.Exists(DbPath + "-shm"));

        var result = await collector.CollectAsync(Context(), CancellationToken.None);

        Assert.Equal(["p1", "p2"], Table(result, "projects").Rows.Select(r => r.Id));
        Assert.False(File.Exists(DbPath + "-wal"));
        Assert.False(File.Exists(DbPath + "-shm"));
    }

    [Fact]
    public async Task A_database_in_use_by_T3_is_read_without_touching_its_side_files_and_notes_possible_staleness()
    {
        Exec(Seed());
        using var writer = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA wal_autocheckpoint = 0; INSERT INTO projection_projects (project_id, title, workspace_root) VALUES ('p4', 'late', '/home/user/late');";
            command.ExecuteNonQuery();
        }

        string Hash(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        string[] Hashes() => [Hash(DbPath + "-wal"), Hash(DbPath + "-shm")];
        var before = Hashes();

        var result = await collector.CollectAsync(Context(), CancellationToken.None);

        Assert.Equal(["p1", "p2"], Table(result, "projects").Rows.Select(r => r.Id));
        Assert.Contains("projects may miss T3 changes not yet checkpointed", result.Notes!);
        Assert.Equal(before, Hashes());
    }
}
