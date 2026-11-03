using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using Cfgdash.Core.Assistant;
using Microsoft.Win32.SafeHandles;

namespace Cfgdash.Host;

/// <summary>What one <c>claude</c> process is started with. <paramref name="Setsid"/> is required on Linux and macOS.</summary>
internal sealed record AssistantStart(
    string ClaudePath,
    string? Setsid,
    string WorkDir,
    string FilesDir,
    string FilePrefix,
    string Model,
    string Effort,
    decimal? MaxBudgetUsd,
    string SystemPrompt,
    IReadOnlyCollection<string> DisabledPlugins,
    string ExePath,
    int Port);

/// <summary>
/// One <c>claude</c> process (assistant-v1 Runtime): generated settings and MCP config files (0600, deleted on every exit path),
/// a fresh MCP capability, its own process group (Linux/macOS, via <c>setsid</c>) or a kill-on-close Job object (Windows),
/// a bounded stdout line reader and a bounded, redacted stderr buffer.
/// </summary>
internal sealed class AssistantProcess
{
    public const int MaxLineBytes = 1024 * 1024;
    private const int MaxStderrChars = 16 * 1024;
    private static readonly TimeSpan StderrGrace = TimeSpan.FromSeconds(2);
    private const string CapabilityVariable = "CFGDASH_MCP_CAPABILITY";
    private const int SigKill = 9;

