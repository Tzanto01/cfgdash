using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Infrastructure;

namespace Cfgdash.Core.Edits;

/// <summary>
/// Guarded edits (docs/contracts/edits-v1.md). Writes only the confined catalog targets, temp files beside them and
/// <c>&lt;cacheDir&gt;/edits</c> (backups and the append-only audit log). Proposals live in memory.
/// </summary>
internal sealed partial class EditService(
    MachineEnvironment environment,
    IEnvironmentReader environmentReader,
    IProcessRunner processes,
    IRedactor redactor,
    PlatformLocations locations,
    string cacheDir,
    TimeProvider time) : IEditService
{
    private const int MaxChanges = 100;
    private const int MaxProposals = 32;
    private const int MaxFileBytes = 4 * 1024 * 1024;
    private const UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim ApplyGate = new(1, 1);
    private static readonly Lock AuditGate = new();

    private readonly EditTargets targets = new(environment, locations);
    private readonly ConcurrentDictionary<string, Proposal> proposals = new(StringComparer.Ordinal);
    private readonly Lock proposalGate = new();

    private sealed record Change(string Key, string Scope, JsonNode? Value, bool Unset)
    {
        public IReadOnlyList<string> Segments { get; init; } = [];
    }

    private sealed record PlannedFile(EditTarget Target, string Path, byte[]? Before, string? Sha256Before, byte[] After, string Sha256After, bool Delete = false, string? Dir = null);

    private sealed record Proposal(string Dir, DateTimeOffset ExpiresAt, string Confirm, IReadOnlyList<PlannedFile> Files, IReadOnlyList<Change> Changes, string? RevertOf = null);

    public Task<EditResult> ProposeAsync(string dir, JsonNode? changes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Propose(PathUtil.Full(dir), changes));
    }

    public async Task<EditResult> ApplyAsync(string id, string? confirm, CancellationToken cancellationToken)
    {
        if (!proposals.TryGetValue(id, out var proposal))
        {
            return Error(EditStatus.NotFound, "not-found");
        }

        if (proposal.ExpiresAt <= time.GetUtcNow())
        {
            proposals.TryRemove(id, out _);
            return Error(EditStatus.NotFound, "not-found");
        }

        if (confirm is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(confirm), Encoding.UTF8.GetBytes(proposal.Confirm)))
        {
            return Error(EditStatus.ConfirmMismatch, "confirm-mismatch");
        }

        if (!proposals.TryRemove(new KeyValuePair<string, Proposal>(id, proposal)))
        {
            return Error(EditStatus.NotFound, "not-found");
        }

        await ApplyGate.WaitAsync(cancellationToken);
        try
        {
            return Write(proposal);
        }
        finally
        {
            ApplyGate.Release();
        }
    }

    public JsonObject ReadAudit(int limit)
    {
        limit = Math.Clamp(limit, 1, 500);
        var entries = new Queue<JsonNode>();
        var path = Path.Combine(cacheDir, "edits", "audit.jsonl");
        try
        {
            if (File.Exists(path))
            {
                using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
                while (reader.ReadLine() is { } line)
                {
                    if (AuditEntry(line) is { } entry)
                    {
                        entries.Enqueue(entry);
                        if (entries.Count > limit)
                        {
                            entries.Dequeue();
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return new JsonObject { ["entries"] = Nodes(entries.Reverse()) };
    }

    private EditResult Propose(string dir, JsonNode? body)
    {
        if (body is not JsonArray { Count: > 0 and <= MaxChanges } list)
        {
            return Invalid(null, "bad-request");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new Dictionary<string, (EditTarget Target, List<Change> Changes)>(PathUtil.Comparer);
        var order = new List<string>();
        foreach (var item in list)
        {
            Change? change;
            try
            {
                change = ParseChange(item);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                change = null;
            }

            if (change is null)
            {
                return Invalid(KeyOf(item), "bad-request");
            }

            if (EditCatalog.Resolve(change.Key) is not { } resolved)
            {
                return Invalid(change.Key, "unknown-key");
            }

            var (definition, segments) = resolved;
            if (EditCatalog.NotEditableReason(segments, redactor) is not null)
            {
                return Invalid(change.Key, "not-editable");
            }

            if (!definition.Scopes.Contains(change.Scope, StringComparer.Ordinal))
            {
                return Invalid(change.Key, "bad-scope");
            }

            if (!change.Unset && EditCatalog.ValueError(definition, change.Scope, change.Value) is { } valueError)
            {
                return Invalid(change.Key, valueError);
            }

            if (!seen.Add(change.Key + "\0" + change.Scope))
            {
                return Invalid(change.Key, "duplicate");
            }

            if (targets.Resolve(change.Scope, dir, out var targetError) is not { } target)
            {
                return Invalid(change.Key, targetError!);
            }

            change = change with { Segments = segments };
            if (!files.TryGetValue(target.Real, out var file))
            {
                file = (target, []);
                files[target.Real] = file;
                order.Add(target.Real);
            }

            file.Changes.Add(change);
        }

        var planned = new List<PlannedFile>();
        var bodyFiles = new JsonArray();
        foreach (var (target, fileChanges) in order.Select(real => files[real]))
        {
            if (!TryReadRaw(target.Real, out var raw))
            {
                return Invalid(fileChanges[0].Key, "parse-error");
            }

            if (raw is null && fileChanges.All(c => c.Unset))
            {
                continue;
            }

            var newLine = raw is not null && raw.AsSpan().IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
            string canonical = string.Empty;
            var root = raw is null ? new JsonObject() : JsonEdit.Parse(raw, newLine, out canonical);
            if (root is null)
            {
                return Invalid(fileChanges[0].Key, "parse-error");
            }

            var redactedBefore = raw is null ? null : JsonEdit.Serialize(redactor.RedactJson(root)!, "\n");
            foreach (var change in fileChanges)
            {
                if ((change.Unset ? JsonEdit.Unset(root, change.Segments) : JsonEdit.Set(root, change.Segments, change.Value)) is { } editError)
                {
                    return Invalid(change.Key, editError);
                }
            }

            var after = Encoding.UTF8.GetBytes(JsonEdit.Serialize(root, newLine));
            if (raw is not null && after.AsSpan().SequenceEqual(raw))
            {
                continue;
            }

            var path = PathUtil.ToJson(target.Real);
            var reformatted = raw is not null && !Encoding.UTF8.GetBytes(canonical).AsSpan().SequenceEqual(raw);
            var diff = UnifiedDiff.Create(redactedBefore, JsonEdit.Serialize(redactor.RedactJson(root)!, "\n"), path, reformatted);
            var file = new PlannedFile(target, path, raw, raw is null ? null : Hash(raw), after, Hash(after));
            planned.Add(file);
            bodyFiles.Add(new JsonObject { ["path"] = path, ["scope"] = target.Scope, ["sha256Before"] = file.Sha256Before, ["diff"] = diff });
        }

        if (planned.Count == 0)
        {
            return Invalid(null, "no-change");
        }

        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var proposal = new Proposal(dir, time.GetUtcNow() + Lifetime, Confirm(planned), planned, [.. planned.SelectMany(p => files[p.Target.Real].Changes)]);
        Store(id, proposal);
        return new EditResult(EditStatus.Ok, new JsonObject
        {
            ["id"] = id,
            ["expiresAt"] = IsoTime.Format(proposal.ExpiresAt),
            ["files"] = bodyFiles,
            ["confirm"] = proposal.Confirm,
        });
    }

    private void Store(string id, Proposal proposal)
    {
        lock (proposalGate)
        {
            var now = time.GetUtcNow();
            foreach (var (key, stored) in proposals)
            {
                if (stored.ExpiresAt <= now)
                {
                    proposals.TryRemove(key, out _);
                }
            }

            while (proposals.Count >= MaxProposals)
            {
                proposals.TryRemove(proposals.MinBy(p => p.Value.ExpiresAt).Key, out _);
            }

            proposals[id] = proposal;
        }
    }

    private EditResult Write(Proposal proposal)
    {
        if (!proposal.Files.All(f => IsCurrent(proposal.Dir, f)))
        {
            return Error(EditStatus.Stale, "stale");
        }

        var now = time.GetUtcNow();
        var auditId = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var temps = new string?[proposal.Files.Count];
        var written = new List<PlannedFile>();
        try
        {
            var editsDir = PrivateDirectory(Path.Combine(cacheDir, "edits"));
            for (var i = 0; i < proposal.Files.Count; i++)
            {
                var file = proposal.Files[i];
                if (file.Before is not null)
                {
                    var backups = PrivateDirectory(Path.Combine(PrivateDirectory(Path.Combine(editsDir, "backups")), auditId));
                    using var backup = new FileStream(Path.Combine(backups, $"{i}-{file.Target.Scope}-{Path.GetFileName(file.Target.Real)}"), PrivateOptions(FileMode.CreateNew));
                    backup.Write(file.Before);
                    backup.Flush(true);
                }

                temps[i] = file.Delete ? null : WriteTemp(file);
            }

            for (var i = 0; i < proposal.Files.Count; i++)
            {
                var file = proposal.Files[i];
                if (!IsCurrent(proposal.Dir, file))
                {
                    return written.Count == 0 ? Error(EditStatus.Stale, "stale") : Failed(auditId, now, proposal, written);
                }

                if (file.Delete)
                {
                    File.Delete(file.Target.Real);
                }
                else
                {
                    File.Move(temps[i]!, file.Target.Real, overwrite: true);
                    temps[i] = null;
                }

                written.Add(file);
            }

            AppendAudit(cacheDir, AuditLine(auditId, now, proposal, written, failed: false));
            return new EditResult(EditStatus.Ok, new JsonObject { ["applied"] = true, ["auditId"] = auditId }, Wrote: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed(auditId, now, proposal, written);
        }
        finally
        {
            foreach (var temp in temps)
            {
                if (temp is not null)
                {
                    TryDelete(temp);
                }
            }
        }
    }

    private EditResult Failed(string auditId, DateTimeOffset at, Proposal proposal, List<PlannedFile> written)
    {
        try
        {
            AppendAudit(cacheDir, AuditLine(auditId, at, proposal, written, failed: true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return new EditResult(
            EditStatus.WriteFailed,
            new JsonObject { ["error"] = "write-failed", ["auditId"] = auditId, ["written"] = Nodes(written.Select(f => JsonValue.Create(f.Path))) },
            Wrote: written.Count > 0);
    }

    private static JsonObject AuditLine(string auditId, DateTimeOffset at, Proposal proposal, List<PlannedFile> written, bool failed)
    {
        var line = new JsonObject
        {
            ["auditId"] = auditId,
            ["at"] = IsoTime.Format(at),
            ["listener"] = "ui",
            ["files"] = Nodes(written.Select(f => new JsonObject { ["path"] = f.Path, ["scope"] = f.Target.Scope, ["sha256Before"] = f.Sha256Before, ["sha256After"] = f.Delete ? null : f.Sha256After })),
            ["changes"] = Nodes(proposal.Changes.Select(c => new JsonObject { ["key"] = c.Key, ["scope"] = c.Scope, ["action"] = proposal.RevertOf is not null ? "revert" : c.Unset ? "unset" : "set" })),
        };
        if (proposal.RevertOf is not null)
        {
            line["revertOf"] = proposal.RevertOf;
        }

        if (failed)
        {
            line["failed"] = true;
        }

        return line;
    }

    private bool IsCurrent(string dir, PlannedFile file) =>
        targets.Resolve(file.Target.Scope, file.Dir ?? dir, out _) is { } target
        && PathUtil.PathEquals(target.Real, file.Target.Real)
        && TryReadRaw(target.Real, out var bytes)
        && (bytes is null ? file.Before is null : file.Sha256Before is not null && Hash(bytes) == file.Sha256Before);

    private static string WriteTemp(PlannedFile file)
    {
        var real = file.Target.Real;
        var dir = Path.GetDirectoryName(real)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(real)}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.cfgdash.tmp");
        var stream = new FileStream(temp, PrivateOptions(FileMode.CreateNew));
        try
        {
            using (stream)
            {
                stream.Write(file.After);
                stream.Flush(true);
            }

            if (!OperatingSystem.IsWindows() && file.Before is not null)
            {
                File.SetUnixFileMode(temp, File.GetUnixFileMode(real));
            }

            return temp;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    internal static void AppendAudit(string cacheDir, JsonObject entry)
    {
        lock (AuditGate)
        {
            var path = Path.Combine(PrivateDirectory(Path.Combine(cacheDir, "edits")), "audit.jsonl");
            var needsNewline = false;
            if (File.Exists(path))
            {
                using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (existing.Length > 0)
                {
                    existing.Seek(-1, SeekOrigin.End);
                    needsNewline = existing.ReadByte() != '\n';
                }
            }

            var bytes = Encoding.UTF8.GetBytes((needsNewline ? "\n" : string.Empty) + entry.ToJsonString() + "\n");
            using var stream = new FileStream(path, PrivateOptions(FileMode.Append));
            stream.Write(bytes);
            stream.Flush(true);
        }
    }

    private static JsonObject? AuditEntry(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject entry && entry["auditId"] is JsonValue && entry["files"] is JsonArray ? entry : null;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static Change? ParseChange(JsonNode? item)
    {
        if (item is not JsonObject change
            || change.Any(p => p.Key is not ("key" or "scope" or "value" or "unset"))
            || Text(change["key"]) is not { } key
            || Text(change["scope"]) is not { } scope)
        {
            return null;
        }

        var hasValue = change.TryGetPropertyValue("value", out var value);
        var hasUnset = change.TryGetPropertyValue("unset", out var unset);
        return hasValue == hasUnset || (hasUnset && unset?.GetValueKind() != JsonValueKind.True) ? null : new Change(key, scope, value, hasUnset);
    }

    private static string? KeyOf(JsonNode? item)
    {
        try
        {
            return item is JsonObject change ? Text(change["key"]) : null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>False when unreadable or larger than 4 MiB; <paramref name="bytes"/> is null when the file is absent.</summary>
    private static bool TryReadRaw(string path, out byte[]? bytes)
    {
        bytes = null;
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var memory = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                memory.Write(buffer, 0, read);
                if (memory.Length > MaxFileBytes)
                {
                    return false;
                }
            }

            bytes = memory.ToArray();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>SHA-256 over, per file in order, <c>len:path</c>, <c>len:sha256Before-or-empty</c> and <c>len:afterContent</c> (UTF-8 byte lengths).</summary>
    private static string Confirm(IEnumerable<PlannedFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            foreach (var part in new[] { Encoding.UTF8.GetBytes(file.Path), Encoding.UTF8.GetBytes(file.Sha256Before ?? string.Empty), file.After })
            {
                hash.AppendData(Encoding.UTF8.GetBytes(part.Length.ToString(CultureInfo.InvariantCulture) + ":"));
                hash.AppendData(part);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static JsonArray Nodes(IEnumerable<JsonNode?> items) => new(items.ToArray());

    private static EditResult Invalid(string? key, string reason) =>
        new(EditStatus.InvalidChange, new JsonObject { ["error"] = "invalid-change", ["key"] = key, ["reason"] = reason });

    private static EditResult Error(EditStatus status, string error) => new(status, new JsonObject { ["error"] = error });

    private static string PrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, PrivateDirectoryMode);
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }

        return path;
    }

    private static FileStreamOptions PrivateOptions(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = PrivateFileMode;
        }

        return options;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
