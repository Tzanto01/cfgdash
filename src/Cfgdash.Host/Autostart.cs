using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using Cfgdash.Core;
using Microsoft.Win32;

namespace Cfgdash.Host;

/// <summary>The OS start-at-login entry: <paramref name="Actual"/> is <c>on</c>, <c>off</c> or <c>unsupported</c>; <paramref name="Detail"/> explains a mismatch or why it is unsupported.</summary>
internal sealed record AutostartState(string Actual, string? Detail = null);

/// <summary>Reads and changes cfgdash's one OS start-at-login entry (cfgdash-config-v1).</summary>
internal interface IAutostart
{
    Task<AutostartState> GetAsync(CancellationToken ct);

    /// <summary>Null on success, else a short reason.</summary>
    Task<string?> SetAsync(bool on, CancellationToken ct);
}

/// <summary>The <c>cfgdash</c> value under the HKCU Run key.</summary>
internal interface IRunKey
{
    string? Get();

    void Set(string value);

    void Delete();
}

[SupportedOSPlatform("windows")]
internal sealed class RegistryRunKey : IRunKey
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "cfgdash";

    public string? Get()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    public void Set(string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue(ValueName, value, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>Windows: the HKCU Run value <c>cfgdash</c> set to <see cref="Command"/>.</summary>
internal sealed class RunKeyAutostart(IRunKey key, string exePath) : IAutostart
{
    internal static string Command(string exe) => $"\"{exe}\" run --autostart";

    public Task<AutostartState> GetAsync(CancellationToken ct) => Task.FromResult(key.Get() switch
    {
        null => new AutostartState("off"),
        var value when value == Command(exePath) => new AutostartState("on"),
        _ => new AutostartState("on", "the Run value differs from the expected command"),
    });

    public Task<string?> SetAsync(bool on, CancellationToken ct)
    {
        try
        {
            if (on)
            {
                key.Set(Command(exePath));
            }
            else
            {
                key.Delete();
            }

            return Task.FromResult<string?>(null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Task.FromResult<string?>($"could not change the Run value ({e.GetType().Name})");
        }
    }
}

/// <summary>Linux: the user unit <paramref name="unitPath"/> (<c>cfgdash.service</c>) enabled through <c>systemctl --user</c>.</summary>
internal sealed class SystemdAutostart(IProcessRunner processes, string? systemctl, string unitPath, string exePath) : IAutostart
{
    private const string UnitName = "cfgdash.service";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The unit file text, or null when <paramref name="exe"/> contains a control character.</summary>
    internal static string? Unit(string exe)
    {
        if (exe.Any(char.IsControl))
        {
            return null;
        }

        var escaped = exe.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%").Replace("$", "$$");
        return $"[Unit]\nDescription=cfgdash harness dashboard\n\n[Service]\nExecStart=\"{escaped}\" run --autostart\nRestart=on-failure\nRestartSec=10\n\n[Install]\nWantedBy=default.target\n";
    }

    public async Task<AutostartState> GetAsync(CancellationToken ct)
    {
        if (await UnsupportedReasonAsync(ct) is { } reason)
        {
            return new AutostartState("unsupported", reason);
        }

        if (!File.Exists(unitPath))
        {
            return new AutostartState("off");
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(unitPath, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AutostartState("on", "the unit file cannot be read");
        }

        if (content != Unit(exePath))
        {
            return new AutostartState("on", "the unit file differs from the expected one");
        }

        var enabled = await processes.RunAsync(systemctl!, ["--user", "is-enabled", UnitName], Timeout, ct);
        return enabled.StandardOutput.Trim() == "enabled" ? new AutostartState("on") : new AutostartState("on", "the unit is not enabled");
    }

    public async Task<string?> SetAsync(bool on, CancellationToken ct)
    {
        if (!on && !File.Exists(unitPath))
        {
            return null;
        }

        if (await UnsupportedReasonAsync(ct) is { } reason)
        {
            return reason;
        }

        return on ? await EnableAsync(ct) : await DisableAsync(ct);
    }

    private async Task<string?> EnableAsync(CancellationToken ct)
    {
        if (Unit(exePath) is not { } unit)
        {
            return "unsupported executable path";
        }

        var existed = File.Exists(unitPath);
        string? temp = null;
        try
        {
            var dir = Path.GetDirectoryName(unitPath)!;
            Directory.CreateDirectory(dir);
            temp = Path.Combine(dir, $".{UnitName}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8))}.cfgdash.tmp");
            await File.WriteAllTextAsync(temp, unit, ct);
            File.Move(temp, unitPath, overwrite: true);
            temp = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"could not write the unit file ({e.GetType().Name})";
        }
        finally
        {
            if (temp is not null)
            {
                TryDelete(temp);
            }
        }

        var result = await processes.RunAsync(systemctl!, ["--user", "enable", "--now", UnitName], Timeout, ct);
        if (result.TimedOut || result.ExitCode != 0)
        {
            if (!existed)
            {
                TryDelete(unitPath);
            }

            return $"systemctl --user enable failed: {FirstLine(result.TimedOut ? "timed out" : result.StandardError, 200)}";
        }

        return null;
    }

    private async Task<string?> DisableAsync(CancellationToken ct)
    {
        var result = await processes.RunAsync(systemctl!, ["--user", "disable", "--now", "--no-block", UnitName], Timeout, ct);
        if (result.TimedOut || result.ExitCode != 0)
        {
            return $"systemctl --user disable failed: {FirstLine(result.TimedOut ? "timed out" : result.StandardError, 200)}";
        }

        try
        {
            File.Delete(unitPath);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"could not delete the unit file ({e.GetType().Name})";
        }
    }

    private async Task<string?> UnsupportedReasonAsync(CancellationToken ct)
    {
        if (systemctl is null)
        {
            return "systemctl not found";
        }

        var result = await processes.RunAsync(systemctl, ["--user", "is-system-running"], Timeout, ct);
        if (result.StandardOutput.Trim() is "running" or "degraded" or "starting" or "initializing")
        {
            return null;
        }

        var output = result.TimedOut ? "timed out" : result.StandardOutput.Trim().Length > 0 ? result.StandardOutput : result.StandardError;
        return $"no user systemd ({FirstLine(output, 200)})";
    }

    private static string FirstLine(string text, int max)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "no details";
        return line.Length > max ? line[..max] : line;
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

/// <summary>Start at login is not available here; <paramref name="reason"/> says why.</summary>
internal sealed class UnsupportedAutostart(string reason) : IAutostart
{
    public Task<AutostartState> GetAsync(CancellationToken ct) => Task.FromResult(new AutostartState("unsupported", reason));

    public Task<string?> SetAsync(bool on, CancellationToken ct) => Task.FromResult<string?>(reason);
}

internal static class AutostartFactory
{
    /// <summary>The start-at-login implementation for this OS and executable.</summary>
    public static IAutostart Create(CoreServices core, string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || string.Equals(Path.GetFileNameWithoutExtension(exePath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return new UnsupportedAutostart("start at login needs the installed cfgdash executable");
        }

        if (OperatingSystem.IsWindows())
        {
            return new RunKeyAutostart(new RegistryRunKey(), exePath);
        }

        if (OperatingSystem.IsLinux())
        {
            var xdg = core.EnvironmentReader.Get("XDG_CONFIG_HOME");
            var configHome = xdg is not null && Path.IsPathFullyQualified(xdg) ? xdg : Path.Combine(core.Environment.HomeDir, ".config");
            return new SystemdAutostart(core.Processes, core.Environment.Tools.GetValueOrDefault("systemctl"), Path.Combine(configHome, "systemd", "user", "cfgdash.service"), exePath);
        }

        return new UnsupportedAutostart("start at login is supported on Windows and Linux");
    }
}
