using Cfgdash.Core.Files;
using Cfgdash.Core.Redaction;

namespace Cfgdash.Core.Tests.Support;

/// <summary>A <see cref="CollectContext"/> over a fresh file registry, the real redactor and a fake process runner.</summary>
internal static class TestContexts
{
    public static CollectContext Create(MachineEnvironment environment, string targetDir) => new()
    {
        Environment = environment,
        TargetDir = targetDir,
        Files = new FileRegistry([]),
        Redactor = new Redactor(),
        Processes = new FakeProcessRunner(),
    };

    public static MachineEnvironment WithConfigDir(TempTree tree, string configDir, OsFamily? os = null) =>
        TestEnvironments.Create(tree, paths: [TestEnvironments.ContextPath(PathKeys.ClaudeConfigDir, configDir)], os: os);
}
