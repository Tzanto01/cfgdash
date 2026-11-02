using System.Text.Json;
using Cfgdash.Core;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.FileProviders;

namespace Cfgdash.Host.Tests;

internal sealed class Resp(HttpResponseMessage message, string body)
{
    public HttpResponseMessage Message { get; } = message;

    public string Body { get; } = body;

    public int Status => (int)Message.StatusCode;

    public JsonElement Json => JsonDocument.Parse(Body).RootElement;

    public string? Header(string name) =>
        Message.Headers.TryGetValues(name, out var values) || Message.Content.Headers.TryGetValues(name, out values) ? string.Join(",", values) : null;

    public string? SetCookie => Header("Set-Cookie");

    public string? SessionCookieValue => SetCookie?.Split(';')[0].Split('=', 2)[1];

    public string? Error => Json.TryGetProperty("error", out var error) ? error.GetString() : null;

    public string? Reason => Json.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
}

/// <summary>A real Kestrel instance on a free loopback port with fake core services, plus an HTTP client that can forge Host and other headers.</summary>
internal sealed class ServerHost : IAsyncDisposable
{
    public const string DnsName = "pc.example.ts.net";
    public const string Login = "user@github";

    private readonly HttpClient http = new(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false });
    private readonly TempDir owned;

    private ServerHost(TempDir owned, CfgdashServer server, string token, string startDir, FakeSnapshots snapshots, FakeProcesses processes, FakeTimeProvider time, string logDir)
    {
        this.owned = owned;
        Server = server;
        Token = token;
        StartDir = startDir;
        Snapshots = snapshots;
        Processes = processes;
        Time = time;
        LogDir = logDir;
    }

    public CfgdashServer Server { get; }

    public string Token { get; }

    public string StartDir { get; }

    public FakeSnapshots Snapshots { get; }

    public FakeProcesses Processes { get; }

    public FakeTimeProvider Time { get; }

    public string LogDir { get; }

    public string ExportsDir => owned.Combine("cache/exports");

    public string CacheDir => owned.Combine("cache");

    public string ConfigDir => owned.Combine("config");

    public int Port => Server.Port;

    public int BackendPort => Server.BackendPort!.Value;

    public string TailnetHost => $"{DnsName}:{Server.HttpsPort}";

    public static TailnetListenerOptions TailnetOptions(IReadOnlyList<string>? allowedTags = null) =>
        new(DnsName + ".", [Login], "example.ts.net", allowedTags ?? []);

    public static async Task<ServerHost> StartAsync(TailnetListenerOptions? tailnet = null, IFileProvider? ui = null, FakeProcesses? processes = null, FakeSnapshots? snapshots = null, MachineEnvironment? environment = null, Func<CoreServices, string?, IAutostart>? autostart = null, bool autostartMode = false)
    {
        var dirs = new TempDir();
        var startDir = dirs.Sub("start");
        var logDir = dirs.Combine("logs");
        processes ??= new FakeProcesses();
        snapshots ??= new FakeSnapshots();
        var machine = environment ?? TestCore.Machine(tools: tailnet is null ? null : new Dictionary<string, string> { ["tailscale"] = "tailscale-fake" });
        var core = TestCore.Create(machine, processes, snapshots);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var token = TokenVerifier.CreateToken();
        var deps = TestCore.Deps(core, dirs.Combine("cache"), time: time, ui: ui, autostart: autostart);
        var server = await CfgdashServer.StartAsync(new ServerOptions(0, startDir, token, tailnet, autostartMode), core, deps, new CfgdashLog(logDir, time), CancellationToken.None);
        return new ServerHost(dirs, server, token, CfgdashServer.NormalizeDir(startDir)!, snapshots, processes, time, logDir);
    }

    public Task<Resp> GetAsync(string path, string? bearer = "", params (string Name, string Value)[] headers) =>
        SendAsync(HttpMethod.Get, path, Port, null, bearer, headers);

    /// <summary>Sends to the tailnet backend listener with the tailnet Host header unless overridden.</summary>
    public Task<Resp> TailAsync(string path, string? bearer = "", string? host = null, HttpMethod? method = null, params (string Name, string Value)[] headers) =>
        SendAsync(method ?? HttpMethod.Get, path, BackendPort, host ?? TailnetHost, bearer, headers);

    /// <param name="bearer">"" sends the server token, null sends no Authorization header.</param>
    public async Task<Resp> SendAsync(HttpMethod method, string path, int port, string? host, string? bearer, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
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

        var response = await http.SendAsync(request);
        return new Resp(response, await response.Content.ReadAsStringAsync());
    }

    public string ReadLog()
    {
        try
        {
            return string.Concat(Directory.GetFiles(LogDir, "*.log").Select(f =>
            {
                using var reader = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                return reader.ReadToEnd();
            }));
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await Server.DisposeAsync();
        owned.Dispose();
    }

    public static string Whois(string name, params string[] tags) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Node"] = tags.Length == 0 ? new Dictionary<string, object> { ["Name"] = name } : new Dictionary<string, object> { ["Name"] = name, ["Tags"] = tags },
            ["UserProfile"] = new Dictionary<string, object> { ["LoginName"] = "tagged-devices" },
        });
}
