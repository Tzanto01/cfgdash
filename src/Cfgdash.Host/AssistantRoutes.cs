using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core;
using Cfgdash.Core.Assistant;
using Cfgdash.Core.Context;
using Cfgdash.Core.Edits;
using Microsoft.AspNetCore.Http.Features;

namespace Cfgdash.Host;

internal sealed partial class CfgdashServer
{
    private const string InternalPrefix = "/api/v2/assistant/internal/";
    private const int MaxContextItems = 8;
    private const int MaxMessageChars = 16000;
    private const int MaxRationaleChars = 2000;
    private const int MaxToolBodyBytes = 64 * 1024;
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);
    private static readonly string[] AssistantToolNames = ["get_snapshot", "get_findings", "get_file", "search", "propose_change"];
    private static readonly string[] UnsafeTargetParts = ["..", "%2F", "%2f", "%5C", "%5c", "%2E", "%2e", "\\"];

    private const string CheckPrompt =
        "Run a full check of the bound folder. Start from get_findings, then read what you need with the other tools. "
        + "Answer with suggestions ranked by impact. Use propose_change where the policy allows it; for anything else, explain and suggest text for me to paste.";

    private Lazy<IEditService>? editService;
    private AssistantRegistry assistants = null!;

    /// <summary>True for a UI-listener request whose raw target starts with exactly <c>/api/v2/assistant/internal/</c> (ordinal) and holds no dot segment, encoded slash, backslash or encoded dot. Those routes check the session capability themselves.</summary>
    private static bool IsAssistantInternalTarget(HttpContext ctx) =>
        ctx.Request.Path.StartsWithSegments("/api/v2/assistant/internal", StringComparison.Ordinal)
        && ctx.Features.Get<IHttpRequestFeature>()?.RawTarget is { } raw
        && raw.StartsWith(InternalPrefix, StringComparison.Ordinal)
        && !UnsafeTargetParts.Any(part => raw.Contains(part, StringComparison.Ordinal));

    private void MapAssistant(HostDependencies dependencies)
    {
        assistants = new AssistantRegistry(core, dependencies, () => Port, log);
        app.Lifetime.ApplicationStopping.Register(assistants.StopAll);
        app.MapGet("/api/v2/assistant/status", ctx => AssistantStatusAsync(ctx));
        app.MapPost("/api/v2/assistant/sessions", ctx => CreateAssistantSessionAsync(ctx));
        app.MapPost("/api/v2/assistant/sessions/{id}/messages", ctx => AssistantTurnAsync(ctx, check: false));
        app.MapPost("/api/v2/assistant/sessions/{id}/check", ctx => AssistantTurnAsync(ctx, check: true));
        app.MapGet("/api/v2/assistant/sessions/{id}/events", ctx => AssistantEventsAsync(ctx));
        app.MapDelete("/api/v2/assistant/sessions/{id}", ctx => DeleteAssistantSessionAsync(ctx));
        app.MapPost("/api/v2/assistant/internal/{tool}", ctx => AssistantToolAsync(ctx));
    }

    private async Task AssistantStatusAsync(HttpContext ctx)
    {
        if (OnTailnet(ctx))
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        var probe = await assistants.ProbeAsync(ctx.RequestAborted);
        var body = new JsonObject { ["available"] = probe.Available };
        if (probe.Reason is not null)
        {
            body["reason"] = probe.Reason;
        }

        if (probe.ClaudePath is not null)
        {
            body["claudePath"] = Slash(probe.ClaudePath);
        }

        if (probe.Version is not null)
        {
            body["claudeVersion"] = probe.Version;
        }

        body["models"] = new JsonArray([.. (probe.Available ? AssistantRegistry.Models : []).Select(m => (JsonNode)m)]);
        body["sessions"] = new JsonArray([.. assistants.List().Select(s => (JsonNode)new JsonObject
        {
            ["id"] = s.Id,
            ["dir"] = Slash(s.Dir),
            ["model"] = s.Model,
            ["effort"] = s.Effort,
            ["live"] = s.State != AssistantState.Dead,
            ["costUsd"] = s.CostUsd,
        })]);
        await WriteJsonAsync(ctx, StatusCodes.Status200OK, body);
    }

    private async Task CreateAssistantSessionAsync(HttpContext ctx)
    {
        if (OnTailnet(ctx))
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        if (await ReadEditBodyAsync(ctx) is not { } bytes)
        {
            await WriteEditForbiddenAsync(ctx);
            return;
        }

        if (ParseObject(bytes) is not { } body
            || OptionalString(body, "model", string.Empty) is not { } model
            || OptionalString(body, "effort", string.Empty) is not { } effort
            || (model.Length > 0 && !AssistantLaunch.IsValidModel(model))
            || (effort.Length > 0 && !AssistantLaunch.IsValidEffort(effort)))
        {
            await WriteBadRequestAsync(ctx);
            return;
        }

        if (NormalizeDir(OptionalString(body, "dir", null)) is not { } dir)
        {
            await WriteBadDirAsync(ctx);
            return;
        }

        var (status, answer) = await assistants.CreateAsync(dir, model.Length > 0 ? model : null, effort.Length > 0 ? effort : null, ctx.RequestAborted);
        await WriteJsonAsync(ctx, status, answer);
    }

    private async Task AssistantTurnAsync(HttpContext ctx, bool check)
    {
        if (OnTailnet(ctx))
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        if (await ReadEditBodyAsync(ctx) is not { } bytes)
        {
            await WriteEditForbiddenAsync(ctx);
            return;
        }

        if (assistants.Find(ctx.Request.RouteValues["id"] as string ?? string.Empty) is not { } session)
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        var text = CheckPrompt;
        if (!check)
        {
            if (ParseObject(bytes) is not { } body
                || body["text"] is not JsonValue textValue
                || textValue.GetValueKind() != JsonValueKind.String
                || textValue.GetValue<string>() is not { Length: >= 1 and <= MaxMessageChars } userText
                || body["context"] is not (null or JsonArray { Count: <= MaxContextItems }))
            {
                await WriteBadRequestAsync(ctx);
                return;
            }

            text = userText;
            if (body["context"] is JsonArray { Count: > 0 } context)
            {
                if (await ContextBlockAsync(ctx, session, context) is not { } block)
                {
                    if (!ctx.Response.HasStarted)
                    {
                        await WriteJsonAsync(ctx, StatusCodes.Status400BadRequest, new { error = "bad-context" });
                    }

                    return;
                }

                text += "\n\n" + block;
            }
        }

        var (status, answer) = await assistants.SendAsync(session, text, ctx.RequestAborted);
        if (answer is null)
        {
            ctx.Response.StatusCode = status;
            return;
        }

        await WriteJsonAsync(ctx, status, answer);
    }

    /// <summary>The nonce-fenced reference block (kind, ids and redacted title per item); null when any item does not resolve in the session's assistant view.</summary>
    private async Task<string?> ContextBlockAsync(HttpContext ctx, AssistantSession session, JsonArray context)
    {
        if (await GetSnapshotAsync(ctx, session.Dir, refresh: false) is not { } result)
        {
            return null;
        }

        var view = AssistantView.Create(result, core.Environment, session.Dir);
        var block = new StringBuilder($"<context-{session.Nonce}>\n");
        foreach (var node in context)
        {
            if (node is not JsonObject item
                || item["kind"] is not JsonValue kindValue
                || kindValue.GetValueKind() != JsonValueKind.String
                || ContextReport.Titles(view.Snapshot, kindValue.GetValue<string>(), item) is not { } title)
            {
                return null;
            }

            block.Append("- ").Append(item.ToJsonString()).Append(": ").Append(title).Append('\n');
        }

        return block.Append($"</context-{session.Nonce}>").ToString();
    }

    private async Task AssistantEventsAsync(HttpContext ctx)
    {
        if (OnTailnet(ctx) || assistants.Find(ctx.Request.RouteValues["id"] as string ?? string.Empty) is not { } session)
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, app.Lifetime.ApplicationStopping);
        var channel = session.Subscribe();
        try
        {
            await ctx.Response.WriteAsync(": connected\n\n", ended.Token);
            await ctx.Response.Body.FlushAsync(ended.Token);
            while (true)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ended.Token);
                wait.CancelAfter(KeepAliveInterval);
                bool more;
                try
                {
                    more = await channel.Reader.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!ended.IsCancellationRequested)
                {
                    await ctx.Response.WriteAsync(": keep-alive\n\n", ended.Token);
                    await ctx.Response.Body.FlushAsync(ended.Token);
                    continue;
                }

                if (!more)
                {
                    return;
                }

                while (channel.Reader.TryRead(out var frame))
                {
                    await ctx.Response.WriteAsync(frame, ended.Token);
                }

                await ctx.Response.Body.FlushAsync(ended.Token);
            }
        }
        catch (OperationCanceledException) when (ended.IsCancellationRequested)
        {
        }
        finally
        {
            session.Unsubscribe(channel);
        }
    }

    private async Task DeleteAssistantSessionAsync(HttpContext ctx)
    {
        if (OnTailnet(ctx))
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        if (await ReadEditBodyAsync(ctx) is null)
        {
            await WriteEditForbiddenAsync(ctx);
            return;
        }

        if (!assistants.Delete(ctx.Request.RouteValues["id"] as string ?? string.Empty))
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>One MCP tool call from the <c>cfgdash mcp</c> grandchild. 401 for a cookie or a missing, wrong or revoked capability (the main token is never accepted); every tool outcome is 200 with a body.</summary>
    private async Task AssistantToolAsync(HttpContext ctx)
    {
        if (OnTailnet(ctx))
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        if (ctx.Request.Headers.Cookie.Count > 0)
        {
            await WriteUnauthenticatedAsync(ctx);
            return;
        }

        var tool = ctx.Request.RouteValues["tool"] as string;
        if (!AssistantToolNames.Contains(tool, StringComparer.Ordinal))
        {
            await WriteNotFoundAsync(ctx);
            return;
        }

        if (assistants.Authorize(Bearer(ctx.Request), out var limited) is not { } session)
        {
            await WriteUnauthenticatedAsync(ctx);
            return;
        }

        var watch = Stopwatch.StartNew();
        var args = await ReadToolArgsAsync(ctx);
        JsonObject answer;
        var ok = false;
        if (limited)
        {
            answer = new JsonObject { ["error"] = "tool-limit" };
        }
        else if (args is null)
        {
            answer = new JsonObject { ["error"] = "bad-request" };
        }
        else
        {
            session.Emit("tool", new JsonObject { ["name"] = tool, ["status"] = "started" });
            (answer, ok) = await RunAssistantToolAsync(session, tool!, args, ctx.RequestAborted);
            session.Emit("tool", new JsonObject { ["name"] = tool, ["status"] = ok ? "done" : "error" });
        }

        assistants.LogTool(session, tool!, args, watch.ElapsedMilliseconds, ok);
        await WriteJsonAsync(ctx, StatusCodes.Status200OK, answer);
    }

    private async Task<(JsonObject Answer, bool Ok)> RunAssistantToolAsync(AssistantSession session, string tool, JsonObject args, CancellationToken cancellationToken)
    {
        AssistantView view;
        try
        {
            var result = await snapshots.GetOrAdd(session.Dir, NewBuild).Value.WaitAsync(cancellationToken);
            view = AssistantView.Create(result, core.Environment, session.Dir);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error(e);
            return (new JsonObject { ["error"] = "snapshot-failed" }, false);
        }

        switch (tool)
        {
            case "get_snapshot":
                return (AssistantTools.GetSnapshot(view, StringArg(args, "view"), StringArg(args, "collectorId")), true);
            case "get_findings":
                return (AssistantTools.GetFindings(view), true);
            case "get_file":
                if (StringArg(args, "fileId") is not { } fileId)
                {
                    return (new JsonObject { ["error"] = "bad-request" }, false);
                }

                try
                {
                    return await AssistantTools.GetFileAsync(view, core.FileReader, fileId, session.Nonce, cancellationToken) is { } file
                        ? (file, true)
                        : (new JsonObject { ["error"] = "not-found" }, false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return (new JsonObject { ["error"] = "not-found" }, false);
                }

            case "search":
                if (StringArg(args, "q") is not { } q)
                {
                    return (new JsonObject { ["error"] = "bad-request" }, false);
                }

                var hits = await Task.Run(() => AssistantTools.Search(view, core.Search, core.FileReader, q, cancellationToken), cancellationToken);
                return (hits, !hits.ContainsKey("error"));
            default:
                return await ProposeForAssistantAsync(session, view, args, cancellationToken);
        }
    }

    private async Task<(JsonObject Answer, bool Ok)> ProposeForAssistantAsync(AssistantSession session, AssistantView view, JsonObject args, CancellationToken cancellationToken)
    {
        var result = await editService!.Value.ProposeForAssistantAsync(session.Dir, args["changes"], session.Id, cancellationToken);
        switch (result.Status)
        {
            case EditStatus.Ok:
                var rationale = AssistantRedaction.Text(StringArg(args, "rationale") ?? string.Empty);
                if (rationale.Length > MaxRationaleChars)
                {
                    rationale = rationale[..(char.IsHighSurrogate(rationale[MaxRationaleChars - 1]) ? MaxRationaleChars - 1 : MaxRationaleChars)];
                }

                session.Emit("proposal", new JsonObject
                {
                    ["proposalId"] = result.Body["id"]?.DeepClone(),
                    ["expiresAt"] = result.Body["expiresAt"]?.DeepClone(),
                    ["files"] = result.Body["files"]?.DeepClone(),
                    ["confirm"] = result.Body["confirm"]?.DeepClone(),
                    ["rationale"] = rationale,
                });
                return (AssistantTools.ProposalForModel(result, view), true);
            case EditStatus.Denied:
            case EditStatus.InvalidChange:
                return (AssistantTools.ProposalForModel(result, view), false);
            default:
                return (new JsonObject { ["error"] = "propose-failed" }, false);
        }
    }

    /// <summary>The JSON object body (empty body: empty object); null when not an object or over 64 KiB.</summary>
    private static async Task<JsonObject?> ReadToolArgsAsync(HttpContext ctx)
    {
        var request = ctx.Request;
        if (request.ContentLength > MaxToolBodyBytes)
        {
            return null;
        }

        var buffer = new byte[MaxToolBodyBytes + 1];
        var total = 0;
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(buffer.AsMemory(total), ctx.RequestAborted)) > 0)
            {
                total += read;
                if (total > MaxToolBodyBytes)
                {
                    return null;
                }
            }
        }
        catch (BadHttpRequestException)
        {
            return null;
        }

        return total == 0 ? [] : ParseObject(buffer[..total]);
    }

    private static JsonObject? ParseObject(byte[] bytes)
    {
        try
        {
            return JsonNode.Parse(bytes) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The string at <paramref name="name"/>, <paramref name="fallback"/> when absent or null, and null for any other JSON kind.</summary>
    private static string? OptionalString(JsonObject body, string name, string? fallback) => body[name] switch
    {
        null => fallback,
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        _ => null,
    };

    private static string? StringArg(JsonObject args, string name) =>
        args[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static Task WriteBadRequestAsync(HttpContext ctx) =>
        WriteJsonAsync(ctx, StatusCodes.Status400BadRequest, new { error = "bad-request" });
}
