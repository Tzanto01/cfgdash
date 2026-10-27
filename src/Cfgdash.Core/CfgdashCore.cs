using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Cfgdash.Core.Environment;
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

/// <summary>Composition root of the core. The host calls only this. The placeholder internals below are replaced by the core team.</summary>
public static class CfgdashCore
{
    public static Task<CoreServices> CreateAsync(string cfgdashVersion, CancellationToken cancellationToken)
    {
        var reader = new AllowlistEnvironmentReader();
        var processes = new ProcessRunner();
        var os = OperatingSystem.IsWindows() ? OsFamily.Windows : OperatingSystem.IsMacOS() ? OsFamily.Macos : OsFamily.Linux;
        var environment = new MachineEnvironment(
            os,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            System.Environment.MachineName,
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            [], new Dictionary<string, string>(), null, [], [], [], []);
        var placeholder = new PlaceholderServices(environment, cfgdashVersion);
        return Task.FromResult(new CoreServices(environment, reader, processes, placeholder, placeholder, placeholder));
    }

    private sealed class PlaceholderServices(MachineEnvironment environment, string version) : ISnapshotSource, IFileContentReader, ISnapshotSearch, IFileCatalog
    {
        public IReadOnlyList<RegisteredFile> All => [];

        public Task<SnapshotResult> BuildAsync(string targetDir, CancellationToken cancellationToken)
        {
            var machine = new MachineInfo(environment.Hostname, null, new OsInfo(environment.Os, environment.OsDescription, environment.Arch), DateTimeOffset.UtcNow, version);
            var snapshot = new HarnessSnapshot(HarnessSnapshot.CurrentSchemaVersion, machine, new SnapshotContext(targetDir, environment.Paths), [], [], []);
            return Task.FromResult(new SnapshotResult(snapshot, this));
        }

        public Task<FileContent> ReadAsync(RegisteredFile file, CancellationToken cancellationToken) =>
            throw new FileNotFoundException("No files are registered.");

        public SearchResponse Search(HarnessSnapshot snapshot, IFileCatalog files, IFileContentReader reader, string query, CancellationToken cancellationToken) =>
            new(query, [], false);

        public bool TryGet(string id, [MaybeNullWhen(false)] out RegisteredFile file)
        {
            file = null;
            return false;
        }
    }
}
