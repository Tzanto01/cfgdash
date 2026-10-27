using Cfgdash.Core.Infrastructure;

namespace Cfgdash.Core.Collectors;

/// <summary>The built-in collectors in display order (one line per collector, no reflection).</summary>
internal static class CollectorCatalog
{
    public static IReadOnlyList<ICollector> All(IEnvironmentReader environmentReader) => Create(environmentReader, PlatformLocations.Default);

    public static IReadOnlyList<ICollector> Create(IEnvironmentReader environmentReader, PlatformLocations locations) =>
    [
        new MachineCollector(CfgdashPaths.OverridesFile(environmentReader)),
        new ConfigRepoCollector(),
    ];
}
