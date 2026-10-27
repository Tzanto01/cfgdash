namespace Cfgdash.Core.Environment;

/// <summary>The single place cfgdash reads environment variables. Unknown names throw.</summary>
public sealed class AllowlistEnvironmentReader : IEnvironmentReader
{
    private static readonly HashSet<string> Values = new(HarnessEnvAllowlist.Harness.Concat(HarnessEnvAllowlist.Runtime), StringComparer.Ordinal);
    private static readonly HashSet<string> Presence = new(HarnessEnvAllowlist.PresenceOnly, StringComparer.Ordinal);

    public string? Get(string name) => Read(name, EnvironmentVariableTarget.Process);

    public string? GetScoped(string name, EnvironmentVariableTarget target) =>
        OperatingSystem.IsWindows() ? Read(name, target) : null;

    private static string? Read(string name, EnvironmentVariableTarget target)
    {
        if (Presence.Contains(name))
        {
            return string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable(name, target)) ? null : HarnessEnvAllowlist.PresenceMarker;
        }

        if (!Values.Contains(name))
        {
            throw new ArgumentException($"Environment variable '{name}' is not on the cfgdash allowlist.", nameof(name));
        }

        var value = System.Environment.GetEnvironmentVariable(name, target);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