    private readonly Process process;
    private readonly int pid;
    private readonly string[] files;
    private readonly bool ownGroup;
    private readonly Channel<string> lines = Channel.CreateBounded<string>(new BoundedChannelOptions(256) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource stopReading = new();
    private readonly SemaphoreSlim stdin = new(1, 1);
    private readonly StringBuilder stderr = new();
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SafeFileHandle? job;
    private Task stderrDrained = Task.CompletedTask;
    private int killed;
    private volatile bool initPassed;
    private volatile string? failure;

    private AssistantProcess(Process process, string[] files, bool ownGroup, string capability)
    {
        this.process = process;
        pid = process.Id;
        this.files = files;
        this.ownGroup = ownGroup;
        Capability = capability;
    }

    /// <summary>The bearer the <c>cfgdash mcp</c> grandchild presents; valid only while this process is the session's live one.</summary>
    public string Capability { get; }

    /// <summary>Set once the init check passed. Until then no tool call is served and nothing but <c>error</c> is forwarded.</summary>
    public bool InitPassed
    {
        get => initPassed;
        set => initPassed = value;
    }

    /// <summary>The last <c>total_cost_usd</c> this process reported; the CLI reports a running total per process.</summary>
    public double ReportedCostUsd { get; set; }

    /// <summary>True once killed or exited.</summary>
    public bool Ended => Volatile.Read(ref killed) == 1 || exited.Task.IsCompleted;

    /// <summary><c>line-too-long</c> when the stdout reader stopped on the line cap; null otherwise.</summary>
    public string? Failure => failure;

    /// <summary>Complete stdout lines; completes when stdout ends or the process is killed.</summary>
    public ChannelReader<string> Lines => lines.Reader;

    /// <summary>Completes after the process exited (or was killed) and the generated files are gone.</summary>
    public Task Exited => exited.Task;

    /// <summary>Completes when stderr reached its end (or failed); <see cref="StderrSummary"/> is complete after it.</summary>
    public Task StderrDrained => stderrDrained;

    /// <summary>The first stderr line, redacted and capped; for the status <c>detail</c> only.</summary>
    public string StderrSummary
    {
        get
        {
            lock (stderr)
            {
                var first = stderr.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
                var redacted = AssistantRedaction.Text(first);
                return redacted.Length > 300 ? redacted[..300] : redacted;
            }
        }
    }

    /// <summary>Writes the generated files and starts the process. Throws when the files cannot be written, the process cannot start or (Windows) the Job object cannot be assigned; nothing is left behind then.</summary>
    public static AssistantProcess Start(AssistantStart start)
    {
        var capability = TokenVerifier.CreateToken();
        var settingsPath = Path.Combine(start.FilesDir, $"settings-{start.FilePrefix}.json");
        var mcpPath = Path.Combine(start.FilesDir, $"mcp-{start.FilePrefix}.json");
        string[] files = [settingsPath, mcpPath];
        Process? process = null;
        int? pid = null;
        SafeFileHandle? job = null;
        try
        {
            WriteFile(settingsPath, AssistantLaunch.SettingsJson(start.DisabledPlugins));
            WriteFile(mcpPath, AssistantLaunch.McpConfigJson(start.ExePath, start.Port, capability));
            var startInfo = new ProcessStartInfo(start.Setsid ?? start.ClaudePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = start.WorkDir,
            };
            if (start.Setsid is not null)
            {
                startInfo.ArgumentList.Add(start.ClaudePath);
            }

            foreach (var argument in AssistantLaunch.Arguments(settingsPath, mcpPath, start.Model, start.Effort, start.MaxBudgetUsd, start.SystemPrompt))
            {
                startInfo.ArgumentList.Add(argument);
            }

            startInfo.Environment.Remove(CapabilityVariable);
            foreach (var (name, value) in AssistantLaunch.ChildEnvironment)
            {
                startInfo.Environment[name] = value;
            }

            process = new Process { StartInfo = startInfo };
            process.Start();
            pid = process.Id;
            if (OperatingSystem.IsWindows())
            {
                job = WindowsJob.Assign(process);
            }

            var assistant = new AssistantProcess(process, files, start.Setsid is not null, capability) { job = job };
            assistant.Run();
            return assistant;
        }
        catch
        {
            if (process is not null)
            {
                if (pid is { } started)
                {
                    KillTree(process, started, start.Setsid is not null);
                }

                process.Dispose();
            }

            job?.Dispose();
            DeleteFiles(files);
            throw;
        }
    }

    /// <summary>Writes one stdin line (UTF-8, no BOM). False when stdin is gone.</summary>
    public async Task<bool> SendAsync(string line)
    {
        await stdin.WaitAsync();
        try
        {
            await process.StandardInput.WriteAsync(line + "\n");
            await process.StandardInput.FlushAsync();
            return true;
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            stdin.Release();
        }
    }

    /// <summary>Kills the whole tree: <see cref="Process.Kill(bool)"/> first, then the process group while the leader was not observed exited (Linux/macOS) or the Job object (Windows). Deletes the generated files. Idempotent.</summary>
    public void Kill()
    {
        if (Interlocked.Exchange(ref killed, 1) == 1)
        {
            return;
        }

        try
        {
            stopReading.Cancel();
            KillTree(process, pid, ownGroup);
        }
        finally
        {
            job?.Dispose();
            DeleteFiles(files);
        }
    }

    /// <summary>Signals the process group first, so members orphaned by an exited leader die too (a pgid is not reused while any member lives), then kills the tree.</summary>
    private static void KillTree(Process process, int pid, bool ownGroup)
    {
        if (ownGroup && !OperatingSystem.IsWindows())
        {
            _ = SignalGroup(-pid, SigKill);
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
        }
    }

    private void Run()
    {
        var reader = Task.Run(ReadStdoutAsync);
        var drain = stderrDrained = Task.Run(DrainStderrAsync);
        _ = Task.Run(async () =>
        {
            try
            {
                await reader;
                await process.WaitForExitAsync();
            }
            catch (Exception e) when (e is InvalidOperationException or OperationCanceledException)
            {
            }
            finally
            {
                await Task.WhenAny(drain, Task.Delay(StderrGrace));
                job?.Dispose();
                DeleteFiles(files);
                process.Dispose();
                exited.TrySetResult();
            }
        });
    }

    private async Task ReadStdoutAsync()
    {
        var stream = process.StandardOutput.BaseStream;
        var buffer = new byte[64 * 1024];
        var line = new MemoryStream();
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, stopReading.Token)) > 0)
            {
                var start = 0;
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] != (byte)'\n')
                    {
                        continue;
                    }

                    line.Write(buffer, start, i - start);
                    start = i + 1;
                    if (!await EmitAsync(line))
                    {
                        return;
                    }
                }

                line.Write(buffer, start, read - start);
                if (line.Length > MaxLineBytes)
                {
                    failure = "line-too-long";
                    Kill();
                    return;
                }
            }

            if (line.Length > 0)
            {
                await EmitAsync(line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            lines.Writer.TryComplete();
        }
    }

    private async Task<bool> EmitAsync(MemoryStream line)
    {
        if (line.Length > MaxLineBytes)
        {
            failure = "line-too-long";
            Kill();
            return false;
        }

        var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
        line.SetLength(0);
        if (text.Length > 0)
        {
            await lines.Writer.WriteAsync(text, stopReading.Token);
        }

        return true;
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await process.StandardError.ReadAsync(buffer, CancellationToken.None)) > 0)
            {
                lock (stderr)
                {
                    stderr.Append(buffer, 0, Math.Min(read, MaxStderrChars - stderr.Length));
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    /// <summary>Creates a generated file: 0600 on Linux/macOS, a protected current-user-only ACL on Windows. Fails if it exists.</summary>
    internal static void WriteFile(string path, string content)
    {
        using var stream = OperatingSystem.IsWindows() ? UserOnlyWindowsFile(path) : PrivateFiles.Open(path, FileMode.CreateNew);
        stream.Write(new UTF8Encoding(false).GetBytes(content));
    }

    /// <summary>Creates the file with a protected ACL that grants only the current user, so no inherited entry applies.</summary>
    [SupportedOSPlatform("windows")]
    private static FileStream UserOnlyWindowsFile(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security);
    }

    private static void DeleteFiles(string[] paths)
    {
        foreach (var path in paths)
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

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SignalGroup(int pid, int signal);

    /// <summary>A Job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: closing the handle kills every process in it.</summary>
    internal static class WindowsJob
    {
        private const int ExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;

        public static SafeFileHandle Assign(Process process)
        {
            var job = CreateJobObjectW(IntPtr.Zero, null);
            if (job.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
            if (!SetInformationJobObject(job, ExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimit>())
                || !AssignProcessToJobObject(job, process.Handle))
            {
                var error = Marshal.GetLastWin32Error();
                job.Dispose();
                throw new Win32Exception(error);
            }

            return job;
        }

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimit info, uint length);

        [DllImport("kernel32", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimit
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimit
        {
            public BasicLimit Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
