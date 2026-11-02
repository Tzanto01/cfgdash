using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cfgdash.Core;
using Cfgdash.Core.Environment;
using Cfgdash.Core.Snapshot;
using Microsoft.Extensions.Time.Testing;

namespace Cfgdash.Host.Tests;

/// <summary>The SSE stream of one assistant session, read in the background.</summary>
internal sealed class SseCapture : IDisposable
{
    private readonly List<(string Name, string Data)> frames = [];
    private readonly CancellationTokenSource cts = new();
    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false });
    private Task reader = Task.CompletedTask;

    public static async Task<SseCapture> OpenAsync(string url, string token)
    {
        var capture = new SseCapture();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        var response = await capture.http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, capture.cts.Token);
        capture.Status = (int)response.StatusCode;
        capture.reader = Task.Run(async () =>
        {
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(capture.cts.Token);
                using var lines = new StreamReader(stream, Encoding.UTF8);
                string? name = null;
                while (await lines.ReadLineAsync(capture.cts.Token) is { } line)
                {
                    if (line.StartsWith("event: ", StringComparison.Ordinal))
                    {
                        name = line[7..];
                    }
                    else if (line.StartsWith("data: ", StringComparison.Ordinal) && name is not null)
                    {
                        lock (capture.frames)
                        {
                            capture.frames.Add((name, line[6..]));
                        }

                        name = null;
                    }
                }
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or HttpRequestException or ObjectDisposedException)
            {
            }
            finally
            {
                response.Dispose();
            }
        });
        return capture;
    }

    public int Status { get; private set; }

    public IReadOnlyList<(string Name, string Data)> Frames
    {
        get
        {
            lock (frames)
            {
                return [.. frames];
            }
        }
    }

    public IReadOnlyList<string> Names => [.. Frames.Select(f => f.Name)];

    public Task<bool> WaitForAsync(string name, int timeoutMs = 15_000) => Poll.UntilAsync(() => Frames.Any(f => f.Name == name), timeoutMs);

    public JsonElement First(string name) => JsonDocument.Parse(Frames.First(f => f.Name == name).Data).RootElement.Clone();

    public string DeltaText() => string.Concat(Frames.Where(f => f.Name == "delta").Select(f => JsonDocument.Parse(f.Data).RootElement.GetProperty("text").GetString()));

    public void Dispose()
    {
        cts.Cancel();
        http.Dispose();
    }
}

internal sealed record AssistantHostOptions
{
    public bool Tailnet { get; init; }

    public bool ClaudeOnPath { get; init; } = true;

    public string ExePath { get; init; } = "/opt/example/cfgdash";

    public Action<AssistantHostPaths>? Prepare { get; init; }

    public IReadOnlyList<Cfgdash.Core.Snapshot.Section>? Sections { get; init; }
}

internal sealed record AssistantHostPaths(string Root, string Home, string ConfigDir, string Project, string Cache);

