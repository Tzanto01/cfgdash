using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Cfgdash.Host;

/// <summary>Contents of <c>run.json</c> (api-v1: run state).</summary>
internal sealed record RunState(int Pid, DateTimeOffset ProcessStart, string ExePath, int Port, string Token, string StartDir, RunTailnet? Tailnet, DateTimeOffset StartedAt);

internal sealed record RunTailnet(int HttpsPort, int BackendPort, string DnsName);

/// <summary><c>run.json</c> in the cache dir: written temp + rename with user-only permissions.</summary>
internal sealed class RunStateStore(string cacheDir)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string FilePath { get; } = Path.Combine(cacheDir, "run.json");

    /// <summary>Null when absent; an unreadable file is deleted and treated as absent.</summary>
    public RunState? Read()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize<RunState>(stream, Json);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (JsonException)
        {
            Delete();
            return null;
        }
    }

    public void Write(RunState state)
    {
        PrivateFiles.EnsureDirectory(cacheDir);
        var temp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        using (var stream = PrivateFiles.Open(temp, FileMode.CreateNew))
        {
            JsonSerializer.Serialize(stream, state, Json);
        }

        File.Move(temp, FilePath, overwrite: true);
    }

    public void Delete()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The recorded process still runs: same PID, start time within 1 s, same executable path.</summary>
    public static bool IsLive(RunState state)
    {
        try
        {
            using var process = Process.GetProcessById(state.Pid);
            if (process.HasExited || (process.StartTime.ToUniversalTime() - state.ProcessStart.UtcDateTime).Duration() > TimeSpan.FromSeconds(1))
            {
                return false;
            }

            return process.MainModule?.FileName is { } path
                && string.Equals(Path.GetFullPath(path), Path.GetFullPath(state.ExePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    public static DateTimeOffset CurrentProcessStart()
    {
        using var self = Process.GetCurrentProcess();
        return new DateTimeOffset(self.StartTime.ToUniversalTime());
    }
}

/// <summary>cfgdash's own log in <c>&lt;cache&gt;/logs/cfgdash-&lt;yyyy-MM-dd&gt;.log</c> (UTC). Never throws.</summary>
internal sealed class CfgdashLog
{
    private readonly string directory;
    private readonly TimeProvider time;
    private readonly Lock gate = new();

    public CfgdashLog(string directory, TimeProvider time)
    {
        this.directory = directory;
        this.time = time;
        try
        {
            PrivateFiles.EnsureDirectory(directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Write(string message)
    {
        try
        {
            var now = time.GetUtcNow();
            var line = Encoding.UTF8.GetBytes($"{now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)} {message}\n");
            lock (gate)
            {
                using var stream = PrivateFiles.Open(Path.Combine(directory, $"cfgdash-{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log"), FileMode.Append);
                stream.Write(line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Method, path and status only: never the query string, headers or bodies.</summary>
    public void Request(string method, string? path, int status) =>
        Write($"{Clean(method)} {Clean(path)} {status.ToString(CultureInfo.InvariantCulture)}");

    public void Error(Exception exception) => Write($"error {exception.GetType().Name}");

    private static string Clean(string? value) =>
        string.IsNullOrEmpty(value) ? "-" : string.Concat(value.Select(c => char.IsControl(c) || c == ' ' ? '?' : c));
}

internal static class PrivateFiles
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileModeUserOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Creates the directory (Linux/macOS 0700) and returns its path.</summary>
    public static string EnsureDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, DirectoryMode);
            File.SetUnixFileMode(path, DirectoryMode);
        }

        return path;
    }

    /// <summary>Opens a file for writing; a new file is 0600 on Linux/macOS.</summary>
    public static FileStream Open(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileModeUserOnly;
        }

        return new FileStream(path, options);
    }
}
