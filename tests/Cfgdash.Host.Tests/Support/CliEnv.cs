using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Cfgdash.Core;
using Microsoft.Extensions.Time.Testing;

namespace Cfgdash.Host.Tests;

/// <summary>An isolated cfgdash command line: temp cache dir, temp "installed" exe, captured output, fake core.</summary>
internal sealed class CliEnv : IDisposable
{
    private readonly TempDir temp = new();

    public CliEnv(MachineEnvironment? environment = null, FakeProcesses? processes = null, IDictionary<string, string>? variables = null, FakeSnapshots? snapshots = null)
    {
        Processes = processes ?? new FakeProcesses();
        Core = TestCore.Create(environment, Processes, snapshots, new FakeEnv(variables));
        Dir = CfgdashServer.NormalizeDir(temp.Sub("project"))!;
        Exe = temp.Combine(OperatingSystem.IsWindows() ? "cfgdash.exe" : "cfgdash");
        File.WriteAllText(Exe, "OLD");
        Store = new RunStateStore(Cache);
    }

    public FakeProcesses Processes { get; }

    public CoreServices Core { get; }

    public string Dir { get; }

    public string Exe { get; set; }

    public string Cache => temp.Combine("cache");

    public RunStateStore Store { get; }

    public CaptureWriter Out { get; } = new();

    public CaptureWriter Error { get; } = new();

    public List<string> Opened { get; } = [];

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    public string Root => temp.Root;

    public Task<int> RunAsync(params string[] args) =>
        CfgdashCli.RunAsync(args, TestCore.Deps(Core, Cache, Out, Error, Time, Opened.Add, Exe));

    /// <summary>Starts <c>run</c> in-process and returns once run.json exists and the URL was printed.</summary>
    public async Task<(Task<int> Exit, RunState State)> StartRunAsync(params string[] args)
    {
        var exit = RunAsync(args);
        await Poll.UntilAsync(() => exit.IsCompleted || (Store.Read() is not null && Out.Text.Contains("?token=")));
        if (exit.IsCompleted)
        {
            throw new InvalidOperationException($"run exited early with {await exit}: {Error.Text}");
        }

        return (exit, Store.Read()!);
    }

    public async Task ShutdownAsync(RunState state)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{state.Port}/api/v1/shutdown");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + state.Token);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public RunState LiveRecord(int port, string token, RunTailnet? tailnet = null)
    {
        using var self = Process.GetCurrentProcess();
        return new RunState(Environment.ProcessId, RunStateStore.CurrentProcessStart(), self.MainModule!.FileName, port, token, Dir, tailnet, DateTimeOffset.UtcNow);
    }

    public static RunState DeadRecord(RunTailnet? tailnet = null) =>
        new(int.MaxValue, DateTimeOffset.UtcNow, "cfgdash", 7820, "stale-token", "unused", tailnet, DateTimeOffset.UtcNow);

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    public static bool PortIsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public void Dispose() => temp.Dispose();
}

/// <summary>Answers the tailscale CLI calls cfgdash makes, by exact argument lists, and keeps a serve table.</summary>
internal sealed class FakeTailscale : FakeProcesses
{
    public const string Dns = "pc.example.ts.net";

    public HashSet<string> Tcp { get; } = [];

    public Dictionary<string, string> Web { get; } = [];

    public bool ServeFails { get; set; }

    public bool OffFails { get; set; }

    public void Claim(int port, string dns, string proxy)
    {
        Tcp.Add(port.ToString());
        Web[$"{dns}:{port}"] = proxy;
    }

    public bool Called(params string[] args) => Calls.Any(c => c.Args.SequenceEqual(args));

    public void AssertNeverFunnelOrReset(params int[] ownedPorts)
    {
        Assert.DoesNotContain(Calls, c => c.Args.Any(a => a.Contains("funnel", StringComparison.OrdinalIgnoreCase) || a.Contains("reset", StringComparison.OrdinalIgnoreCase)));
        foreach (var off in Calls.Where(c => c.Args is ["serve", _, "off"]))
        {
            Assert.Contains(off.Args[1], ownedPorts.Select(p => $"--https={p}"));
        }
    }

    protected override ProcessResult Handle(RecordedCall call)
    {
        lock (Web)
        {
            switch (call.Args)
            {
                case ["serve", "status", "--json"]:
                    return Ok(JsonSerializer.Serialize(new
                    {
                        TCP = Tcp.ToDictionary(p => p, _ => new { HTTPS = true }),
                        Web = Web.ToDictionary(w => w.Key, w => new { Handlers = new Dictionary<string, object> { ["/"] = new { Proxy = w.Value } } }),
                    }));
                case ["serve", "--bg", var https, var target] when https.StartsWith("--https=", StringComparison.Ordinal):
                    if (ServeFails)
                    {
                        return Fail("serve is not enabled on your tailnet");
                    }

                    Claim(int.Parse(https["--https=".Length..]), Dns, target);
                    return Ok();
                case ["serve", var https, "off"] when https.StartsWith("--https=", StringComparison.Ordinal):
                    if (OffFails)
                    {
                        return Fail("serve off denied");
                    }

                    var port = https["--https=".Length..];
                    Tcp.Remove(port);
                    foreach (var key in Web.Keys.Where(k => k.EndsWith(":" + port, StringComparison.Ordinal)).ToList())
                    {
                        Web.Remove(key);
                    }

                    return Ok();
                default:
                    throw new InvalidOperationException($"unexpected tailscale call: {call.Line}");
            }
        }
    }
}

internal static class TailnetEnv
{
    public static MachineEnvironment Machine(string flaw = "", IReadOnlyList<string>? logins = null, IReadOnlyList<string>? tags = null)
    {
        var noIdentity = flaw == "noidentity";
        var status = new TailnetStatus(
            flaw == "state" ? "NeedsLogin" : "Running",
            "pc.example.ts.net.",
            noIdentity ? "example-net" : "example.github",
            noIdentity ? string.Empty : "example.ts.net",
            flaw != "magicdns",
            flaw == "certs" ? [] : ["pc.example.ts.net"],
            [],
            noIdentity ? null : "user@github");
        return TestCore.Machine(
            tools: flaw == "notool" ? null : new Dictionary<string, string> { ["tailscale"] = "tailscale-fake" },
            tailnet: flaw == "notailnet" ? null : status,
            logins: logins,
            tags: tags);
    }
}
