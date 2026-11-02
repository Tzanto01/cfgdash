using System.Text.Json.Nodes;
using Cfgdash.Core.Redaction;

namespace Cfgdash.Core.Assistant;

/// <summary>The core redactor (security-v1) for assistant text that leaves the core: tool-log args, rationale, reasons, stderr, titles.</summary>
public static class AssistantRedaction
{
    private static readonly Redactor Redactor = new();

    public static string Text(string value) => Redactor.RedactText(value);

    /// <summary>A redacted deep copy.</summary>
    public static JsonNode? Json(JsonNode? node) => Redactor.RedactJson(node);
}
