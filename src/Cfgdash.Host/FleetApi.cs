using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core;
using Cfgdash.Core.Fleet;

namespace Cfgdash.Host;

internal sealed partial class CfgdashServer
{
    /// <summary>Marks a fleet peer request (fleet-v1); only the tailnet listener honors it.</summary>
    internal const string FleetHeader = "X-Cfgdash-Fleet";
    private const string AgainstExport = "export:";
    private const string AgainstPeer = "peer:";

    private string exportsDir;

    /// <summary>Whether this machine answers fleet peers; read from <c>config.json</c> on every call.</summary>
    internal Func<bool> FleetEnabled { get; set; }

    internal FleetPeers Fleet { get; set; }

    [MemberNotNull(nameof(exportsDir), nameof(FleetEnabled), nameof(Fleet))]
    private void MapFleet(HostDependencies dependencies)
    {
        var configFile = Path.Combine(CfgdashPaths.ConfigDir(core.EnvironmentReader), "config.json");
        FleetEnabled = () => FleetConfig.Enabled(configFile);
        exportsDir = Path.Combine(dependencies.CacheDir(core.EnvironmentReader), "exports");
        core.Environment.Tools.TryGetValue("tailscale", out var tailscalePath);
        Fleet = new FleetPeers(
            core.Processes,
            tailscalePath,
            options.Tailnet?.AllowedLogins ?? TailnetRules.AllowedLogins(core.Environment),
            options.Tailnet?.AllowedTags ?? core.Environment.AllowedTags,
            core.Environment,
            time);
        app.MapGet("/api/v1/exports", ctx => WriteJsonAsync(ctx, StatusCodes.Status200OK, new { exports = ExportStore.List(exportsDir) }));
        app.MapGet("/api/v1/compare", ctx => CompareAsync(ctx));
        app.MapGet("/api/v1/fleet", ctx => FleetAsync(ctx));
    }

    /// <summary>The tailnet listener's fleet branch: no token, no cookie, no idle touch; a GET without query of the snapshot, findings or health path from an allowed peer, with fleet enabled here.</summary>
    private async Task FleetRequestAsync(HttpContext ctx, RequestDelegate next)
    {
        var request = ctx.Request;
        var header = request.Headers[FleetHeader];
        if (header.Count != 1
            || header[0] != "1"
            || !FleetEnabled()
            || !HttpMethods.IsGet(request.Method)
            || request.Path.Value is not ("/api/v1/snapshot" or "/api/v1/findings" or "/api/v1/health")
            || request.QueryString.HasValue
            || !await identity!.IsAllowedPeerAsync(request, ctx.RequestAborted))
        {
            await WriteJsonAsync(ctx, StatusCodes.Status403Forbidden, new { error = "forbidden", reason = "fleet" });
            return;
        }

        await next(ctx);
    }

    private async Task FleetAsync(HttpContext ctx) =>
        await WriteJsonAsync(ctx, StatusCodes.Status200OK, await Fleet.GetAsync(ctx.RequestAborted));

    private async Task CompareAsync(HttpContext ctx)
    {
        if (!TryGetDir(ctx.Request.Query["dir"], out var dir))
        {
            await WriteBadDirAsync(ctx);
            return;
        }

        var against = ctx.Request.Query["against"];
        if (against.Count != 1 || against[0] is not { } value)
        {
            await WriteBadAgainstAsync(ctx);
            return;
        }

        JsonObject other;
        if (value.StartsWith(AgainstExport, StringComparison.Ordinal))
        {
            var id = value[AgainstExport.Length..];
            if (!ExportStore.IsValidId(id))
            {
                await WriteBadAgainstAsync(ctx);
                return;
            }

            JsonObject? export;
            try
            {
                export = ExportStore.Load(exportsDir, id);
            }
            catch (InvalidDataException)
            {
                await WriteJsonAsync(ctx, StatusCodes.Status422UnprocessableEntity, new { error = "bad-export" });
                return;
            }

            if (export is null)
            {
                await WriteNotFoundAsync(ctx);
                return;
            }

            other = export;
        }
        else if (value.StartsWith(AgainstPeer, StringComparison.Ordinal))
        {
            var peer = await Fleet.FetchSnapshotAsync(value[AgainstPeer.Length..], ctx.RequestAborted);
            if (!peer.Known)
            {
                await WriteNotFoundAsync(ctx);
                return;
            }

            if (peer.Snapshot is null)
            {
                await WriteJsonAsync(ctx, StatusCodes.Status502BadGateway, new { error = "peer-unavailable", reason = peer.Reason });
                return;
            }

            other = peer.Snapshot;
        }
        else
        {
            await WriteBadAgainstAsync(ctx);
            return;
        }

        if (await GetSnapshotAsync(ctx, dir, refresh: false) is not { } result)
        {
            return;
        }

        var local = JsonSerializer.SerializeToNode(result.Snapshot, SnapshotJson.Options)!.AsObject();
        await WriteJsonAsync(ctx, StatusCodes.Status200OK, SnapshotDiff.Compare(local, other));
    }

    private static Task WriteBadAgainstAsync(HttpContext ctx) =>
        WriteJsonAsync(ctx, StatusCodes.Status400BadRequest, new { error = "bad-against" });
}