/// <summary>A real Kestrel instance with the real process runner and a fake <c>claude</c> on its PATH, fake time and a synthetic home.</summary>
internal sealed class AssistantHost : IAsyncDisposable
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false });
    private readonly TempDir temp;

    private AssistantHost(TempDir temp, FakeClaude claude, CfgdashServer server, AssistantHostPaths paths, string token, FakeTimeProvider time, FakeSnapshots snapshots, string exePath)
    {
        this.temp = temp;
        Claude = claude;
        Server = server;
        Paths = paths;
        Token = token;
        Time = time;
        Snapshots = snapshots;
        ExePath = exePath;
    }

    public FakeClaude Claude { get; }

    public CfgdashServer Server { get; }

    public AssistantHostPaths Paths { get; }

    public string Token { get; }

    public FakeTimeProvider Time { get; }

    public FakeSnapshots Snapshots { get; }

    public string ExePath { get; }

    public int Port => Server.Port;

    public string Origin => $"http://127.0.0.1:{Port}";

    public string Project => Paths.Project;

    public string AssistantDir => Path.Combine(Paths.Cache, "assistant");

    public static async Task<AssistantHost> StartAsync(AssistantHostOptions? options = null)
    {
        options ??= new AssistantHostOptions();
        var temp = new TempDir();
        var claude = new FakeClaude();
        var paths = new AssistantHostPaths(temp.Root, temp.Sub("home"), temp.Sub("claude-config"), CfgdashServer.NormalizeDir(temp.Sub("project"))!, temp.Combine("cache"));
        options.Prepare?.Invoke(paths);
        var emptyBin = temp.Sub("empty-bin");
        var path = (options.ClaudeOnPath ? claude.BinDir : emptyBin) + Path.PathSeparator + "/usr/bin";
        var machine = TestCore.Machine(
            home: paths.Home,
            paths: [new ContextPath(PathKeys.ClaudeConfigDir, paths.ConfigDir, null, true)],
            tools: options.Tailnet ? new Dictionary<string, string> { ["tailscale"] = "tailscale-fake" } : null);
        var snapshots = new FakeSnapshots();
        if (options.Sections is not null)
        {
            snapshots.Sections = options.Sections;
        }

        var core = new CoreServices(machine, new FakeEnv(new Dictionary<string, string> { ["PATH"] = path }), new ProcessRunner(), snapshots, new FakeFileReader(), new FakeSearch(), new FakeDetails());
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var token = TokenVerifier.CreateToken();
        var deps = TestCore.Deps(core, paths.Cache, time: time, exe: options.ExePath);
        var server = await CfgdashServer.StartAsync(
            new ServerOptions(0, paths.Project, token, options.Tailnet ? ServerHost.TailnetOptions() : null),
            core,
            deps,
            new CfgdashLog(temp.Combine("logs"), time),
            CancellationToken.None);
        return new AssistantHost(temp, claude, server, paths, token, time, snapshots, options.ExePath);
    }

    public int BackendPort => Server.BackendPort!.Value;

    public string TailnetHost => $"{ServerHost.DnsName}:{Server.HttpsPort}";

    public async Task<Resp> SendAsync(HttpMethod method, string path, string? body = null, string? bearer = "", int? port = null, string? host = null, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port ?? Port}{path}");
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (bearer is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (bearer.Length == 0 ? Token : bearer));
        }

        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await Http.SendAsync(request);
        return new Resp(response, await response.Content.ReadAsStringAsync());
    }

    public Task<Resp> GetAsync(string path, string? bearer = "") => SendAsync(HttpMethod.Get, path, bearer: bearer);

    /// <summary>A mutating UI request with the edits-v1 Origin and marker headers.</summary>
    public Task<Resp> UiAsync(HttpMethod method, string path, string? json = "{}", params (string Name, string Value)[] extra) =>
        SendAsync(method, path, json, headers: [("Origin", Origin), ("X-Cfgdash-Request", "1"), .. extra]);

    public Task<Resp> InternalAsync(string tool, string? capability, string json = "{}", params (string Name, string Value)[] headers) =>
        SendAsync(HttpMethod.Post, "/api/v2/assistant/internal/" + tool, json, capability, headers: headers);

    public async Task<string> CreateSessionAsync(string? dir = null, string? body = null)
    {
        var response = await UiAsync(HttpMethod.Post, "/api/v2/assistant/sessions", body ?? JsonSerializer.Serialize(new { dir = dir ?? Project }));
        Assert.True(response.Status == 200, $"create answered {response.Status}: {response.Body}");
        return response.Json.GetProperty("id").GetString()!;
    }

    public Task<Resp> MessageAsync(string id, string text = "hi") =>
        UiAsync(HttpMethod.Post, $"/api/v2/assistant/sessions/{id}/messages", JsonSerializer.Serialize(new { text, context = Array.Empty<object>() }));

    public Task<SseCapture> EventsAsync(string id) => SseCapture.OpenAsync($"{Origin}/api/v2/assistant/sessions/{id}/events", Token);

    /// <summary>Sends a request line with the exact <paramref name="target"/> (no normalization) and returns the status code and body.</summary>
    public async Task<(int Status, string Body)> RawAsync(string method, string target, string? bearer = null)
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", Port);
        var stream = client.GetStream();
        var request = $"{method} {target} HTTP/1.1\r\nHost: 127.0.0.1:{Port}\r\nConnection: close\r\nContent-Length: 2\r\nContent-Type: application/json\r\n"
            + (bearer is null ? string.Empty : $"Authorization: Bearer {bearer}\r\n")
            + "\r\n{}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var response = await reader.ReadToEndAsync();
        var status = int.Parse(response.Split(' ', 3)[1]);
        return (status, response[(response.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
    }

    /// <summary>Advances fake time in steps until <paramref name="condition"/> holds.</summary>
    public async Task<bool> AdvanceUntilAsync(Func<bool> condition, TimeSpan step, int maxSteps = 200)
    {
        for (var i = 0; i < maxSteps && !condition(); i++)
        {
            Time.Advance(step);
            await Task.Delay(25);
        }

        return condition();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Server.DisposeAsync();
        }
        finally
        {
            Claude.Dispose();
            temp.Dispose();
        }
    }
}
