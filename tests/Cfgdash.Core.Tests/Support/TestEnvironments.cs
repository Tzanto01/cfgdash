using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Tests.Support;

/// <summary>Builds a synthetic <see cref="MachineEnvironment"/> rooted in a temp tree.</summary>
internal static class TestEnvironments
{
    public static OsFamily CurrentOs =>
        OperatingSystem.IsWindows() ? OsFamily.Windows : OperatingSystem.IsMacOS() ? OsFamily.Macos : OsFamily.Linux;

    public static ContextPath ContextPath(string key, string dir) =>
        new(key, PathUtil.ToJson(dir), "test", Directory.Exists(dir));

    public static MachineEnvironment Create(
        TempTree tree,
        IReadOnlyList<ContextPath>? paths = null,
        IReadOnlyDictionary<string, string>? tools = null,
        OsFamily? os = null,
        TailnetStatus? tailnet = null,
        IReadOnlyList<string>? companyPrefixes = null) =>
        new(
            os ?? CurrentOs,
            "Test OS 1.0",
            "x64",
            "devbox",
            tree.Dir("home/user"),
            paths ?? [],
            tools ?? new Dictionary<string, string>(),
            tailnet,
            companyPrefixes ?? [],
            [],
            [],
            []);
}
