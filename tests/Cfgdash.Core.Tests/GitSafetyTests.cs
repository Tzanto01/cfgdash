using System.Collections.Concurrent;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Environment;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class GitSafetyTests
{
    private sealed class RecordingRunner(IProcessRunner inner) : IProcessRunner
    {
        public ConcurrentQueue<(string Executable, IReadOnlyList<string> Arguments, string? WorkingDirectory)> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? workingDirectory = null, string? standardInput = null)
        {
            Calls.Enqueue((executable, arguments, workingDirectory));
            return inner.RunAsync(executable, arguments, timeout, cancellationToken, workingDirectory, standardInput);
        }
    }

    private static (string Repo, string Canary) TrappedRepo(TempTree tree, string name, string globalConfig)
    {
        var repo = tree.Dir(name);
        TestGit.Run(repo, globalConfig, "init", "-q", "-b", "main");
        tree.Write(name + "/a.md", "one\n");
        TestGit.Run(repo, globalConfig, "add", "a.md");
        TestGit.Run(repo, globalConfig, "commit", "-q", "-m", "initial commit");
        var canary = tree.Path(name + "-canary");
        TestGit.Run(repo, globalConfig, "config", "core.fsmonitor", $"echo hit > '{canary.Replace('\\', '/')}'");
        return (repo, canary);
    }

    [Fact]
    public async Task Snapshot_runs_git_only_against_the_config_repo_and_never_fires_an_fsmonitor()
    {
        using var tree = new TempTree();
        var globalConfig = tree.Write("gitconfig", string.Empty);
        var (target, targetCanary) = TrappedRepo(tree, "target", globalConfig);
        var (configRepo, configCanary) = TrappedRepo(tree, "config", globalConfig);
        var environment = TestEnvironments.Create(
            tree,
            paths: [TestEnvironments.ContextPath(PathKeys.ConfigRepo, configRepo)],
            tools: new Dictionary<string, string> { ["git"] = "git" });
        var catalog = CollectorCatalog.Create(new FakeEnvironmentReader(), new PlatformLocations(tree.Dir("managed"), null, _ => null));
        var processes = new RecordingRunner(new ProcessRunner());
        var builder = new SnapshotBuilder(environment, catalog, new Redactor(), processes, "0.1.0");

        var snapshot = (await builder.BuildAsync(target, CancellationToken.None)).Snapshot;

        Assert.Equal(CollectorState.Active, snapshot.Collectors.Single(c => c.Id == "overview.config-repo").State);
        var gitCalls = processes.Calls.Where(c => c.Executable == "git").ToList();
        Assert.NotEmpty(gitCalls);
        Assert.All(gitCalls, c => Assert.Contains("--git-dir=" + Path.Combine(configRepo, ".git"), c.Arguments));
        var targetPath = target.Replace('\\', '/');
        Assert.DoesNotContain(processes.Calls, c => c.Arguments.Append(c.WorkingDirectory ?? string.Empty)
            .Any(a => a.Replace('\\', '/').Contains(targetPath, StringComparison.OrdinalIgnoreCase)));
        Assert.False(File.Exists(targetCanary), "a snapshot ran git in the target dir");
        Assert.False(File.Exists(configCanary), "config repo git ran with fsmonitor enabled");

        TestGit.Run(target, globalConfig, "status");
        TestGit.Run(configRepo, globalConfig, "status");
        Assert.True(File.Exists(targetCanary) && File.Exists(configCanary), "the fsmonitor trap did not fire for plain git status");
    }
}
