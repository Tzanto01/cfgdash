using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core;

namespace Cfgdash.Host;

internal sealed partial class CfgdashServer
{
    private const int MaxFileEditBodyBytes = 320 * 1024;

    private void MapApiV2Files() => app.MapPost("/api/v2/files/{id}/proposals", ctx => ProposeFileAsync(ctx));

    /// <summary>The api-v1 file answer plus the edits-v2 <c>edit</c> object on the UI listener; an editable file is served as its raw text.</summary>
    private async Task<object> FileAnswerAsync(HttpContext ctx, string dir, RegisteredFile file, FileContent content)
    {
        if (OnTailnet(ctx) || editService is null)
        {
            return content;
        }

        var info = await editService.Value.GetFileEditAsync(dir, file, ctx.RequestAborted);
        var body = (JsonObject)JsonSerializer.SerializeToNode(info.Content is null ? content : content with { Content = info.Content, Truncated = false }, SnapshotJson.Options)!;
        var edit = new JsonObject { ["editable"] = info.Editable };
        if (info.Reason is not null)
        {
            edit["reason"] = info.Reason;
        }

        if (info.Sha256 is not null)
        {
            edit["sha256"] = info.Sha256;
            edit["gitTracked"] = info.GitTracked;
        }

        body["edit"] = edit;
        return body;
    }

    private async Task ProposeFileAsync(HttpContext ctx)
    {
        if (OnTailnet(ctx) || editService is null)
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        if (await ReadEditBodyAsync(ctx, MaxFileEditBodyBytes) is not { } body)
        {
            await WriteEditForbiddenAsync(ctx);
            return;
        }

        if (!TryReadFileProposal(body, out var content, out var sha256Before))
        {
            await WriteJsonAsync(ctx, StatusCodes.Status400BadRequest, new JsonObject { ["error"] = "invalid-change", ["key"] = null, ["reason"] = "bad-request" });
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

        var id = ctx.Request.RouteValues["id"] as string ?? string.Empty;
        await WriteEditResultAsync(ctx, await editService.Value.ProposeFileAsync(dir, result.Files, id, content, sha256Before, null, ctx.RequestAborted));
    }

    private static bool TryReadFileProposal(byte[] body, out string content, out string sha256Before)
    {
        content = sha256Before = string.Empty;
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root
                || root["content"] is not JsonValue contentValue || !contentValue.TryGetValue<string>(out var text)
                || root["sha256Before"] is not JsonValue shaValue || !shaValue.TryGetValue<string>(out var sha))
            {
                return false;
            }

            content = text;
            sha256Before = sha;
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
