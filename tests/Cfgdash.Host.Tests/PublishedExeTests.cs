using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Cfgdash.Host.Tests;

internal sealed class PublishedExeFactAttribute : FactAttribute
{
    public PublishedExeFactAttribute(bool linuxOnly = false, bool tailnet = false)
    {
        var exe = Environment.GetEnvironmentVariable("CFGDASH_SMOKE_EXE");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            Skip = "CFGDASH_SMOKE_EXE does not point to a published cfgdash binary";
        }
        else if (linuxOnly && !OperatingSystem.IsLinux())
        {
            Skip = "Linux only";
        }
        else if (tailnet && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CFGDASH_SMOKE_TAILNET_PORT")))
        {
            Skip = "CFGDASH_SMOKE_TAILNET_PORT is not set to a free tailnet HTTPS port";
        }
    }
}

/// <summary>Drives the published single-file binary through its real entry point. Opt in with CFGDASH_SMOKE_EXE and CFGDASH_SMOKE_VERSION.</summary>
[Trait("Category", "PublishedExe")]
public sealed partial class PublishedExeTests(ITestOutputHelper output)
{
    private static readonly string Exe = Environment.GetEnvironmentVariable("CFGDASH_SMOKE_EXE") ?? string.Empty;
    private static readonly string Version = Environment.GetEnvironmentVariable("CFGDASH_SMOKE_VERSION") ?? "0.0.0-dev";

    [PublishedExeFact]
    public async Task Version_flag_prints_the_expected_version()
    {
        using var sandbox = new Sandbox();

        var (exit, stdout, _) = await sandbox.RunAsync("--version");

        Assert.Equal(0, exit);
        Assert.Contains(Version, stdout);
    }

