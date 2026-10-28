using System.Text.Json;

namespace Cfgdash.Core.Collectors;

/// <summary>What the index keeps per transcript file: counts, tokens, cost and redacted signal messages. Never prompts, tool calls or outputs.</summary>
internal sealed record SessionFileSummary(
    string? Cwd,
    DateTimeOffset? Started,
    DateTimeOffset? Last,
    int Prompts,
    int ToolCalls,
    IReadOnlyList<string> Models,
    TokenCounts Tokens,
    double CostUsd,
    bool Unpriced,
    IReadOnlyList<SessionSignal> Signals);

internal sealed record SessionIndexEntry(string Path, long Length, long LastWriteUtcTicks, SessionFileSummary Summary);

/// <summary>
/// The incremental transcript index in the cfgdash cache dir, keyed by full path, length and last write time.
/// A missing, corrupt or old-format file is treated as empty. <see cref="Save"/> keeps only the entries used since load.
/// </summary>
internal sealed class SessionIndex
{
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    private readonly Dictionary<string, SessionIndexEntry> entries;
    private readonly Dictionary<string, SessionIndexEntry> used = new(StringComparer.Ordinal);

    private SessionIndex(Dictionary<string, SessionIndexEntry> entries) => this.entries = entries;

    public IEnumerable<SessionIndexEntry> Entries => entries.Values;

    public static SessionIndex Load(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                using var stream = File.OpenRead(file);
                if (JsonSerializer.Deserialize<IndexDocument>(stream, Json) is { Version: FormatVersion } document && document.PricesAsOf == SessionPrices.AsOf)
                {
                    return new SessionIndex(document.Files.ToDictionary(e => e.Path, StringComparer.Ordinal));
                }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
        }

        return new SessionIndex(new Dictionary<string, SessionIndexEntry>(StringComparer.Ordinal));
    }

    /// <summary>The cached summary when the file is unchanged since it was indexed.</summary>
    public SessionFileSummary? Get(FileInfo file)
    {
        if (!entries.TryGetValue(file.FullName, out var entry) || entry.Length != file.Length || entry.LastWriteUtcTicks != file.LastWriteTimeUtc.Ticks)
        {
            return null;
        }

        used[entry.Path] = entry;
        return entry.Summary;
    }

    public void Put(FileInfo file, SessionFileSummary summary)
    {
        var entry = new SessionIndexEntry(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, summary);
        entries[entry.Path] = entry;
        used[entry.Path] = entry;
    }

    /// <summary>Writes the used entries atomically (temp file, then replace); user-only (0700 dir, 0600 file) on Linux and macOS.</summary>
    public void Save(string file)
    {
        var dir = Path.GetDirectoryName(file)!;
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var temp = file + ".tmp";
        File.Delete(temp);
        using (var stream = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(stream, new IndexDocument(FormatVersion, SessionPrices.AsOf, [.. used.Values]), Json);
        }

        File.Move(temp, file, overwrite: true);
    }

    private sealed record IndexDocument(int Version, string PricesAsOf, IReadOnlyList<SessionIndexEntry> Files);
}
