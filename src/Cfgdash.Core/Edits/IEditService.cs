using System.Text.Json.Nodes;

namespace Cfgdash.Core.Edits;

/// <summary>Outcome of a proposal or apply (docs/contracts/edits-v1.md).</summary>
public enum EditStatus { Ok, InvalidChange, NotFound, ConfirmMismatch, Stale, WriteFailed }

/// <summary><see cref="Body"/> is the complete JSON response body for the status; <see cref="Wrote"/> is true when at least one file was replaced.</summary>
public sealed record EditResult(EditStatus Status, JsonObject Body, bool Wrote = false);

/// <summary>Guarded edits of catalog settings (docs/contracts/edits-v1.md). Responses carry explicit JSON nulls.</summary>
public interface IEditService
{
    /// <summary>The <c>GET /api/v2/settings</c> body for <paramref name="dir"/>; values are redacted.</summary>
    Task<JsonObject> GetSettingsAsync(string dir, CancellationToken cancellationToken);

    /// <summary>Validates <paramref name="changes"/> (the request's <c>changes</c> array) and stores a dry-run proposal; nothing is written.</summary>
    Task<EditResult> ProposeAsync(string dir, JsonNode? changes, CancellationToken cancellationToken);

    /// <summary>Applies a stored proposal once when <paramref name="confirm"/> matches and no target changed since the proposal.</summary>
    Task<EditResult> ApplyAsync(string id, string? confirm, CancellationToken cancellationToken);

    /// <summary>The <c>GET /api/v2/audit</c> body: newest first, <paramref name="limit"/> clamped to 1..500.</summary>
    JsonObject ReadAudit(int limit);
}
