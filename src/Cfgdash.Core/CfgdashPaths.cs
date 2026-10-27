namespace Cfgdash.Core;

/// <summary>cfgdash's own folders. The cache dir holds run state, logs and exports; the config dir holds the optional overrides.json.</summary>
public static class CfgdashPaths
{
    public const string Name = "cfgdash";

    public static string CacheDir(IEnvironmentReader environment) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), Name)
            : Path.Combine(environment.Get("XDG_CACHE_HOME") ?? Path.Combine(Home(), ".cache"), Name);

    public static string ConfigDir(IEnvironmentReader environment) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), Name)
            : Path.Combine(environment.Get("XDG_CONFIG_HOME") ?? Path.Combine(Home(), ".config"), Name);

    public static string OverridesFile(IEnvironmentReader environment) => Path.Combine(ConfigDir(environment), "overrides.json");

    private static string Home() => System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
}
