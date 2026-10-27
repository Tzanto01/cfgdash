using System.Diagnostics;

namespace Cfgdash.Core.Tests.Support;

/// <summary>Runs the real git for fixture setup in <paramref name="repo"/> with an isolated global config and no system config.</summary>
internal static class TestGit
{
    public static void Run(string repo, string globalConfig, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[] { "-c", "user.name=user", "-c", "user.email=user@host.example" }.Concat(args))
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["GIT_CONFIG_GLOBAL"] = globalConfig;
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Result}");
        }
    }
}
