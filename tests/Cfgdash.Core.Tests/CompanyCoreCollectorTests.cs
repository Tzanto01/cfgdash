using System.Security.Cryptography;
using System.Text;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Environment;
using Cfgdash.Core.Files;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

/// <summary>
/// A synthetic workspace <c>ws/config</c> + <c>ws/acme</c> with a fake <c>agent-core.ps1</c> that derives the company prefix
/// from its own location, run once through real pwsh.
/// </summary>
public sealed class CompanyCoreFixture : IDisposable
{
    public const string ReadyCore = "Grüße aus der Küche: 5 € pro Tag";

    private const string FakeAgentCore = """
        function Get-CompanyPrefixes {
            param([string]$PathsFile, [switch]$DerivedOnly)
            if (-not $DerivedOnly) { throw 'DerivedOnly is required' }
            if (-not (Test-Path -LiteralPath $PathsFile)) { throw 'paths file not found' }
            return @([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../../acme')))
        }

        function Get-AgentComposedContext {
            param([string[]]$Prefixes, [string]$AgentType)
            $core = Join-Path $Prefixes[0] ".claude/custom/agents/$AgentType/core.md"
            if (-not (Test-Path -LiteralPath $core)) { return @{ State = 'Missing'; Context = $null } }
            $text = [IO.File]::ReadAllText($core, [Text.Encoding]::UTF8)
            if ([string]::IsNullOrWhiteSpace($text)) { return @{ State = 'Empty'; Context = $null } }
            return @{ State = 'Ready'; Context = "Core for ${AgentType}:`n" + $text }
        }
        """;

    public CompanyCoreFixture()
    {
        Tree.Write("ws/config/.claude/hooks/lib/agent-core.ps1", FakeAgentCore);
        Tree.Write("ws/config/.claude/hooks/company-paths.psd1", "@{ CompanyPathPrefixes = @('acme') }");
        ReadyFile = Tree.Write("ws/acme/.claude/custom/agents/reviewer/core.md", ReadyCore);
        Tree.Write("ws/acme/.claude/custom/agents/blank/core.md", "  \n");
        Tree.Dir("ws/acme/.claude/custom/agents/nocore");
        Tree.Write("ws/acme/.claude/custom/agents/bad name/core.md", "bad");
        Tree.Write("ws/other/.claude/custom/agents/stray/core.md", "stray");

        var pwsh = new ProcessRunner().RunAsync("pwsh", ["-NoProfile", "-NonInteractive", "-Command", "exit 0"], TimeSpan.FromSeconds(30), CancellationToken.None)
            .ContinueWith(t => t.IsCompletedSuccessfully && t.Result.ExitCode == 0).GetAwaiter().GetResult();
        Assert.True(pwsh, "pwsh (PowerShell 7) must be on PATH to run the agents.company-core tests");

        Machine = CreateEnvironment(Tree, "pwsh");
        Files = Context(Machine, new ProcessRunner());
        Result = new CompanyCoreCollector().CollectAsync(Files, CancellationToken.None).GetAwaiter().GetResult();
    }

    public CollectContext Files { get; }

    public TempTree Tree { get; } = new();

    public string ReadyFile { get; }

    public MachineEnvironment Machine { get; }

    public CollectorResult Result { get; }

    public void Dispose() => Tree.Dispose();

    internal static MachineEnvironment CreateEnvironment(TempTree tree, string pwsh) => TestEnvironments.Create(
        tree,
        paths:
        [
            TestEnvironments.ContextPath(PathKeys.ConfigRepo, tree.Path("ws/config")),
            TestEnvironments.ContextPath(PathKeys.CompanyLayer, tree.Path("ws/acme")),
        ],
        tools: new Dictionary<string, string> { ["pwsh"] = pwsh },
        companyPrefixes: [tree.Path("ws/acme"), tree.Path("ws/other")]);

    internal static CollectContext Context(MachineEnvironment environment, IProcessRunner processes) => new()
    {
        Environment = environment,
        TargetDir = environment.HomeDir,
        Files = new FileRegistry([]),
        Redactor = new Redactor(),
        Processes = processes,
    };
}

