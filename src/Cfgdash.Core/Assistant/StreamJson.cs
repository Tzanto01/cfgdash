using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cfgdash.Core.Assistant;

/// <summary>One parsed stdout line of <c>claude --output-format=stream-json</c>.</summary>
public abstract record StreamEvent
{
    private StreamEvent()
    {
    }

    /// <summary>The <c>system</c>/<c>init</c> event, checked by <see cref="InitCheck"/>.</summary>
    public sealed record Init(JsonObject Event) : StreamEvent;

    /// <summary>Partial assistant text (<c>stream_event</c> / <c>content_block_delta</c> / <c>text_delta</c>), not yet redacted.</summary>
    public sealed record TextDelta(string Text) : StreamEvent;

    /// <summary>The final <c>result</c> line. <see cref="IsError"/> is true unless <c>is_error</c> is false. <see cref="Text"/> is the unredacted <c>result</c> text.</summary>
    public sealed record Result(double? CostUsd, bool IsError, string? Subtype, string? Text) : StreamEvent;

    /// <summary>An inbound <c>control_request</c>; the host ignores it.</summary>
    public sealed record ControlRequest : StreamEvent;

    /// <summary>Anything else, including malformed JSON.</summary>
    public sealed record Other : StreamEvent;
}

/// <summary>Parses stream-json lines into <see cref="StreamEvent"/>s.</summary>
public static class StreamJson
{
    public static StreamEvent Parse(string line)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(line) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return new StreamEvent.Other();
        }

        switch (obj is null ? null : Text(obj["type"]))
        {
            case "system" when Text(obj!["subtype"]) == "init":
                return new StreamEvent.Init(obj);
            case "stream_event"
                when obj!["event"] is JsonObject { } ev && Text(ev["type"]) == "content_block_delta"
                && ev["delta"] is JsonObject delta && Text(delta["type"]) == "text_delta" && Text(delta["text"]) is { } text:
                return new StreamEvent.TextDelta(text);
            case "result":
                double? cost = obj!["total_cost_usd"] is JsonValue c && c.GetValueKind() == JsonValueKind.Number && c.TryGetValue<double>(out var d) ? d : null;
                return new StreamEvent.Result(cost, obj["is_error"]?.GetValueKind() != JsonValueKind.False, Text(obj["subtype"]), Text(obj["result"]));
            case "control_request":
                return new StreamEvent.ControlRequest();
            default:
                return new StreamEvent.Other();
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
}
