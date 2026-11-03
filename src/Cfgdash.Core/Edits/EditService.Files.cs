using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;

namespace Cfgdash.Core.Edits;

/// <summary>edits-v2: whole-file proposals for editable text config files, applied and reverted through the edits-v1 machinery.</summary>
internal sealed partial class EditService
{
    private const string FileScope = "file";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    public async Task<FileEditInfo> GetFileEditAsync(string dir, RegisteredFile file, CancellationToken cancellationToken)
    {
        if (Inspect(PathUtil.Full(dir), file, out var target, out var raw, out var text) is { } reason)
        {
            return new FileEditInfo(false, reason, null, null, null);
        }

        return new FileEditInfo(true, null, Hash(raw!), await GitTrackedAsync(target!.Real, cancellationToken), text);
    }

    public Task<EditResult> ProposeFileAsync(string dir, IFileCatalog files, string fileId, string content, string? sha256Before, string? assistantSessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ProposeFile(PathUtil.Full(dir), files, fileId, content, sha256Before, assistantSessionId));
    }

    private EditResult ProposeFile(string dir, IFileCatalog files, string fileId, string content, string? sha256Before, string? assistantSession)
    {
        if (!files.TryGet(fileId, out var file))
        {
            return Error(EditStatus.NotFound, "not-found");
        }

        var reason = Inspect(dir, file, out var target, out var raw, out var before);
        if (reason is not null || raw is null)
        {
            return Invalid(null, reason ?? "unreadable");
        }

        byte[] after;
        try
        {
            after = StrictUtf8.GetBytes(content);
        }
        catch (EncoderFallbackException)
        {
            return Invalid(null, "not-text");
        }

        if (content.Contains('\0', StringComparison.Ordinal))
        {
            return Invalid(null, "not-text");
        }

        if (after.Length > FileTargets.MaxBytes)
        {
            return Invalid(null, "too-large");
        }

        if (content.Contains(Redactor.Marker, StringComparison.Ordinal))
        {
            return Invalid(null, "redacted-value");
        }

        var json = target!.Settings ? JsonEdit.Parse(after, "\n", out _) : null;
        if (target.Settings && json is null)
        {
            return Invalid(null, "parse-error");
        }

        var hidden = redactor.RedactText(content) != content || (json is not null && !JsonNode.DeepEquals(redactor.RedactJson(json), json));
        if (assistantSession is not null && hidden)
        {
            return Invalid(null, "would-hide");
        }

        if (sha256Before is not null && !string.Equals(sha256Before, Hash(raw), StringComparison.Ordinal))
        {
            return Error(EditStatus.Stale, "stale");
        }

        if (raw.AsSpan().StartsWith(Bom) && !content.StartsWith('﻿'))
        {
            after = [.. Bom, .. after];
        }

        if (after.AsSpan().SequenceEqual(raw))
        {
            return Invalid(null, "no-change");
        }

        var shownBefore = before;
        var shownAfter = !hidden ? content : redactor.RedactText(content);
        if (hidden && json is not null)
        {
            shownAfter = JsonEdit.Serialize(redactor.RedactJson(json)!, "\n");
            if (JsonEdit.Parse(raw, "\n", out var canonical) is not null)
            {
                shownBefore = canonical;
            }
        }

        var path = PathUtil.ToJson(target.Real);
        var planned = new PlannedFile(new EditTarget(FileScope, target.Real) { File = target }, path, raw, Hash(raw), after, Hash(after), Dir: dir);
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var proposal = new Proposal(dir, time.GetUtcNow() + Lifetime, Confirm([planned]), [planned], [new Change(fileId, FileScope, null, false) { File = fileId }], AssistantSession: assistantSession);
        if (!Store(id, proposal))
        {
            return Invalid(null, "quota");
        }

        return new EditResult(EditStatus.Ok, new JsonObject
        {
            ["id"] = id,
            ["expiresAt"] = IsoTime.Format(proposal.ExpiresAt),
            ["files"] = new JsonArray(new JsonObject
            {
                ["path"] = path,
                ["scope"] = FileScope,
                ["sha256Before"] = planned.Sha256Before,
                ["diff"] = UnifiedDiff.Create(shownBefore, shownAfter, path, false),
            }),
            ["confirm"] = proposal.Confirm,
        });
    }

    /// <summary>
    /// Null when <paramref name="file"/> is editable from <paramref name="dir"/> (path rule, at most 256 KiB, strict UTF-8 without NUL, redaction a no-op);
    /// otherwise the reason. <paramref name="text"/> is the raw text without a leading BOM.
    /// </summary>
    private string? Inspect(string dir, RegisteredFile file, out FileTarget? target, out byte[]? raw, out string? text)
    {
        raw = null;
        text = null;
        target = null;
        if (file.Ref.Company)
        {
            return "company";
        }

        target = FileTargets.Resolve(dir, file.FullPath, file.Ref.Kind, out var reason);
        if (target is null)
        {
            return reason;
        }

        if (new FileInfo(target.Real).Length > FileTargets.MaxBytes)
        {
            return "too-large";
        }

        if (!TryReadRaw(target.Real, out raw) || raw is null || raw.Length > FileTargets.MaxBytes)
        {
            return "unreadable";
        }

        try
        {
            text = StrictUtf8.GetString(raw).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return "not-text";
        }

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return "not-text";
        }

        var root = target.Settings ? JsonEdit.Parse(raw, "\n", out _) : null;
        if (target.Settings && root is null)
        {
            return "parse-error";
        }

        if (redactor.RedactText(text) != text || (root is not null && !JsonNode.DeepEquals(redactor.RedactJson(root), root)))
        {
            return "contains-redacted-values";
        }

        return null;
    }
}
