using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Config;
using Cfgdash.Core.Edits;

namespace Cfgdash.Host;

internal sealed partial class CfgdashServer
{
    private void MapCfgdashSettings(HostDependencies dependencies)
    {
        var settings = new Lazy<CfgdashSettingsService>(() => new CfgdashSettingsService(
            new CfgdashConfig(dependencies.ConfigDir(core.EnvironmentReader), dependencies.CacheDir(core.EnvironmentReader), dependencies.TimeProvider),
            dependencies.Autostart(core, dependencies.ExePath)));
        app.MapGet("/api/v2/cfgdash-settings", async ctx =>
        {
            if (OnTailnet(ctx))
            {
                await WriteNotFoundAsync(ctx);
                return;
            }

            await WriteJsonAsync(ctx, StatusCodes.Status200OK, await settings.Value.GetAsync(ctx.RequestAborted));
        });
        app.MapPut("/api/v2/cfgdash-settings", async ctx =>
        {
            if (OnTailnet(ctx))
            {
                await WriteNotFoundAsync(ctx);
                return;
            }

            if (await ReadEditBodyAsync(ctx) is not { } body)
            {
                await WriteEditForbiddenAsync(ctx);
                return;
            }

            JsonNode? partial;
            try
            {
                partial = JsonNode.Parse(body);
            }
            catch (Exception e) when (e is JsonException or ArgumentException)
            {
                await WriteJsonAsync(ctx, StatusCodes.Status400BadRequest, CfgdashSettingsService.InvalidSetting(null, "bad-request"));
                return;
            }

            var (status, result) = await settings.Value.UpdateAsync(partial, "ui", ctx.RequestAborted);
            await WriteJsonAsync(ctx, status, result);
        });
    }

    private async Task RevertAsync(HttpContext ctx, Lazy<IEditService> edits)
    {
        if (OnTailnet(ctx))
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        if (await ReadEditBodyAsync(ctx) is not { } body)
        {
            await WriteEditForbiddenAsync(ctx);
            return;
        }

        bool isObject;
        try
        {
            isObject = JsonNode.Parse(body) is JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            isObject = false;
        }

        if (!isObject)
        {
            await WriteJsonAsync(ctx, StatusCodes.Status400BadRequest, new { error = "bad-request" });
            return;
        }

        var auditId = ctx.Request.RouteValues["auditId"] as string ?? string.Empty;
        await WriteEditResultAsync(ctx, await edits.Value.RevertAsync(auditId, ctx.RequestAborted));
    }

    /// <summary>Starts the start-dir snapshot build in the background so the first page load finds it ready.</summary>
    internal void WarmStartSnapshot() =>
        _ = snapshots.GetOrAdd(startDir, NewBuild).Value.ContinueWith(
            t => log.Error(t.Exception!),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
