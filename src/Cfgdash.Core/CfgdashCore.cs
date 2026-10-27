using Cfgdash.Core.Collectors;
using Cfgdash.Core.Environment;
using Cfgdash.Core.Files;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Search;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core;

/// <summary>Everything the host needs from the core, built once per run.</summary>
public sealed record CoreServices(
    MachineEnvironment Environment,
    IEnvironmentReader EnvironmentReader,
    IProcessRunner Processes,
    ISnapshotSource Snapshots,
    IFileContentReader FileReader,
    ISnapshotSearch Search);

/// <summary>Composition root of the core. The host calls only this.</summary>
public static class CfgdashCore
{
    public static async Task<CoreServices> CreateAsync(string cfgdashVersion, CancellationToken cancellationToken)
    {
        var reader = new AllowlistEnvironmentReader();
        var processes = new ProcessRunner();
        var environment = await MachineEnvironmentDiscovery.DiscoverAsync(reader, processes, DiscoveryOptions.Default(reader), cancellationToken);
        var redactor = new Redactor();
        var fileReader = new FileContentReader(redactor);
        var snapshots = new SnapshotBuilder(environment, CollectorCatalog.All(reader), redactor, processes, cfgdashVersion);
        return new CoreServices(environment, reader, processes, snapshots, fileReader, new SnapshotSearch());
    }
}