public sealed class CompanyCoreCollectorTests(CompanyCoreFixture fixture) : IClassFixture<CompanyCoreFixture>
{
    private static readonly string[] PwshArguments = ["-NoProfile", "-NonInteractive", "-Command", "-"];

    private IReadOnlyList<TreeNode> Nodes => ((TreeBlock)fixture.Result.Blocks.Single()).Nodes;

    [Fact]
    public void Ready_core_carries_the_exact_composed_text_and_its_hash()
    {
        Assert.Null(fixture.Result.UnavailableReason);
        var reviewer = Nodes.Single(n => n.Label == "reviewer");
        var expected = "Core for reviewer:\n" + CompanyCoreFixture.ReadyCore;

        Assert.Equal(expected, reviewer.Meta!["text"]!.GetValue<string>());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(expected))), reviewer.Meta["sha256"]!.GetValue<string>());
        Assert.Equal(expected.Length, reviewer.Meta["chars"]!.GetValue<int>());
        Assert.Equal(["ready", "company"], reviewer.Badges);
        Assert.Equal(FileRegistry.IdFor(fixture.ReadyFile), reviewer.FileId);
    }

    [Fact]
    public void States_are_reported_per_type_in_sorted_order()
    {
        Assert.Equal(["bad name", "blank", "reviewer", "stray"], Nodes.Select(n => n.Label));
        Assert.Equal(["Invalid", "Empty", "Ready", "Missing"], Nodes.Select(n => n.Meta!["state"]!.GetValue<string>()));
        Assert.Equal(["empty", "company"], Nodes.Single(n => n.Label == "blank").Badges);
        Assert.False(Nodes.Single(n => n.Label == "blank").Meta!.ContainsKey("text"));
        Assert.Contains("Missing: 1, Empty: 1, Invalid: 1", fixture.Result.Notes!);
        Assert.Contains("Derived company prefixes: 1", fixture.Result.Notes!);
    }

    [Fact]
    public void Core_files_are_registered_as_company_files()
    {
        var reviewer = Nodes.Single(n => n.Label == "reviewer");

        Assert.True(fixture.Files.Files.TryGet(reviewer.FileId!, out var file));
        Assert.True(file.Ref.Company);
        Assert.Equal("agent", file.Ref.Kind);
        Assert.DoesNotContain(Nodes, n => n.Label == "nocore");
    }

    [Fact]
    public async Task Script_is_one_line_and_never_embeds_an_invalid_type_name()
    {
        var runner = new FakeProcessRunner().On("pwsh-fake", PwshArguments, "not base64 output");
        var result = await new CompanyCoreCollector().CollectAsync(CompanyCoreFixture.Context(CompanyCoreFixture.CreateEnvironment(fixture.Tree, "pwsh-fake"), runner), CancellationToken.None);

        var script = Assert.Single(runner.StandardInputs)!;
        Assert.DoesNotContain('\n', script);
        Assert.DoesNotContain('\r', script);
        Assert.DoesNotContain("bad name", script, StringComparison.Ordinal);
        Assert.Contains("'reviewer'", script, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Tree.Root, script, StringComparison.Ordinal);
        Assert.Equal("unexpected output from agent-core.ps1", result.UnavailableReason);
    }

    [Fact]
    public async Task Non_zero_exit_becomes_unavailable_with_the_first_stderr_line()
    {
        var runner = new FakeProcessRunner().On("pwsh-fake", PwshArguments, string.Empty, exitCode: 1, stderr: "agent-core exploded\nat line 2");
        var result = await new CompanyCoreCollector().CollectAsync(CompanyCoreFixture.Context(CompanyCoreFixture.CreateEnvironment(fixture.Tree, "pwsh-fake"), runner), CancellationToken.None);

        Assert.Equal("pwsh failed: agent-core exploded", result.UnavailableReason);
        Assert.Empty(result.Blocks);
    }
}
