using Cfgdash.Core.Collectors;
using Cfgdash.Core.Edits;
using Cfgdash.Core.Environment;
using Cfgdash.Core.Files;
using Cfgdash.Core.Health;
using Cfgdash.Core.Infrastructure;
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
    ISnapshotSearch Search,
    IDetailReader Details);

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
        var collectors = CollectorCatalog.All(reader);
        var snapshots = new SnapshotBuilder(environment, collectors, redactor, processes, cfgdashVersion);
        var details = new DetailReader(environment, collectors, redactor, processes);
        return new CoreServices(environment, reader, processes, snapshots, fileReader, new SnapshotSearch(), details);
    }

    /// <summary>The health-v1 findings for <paramref name="snapshot"/> (docs/contracts/health-v1.md), details redacted.</summary>
    public static FindingsReport Findings(HarnessSnapshot snapshot) => HealthRules.Evaluate(snapshot, new Redactor());

    /// <summary>The guarded edit service (docs/contracts/edits-v1.md); backups and the audit log go to <c>&lt;cacheDir&gt;/edits</c>.</summary>
    public static IEditService CreateEdits(CoreServices core, string cacheDir, TimeProvider time) =>
        new EditService(core.Environment, core.EnvironmentReader, core.Processes, new Redactor(), PlatformLocations.Default, cacheDir, time);
}
