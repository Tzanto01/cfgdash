using Cfgdash.Core;
using Cfgdash.Core.Snapshot;
using Microsoft.Extensions.FileProviders;

namespace Cfgdash.Host.Tests;

public sealed class ApiFixture : IAsyncLifetime
{
    private readonly TempDir temp = new();

    internal ServerHost Host { get; private set; } = null!;

    internal string Tree { get; private set; } = null!;

    internal string Workspace { get; private set; } = null!;

    internal string Home { get; private set; } = null!;

    internal string Project { get; private set; } = null!;

    internal string Missing => temp.Combine("missing");

    public async Task InitializeAsync()
    {
        Tree = temp.Sub("tree");
        foreach (var name in new[] { "Beta", "alpha", ".hidden", "gamma-2" })
        {
            Directory.CreateDirectory(Path.Combine(Tree, name));
        }

        File.WriteAllText(Path.Combine(Tree, "x.txt"), "file, not a directory");
        Workspace = CfgdashServer.NormalizeDir(temp.Sub("ws"))!;
        Home = CfgdashServer.NormalizeDir(temp.Sub("home"))!;
        Project = CfgdashServer.NormalizeDir(temp.Sub("proj"))!;
        var environment = TestCore.Machine(
            home: Home,
            paths: [new ContextPath(PathKeys.WorkspaceRoot, Workspace, null, true)],
            projects: [Workspace, Missing, Project]);
        Host = await ServerHost.StartAsync(environment: environment);
    }

    public async Task DisposeAsync()
    {
        await Host.DisposeAsync();
        temp.Dispose();
    }

    internal string NewDir() => CfgdashServer.NormalizeDir(temp.Sub(Guid.NewGuid().ToString("N")))!;
}

