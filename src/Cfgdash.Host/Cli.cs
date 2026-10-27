using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cfgdash.Core;

namespace Cfgdash.Host;

/// <summary>Parsed command line. <paramref name="Dir"/> is an existing, normalized absolute directory.</summary>
internal sealed record CliOptions(string Command, string Dir, int Port = CfgdashCli.DefaultPort, bool Tailnet = false, bool NoOpen = false, bool Check = false);

/// <summary>The <c>cfgdash</c> command line (api-v1: CLI). Exit codes: 0 ok, 1 error, 2 prerequisite missing.</summary>
internal static partial class CfgdashCli
{
    public const int ExitOk = 0;
    public const int ExitError = 1;
    public const int ExitPrerequisite = 2;
    public const int DefaultPort = 7820;

    private const string Usage = """
        Usage:
          cfgdash [run] [--port <n>] [--dir <path>] [--tailnet] [--no-open]
          cfgdash stop
          cfgdash update [--check]
          cfgdash export [--dir <path>]
          cfgdash --version
          cfgdash --help

        """;

    public static async Task<int> RunAsync(string[] args, HostDependencies dependencies)
    {
        DeleteUpdateLeftover(dependencies.ExePath);
        if (args is ["--version"])
        {
            dependencies.Out.WriteLine($"cfgdash {HostDependencies.Version}");
            return ExitOk;
        }

        if (args is ["--help"] or ["-h"])
        {
            dependencies.Out.Write(Usage);
            return ExitOk;
        }

        if (Parse(args, out var error) is not { } options)
        {
            dependencies.Error.WriteLine($"cfgdash: {error}");
            dependencies.Error.Write(Usage);
            return ExitError;
        }

        return options.Command switch
        {
            "stop" => await StopAsync(dependencies),
            "update" => NotImplemented(dependencies, "update"),
            "export" => await ExportAsync(options, dependencies),
            _ => await RunServerAsync(options, dependencies),
        };
    }

    /// <summary>Null with <paramref name="error"/> set on an unknown command, unknown option or bad value.</summary>
    internal static CliOptions? Parse(string[] args, out string? error)
    {
        error = null;
        var index = 0;
        var command = "run";
        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            command = args[0];
            index = 1;
        }

        if (command is not ("run" or "stop" or "update" or "export"))
        {
            error = $"unknown command '{command}'";
            return null;
        }