    [PublishedExeFact]
    public async Task Published_binary_serves_securely_and_stops_on_cfgdash_stop()
    {
        using var sandbox = new Sandbox();
        sandbox.AssertNoLiveInstance();
        using var temp = new TempDir();
        var (child, port, token, _) = await sandbox.StartServerAsync(temp.Root);
        var killed = false;
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false });
            var baseUrl = $"http://127.0.0.1:{port}";

            Assert.Equal(401, (int)(await http.GetAsync(baseUrl + "/api/v1/health")).StatusCode);

            using var health = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v1/health");
            health.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var healthResponse = await http.SendAsync(health);
            using var healthJson = JsonDocument.Parse(await healthResponse.Content.ReadAsStringAsync());
            Assert.Equal(200, (int)healthResponse.StatusCode);
            Assert.Equal(Version, healthJson.RootElement.GetProperty("version").GetString());

            using var login = await http.GetAsync($"{baseUrl}/?token={token}");
            Assert.Equal(302, (int)login.StatusCode);
            var session = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=', 2)[1];
            Assert.NotEmpty(session);
            Assert.NotEqual(token, session);

            using var snapshot = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v1/snapshot");
            snapshot.Headers.TryAddWithoutValidation("Cookie", $"cfgdash_session={session}");
            using var snapshotResponse = await http.SendAsync(snapshot);
            using var snapshotJson = JsonDocument.Parse(await snapshotResponse.Content.ReadAsStringAsync());
            Assert.Equal(200, (int)snapshotResponse.StatusCode);
            Assert.Equal(1, snapshotJson.RootElement.GetProperty("schemaVersion").GetInt32());

            using var evil = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/v1/health");
            evil.Headers.Host = "evil.example";
            evil.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            using var evilResponse = await http.SendAsync(evil);
            Assert.Equal(400, (int)evilResponse.StatusCode);

            using var root = await http.GetAsync(baseUrl + "/");
            Assert.Equal(200, (int)root.StatusCode);
            Assert.StartsWith("text/html", root.Content.Headers.ContentType?.MediaType ?? string.Empty);

            var (stopExit, _, _) = await sandbox.RunAsync("stop");
            Assert.Equal(0, stopExit);
            Assert.True(child.WaitForExit(15_000), "the server did not exit within 15 s of cfgdash stop");
            Assert.False(File.Exists(sandbox.RunJson));
        }
        finally
        {
            killed = KillIfRunning(child);
        }

        Assert.False(killed, "the server had to be killed");
    }

    [PublishedExeFact]
    public async Task Export_writes_a_snapshot_file_and_prints_its_path()
    {
        using var sandbox = new Sandbox();
        using var temp = new TempDir();

        var (exit, stdout, _) = await sandbox.RunAsync("export", "--dir", temp.Root);

        var path = stdout.Trim();
        try
        {
            Assert.Equal(0, exit);
            Assert.True(File.Exists(path), $"export printed '{path}'");
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [PublishedExeFact(linuxOnly: true)]
    public async Task Sigterm_stops_the_server_gracefully_and_removes_run_state()
    {
        using var sandbox = new Sandbox();
        using var temp = new TempDir();
        var (child, _, _, _) = await sandbox.StartServerAsync(temp.Root);
        var killed = false;
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", ["-TERM", child.Id.ToString()]) { UseShellExecute = false })!;
            await kill.WaitForExitAsync();

            Assert.True(child.WaitForExit(15_000), "the server did not exit within 15 s of SIGTERM");
            Assert.Equal(0, child.ExitCode);
            Assert.False(File.Exists(sandbox.RunJson));
        }
        finally
        {
            killed = KillIfRunning(child);
        }

        Assert.False(killed, "the server had to be killed");
    }

    [PublishedExeFact(linuxOnly: true)]
    public async Task Sighup_stops_the_server_gracefully_and_logs_the_hangup()
    {
        using var sandbox = new Sandbox();
        using var temp = new TempDir();
        var (child, _, _, _) = await sandbox.StartServerAsync(temp.Root);
        var killed = false;
        try
        {
            using var kill = Process.Start(new ProcessStartInfo("kill", ["-HUP", child.Id.ToString()]) { UseShellExecute = false })!;
            await kill.WaitForExitAsync();

            Assert.True(child.WaitForExit(15_000), "the server did not exit within 15 s of SIGHUP");
            Assert.Equal(0, child.ExitCode);
            Assert.False(File.Exists(sandbox.RunJson));
            var log = Directory.GetFiles(Path.Combine(sandbox.CacheDir, "logs"), "*.log").Select(File.ReadAllLines).SelectMany(l => l).Last(l => l.Length > 0);
            Assert.EndsWith("stop hangup", log);
        }
        finally
        {
            killed = KillIfRunning(child);
        }

        Assert.False(killed, "the server had to be killed");
    }

    /// <summary>Live tailnet check on a machine with tailscale, MagicDNS and tailnet HTTPS. CFGDASH_SMOKE_TAILNET_PORT must be an HTTPS port without a serve handler. A forged identity header or X-Forwarded-For only keeps the tagged-device answer if serve strips and overwrites them.</summary>
    [PublishedExeFact(tailnet: true)]
    public async Task Tailnet_publish_needs_token_and_identity_ignores_forged_headers_and_is_undone_on_stop()
    {
        using var sandbox = new Sandbox();
        sandbox.AssertNoLiveInstance();
        using var temp = new TempDir();
        var (child, _, token, tailnetUrl) = await sandbox.StartServerAsync(temp.Root, Environment.GetEnvironmentVariable("CFGDASH_SMOKE_TAILNET_PORT")!, tailnet: true);
        var killed = false;
        try
        {
            using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };
            Assert.NotNull(tailnetUrl);
            var baseUrl = tailnetUrl[..tailnetUrl.IndexOf("/?token=", StringComparison.Ordinal)];

            async Task<HttpResponseMessage> GetAsync(string path, params (string Name, string Value)[] headers)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
                foreach (var (name, value) in headers)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }

                return await http.SendAsync(request);
            }

            var bearer = ("Authorization", "Bearer " + token);
            Assert.Equal(200, (int)(await GetAsync("/api/v1/health", bearer)).StatusCode);
            Assert.Equal(200, (int)(await GetAsync("/api/v1/health", bearer, ("Tailscale-User-Login", "other@example.com"))).StatusCode);
            Assert.Equal(200, (int)(await GetAsync("/api/v1/health", bearer, ("X-Forwarded-For", "192.0.2.1"))).StatusCode);
            Assert.Equal(401, (int)(await GetAsync("/api/v1/health")).StatusCode);

            using var login = await GetAsync($"/?token={token}");
            Assert.Equal(302, (int)login.StatusCode);
            var cookie = login.Headers.GetValues("Set-Cookie").Single();
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            var session = cookie.Split(';')[0].Split('=', 2)[1];
            Assert.NotEqual(token, session);
            Assert.Equal(200, (int)(await GetAsync("/api/v1/health", ("Cookie", $"cfgdash_session={session}"))).StatusCode);

            var (stopExit, _, _) = await sandbox.RunAsync("stop");
            Assert.Equal(0, stopExit);
            Assert.True(child.WaitForExit(15_000), "the server did not exit within 15 s of cfgdash stop");
            Assert.False(File.Exists(sandbox.RunJson));
            var log = Directory.GetFiles(Path.Combine(sandbox.CacheDir, "logs"), "*.log").Select(File.ReadAllLines).SelectMany(l => l).ToList();
            Assert.Contains("serve-off ok", log.Last(l => l.Contains(" serve-off ", StringComparison.Ordinal)));
        }
        finally
        {
            killed = KillIfRunning(child);
        }

        Assert.False(killed, "the server had to be killed");
    }

    private bool KillIfRunning(Process child)
    {
        try
        {
            if (child.HasExited)
            {
                return false;
            }

            output.WriteLine($"killing still-running child pid {child.Id} started by this test");
            child.Kill(entireProcessTree: true);
            child.WaitForExit(5_000);
            return true;
        }
        finally
        {
            child.Dispose();
        }
    }

    private sealed partial class Sandbox : IDisposable
    {
        private readonly TempDir temp = new();

        public string CacheDir => OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cfgdash")
            : Path.Combine(temp.Combine("xdg"), "cfgdash");

        public string RunJson => Path.Combine(CacheDir, "run.json");

        public void AssertNoLiveInstance()
        {
            if (new RunStateStore(CacheDir).Read() is { } state && RunStateStore.IsLive(state))
            {
                Assert.Fail($"A cfgdash instance (pid {state.Pid}) is already running and recorded in {RunJson}. Run `cfgdash stop` first.");
            }
        }

        public async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
        {
            using var process = Start(args);
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout, await stderr);
        }

        public async Task<(Process Child, int Port, string Token, string? TailnetUrl)> StartServerAsync(string dir, string port = "0", bool tailnet = false)
        {
            var child = tailnet ? Start("run", "--port", port, "--tailnet", "--no-open", "--dir", dir) : Start("run", "--port", port, "--no-open", "--dir", dir);
            _ = child.StandardError.ReadToEndAsync();
            var lines = new List<string>();
            Match? local = null;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                var read = child.StandardOutput.ReadLineAsync();
                if (await Task.WhenAny(read, Task.Delay(deadline - DateTime.UtcNow)) != read || await read is not { } line)
                {
                    break;
                }

                lines.Add(line);
                local ??= UrlLine().Match(line) is { Success: true } match ? match : null;
                var tailnetUrl = tailnet && line.StartsWith("https://", StringComparison.Ordinal) ? line : null;
                if (local is not null && (!tailnet || tailnetUrl is not null))
                {
                    _ = child.StandardOutput.ReadToEndAsync();
                    return (child, int.Parse(local.Groups["port"].Value), local.Groups["token"].Value, tailnetUrl);
                }
            }

            child.Kill(entireProcessTree: true);
            child.Dispose();
            throw new InvalidOperationException("the server did not print its URL; output: " + string.Join(" | ", lines));
        }

        public void Dispose() => temp.Dispose();

        private Process Start(params string[] args)
        {
            var info = new ProcessStartInfo(Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }

            if (!OperatingSystem.IsWindows())
            {
                info.Environment["XDG_CACHE_HOME"] = temp.Combine("xdg");
            }

            return Process.Start(info)!;
        }

        [GeneratedRegex(@"^http://127\.0\.0\.1:(?<port>\d+)/\?token=(?<token>\S+)$")]
        private static partial Regex UrlLine();
    }
}
