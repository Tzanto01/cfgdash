using System.Text.Json.Nodes;

namespace Cfgdash.Core.Edits;

/// <summary>Outcome of a proposal or apply (docs/contracts/edits-v1.md).</summary>
public enum EditStatus { Ok, InvalidChange, NotFound, ConfirmMismatch, Stale, WriteFailed, Denied }

/// <summary><see cref="Body"/> is the complete JSON response body for the status; <see cref="Wrote"/> is true when at least one file was replaced.</summary>
public sealed record EditResult(EditStatus Status, JsonObject Body, bool Wrote = false);

/// <summary>
/// The edits-v2 <c>edit</c> info of a registered file. <see cref="Sha256"/>, <see cref="GitTracked"/> and <see cref="Content"/> are set only when
/// <see cref="Editable"/>; <see cref="Content"/> is then the raw text (BOM removed), which equals its redacted form and is what the UI shows.
/// </summary>
public sealed record FileEditInfo(bool Editable, string? Reason, string? Sha256, bool? GitTracked, string? Content);

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

    /// <summary>The edits-v2 editability of <paramref name="file"/> seen from the target <paramref name="dir"/>.</summary>
    Task<FileEditInfo> GetFileEditAsync(string dir, RegisteredFile file, CancellationToken cancellationToken);

    /// <summary>
    /// A dry-run edits-v2 proposal that replaces the whole file <paramref name="fileId"/>, which must be in <paramref name="files"/>
    /// (the snapshot's registry, or <c>AssistantView.Files</c> for the assistant). Null <paramref name="sha256Before"/> means the current content.
    /// A non-null <paramref name="assistantSessionId"/> adds <c>would-hide</c>, the quota of 8 open assistant proposals and the assistant audit origin.
    /// NotFound for an id outside <paramref name="files"/>, Stale when <paramref name="sha256Before"/> differs, otherwise InvalidChange with a reason.
    /// </summary>
    Task<EditResult> ProposeFileAsync(string dir, IFileCatalog files, string fileId, string content, string? sha256Before, string? assistantSessionId, CancellationToken cancellationToken);

    /// <summary>The <c>GET /api/v2/audit</c> body: newest first, <paramref name="limit"/> clamped to 1..500.</summary>
    JsonObject ReadAudit(int limit);
}
