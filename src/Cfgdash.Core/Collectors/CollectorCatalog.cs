using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;

namespace Cfgdash.Core.Collectors;

/// <summary>The built-in collectors in display order (one line per collector, no reflection).</summary>
internal static class CollectorCatalog
{
    public static IReadOnlyList<ICollector> All(IEnvironmentReader environmentReader) => Create(environmentReader, PlatformLocations.Default);

    public static IReadOnlyList<ICollector> Create(IEnvironmentReader environmentReader, PlatformLocations locations)
    {
        var settings = new SettingsLayers(locations);
        return
        [
            new MachineCollector(CfgdashPaths.OverridesFile(environmentReader)),
            new ConfigRepoCollector(),
            new SettingsEffectiveCollector(settings),
            new InstructionChainCollector(settings, locations),
            new AgentDefinitionsCollector(settings, locations),
            new CompanyCoreCollector(),
            new SkillsCatalogCollector(settings, locations),
            new HooksRegisteredCollector(settings, locations),
            new McpServersCollector(settings, locations),
            new SettingsHighlightsCollector(settings),
            new EnvHarnessCollector(settings, environmentReader),
            new WindowsTasksCollector(),
            new SystemdUserCollector(environmentReader),
            new CrontabCollector(),
        ];
    }
}
