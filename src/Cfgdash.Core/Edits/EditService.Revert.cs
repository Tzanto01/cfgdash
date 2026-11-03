using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cfgdash.Core.Infrastructure;

namespace Cfgdash.Core.Edits;

internal sealed partial class EditService
{
    public Task<EditResult> RevertAsync(string auditId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(Revert(auditId));
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(Error(EditStatus.NotFound, "not-found"));
        }
    }

    private EditResult Revert(string auditId)
    {
        var notFound = Error(EditStatus.NotFound, "not-found");
        if (!AuditIdPattern().IsMatch(auditId) || FindAuditEntry(auditId) is not { } entry || entry["files"] is not JsonArray { Count: > 0 } files)
        {
            return notFound;
        }

        var backups = Path.Combine(cacheDir, "edits", "backups", auditId);
        var checkedFiles = new List<(EditTarget Target, string Dir, byte[]? Current, byte[]? Backup)>();
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i] is not JsonObject file
                || Text(file["path"]) is not { } path
                || !TryHash(file["sha256Before"], out var sha256Before)
                || !TryHash(file["sha256After"], out var sha256After))
            {
                return notFound;
            }

            var name = Path.GetFileName(path);
            var scope = Text(file["scope"]) ?? EditTargets.Scopes.FirstOrDefault(s => File.Exists(Path.Combine(backups, $"{i}-{s}-{name}")));
            if (scope is null || !(EditTargets.Scopes.Contains(scope, StringComparer.Ordinal) || scope == FileScope))
            {
                return notFound;
            }

            byte[]? backup = null;
            if (sha256Before is not null
                && (!TryReadRaw(Path.Combine(backups, $"{i}-{scope}-{name}"), out backup) || backup is null || Hash(backup) != sha256Before))
            {
                return notFound;
            }

            var full = PathUtil.Full(path);
            string? dir;
            EditTarget? target;
            if (scope == FileScope)
            {
                dir = Path.GetDirectoryName(full);
                target = dir is not null && FileTargets.Resolve(dir, full, "instruction", out _) is { } fileTarget && sha256Before is not null
                    ? new EditTarget(FileScope, fileTarget.Real) { File = fileTarget }
                    : null;
            }
            else
            {
                dir = Path.GetDirectoryName(Path.GetDirectoryName(full));
                target = dir is null ? null : targets.Resolve(scope, dir, out _);
            }

            if (dir is null || target is null || !PathUtil.PathEquals(target.Real, full))
            {
                return notFound;
            }

            if (!TryReadRaw(target.Real, out var current) || (current is null ? sha256After is not null : sha256After is null || Hash(current) != sha256After))
            {
                return Error(EditStatus.Stale, "stale");
            }

            checkedFiles.Add((target, dir, current, backup));
        }

        var planned = new List<PlannedFile>();
        var bodyFiles = new JsonArray();
        foreach (var (target, dir, current, backup) in checkedFiles)
        {
            if (current is null && backup is null)
            {
                continue;
            }

            if (!TryRedacted(target, current, out var redactedBefore) || !TryRedacted(target, backup, out var redactedAfter))
            {
                return notFound;
            }

            var path = PathUtil.ToJson(target.Real);
            var diff = UnifiedDiff.Create(redactedBefore, redactedAfter ?? string.Empty, path, false);
            if (backup is null)
            {
                var header = "+++ b/" + path.TrimStart('/') + "\n";
                var at = diff.IndexOf("\n" + header, StringComparison.Ordinal) + 1;
                diff = diff[..at] + "+++ /dev/null\n" + diff[(at + header.Length)..];
            }

            var file = new PlannedFile(target, path, current, current is null ? null : Hash(current), backup ?? [], Hash(backup ?? []), Delete: backup is null, Dir: dir);
            planned.Add(file);
            bodyFiles.Add(new JsonObject { ["path"] = path, ["scope"] = target.Scope, ["sha256Before"] = file.Sha256Before, ["diff"] = diff });
        }

        if (planned.Count == 0)
        {
            return Invalid(null, "no-change");
        }

        var changes = entry["changes"] is JsonArray list
            ? list.OfType<JsonObject>()
                .Select(c => Text(c["file"]) is { } fileId
                    ? new Change(fileId, FileScope, null, false) { File = fileId }
                    : Text(c["key"]) is { } key && Text(c["scope"]) is { } changeScope ? new Change(key, changeScope, null, false) : null)
                .OfType<Change>()
                .ToList()
            : [];
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var proposal = new Proposal(planned[0].Dir!, time.GetUtcNow() + Lifetime, Confirm(planned), planned, changes, RevertOf: auditId);
        Store(id, proposal);
        return new EditResult(EditStatus.Ok, new JsonObject
        {
            ["id"] = id,
            ["expiresAt"] = IsoTime.Format(proposal.ExpiresAt),
            ["files"] = bodyFiles,
            ["confirm"] = proposal.Confirm,
        });
    }

    private JsonObject? FindAuditEntry(string auditId)
    {
        var path = Path.Combine(cacheDir, "edits", "audit.jsonl");
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
            while (reader.ReadLine() is { } line)
            {
                if (AuditEntry(line) is { } entry && Text(entry["auditId"]) == auditId)
                {
                    return entry.ContainsKey("kind") ? null : entry;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static bool TryHash(JsonNode? node, out string? hash)
    {
        hash = Text(node);
        return node is null || hash is not null;
    }

    private bool TryRedacted(EditTarget target, byte[]? bytes, out string? redacted)
    {
        redacted = null;
        if (bytes is null)
        {
            return true;
        }

        if (target.File is { Settings: false })
        {
            redacted = redactor.RedactText(new UTF8Encoding(false).GetString(bytes).TrimStart('\uFEFF'));
            return true;
        }

        if (JsonEdit.Parse(bytes, "\n", out _) is not { } root)
        {
            return false;
        }

        redacted = JsonEdit.Serialize(redactor.RedactJson(root)!, "\n");
        return true;
    }

    [GeneratedRegex("^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{8}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AuditIdPattern();
}