public sealed class ApiTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private ServerHost Host => fixture.Host;

    private static string Slash(string path) => path.Replace('\\', '/');

    private static string Dir(string path) => "dir=" + Uri.EscapeDataString(path);

    [Fact]
    public async Task Health_reports_status_version_and_schema_version()
    {
        var response = await Host.GetAsync("/api/v1/health");

        Assert.Equal(200, response.Status);
        Assert.Equal("ok", response.Json.GetProperty("status").GetString());
        Assert.Equal(HostDependencies.Version, response.Json.GetProperty("version").GetString());
        Assert.Equal(HarnessSnapshot.CurrentSchemaVersion, response.Json.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task Snapshot_without_dir_is_built_for_the_start_dir()
    {
        var response = await Host.GetAsync("/api/v1/snapshot");

        Assert.Equal(200, response.Status);
        Assert.Equal(1, response.Json.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Host.StartDir, response.Json.GetProperty("context").GetProperty("targetDir").GetString());
        Assert.True(Host.Snapshots.CountFor(Host.StartDir) >= 1);
    }

    [Fact]
    public async Task Snapshot_uses_the_requested_dir()
    {
        var dir = fixture.NewDir();

        var response = await Host.GetAsync($"/api/v1/snapshot?{Dir(dir)}");

        Assert.Equal(200, response.Status);
        Assert.Equal(dir, response.Json.GetProperty("context").GetProperty("targetDir").GetString());
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("MISSING")]
    [InlineData("FILE")]
    public async Task Snapshot_rejects_relative_missing_and_non_directory_dirs(string kind)
    {
        var dir = kind switch
        {
            "MISSING" => fixture.Missing,
            "FILE" => Path.Combine(fixture.Tree, "x.txt"),
            _ => kind,
        };

        var response = await Host.GetAsync($"/api/v1/snapshot?{Dir(dir)}");

        Assert.Equal(400, response.Status);
        Assert.Equal("bad-dir", response.Error);
    }

    [Fact]
    public async Task Snapshot_is_cached_per_dir_until_refresh_is_requested()
    {
        var dir = fixture.NewDir();
        var other = fixture.NewDir();

        await Host.GetAsync($"/api/v1/snapshot?{Dir(dir)}");
        await Host.GetAsync($"/api/v1/snapshot?{Dir(dir)}");
        var cachedCount = Host.Snapshots.CountFor(dir);
        await Host.GetAsync($"/api/v1/snapshot?{Dir(other)}");
        await Host.GetAsync($"/api/v1/snapshot?{Dir(dir)}&refresh=true");
        await Host.GetAsync($"/api/v1/snapshot?{Dir(dir)}");

        Assert.Equal(1, cachedCount);
        Assert.Equal(1, Host.Snapshots.CountFor(other));
        Assert.Equal(2, Host.Snapshots.CountFor(dir));
    }

    [Fact]
    public async Task Failed_snapshot_build_answers_500_and_the_next_request_retries()
    {
        var dir = fixture.NewDir();
        Host.Snapshots.FailWith = (d, n) => d == dir && n == 1 ? new InvalidOperationException("collector exploded") : null;

        var failed = await Host.GetAsync($"/api/v1/snapshot?{Dir(dir)}");
        var retried = await Host.GetAsync($"/api/v1/snapshot?{Dir(dir)}");

        Assert.Equal(500, failed.Status);
        Assert.Equal("snapshot-failed", failed.Error);
        Assert.DoesNotContain("collector exploded", failed.Body);
        Assert.Equal(200, retried.Status);
        Assert.Equal(2, Host.Snapshots.CountFor(dir));
    }

    [Fact]
    public async Task A_snapshot_build_that_blocks_synchronously_does_not_hold_the_request_or_the_server()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var snapshots = new FakeSnapshots
        {
            FailWith = (_, _) =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(30));
                return null;
            },
        };
        await using var host = await ServerHost.StartAsync(snapshots: snapshots);
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var abort = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{host.Port}/api/v1/snapshot");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + host.Token);
        try
        {
            var pending = http.SendAsync(request, abort.Token);
            Assert.True(await Poll.UntilAsync(() => entered.IsSet), "the build never started");

            var health = host.GetAsync("/api/v1/health");
            var answered = await Task.WhenAny(health, Task.Delay(5_000)) == health;
            await abort.CancelAsync();
            var logged = await Poll.UntilAsync(() => host.ReadLog().Contains("GET /api/v1/snapshot"), 5_000);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

            Assert.True(answered, "health did not answer while a build was blocked");
            Assert.True(logged, "the aborted snapshot request stayed stuck behind the blocking build");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task File_known_to_the_snapshot_returns_its_content()
    {
        var response = await Host.GetAsync("/api/v1/files/f1");

        Assert.Equal(200, response.Status);
        Assert.Equal("f1", response.Json.GetProperty("id").GetString());
        Assert.Equal("memory/notes.md", response.Json.GetProperty("path").GetString());
        Assert.Equal("markdown", response.Json.GetProperty("contentType").GetString());
        Assert.Equal("content of f1", response.Json.GetProperty("content").GetString());
        Assert.False(response.Json.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("unknown-id")]
    [InlineData("gone")]
    public async Task Unknown_file_ids_and_files_deleted_since_the_snapshot_answer_404(string id)
    {
        var response = await Host.GetAsync($"/api/v1/files/{id}");

        Assert.Equal(404, response.Status);
        Assert.Equal("not-found", response.Error);
    }

    [Fact]
    public async Task Detail_emitted_by_the_snapshot_returns_its_content()
    {
        var response = await Host.GetAsync("/api/v1/details/" + Uri.EscapeDataString("c1:emitted"));

        Assert.Equal(200, response.Status);
        Assert.Equal("c1:emitted", response.Json.GetProperty("id").GetString());
        Assert.Equal("Title c1:emitted", response.Json.GetProperty("title").GetString());
        Assert.Equal("markdown", response.Json.GetProperty("contentType").GetString());
        Assert.Equal("detail of c1:emitted", response.Json.GetProperty("content").GetString());
        Assert.False(response.Json.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Detail_not_emitted_by_the_snapshot_answers_404_even_when_the_provider_resolves_it()
    {
        var response = await Host.GetAsync("/api/v1/details/" + Uri.EscapeDataString("c1:other"));

        Assert.Equal(404, response.Status);
        Assert.Equal("not-found", response.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?q=")]
    [InlineData("?q=a")]
    public async Task Search_needs_at_least_two_characters(string query)
    {
        var response = await Host.GetAsync("/api/v1/search" + query);

        Assert.Equal(400, response.Status);
        Assert.Equal("bad-query", response.Error);
    }

    [Fact]
    public async Task Search_returns_the_search_services_response()
    {
        var response = await Host.GetAsync("/api/v1/search?q=ab");

        Assert.Equal(200, response.Status);
        Assert.Equal("ab", response.Json.GetProperty("query").GetString());
        Assert.Equal("snippet for ab", response.Json.GetProperty("results")[0].GetProperty("snippet").GetString());
    }

    [Fact]
    public async Task Dirs_lists_only_directories_including_hidden_sorted_case_insensitively_with_forward_slashes()
    {
        var response = await Host.GetAsync($"/api/v1/dirs?path={Uri.EscapeDataString(fixture.Tree)}");

        Assert.Equal(200, response.Status);
        Assert.Equal(Slash(fixture.Tree), response.Json.GetProperty("path").GetString());
        Assert.Equal(Slash(Directory.GetParent(fixture.Tree)!.FullName), response.Json.GetProperty("parent").GetString());
        var entries = response.Json.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal([".hidden", "alpha", "Beta", "gamma-2"], entries.Select(e => e.GetProperty("name").GetString()));
        Assert.Equal(Slash(Path.Combine(fixture.Tree, "alpha")), entries[1].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Dirs_without_a_path_answers_for_the_start_dir()
    {
        var response = await Host.GetAsync("/api/v1/dirs");

        Assert.Equal(200, response.Status);
        Assert.Equal(Slash(Host.StartDir), response.Json.GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("MISSING")]
    public async Task Dirs_rejects_relative_and_missing_paths(string kind)
    {
        var path = kind == "MISSING" ? fixture.Missing : kind;

        var response = await Host.GetAsync($"/api/v1/dirs?path={Uri.EscapeDataString(path)}");

        Assert.Equal(400, response.Status);
        Assert.Equal("bad-dir", response.Error);
    }

    [WindowsFact]
    public async Task Dirs_rejects_device_and_unc_style_paths_even_when_they_name_an_existing_directory()
    {
        var device = @"\\?\" + fixture.Tree;
        var slashes = "//?/" + Slash(fixture.Tree);

        var plain = await Host.GetAsync($"/api/v1/dirs?path={Uri.EscapeDataString(fixture.Tree)}");
        var backslashes = await Host.GetAsync($"/api/v1/dirs?path={Uri.EscapeDataString(device)}");
        var forward = await Host.GetAsync($"/api/v1/dirs?path={Uri.EscapeDataString(slashes)}");

        Assert.Equal(200, plain.Status);
        Assert.Equal(400, backslashes.Status);
        Assert.Equal("bad-dir", backslashes.Error);
        Assert.Equal(400, forward.Status);
        Assert.Equal("bad-dir", forward.Error);
    }

    [Fact]
    public async Task Dirs_of_a_filesystem_root_reports_a_null_parent()
    {
        var root = Path.GetPathRoot(fixture.Tree)!;

        var response = await Host.GetAsync($"/api/v1/dirs?path={Uri.EscapeDataString(root)}");

        Assert.Equal(200, response.Status);
        Assert.True(response.Json.TryGetProperty("parent", out var parent), "parent is missing from the JSON");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, parent.ValueKind);
    }

    [Fact]
    public async Task Dirs_suggests_each_existing_known_directory_once()
    {
        var response = await Host.GetAsync("/api/v1/dirs");

        var suggestions = response.Json.GetProperty("suggestions").EnumerateArray().Select(s => s.GetProperty("path").GetString()).ToList();
        Assert.Equal([Slash(Host.StartDir), Slash(fixture.Workspace), Slash(fixture.Home), Slash(fixture.Project)], suggestions);
    }

    [Fact]
    public async Task Unknown_api_path_answers_a_json_404()
    {
        var response = await Host.GetAsync("/api/v1/nope");

        Assert.Equal(404, response.Status);
        Assert.Equal("not-found", response.Error);
        Assert.StartsWith("application/json", response.Header("Content-Type"));
    }

    [Fact]
    public async Task Without_built_ui_non_api_paths_show_the_not_built_page()
    {
        var response = await Host.GetAsync("/settings/mcp", null);

        Assert.Equal(200, response.Status);
        Assert.StartsWith("text/html", response.Header("Content-Type"));
        Assert.Contains("cfgdash UI not built", response.Body);
    }

    [Fact]
    public async Task Built_ui_serves_assets_and_falls_back_to_index_html_but_never_for_api_paths()
    {
        using var ui = new TempDir();
        File.WriteAllText(Path.Combine(ui.Root, "index.html"), "<html>spa-index-marker</html>");
        File.WriteAllText(Path.Combine(ui.Root, "app.js"), "console.log('asset-marker')");
        using var files = new PhysicalFileProvider(ui.Root);
        await using var host = await ServerHost.StartAsync(ui: files);

        var route = await host.GetAsync("/settings/mcp", null);
        var root = await host.GetAsync("/", null);
        var asset = await host.GetAsync("/app.js", null);
        var api = await host.GetAsync("/api/v1/nope");

        Assert.Contains("spa-index-marker", route.Body);
        Assert.Contains("spa-index-marker", root.Body);
        Assert.Contains("asset-marker", asset.Body);
        Assert.Equal("no-referrer", asset.Header("Referrer-Policy"));
        Assert.Equal(404, api.Status);
        Assert.DoesNotContain("spa-index-marker", api.Body);
    }
}
