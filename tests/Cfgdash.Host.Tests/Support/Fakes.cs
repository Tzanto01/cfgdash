using System.Collections.Concurrent;
using System.Text;
using Cfgdash.Core;
using Cfgdash.Core.Snapshot;
using Microsoft.Extensions.FileProviders;

namespace Cfgdash.Host.Tests;

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows only";
        }
    }
}

internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux only";
        }
    }
}

/// <summary>Needs a release asset for this OS and architecture (win-x64 or linux-x64).</summary>
internal sealed class UpdateFactAttribute : FactAttribute
{
    public UpdateFactAttribute()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64
            || !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
        {
            Skip = "No release asset for this platform";
        }
    }
}

internal sealed class TempDir : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "cfgdash-host-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Root);

    public string Sub(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public string Combine(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class Poll
{
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(15);
        }

        return condition();
    }
}

internal sealed class CaptureWriter : TextWriter
{
    private readonly StringBuilder builder = new();

    public override Encoding Encoding => Encoding.UTF8;

    public string Text
    {
        get
        {
            lock (builder)
            {
                return builder.ToString();
            }
        }
    }

    public override void Write(char value)
    {
        lock (builder)
        {
            builder.Append(value);
        }
    }

    public override void Write(string? value)
    {
        lock (builder)
        {
            builder.Append(value);
        }
    }

    public override void Write(char[] buffer, int index, int count)
    {
        lock (builder)
        {
            builder.Append(buffer, index, count);
        }
    }
}

internal sealed class FakeEnv(IDictionary<string, string>? values = null) : IEnvironmentReader
{
    public string? Get(string name) => values is not null && values.TryGetValue(name, out var value) ? value : null;

    public string? GetScoped(string name, EnvironmentVariableTarget target) => null;
}

internal sealed record RecordedCall(string Executable, string[] Args)
{
    public string Line => string.Join(' ', Args);
}

internal class FakeProcesses(Func<RecordedCall, ProcessResult>? handler = null) : IProcessRunner
{
    private readonly List<RecordedCall> calls = [];

    public IReadOnlyList<RecordedCall> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public static ProcessResult Ok(string output = "") => new(0, output, string.Empty, false);

    public static ProcessResult Fail(string error = "failed") => new(1, string.Empty, error, false);

    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? workingDirectory = null, string? standardInput = null)
    {
        var call = new RecordedCall(executable, [.. arguments]);
        lock (calls)
        {
            calls.Add(call);
        }

        return Task.FromResult(Handle(call));
    }

    protected virtual ProcessResult Handle(RecordedCall call) =>
        handler is null ? throw new InvalidOperationException($"unexpected process call: {call.Executable} {call.Line}") : handler(call);
}

internal sealed class FakeCatalog(params RegisteredFile[] files) : IFileCatalog
{
    public IReadOnlyList<RegisteredFile> All => files;

    public bool TryGet(string id, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out RegisteredFile file)
    {
        file = files.FirstOrDefault(f => f.Ref.Id == id);
        return file is not null;
    }
}

internal sealed class FakeSnapshots(string hostname = "host.example") : ISnapshotSource
{
    private readonly ConcurrentDictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);

    public IFileCatalog Files { get; } = new FakeCatalog(
        new RegisteredFile(new FileRef("f1", "memory/notes.md", "memory", 10, 3, false), "unused"),
        new RegisteredFile(new FileRef("gone", "memory/gone.md", "memory", 10, 3, false), "unused"));

    public Func<string, int, Exception?>? FailWith { get; set; }

    public int CountFor(string dir) => counts.GetValueOrDefault(dir);

    public Task<SnapshotResult> BuildAsync(string targetDir, CancellationToken cancellationToken)
    {
        var n = counts.AddOrUpdate(targetDir, 1, (_, c) => c + 1);
        if (FailWith?.Invoke(targetDir, n) is { } failure)
        {
            throw failure;
        }

        var machine = new MachineInfo(hostname, null, new OsInfo(OsFamily.Linux, "test os", "x64"), DateTimeOffset.UnixEpoch, "0.0.0-dev");
        var snapshot = new HarnessSnapshot(HarnessSnapshot.CurrentSchemaVersion, machine, new SnapshotContext(targetDir, []), [], [], []);
        return Task.FromResult(new SnapshotResult(snapshot, Files));
    }
}

internal sealed class FakeFileReader : IFileContentReader
{
    public Task<FileContent> ReadAsync(RegisteredFile file, CancellationToken cancellationToken) =>
        file.Ref.Id == "gone"
            ? throw new FileNotFoundException("gone")
            : Task.FromResult(new FileContent(file.Ref.Id, file.Ref.Path, file.Ref.Kind, "markdown", "content of " + file.Ref.Id, false));
}

internal sealed class FakeSearch : ISnapshotSearch
{
    public SearchResponse Search(HarnessSnapshot snapshot, IFileCatalog files, IFileContentReader reader, string query, CancellationToken cancellationToken) =>
        new(query, [new SearchHit("file", "instructions", "c1", "b1", null, "f1", 3, "snippet for " + query)], false);
}

internal static class TestCore
{
    public static MachineEnvironment Machine(
        string hostname = "host.example",
        string home = "",
        IReadOnlyList<ContextPath>? paths = null,
        IReadOnlyDictionary<string, string>? tools = null,
        TailnetStatus? tailnet = null,
        IReadOnlyList<string>? logins = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? projects = null) =>
        new(OsFamily.Linux, "test os", "x64", hostname, home, paths ?? [], tools ?? new Dictionary<string, string>(), tailnet, [], projects ?? [], logins ?? [], tags ?? []);

    public static CoreServices Create(MachineEnvironment? environment = null, IProcessRunner? processes = null, FakeSnapshots? snapshots = null, IEnvironmentReader? reader = null) =>
        new(environment ?? Machine(), reader ?? new FakeEnv(), processes ?? new FakeProcesses(), snapshots ?? new FakeSnapshots(), new FakeFileReader(), new FakeSearch());

    public static HostDependencies Deps(CoreServices core, string cacheDir, TextWriter? output = null, TextWriter? error = null, TimeProvider? time = null, Action<string>? open = null, string? exe = null, IFileProvider? ui = null) =>
        new()
        {
            CreateCore = _ => Task.FromResult(core),
            CacheDir = _ => cacheDir,
            Out = output ?? new CaptureWriter(),
            Error = error ?? new CaptureWriter(),
            TimeProvider = time ?? TimeProvider.System,
            OpenBrowser = open ?? (_ => throw new InvalidOperationException("browser must not open")),
            ExePath = exe,
            UiFiles = ui,
        };
}
