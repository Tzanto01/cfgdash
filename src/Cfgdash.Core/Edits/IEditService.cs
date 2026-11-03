using System.Text.Json.Nodes;

namespace Cfgdash.Core.Edits;

/// <summary>Outcome of a proposal or apply (docs/contracts/edits-v1.md).</summary>
public enum EditStatus { Ok, InvalidChange, NotFound, ConfirmMismatch, Stale, WriteFailed, Denied }

/// <summary><see cref="Body"/> is the complete JSON response body for the status; <see cref="Wrote"/> is true when at least one file was replaced.</summary>
public sealed record EditResult(EditStatus Status, JsonObject Body, bool Wrote = false);

/// <summary>Guarded edits of catalog settings (docs/contracts/edits-v1.md). Responses carry explicit JSON nulls.</summary>
public interface IEditService
{
    /// <summary>The <c>GET /api/v2/settings</c> body for <paramref name="dir"/>; values are redacted.</summary>
    Task<JsonObject> GetSettingsAsync(string dir, CancellationToken cancellationToken);

    /// <summary>Validates <paramref name="changes"/> (the request's <c>changes</c> array) and stores a dry-run proposal; nothing is written.</summary>
    Task<EditResult> ProposeAsync(string dir, JsonNode? changes, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="ProposeAsync"/> for the config assistant (docs/contracts/assistant-v1.md): adds <c>would-hide</c>, the assistant
    /// policy (<see cref="EditStatus.Denied"/>, body <c>{ "denied": key, "reason": "assistant-policy" }</c>) and the global quota of
    /// 8 open assistant proposals (<c>invalid-change</c>, reason <c>quota</c>). The audit line of an apply names <paramref name="sessionId"/>;
    /// with <paramref name="auto"/> (assistant-v1 rev 3 apply mode) it also carries <c>mode: "auto"</c>.
    /// </summary>
    Task<EditResult> ProposeForAssistantAsync(string dir, JsonNode? changes, string sessionId, bool auto, CancellationToken cancellationToken);

    /// <summary>Applies a stored proposal once when <paramref name="confirm"/> matches and no target changed since the proposal.</summary>
    Task<EditResult> ApplyAsync(string id, string? confirm, CancellationToken cancellationToken);

    /// <summary>
    /// Builds a dry-run proposal that restores the backups of the apply <paramref name="auditId"/> (a file it created is deleted);
    /// apply it with <see cref="ApplyAsync"/>. NotFound for an unknown id or missing backup, Stale when a file changed since that apply.
    /// </summary>
    Task<EditResult> RevertAsync(string auditId, CancellationToken cancellationToken);

    /// <summary>The <c>GET /api/v2/audit</c> body: newest first, <paramref name="limit"/> clamped to 1..500.</summary>
    JsonObject ReadAudit(int limit);
}
