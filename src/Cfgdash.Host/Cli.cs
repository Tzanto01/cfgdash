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
            "update" => await UpdateAsync(options, dependencies),
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

        TailnetListenerOptions? tailnetOptions = null;
        if (options.Tailnet && (tailnetOptions = CheckTailnet(core, dependencies.Error)) is null)
        {
            return ExitPrerequisite;
        }

        var tailscale = new TailscaleCli(core.Processes, core.Environment.Tools.GetValueOrDefault("tailscale"));
        var token = TokenVerifier.CreateToken();
        var log = new CfgdashLog(Path.Combine(cacheDir, "logs"), dependencies.TimeProvider);
        CfgdashServer server;
        try
        {
            server = await CfgdashServer.StartAsync(new ServerOptions(options.Port, options.Dir, token, tailnetOptions), core, dependencies, log, CancellationToken.None);
        }
        catch (IOException)
        {
            dependencies.Error.WriteLine($"cfgdash: port {options.Port} is in use; try `cfgdash --port 0` or `cfgdash stop`");
            return ExitError;
        }

        RunTailnet? tailnet = null;
        if (tailnetOptions is not null)
        {
            tailnet = new RunTailnet(server.HttpsPort!.Value, server.BackendPort!.Value, tailnetOptions.DnsName);
            var status = await tailscale.ServeStatusAsync(CancellationToken.None);
            var problem = status is null
                ? "could not read `tailscale serve status --json`"
                : status.UsesPort(tailnet.HttpsPort) ? $"HTTPS port {tailnet.HttpsPort} already has a tailscale serve handler; choose another --port" : null;
            if (problem is not null)
            {
                await server.DisposeAsync();
                dependencies.Error.WriteLine($"cfgdash: {problem}");
                return ExitPrerequisite;
            }
        }

        var reason = "error";
        var published = false;
        try
        {
            store.Write(new RunState(Environment.ProcessId, RunStateStore.CurrentProcessStart(), dependencies.ExePath ?? string.Empty, server.Port, token, options.Dir, tailnet, dependencies.TimeProvider.GetUtcNow()));
            if (tailnet is not null)
            {
                var result = await tailscale.ServeAsync(tailnet.HttpsPort, tailnet.BackendPort, CancellationToken.None);
                if (result.TimedOut || result.ExitCode != 0)
                {
                    dependencies.Error.WriteLine($"cfgdash: tailscale serve failed: {ShortLine(result.TimedOut ? "timed out" : result.StandardError)}");
                    await RemoveServeHandlerIfOursAsync(tailscale, tailnet, log);
                    return ExitPrerequisite;
                }

                published = true;
            }

            log.Write($"start version={HostDependencies.Version} port={server.Port} tailnet={(tailnet is null ? "off" : $"{tailnet.DnsName}:{tailnet.HttpsPort}")}");
            var url = PrintUrls(dependencies.Out, server.Port, token, tailnet);
            if (tailnetOptions is not null)
            {
                dependencies.Out.WriteLine($"Allowed login: {(tailnetOptions.AllowedLogins.Count == 0 ? "none" : string.Join(", ", tailnetOptions.AllowedLogins))}");
                if (tailnetOptions.MagicDnsSuffix.Length > 0)
                {
                    dependencies.Out.WriteLine($"Tagged devices of {core.Environment.Tailnet!.TailnetName} allowed{(tailnetOptions.AllowedTags.Count == 0 ? string.Empty : $" (tags: {string.Join(", ", tailnetOptions.AllowedTags)})")}");
                }
            }

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
            if (published && !await RemoveServeHandlerAsync(tailscale, tailnet!, log))
            {
                dependencies.Error.WriteLine($"cfgdash: could not confirm that the tailscale serve handler is gone; remove it with: tailscale serve --https={tailnet!.HttpsPort} off");
            }

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

    /// <summary>Removes the serve handler recorded in a stale <c>run.json</c>, only when serve status proves it is cfgdash's (exact <c>&lt;dnsName&gt;:&lt;httpsPort&gt;</c> key whose <c>/</c> proxies to the recorded backend port).</summary>
    private static async Task RemoveRecordedServeHandlerAsync(RunState state, CoreServices core)
    {
        if (state.Tailnet is { } tailnet && core.Environment.Tools.GetValueOrDefault("tailscale") is { } executable)
        {
            await RemoveServeHandlerIfOursAsync(new TailscaleCli(core.Processes, executable), tailnet, null);
        }
    }

    private static async Task RemoveServeHandlerIfOursAsync(TailscaleCli tailscale, RunTailnet tailnet, CfgdashLog? log)
    {
        if ((await tailscale.ServeStatusAsync(CancellationToken.None))?.IsOurs(tailnet) == true)
        {
            await RemoveServeHandlerAsync(tailscale, tailnet, log);
        }
    }

    /// <summary><c>serve --https=&lt;port&gt; off</c>, then confirms the port is gone from serve status.</summary>
    private static async Task<bool> RemoveServeHandlerAsync(TailscaleCli tailscale, RunTailnet tailnet, CfgdashLog? log)
    {
        var off = await tailscale.ServeOffAsync(tailnet.HttpsPort, CancellationToken.None);
        var removed = !off.TimedOut && off.ExitCode == 0
            && await tailscale.ServeStatusAsync(CancellationToken.None) is { } status && !status.UsesPort(tailnet.HttpsPort);
        log?.Write(removed ? "serve-off ok" : "serve-off failed");
        return removed;
    }

    /// <summary>The tailnet listener options when every <c>--tailnet</c> precondition holds; otherwise prints the first failure and returns null.</summary>
    private static TailnetListenerOptions? CheckTailnet(CoreServices core, TextWriter error)
    {
        var environment = core.Environment;
        var status = environment.Tailnet;
        var dnsName = status is null ? string.Empty : TailnetRules.TrimDot(status.DnsName);
        var logins = TailnetRules.AllowedLogins(environment);
        var problem =
            !environment.HasTool("tailscale") ? "tailscale is not installed"
            : status is null ? "tailscale is not running or not logged in"
            : status.BackendState != "Running" ? $"tailscale is not running (state: {status.BackendState})"
            : !status.MagicDnsEnabled ? "MagicDNS is off for this tailnet; enable it in the tailscale admin console"
            : dnsName.Length == 0 || !status.CertDomains.Any(d => string.Equals(TailnetRules.TrimDot(d), dnsName, StringComparison.OrdinalIgnoreCase))
                ? "tailnet HTTPS certificates are not enabled; enable HTTPS in the tailscale admin console"
            : logins.Count == 0 && status.MagicDnsSuffix.Length == 0
                ? $"no allowed tailnet identity can be derived; set allowedLogins in {CfgdashPaths.OverridesFile(core.EnvironmentReader)}"
            : null;
        if (problem is not null)
        {
            error.WriteLine($"cfgdash: {problem}");
            return null;
        }

        return new TailnetListenerOptions(dnsName, logins, status!.MagicDnsSuffix, environment.AllowedTags);
    }

    private static string ShortLine(string text)
    {
        var line = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length == 0 ? "no details" : line.Length > 300 ? line[..300] : line;
    }

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
