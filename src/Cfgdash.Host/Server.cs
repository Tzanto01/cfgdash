using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Serialization;
using Cfgdash.Core;
using Cfgdash.Core.Snapshot;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Cfgdash.Host;

/// <summary><paramref name="Port"/> 0 picks a free port; <paramref name="Tailnet"/> adds the loopback backend listener that only <c>tailscale serve</c> targets.</summary>
internal sealed record ServerOptions(int Port, string StartDir, string Token, TailnetListenerOptions? Tailnet = null);

/// <summary>Tailnet listener settings. <paramref name="HttpsPort"/> null means the bound UI port. Constructing the server with these never publishes anything to tailscale.</summary>
internal sealed record TailnetListenerOptions(string DnsName, IReadOnlyList<string> AllowedLogins, string MagicDnsSuffix, IReadOnlyList<string> AllowedTags, int? HttpsPort = null);

internal sealed record DirEntry(string Name, string Path);

internal sealed record DirSuggestion(string Label, string Path);

internal sealed record DirsResponse(
    string Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Parent,
    IReadOnlyList<DirEntry> Entries,
    IReadOnlyList<DirSuggestion> Suggestions);

/// <summary>The cfgdash HTTP server: Kestrel on 127.0.0.1 only, the security pipeline, <c>/api/v1</c> and the embedded UI.</summary>
internal sealed class CfgdashServer : IAsyncDisposable
{
    public const string SessionCookie = "cfgdash_session";
    private const string ContentSecurityPolicy = "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'";
    private const string NotBuiltPage = "<!doctype html><html><head><meta charset=\"utf-8\"><title>cfgdash</title></head><body><p>cfgdash UI not built</p></body></html>";
    private const int MaxDirEntries = 500;
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromSeconds(30);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly WebApplication app;
    private readonly ServerOptions options;
    private readonly CoreServices core;
    private readonly CfgdashLog log;
    private readonly TimeProvider time;
    private readonly IFileProvider? ui;
    private readonly TokenVerifier token;
    private readonly TailnetIdentity? identity;
    private readonly string startDir;
    private readonly ConcurrentDictionary<string, Lazy<Task<SnapshotResult>>> snapshots = new(PathComparer);
    private ListenOptions? uiListen;
    private ListenOptions? backendListen;
    private Listeners listeners = Listeners.None;
    private Task idleLoop = Task.CompletedTask;
    private long lastActivityTicks;
    private string? stopReason;