        int port = DefaultPort;
        string? dir = null;
        bool tailnet = false, noOpen = false, check = false;
        for (; index < args.Length; index++)
        {
            var name = args[index];
            string? value = null;
            var equals = name.IndexOf('=');
            if (name.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }

            var takesValue = name is "--port" or "--dir";
            if (takesValue && value is null)
            {
                value = ++index < args.Length ? args[index] : null;
                if (value is null)
                {
                    error = $"{name} needs a value";
                    return null;
                }
            }
            else if (!takesValue && value is not null)
            {
                error = $"{name} takes no value";
                return null;
            }

            switch (command, name)
            {
                case ("run", "--port"):
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || !(port == 0 || port is >= 1024 and <= 65535))
                    {
                        error = $"invalid port '{value}' (0 or 1024-65535)";
                        return null;
                    }

                    break;
                case ("run" or "export", "--dir"):
                    dir = ResolveDir(value!);
                    if (dir is null)
                    {
                        error = $"not an existing directory: '{value}'";
                        return null;
                    }

                    break;
                case ("run", "--tailnet"):
                    tailnet = true;
                    break;
                case ("run", "--no-open"):
                    noOpen = true;
                    break;
                case ("update", "--check"):
                    check = true;
                    break;
                default:
                    error = $"unknown option '{name}' for '{command}'";
                    return null;
            }
        }

        dir ??= ResolveDir(Directory.GetCurrentDirectory());
        if (dir is null)
        {
            error = "the current directory does not exist";
            return null;
        }

        return new CliOptions(command, dir, port, tailnet, noOpen, check);
    }

    private static string? ResolveDir(string value)
    {
        try
        {
            return CfgdashServer.NormalizeDir(Path.GetFullPath(value));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static int NotImplemented(HostDependencies dependencies, string what)
    {
        dependencies.Error.WriteLine($"cfgdash: {what} is not implemented yet.");
        return ExitError;
    }

    private static async Task<int> RunServerAsync(CliOptions options, HostDependencies dependencies)
    {
        var core = await dependencies.CreateCore(CancellationToken.None);
        var cacheDir = PrivateFiles.EnsureDirectory(dependencies.CacheDir(core.EnvironmentReader));
        var store = new RunStateStore(cacheDir);
        if (store.Read() is { } existing)
        {
            if (RunStateStore.IsLive(existing))
            {
                dependencies.Out.WriteLine("cfgdash is already running.");
                PrintUrls(dependencies.Out, existing.Port, existing.Token, existing.Tailnet);
                return ExitOk;
            }

            await CleanupStaleAsync(store, existing, core);
        }

        if (options.Tailnet)
        {
            return NotImplemented(dependencies, "--tailnet");
        }

        var token = TokenVerifier.CreateToken();
        var log = new CfgdashLog(Path.Combine(cacheDir, "logs"), dependencies.TimeProvider);
        CfgdashServer server;
        try
        {
            server = await CfgdashServer.StartAsync(new ServerOptions(options.Port, options.Dir, token), core, dependencies, log, CancellationToken.None);
        }
        catch (IOException)
        {
            dependencies.Error.WriteLine($"cfgdash: port {options.Port} is in use; try `cfgdash --port 0` or `cfgdash stop`");
            return ExitError;
        }

        var reason = "error";
        try
        {
            store.Write(new RunState(Environment.ProcessId, RunStateStore.CurrentProcessStart(), dependencies.ExePath ?? string.Empty, server.Port, token, options.Dir, null, dependencies.TimeProvider.GetUtcNow()));
            log.Write($"start version={HostDependencies.Version} port={server.Port} tailnet=off");
            var url = PrintUrls(dependencies.Out, server.Port, token, null);
            if (!HasDesktop(core.EnvironmentReader))
            {
                if (!options.Tailnet)
                {
                    dependencies.Out.WriteLine("No desktop session: open the URL above on this machine, or use `cfgdash --tailnet` to reach it from your tailnet.");
                }
            }
            else if (!options.NoOpen)
            {
                dependencies.OpenBrowser(url);
            }

            reason = await server.WaitForShutdownAsync();
        }
        finally
        {
            await server.DisposeAsync();
            store.Delete();
            log.Write($"stop {reason}");
        }

        return ExitOk;
    }

    private static async Task<int> StopAsync(HostDependencies dependencies)
    {
        var core = await dependencies.CreateCore(CancellationToken.None);
        var store = new RunStateStore(dependencies.CacheDir(core.EnvironmentReader));
        if (store.Read() is not { } state)
        {
            dependencies.Out.WriteLine("cfgdash is not running.");
            return ExitOk;
        }

        if (!RunStateStore.IsLive(state))
        {
            await CleanupStaleAsync(store, state, core);
            dependencies.Out.WriteLine("cfgdash is not running (removed stale state).");
            return ExitOk;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await RequestShutdownAsync(state, deadline.Token);
        if (await WaitForExitAsync(state.Pid, deadline.Token) || !RunStateStore.IsLive(state))
        {
            dependencies.Out.WriteLine("cfgdash stopped.");
        }
        else
        {
            try
            {
                using var process = Process.GetProcessById(state.Pid);
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
                dependencies.Out.WriteLine($"cfgdash did not stop within 10 s; killed process {state.Pid}.");
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
            {
                dependencies.Error.WriteLine($"cfgdash: could not stop process {state.Pid} ({e.GetType().Name}).");
                return ExitError;
            }
        }

        if (File.Exists(store.FilePath))
        {
            await CleanupStaleAsync(store, state, core);
        }

        return ExitOk;
    }

    private static async Task<int> ExportAsync(CliOptions options, HostDependencies dependencies)
    {
        var core = await dependencies.CreateCore(CancellationToken.None);
        SnapshotResult result;
        try
        {
            result = await core.Snapshots.BuildAsync(options.Dir, CancellationToken.None);
        }
        catch (Exception e)
        {
            dependencies.Error.WriteLine($"cfgdash: snapshot failed ({e.GetType().Name}).");
            return ExitError;
        }

        var exportsDir = PrivateFiles.EnsureDirectory(Path.Combine(PrivateFiles.EnsureDirectory(dependencies.CacheDir(core.EnvironmentReader)), "exports"));
        var stamp = dependencies.TimeProvider.GetUtcNow().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var path = Path.Combine(exportsDir, $"{SanitizeHostname(core.Environment.Hostname)}-{stamp}.json");
        await using (var stream = PrivateFiles.Open(path, FileMode.CreateNew))
        {
            await JsonSerializer.SerializeAsync(stream, result.Snapshot, SnapshotJson.Options);
        }

        dependencies.Out.WriteLine(path);
        return ExitOk;
    }

    private static string PrintUrls(TextWriter output, int port, string token, RunTailnet? tailnet)
    {
        var url = $"http://127.0.0.1:{port}/?token={token}";
        output.WriteLine(url);
        if (tailnet is not null)
        {
            var portPart = tailnet.HttpsPort == 443 ? string.Empty : $":{tailnet.HttpsPort}";
            output.WriteLine($"https://{tailnet.DnsName}{portPart}/?token={token}");
        }

        return url;
    }

    private static async Task CleanupStaleAsync(RunStateStore store, RunState state, CoreServices core)
    {
        await RemoveRecordedServeHandlerAsync(state, core);
        store.Delete();
    }

    private static Task RemoveRecordedServeHandlerAsync(RunState state, CoreServices core) => Task.CompletedTask;

    private static async Task RequestShutdownAsync(RunState state, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{state.Port.ToString(CultureInfo.InvariantCulture)}/api/v1/shutdown");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.Token);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
        }
    }

    private static async Task<bool> WaitForExitAsync(int pid, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            await process.WaitForExitAsync(cancellationToken);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Windows: interactive, session id not 0, not SSH. Linux/macOS: DISPLAY or WAYLAND_DISPLAY set, not SSH.</summary>
    internal static bool HasDesktop(IEnvironmentReader environment)
    {
        if (environment.Get("SSH_CONNECTION") is not null || environment.Get("SSH_TTY") is not null)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            using var self = Process.GetCurrentProcess();
            return Environment.UserInteractive && self.SessionId != 0;
        }

        return environment.Get("DISPLAY") is not null || environment.Get("WAYLAND_DISPLAY") is not null;
    }

    private static void DeleteUpdateLeftover(string? exePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(exePath))
        {
            return;
        }

        try
        {
            File.Delete(exePath + ".old");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static string SanitizeHostname(string hostname)
    {
        var sanitized = UnsafeHostnameChars().Replace(hostname, "_");
        return sanitized.Length == 0 ? "host" : sanitized;
    }

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex UnsafeHostnameChars();
}
