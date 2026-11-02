using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;

namespace Cfgdash.Host.Tests;

public sealed class AssistantWindowsJobTests
{
    private const string SpawnGrandchild =
        "$p = Start-Process -FilePath ping.exe -ArgumentList '-n','300','127.0.0.1' -PassThru -WindowStyle Hidden; "
        + "[Console]::Out.WriteLine($p.Id); [Console]::Out.Flush(); Start-Sleep -Seconds 300";

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public async Task Closing_the_job_kills_the_child_and_the_grandchild_it_spawned()
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", SpawnGrandchild })
        {
            start.ArgumentList.Add(argument);
        }

        using var child = Process.Start(start)!;
        var job = AssistantProcess.WindowsJob.Assign(child);
        Process? grandchild = null;
        try
        {
            var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60));
            grandchild = Process.GetProcessById(int.Parse(line!.Trim(), CultureInfo.InvariantCulture));

            job.Dispose();

            Assert.True(child.WaitForExit(10_000), "child still running after the job closed");
            Assert.True(grandchild.WaitForExit(10_000), "grandchild still running after the job closed");
        }
        finally
        {
            job.Dispose();
            foreach (var process in new[] { grandchild, child })
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                }
            }

            grandchild?.Dispose();
        }
    }
}
