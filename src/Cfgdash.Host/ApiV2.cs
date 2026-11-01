using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core;
using Cfgdash.Core.Edits;

namespace Cfgdash.Host;

internal sealed partial class CfgdashServer
{
    private const int MaxEditBodyBytes = 64 * 1024;
    private const int DefaultAuditLimit = 50;
    private const string EditRequestHeader = "X-Cfgdash-Request";

    private void MapApiV2(HostDependencies dependencies)
    {
        var edits = new Lazy<IEditService>(() => CfgdashCore.CreateEdits(core, dependencies.CacheDir(core.EnvironmentReader), dependencies.TimeProvider));
        app.MapGet("/api/v2/settings", ctx => EditSettingsAsync(ctx, edits));
        app.MapPost("/api/v2/proposals", ctx => ProposeAsync(ctx, edits));
        app.MapPost("/api/v2/proposals/{id}/apply", ctx => ApplyAsync(ctx, edits));
        app.MapGet("/api/v2/audit", ctx => AuditAsync(ctx, edits));
        app.Map("/api/v2/{**rest}", (RequestDelegate)WriteNotFoundAsync);
    }

    private bool OnTailnet(HttpContext ctx)
    {
        var current = Volatile.Read(ref listeners);
        return current.BackendPort != 0 && ctx.Connection.LocalPort == current.BackendPort;
    }

    private async Task EditSettingsAsync(HttpContext ctx, Lazy<IEditService> edits)
    {
        if (OnTailnet(ctx))
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        if (!TryGetDir(ctx.Request.Query["dir"], out var dir))
        {
            await WriteBadDirAsync(ctx);
            return;
        }

        await WriteJsonAsync(ctx, StatusCodes.Status200OK, await edits.Value.GetSettingsAsync(dir, ctx.RequestAborted));
    }

    private async Task ProposeAsync(HttpContext ctx, Lazy<IEditService> edits)
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

        if (!TryReadProposal(body, out var rawDir, out var changes))
        {
            await WriteJsonAsync(ctx, StatusCodes.Status400BadRequest, new JsonObject { ["error"] = "invalid-change", ["key"] = null, ["reason"] = "bad-request" });
            return;
        }

        if (NormalizeDir(rawDir) is not { } dir)
        {
            await WriteBadDirAsync(ctx);
            return;
        }

        await WriteEditResultAsync(ctx, await edits.Value.ProposeAsync(dir, changes, ctx.RequestAborted));
    }

    private async Task ApplyAsync(HttpContext ctx, Lazy<IEditService> edits)
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

        if (!TryReadConfirm(body, out var confirm))
        {
            await WriteJsonAsync(ctx, StatusCodes.Status400BadRequest, new { error = "confirm-mismatch" });
            return;
        }

        var id = ctx.Request.RouteValues["id"] as string ?? string.Empty;
        await WriteEditResultAsync(ctx, await edits.Value.ApplyAsync(id, confirm, ctx.RequestAborted));
    }

    private Task AuditAsync(HttpContext ctx, Lazy<IEditService> edits)
    {
        if (OnTailnet(ctx))
        {
            return WriteNotFoundAsync(ctx);
        }

        var limit = int.TryParse(ctx.Request.Query["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : DefaultAuditLimit;
        return WriteJsonAsync(ctx, StatusCodes.Status200OK, edits.Value.ReadAudit(limit));
    }

    /// <summary>The request body when the edits-v1 POST rules hold (one UI-listener <c>Origin</c>, one <c>X-Cfgdash-Request: 1</c>, at most 64 KiB read into a bounded buffer); null otherwise.</summary>
    private async Task<byte[]?> ReadEditBodyAsync(HttpContext ctx)
    {
        var request = ctx.Request;
        var uiPort = Volatile.Read(ref listeners).UiPort;
        var origin = request.Headers.Origin;
        var marker = request.Headers[EditRequestHeader];
        if (origin.Count != 1
            || !(string.Equals(origin[0], $"http://127.0.0.1:{uiPort}", StringComparison.OrdinalIgnoreCase) || string.Equals(origin[0], $"http://localhost:{uiPort}", StringComparison.OrdinalIgnoreCase))
            || marker.Count != 1
            || !string.Equals(marker[0], "1", StringComparison.Ordinal)
            || request.ContentLength > MaxEditBodyBytes)
        {
            return null;
        }

        var buffer = new byte[MaxEditBodyBytes + 1];
        var total = 0;
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer.AsMemory(total), ctx.RequestAborted)) > 0)
            {
                total += read;
                if (total > MaxEditBodyBytes)
                {
                    return null;
                }
            }
        }
        catch (BadHttpRequestException)
        {
            return null;
        }

        return buffer[..total];
    }

    private static bool TryReadProposal(byte[] body, out string? dir, out JsonNode? changes)
    {
        dir = null;
        changes = null;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root)
            {
                return false;
            }

            dir = root["dir"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            changes = root["changes"];
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadConfirm(byte[] body, out string? confirm)
    {
        confirm = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["confirm"] is JsonValue value && value.TryGetValue<string>(out var text))
            {
                confirm = text;
            }

            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private Task WriteEditResultAsync(HttpContext ctx, EditResult result)
    {
        if (result.Wrote)
        {
            snapshots.Clear();
        }

        var status = result.Status switch
        {
            EditStatus.Ok => StatusCodes.Status200OK,
            EditStatus.InvalidChange or EditStatus.ConfirmMismatch => StatusCodes.Status400BadRequest,
            EditStatus.NotFound => StatusCodes.Status404NotFound,
            EditStatus.Stale => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status500InternalServerError,
        };
        return WriteJsonAsync(ctx, status, result.Body);
    }

    private static Task WriteEditForbiddenAsync(HttpContext ctx) =>
        WriteJsonAsync(ctx, StatusCodes.Status403Forbidden, new { error = "forbidden" });
}