    private CfgdashServer(ServerOptions options, CoreServices core, HostDependencies dependencies, CfgdashLog log)
    {
        this.options = options;
        this.core = core;
        this.log = log;
        time = dependencies.TimeProvider;
        ui = dependencies.UiFiles;
        token = new TokenVerifier(options.Token);
        startDir = NormalizeDir(options.StartDir) ?? options.StartDir;
        if (options.Tailnet is { } tailnet)
        {
            core.Environment.Tools.TryGetValue("tailscale", out var tailscalePath);
            identity = new TailnetIdentity(tailnet, new TailscaleCli(core.Processes, tailscalePath), time);
        }

        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(CfgdashServer).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Loopback, options.Port, listen => uiListen = listen);
            if (options.Tailnet is not null)
            {
                kestrel.Listen(IPAddress.Loopback, 0, listen => backendListen = listen);
            }
        });
        builder.Services.AddRoutingCore();

        app = builder.Build();
        app.Use(GuardAsync);
        if (ui is not null)
        {
            app.UseStaticFiles(new StaticFileOptions { FileProvider = ui });
        }

        app.UseRouting();
        app.MapGet("/api/v1/health", ctx => WriteJsonAsync(ctx, StatusCodes.Status200OK, new { status = "ok", version = HostDependencies.Version, schemaVersion = HarnessSnapshot.CurrentSchemaVersion }));
        app.MapGet("/api/v1/snapshot", ctx => SnapshotAsync(ctx));
        app.MapGet("/api/v1/files/{id}", ctx => FileAsync(ctx));
        app.MapGet("/api/v1/details/{id}", ctx => DetailAsync(ctx));
        app.MapGet("/api/v1/search", ctx => SearchAsync(ctx));
        app.MapGet("/api/v1/dirs", ctx => DirsAsync(ctx));
        app.MapPost("/api/v1/shutdown", ctx => ShutdownAsync(ctx));
        app.MapFallback("{**path}", ctx => FallbackAsync(ctx));
    }

    /// <summary>The bound UI port (the real one when started with port 0).</summary>
    public int Port => listeners.UiPort;

    /// <summary>The bound tailnet backend port; null without tailnet options.</summary>
    public int? BackendPort => listeners.BackendPort == 0 ? null : listeners.BackendPort;

    /// <summary>The tailnet HTTPS port the Host allowlist expects; null without tailnet options.</summary>
    public int? HttpsPort => options.Tailnet is null ? null : listeners.HttpsPort;

    /// <summary>Starts listening. Throws <see cref="IOException"/> when a port cannot be bound.</summary>
    public static async Task<CfgdashServer> StartAsync(ServerOptions options, CoreServices core, HostDependencies dependencies, CfgdashLog log, CancellationToken cancellationToken)
    {
        var server = new CfgdashServer(options, core, dependencies, log);
        try
        {
            await server.app.StartAsync(cancellationToken);
        }
        catch
        {
            await server.app.DisposeAsync();
            throw;
        }

        server.OnStarted();
        return server;
    }

    /// <summary>Completes when a stop was requested; returns the reason (<c>requested</c>, <c>idle</c> or <c>signal</c>).</summary>
    public async Task<string> WaitForShutdownAsync()
    {
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (app.Lifetime.ApplicationStopping.Register(() => stopping.TrySetResult()))
        {
            await stopping.Task;
        }

        return Volatile.Read(ref stopReason) ?? "signal";
    }

    public void Stop(string reason)
    {
        Interlocked.CompareExchange(ref stopReason, reason, null);
        app.Lifetime.StopApplication();
    }

    public async ValueTask DisposeAsync()
    {
        app.Lifetime.StopApplication();
        await app.StopAsync();
        await idleLoop;
        await app.DisposeAsync();
    }

    /// <summary>Null unless an existing fully qualified directory. On Windows a path starting with a separator (UNC <c>\\server\share</c>, <c>//server</c>, device <c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>) is rejected before any file system access.</summary>
    internal static string? NormalizeDir(string? path)
    {
        if (string.IsNullOrEmpty(path)
            || (OperatingSystem.IsWindows() && path[0] is '\\' or '/')
            || !Path.IsPathFullyQualified(path)
            || !Directory.Exists(path))
        {
            return null;
        }

        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? string.Empty;
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    private void OnStarted()
    {
        var uiPort = ((IPEndPoint)uiListen!.EndPoint).Port;
        var backendPort = backendListen is null ? 0 : ((IPEndPoint)backendListen.EndPoint).Port;
        var httpsPort = options.Tailnet?.HttpsPort ?? uiPort;
        var dnsName = options.Tailnet is null ? null : TailnetRules.TrimDot(options.Tailnet.DnsName);
        Volatile.Write(ref listeners, new Listeners(
            uiPort,
            backendPort,
            httpsPort,
            [$"127.0.0.1:{uiPort}", $"localhost:{uiPort}"],
            dnsName is null ? [] : [$"{dnsName}:{httpsPort}", dnsName]));
        Interlocked.Exchange(ref lastActivityTicks, time.GetUtcNow().UtcTicks);
        idleLoop = IdleLoopAsync();
    }

    private async Task GuardAsync(HttpContext ctx, RequestDelegate next)
    {
        try
        {
            await CheckAsync(ctx, next);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            log.Error(e);
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            }
        }
        finally
        {
            log.Request(ctx.Request.Method, ctx.Request.Path.Value, ctx.Response.StatusCode);
        }
    }

    private async Task CheckAsync(HttpContext ctx, RequestDelegate next)
    {
        var request = ctx.Request;
        var headers = ctx.Response.Headers;
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        if (request.Path.StartsWithSegments("/api"))
        {
            headers.CacheControl = "no-store";
        }

        var current = Volatile.Read(ref listeners);
        var tailnet = current.BackendPort != 0 && ctx.Connection.LocalPort == current.BackendPort;
        var host = request.Headers.Host;
        if (host.Count != 1 || host[0] is not { } hostValue || !(tailnet ? current.TailnetHosts : current.UiHosts).Contains(hostValue, StringComparer.OrdinalIgnoreCase))
        {
            await WriteJsonAsync(ctx, StatusCodes.Status400BadRequest, new { error = "bad-host" });
            return;
        }

        if (IsCrossSite(request.Headers["Sec-Fetch-Site"], request.Headers.Origin, (tailnet ? "https://" : "http://") + hostValue))
        {
            await WriteJsonAsync(ctx, StatusCodes.Status403Forbidden, new { error = "forbidden", reason = "cross-site" });
            return;
        }

        if (HttpMethods.IsGet(request.Method) && request.Path.Value == "/" && request.Query.TryGetValue("token", out var queryToken))
        {
            if (queryToken.Count != 1 || !token.Verify(queryToken[0]))
            {
                await WriteUnauthenticatedAsync(ctx);
                return;
            }

            if (tailnet && !await identity!.IsAllowedAsync(request, ctx.RequestAborted))
            {
                await WriteForbiddenIdentityAsync(ctx);
                return;
            }

            Touch();
            ctx.Response.Cookies.Append(SessionCookie, token.SessionValue(tailnet), new CookieOptions
            {
                Path = "/",
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = tailnet,
            });
            ctx.Response.Redirect("/");
            return;
        }

        var authenticated = token.Verify(Bearer(request)) || token.VerifySession(request.Cookies[SessionCookie], tailnet);
        if (tailnet)
        {
            if (!authenticated)
            {
                await WriteUnauthenticatedAsync(ctx);
                return;
            }

            if (!await identity!.IsAllowedAsync(request, ctx.RequestAborted))
            {
                await WriteForbiddenIdentityAsync(ctx);
                return;
            }
        }
        else if (!authenticated && request.Path.StartsWithSegments("/api"))
        {
            await WriteUnauthenticatedAsync(ctx);
            return;
        }

        if (authenticated)
        {
            Touch();
        }

        await next(ctx);
    }

    private static bool IsCrossSite(StringValues fetchSite, StringValues origin, string expectedOrigin) =>
        (fetchSite.Count > 0 && !(fetchSite.Count == 1 && (string.Equals(fetchSite[0], "same-origin", StringComparison.OrdinalIgnoreCase) || string.Equals(fetchSite[0], "none", StringComparison.OrdinalIgnoreCase))))
        || (origin.Count > 0 && !(origin.Count == 1 && string.Equals(origin[0], expectedOrigin, StringComparison.OrdinalIgnoreCase)));

    private static string? Bearer(HttpRequest request)
    {
        var authorization = request.Headers.Authorization;
        return authorization.Count == 1 && authorization[0] is { } value && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value["Bearer ".Length..].Trim()
            : null;
    }

    private void Touch() => Interlocked.Exchange(ref lastActivityTicks, time.GetUtcNow().UtcTicks);

    private async Task IdleLoopAsync()
    {
        using var timer = new PeriodicTimer(IdleCheckInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
            {
                if (time.GetUtcNow().UtcTicks - Interlocked.Read(ref lastActivityTicks) >= IdleLimit.Ticks)
                {
                    Stop("idle");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SnapshotAsync(HttpContext ctx)
    {
        if (!TryGetDir(ctx.Request.Query["dir"], out var dir))
        {
            await WriteBadDirAsync(ctx);
            return;
        }

        var refresh = string.Equals(ctx.Request.Query["refresh"], "true", StringComparison.OrdinalIgnoreCase);
        if (await GetSnapshotAsync(ctx, dir, refresh) is { } result)
        {
            await WriteJsonAsync(ctx, StatusCodes.Status200OK, result.Snapshot);
        }
    }

    private async Task FileAsync(HttpContext ctx)
    {
        if (!TryGetDir(ctx.Request.Query["dir"], out var dir))
        {
            await WriteBadDirAsync(ctx);
            return;
        }

        if (await GetSnapshotAsync(ctx, dir, refresh: false) is not { } result)
        {
            return;
        }

        if (ctx.Request.RouteValues["id"] is not string id || !result.Files.TryGet(id, out var file))
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        FileContent content;
        try
        {
            content = await core.FileReader.ReadAsync(file, ctx.RequestAborted);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            await WriteNotFoundAsync(ctx);
            return;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error(e);
            await WriteJsonAsync(ctx, StatusCodes.Status500InternalServerError, new { error = "read-failed" });
            return;
        }

        await WriteJsonAsync(ctx, StatusCodes.Status200OK, content);
    }

    private async Task DetailAsync(HttpContext ctx)
    {
        if (!TryGetDir(ctx.Request.Query["dir"], out var dir))
        {
            await WriteBadDirAsync(ctx);
            return;
        }

        if (await GetSnapshotAsync(ctx, dir, refresh: false) is not { } result)
        {
            return;
        }

        if (ctx.Request.RouteValues["id"] is not string id || result.DetailIds?.Contains(id) != true)
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        DetailResponse? detail;
        try
        {
            detail = await core.Details.ReadAsync(dir, id, ctx.RequestAborted);
        }
        catch (TimeoutException)
        {
            await WriteJsonAsync(ctx, StatusCodes.Status503ServiceUnavailable, new { error = "unavailable" });
            return;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error(e);
            await WriteJsonAsync(ctx, StatusCodes.Status500InternalServerError, new { error = "read-failed" });
            return;
        }

        if (detail is null)
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        await WriteJsonAsync(ctx, StatusCodes.Status200OK, detail);
    }

    private async Task SearchAsync(HttpContext ctx)
    {
        var q = ctx.Request.Query["q"];
        if (q.Count != 1 || q[0] is not { Length: >= 2 } query)
        {
            await WriteJsonAsync(ctx, StatusCodes.Status400BadRequest, new { error = "bad-query" });
            return;
        }

        if (!TryGetDir(ctx.Request.Query["dir"], out var dir))
        {
            await WriteBadDirAsync(ctx);
            return;
        }

        if (await GetSnapshotAsync(ctx, dir, refresh: false) is not { } result)
        {
            return;
        }

        var aborted = ctx.RequestAborted;
        var response = await Task.Run(() => core.Search.Search(result.Snapshot, result.Files, core.FileReader, query, aborted), aborted);
        await WriteJsonAsync(ctx, StatusCodes.Status200OK, response);
    }

    private Task DirsAsync(HttpContext ctx)
    {
        if (!TryGetDir(ctx.Request.Query["path"], out var dir))
        {
            return WriteBadDirAsync(ctx);
        }

        var parent = Directory.GetParent(dir)?.FullName;
        return WriteJsonAsync(ctx, StatusCodes.Status200OK, new DirsResponse(Slash(dir), parent is null ? null : Slash(parent), ListDirs(dir), Suggestions()));
    }

    private Task ShutdownAsync(HttpContext ctx)
    {
        if (!token.Verify(Bearer(ctx.Request)))
        {
            return WriteUnauthenticatedAsync(ctx);
        }

        ctx.Response.OnCompleted(() =>
        {
            Stop("requested");
            return Task.CompletedTask;
        });
        return WriteJsonAsync(ctx, StatusCodes.Status202Accepted, new { status = "stopping" });
    }

    private async Task FallbackAsync(HttpContext ctx)
    {
        var request = ctx.Request;
        if (request.Path.StartsWithSegments("/api"))
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        ctx.Response.ContentType = "text/html; charset=utf-8";
        if (HttpMethods.IsHead(request.Method))
        {
            return;
        }

        var index = ui?.GetFileInfo("index.html");
        if (index is not { Exists: true })
        {
            await ctx.Response.WriteAsync(NotBuiltPage, ctx.RequestAborted);
            return;
        }

        await using var stream = index.CreateReadStream();
        await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }

    private async Task<SnapshotResult?> GetSnapshotAsync(HttpContext ctx, string dir, bool refresh)
    {
        var build = refresh ? snapshots[dir] = NewBuild(dir) : snapshots.GetOrAdd(dir, NewBuild);
        try
        {
            return await build.Value.WaitAsync(ctx.RequestAborted);
        }
        catch (Exception e) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            log.Error(e);
            await WriteJsonAsync(ctx, StatusCodes.Status500InternalServerError, new { error = "snapshot-failed" });
            return null;
        }
    }

    private Lazy<Task<SnapshotResult>> NewBuild(string dir)
    {
        Lazy<Task<SnapshotResult>>? build = null;
        build = new Lazy<Task<SnapshotResult>>(() => Task.Run(() => BuildAsync(dir, build!)));
        return build;
    }

    private async Task<SnapshotResult> BuildAsync(string dir, Lazy<Task<SnapshotResult>> build)
    {
        try
        {
            return await core.Snapshots.BuildAsync(dir, app.Lifetime.ApplicationStopping);
        }
        catch
        {
            snapshots.TryRemove(new KeyValuePair<string, Lazy<Task<SnapshotResult>>>(dir, build));
            throw;
        }
    }

    private bool TryGetDir(StringValues value, [NotNullWhen(true)] out string? dir)
    {
        dir = value.Count switch
        {
            0 => startDir,
            1 => NormalizeDir(value[0]),
            _ => null,
        };
        return dir is not null;
    }

    private static List<DirEntry> ListDirs(string dir)
    {
        try
        {
            return new DirectoryInfo(dir)
                .EnumerateDirectories("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
                .Select(d => d.Name)
                .Order(StringComparer.OrdinalIgnoreCase)
                .Take(MaxDirEntries)
                .Select(name => new DirEntry(name, Slash(Path.Combine(dir, name))))
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private List<DirSuggestion> Suggestions()
    {
        var environment = core.Environment;
        List<(string? Label, string? Path)> candidates =
        [
            ("Start directory", startDir),
            ("Workspace root", environment.GetPath(PathKeys.WorkspaceRoot)),
            ("Home", environment.HomeDir),
            ("Config repo", environment.GetPath(PathKeys.ConfigRepo)),
            .. environment.KnownProjectDirs.Select(d => ((string?)null, (string?)d)),
        ];
        var seen = new HashSet<string>(PathComparer);
        var suggestions = new List<DirSuggestion>();
        foreach (var (label, path) in candidates)
        {
            if (NormalizeDir(path) is { } normalized && seen.Add(normalized))
            {
                suggestions.Add(new DirSuggestion(label ?? Slash(normalized), Slash(normalized)));
            }
        }

        return suggestions;
    }

    private static string Slash(string path) => Path.DirectorySeparatorChar == '\\' ? path.Replace('\\', '/') : path;

    private static Task WriteJsonAsync(HttpContext ctx, int status, object body)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(body, body.GetType(), SnapshotJson.Options, ctx.RequestAborted);
    }

    private static Task WriteUnauthenticatedAsync(HttpContext ctx) =>
        WriteJsonAsync(ctx, StatusCodes.Status401Unauthorized, new { error = "unauthenticated" });

    private static Task WriteForbiddenIdentityAsync(HttpContext ctx) =>
        WriteJsonAsync(ctx, StatusCodes.Status403Forbidden, new { error = "forbidden", reason = "tailnet-identity" });

    private static Task WriteBadDirAsync(HttpContext ctx) =>
        WriteJsonAsync(ctx, StatusCodes.Status400BadRequest, new { error = "bad-dir" });

    private static Task WriteNotFoundAsync(HttpContext ctx) =>
        WriteJsonAsync(ctx, StatusCodes.Status404NotFound, new { error = "not-found" });

    private sealed record Listeners(int UiPort, int BackendPort, int HttpsPort, string[] UiHosts, string[] TailnetHosts)
    {
        public static readonly Listeners None = new(0, 0, 0, [], []);
    }
}
