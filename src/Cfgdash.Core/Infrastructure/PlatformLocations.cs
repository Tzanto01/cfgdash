using Microsoft.Win32;

namespace Cfgdash.Core.Infrastructure;

/// <summary>Well-known managed-settings locations and the policy registry reader (test seam).</summary>
internal sealed record PlatformLocations(string? ManagedDir, string? IgnoredManagedDir, Func<string, string?> ReadPolicyRegistry)
{
    public static PlatformLocations Default { get; } = Create();

    private static PlatformLocations Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new PlatformLocations(
                Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "ClaudeCode"),
                Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData), "ClaudeCode"),
                ReadRegistry);
        }

        return new PlatformLocations(
            OperatingSystem.IsMacOS() ? "/Library/Application Support/ClaudeCode" : "/etc/claude-code",
            null,
            _ => null);
    }

    private static string? ReadRegistry(string hive)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var root = hive switch
        {
            "HKLM" => Registry.LocalMachine,
            "HKCU" => Registry.CurrentUser,
            _ => null,
        };
        try
        {
            using var key = root?.OpenSubKey(@"SOFTWARE\Policies\ClaudeCode");
            return key?.GetValue("Settings") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
