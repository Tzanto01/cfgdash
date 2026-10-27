using System.Text.Json.Nodes;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Environment;
using Cfgdash.Core.Files;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

/// <summary>One real git repo per class: one commit, one modified tracked file, one untracked file.</summary>
public sealed class GitRepoFixture : IDisposable
{
    public GitRepoFixture()
    {
        Repo = Tree.Dir("repo");
        var globalConfig = Tree.Write("gitconfig", string.Empty);
        TestGit.Run(Repo, globalConfig, "init", "-q", "-b", "main");
        Tree.Write("repo/tracked.md", "one\n");
        TestGit.Run(Repo, globalConfig, "add", "tracked.md");
        TestGit.Run(Repo, globalConfig, "commit", "-q", "-m", "initial commit");
        Tree.Write("repo/tracked.md", "two\n");
        Tree.Write("repo/new.txt", "new\n");
    }

    public TempTree Tree { get; } = new();

    public string Repo { get; }

    public void Dispose() => Tree.Dispose();
}

public sealed class OverviewCollectorTests(GitRepoFixture fixture) : IClassFixture<GitRepoFixture>
{
    private static CollectContext Context(MachineEnvironment environment, IProcessRunner processes) => new()
    {
        Environment = environment,
        TargetDir = environment.HomeDir,
        Files = new FileRegistry([]),
        Redactor = new Redactor(),
        Processes = processes,
    };

    private static Dictionary<string, JsonNode?> Rows(CollectorResult result) =>
        ((KvBlock)result.Blocks[0]).Rows.ToDictionary(r => r.Key, r => r.Value);

    private MachineEnvironment RepoEnvironment(string git, string? repo = null) => TestEnvironments.Create(
        fixture.Tree,
        paths: [TestEnvironments.ContextPath(PathKeys.ConfigRepo, repo ?? fixture.Repo)],
        tools: new Dictionary<string, string> { ["git"] = git });

    private static string[] Git(string repo, params string[] command) =>
        ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=", "--git-dir=" + Path.Combine(repo, ".git"), "--work-tree=" + repo, .. command];

    [Fact]
    public async Task Config_repo_reports_branch_dirty_files_and_last_commit_of_a_real_repo()
    {
        var result = await new ConfigRepoCollector().CollectAsync(Context(RepoEnvironment("git"), new ProcessRunner()), CancellationToken.None);

        Assert.Null(result.UnavailableReason);
        var rows = Rows(result);
        Assert.Equal("main", rows["branch"]!.GetValue<string>());
        Assert.Null(rows["upstream"]);
        Assert.Equal(2, rows["dirtyCount"]!.GetValue<int>());
        Assert.Matches("^[0-9a-f]{40}$", rows["lastCommitHash"]!.GetValue<string>());
        Assert.True(DateTimeOffset.TryParse(rows["lastCommitDate"]!.GetValue<string>(), out _));
        Assert.Equal("initial commit", rows["lastCommitSubject"]!.GetValue<string>());
        var dirty = ((TableBlock)result.Blocks[1]).Rows.ToDictionary(r => r.Id, r => r.Cells["status"]!.GetValue<string>());
        Assert.Equal(new Dictionary<string, string> { ["tracked.md"] = ".M", ["new.txt"] = "untracked" }, dirty);
    }

    [Fact]
    public async Task Config_repo_parses_ahead_behind_and_renames_from_porcelain_v2()
    {
        var repo = Path.GetFullPath(fixture.Repo);
        var status = string.Join("\0",
            "# branch.oid 1111111111111111111111111111111111111111",
            "# branch.head feature",
            "# branch.upstream origin/feature",
            "# branch.ab +2 -1",
            "1 .M N... 100644 100644 100644 aaaa bbbb src/a file.md",
            "2 R. N... 100644 100644 100644 aaaa bbbb R100 docs/new name.md",
            "docs/old name.md",
            "? untracked.txt") + "\0";
        var processes = new FakeProcessRunner()
            .On("git", Git(repo, "status", "--porcelain=v2", "--branch", "-z"), status)
            .On("git", Git(repo, "log", "--no-show-signature", "-1", "--format=%H%x1f%cI%x1f%s"), "abc123\u001f2026-01-01T00:00:00+00:00\u001fsubject line\n");

        var result = await new ConfigRepoCollector().CollectAsync(Context(RepoEnvironment("git"), processes), CancellationToken.None);

        var rows = Rows(result);
        Assert.Equal(("feature", "origin/feature", 2, 1, 3), (rows["branch"]!.GetValue<string>(), rows["upstream"]!.GetValue<string>(), rows["ahead"]!.GetValue<int>(), rows["behind"]!.GetValue<int>(), rows["dirtyCount"]!.GetValue<int>()));
        Assert.Equal("subject line", rows["lastCommitSubject"]!.GetValue<string>());
        var dirty = ((TableBlock)result.Blocks[1]).Rows.Select(r => (r.Id, r.Cells["status"]!.GetValue<string>()));
        Assert.Equal([("src/a file.md", ".M"), ("docs/new name.md", "R."), ("untracked.txt", "untracked")], dirty);
    }

    [Fact]
    public async Task Config_repo_is_unavailable_with_the_first_stderr_line_when_git_status_fails()
    {
        var repo = Path.GetFullPath(fixture.Repo);
        var processes = new FakeProcessRunner()
            .On("git", Git(repo, "status", "--porcelain=v2", "--branch", "-z"), string.Empty, 128, "fatal: not a git repository\nhint: more");

        var result = await new ConfigRepoCollector().CollectAsync(Context(RepoEnvironment("git"), processes), CancellationToken.None);

        Assert.Equal("git status failed: fatal: not a git repository", result.UnavailableReason);
    }

    [Fact]
    public async Task Config_repo_path_without_its_own_git_dir_is_unavailable_instead_of_reporting_the_enclosing_repo()
    {
        var nested = fixture.Tree.Dir("repo/nested-config");

        var result = await new ConfigRepoCollector().CollectAsync(Context(RepoEnvironment("git", nested), new ProcessRunner()), CancellationToken.None);

        Assert.StartsWith("git status failed: fatal: not a git repository", result.UnavailableReason);
        Assert.Empty(result.Blocks);
    }

    [Fact]
    public async Task Machine_reports_the_claude_version_from_claude_version()
    {
        var environment = TestEnvironments.Create(fixture.Tree, tools: new Dictionary<string, string> { ["claude"] = "claude-bin" });
        var processes = new FakeProcessRunner().On("claude-bin", ["--version"], "2.1.311 (Claude Code)\n");

        var result = await new MachineCollector(fixture.Tree.Path("no-overrides.json")).CollectAsync(Context(environment, processes), CancellationToken.None);

        var rows = Rows(result);
        Assert.Equal("2.1.311", rows["claudeCodeVersion"]!.GetValue<string>());
        Assert.Equal("devbox", rows["hostname"]!.GetValue<string>());
        Assert.False(rows.ContainsKey("overridesFile"));
        Assert.Null(result.Notes);
    }

    [Fact]
    public async Task Machine_omits_the_version_row_with_a_note_when_claude_is_absent()
    {
        var environment = TestEnvironments.Create(fixture.Tree);

        var result = await new MachineCollector(fixture.Tree.Path("no-overrides.json")).CollectAsync(Context(environment, new FakeProcessRunner()), CancellationToken.None);

        Assert.False(Rows(result).ContainsKey("claudeCodeVersion"));
        Assert.Equal(["claude not installed"], result.Notes!);
    }
}
