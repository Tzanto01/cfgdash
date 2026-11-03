using System.Text;
using System.Text.Json.Nodes;
using Cfgdash.Core.Edits;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests.Edits;

/// <summary>edits-v2 over a temp tree: workspace <c>ws</c> with target <c>ws/proj</c>, company layer <c>ws/company</c>, managed dir <c>ws/managed</c>, config dir <c>config/.claude</c>.</summary>
public sealed class FileEditTests : IDisposable
{
    private static readonly string TokenLike = "sk-ant-" + new string('b', 30);

    private readonly TempTree tree = new();
    private readonly FileRegistry registry;
    private readonly EditService service;
    private readonly StubGit git = new();

    public FileEditTests()
    {
        Project = tree.Dir("ws/proj");
        Config = tree.Dir("config/.claude");
        var company = tree.Dir("ws/company");
        var paths = new List<ContextPath>
        {
            TestEnvironments.ContextPath(PathKeys.WorkspaceRoot, tree.Path("ws")),
            TestEnvironments.ContextPath(PathKeys.ClaudeConfigDir, Config),
        };
        registry = new FileRegistry([company]);
        service = new EditService(
            TestEnvironments.Create(tree, paths, new Dictionary<string, string> { ["git"] = "git" }, companyPrefixes: [company]),
            new FakeEnvironmentReader(),
            git,
            new Redactor(),
            new PlatformLocations(tree.Dir("ws/managed"), null, _ => null),
            tree.Path("cache"),
            new SettableTime());
    }

    private string Project { get; }

    private string Config { get; }

    private string AuditPath => tree.Path("cache/edits/audit.jsonl");

    public void Dispose() => tree.Dispose();

    private sealed class StubGit : IProcessRunner
    {
        public int ExitCode { get; set; } = 1;

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? workingDirectory = null, string? standardInput = null) =>
            Task.FromResult(new ProcessResult(ExitCode, string.Empty, string.Empty, false));
    }

    private string Register(string relative, string content, string kind = "instruction") => Register(relative, Encoding.UTF8.GetBytes(content), kind);

    private string Register(string relative, byte[] bytes, string kind = "instruction")
    {
        var path = tree.Path(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return registry.Register(path, kind)!.Id;
    }

    private Task<FileEditInfo> Info(string id) => service.GetFileEditAsync(Project, Registered(id), CancellationToken.None);

    private RegisteredFile Registered(string id) => registry.TryGet(id, out var file) ? file : throw new InvalidOperationException(id);

    private Task<EditResult> Propose(string id, string content, string? sha = null, string? assistant = null, IFileCatalog? files = null) =>
        service.ProposeFileAsync(Project, files ?? registry, id, content, sha, assistant, CancellationToken.None);

    private Task<EditResult> Apply(EditResult proposal) =>
        service.ApplyAsync(EditFixture.Text(proposal.Body["id"]), EditFixture.Text(proposal.Body["confirm"]), CancellationToken.None);

    private async Task<string> ReasonOf(string id) => (await Info(id)).Reason ?? "editable";

    [Theory]
    [InlineData("ws/proj/CLAUDE.md", "instruction")]
    [InlineData("ws/proj/CLAUDE.local.md", "other")]
    [InlineData("ws/proj/AGENTS.override.md", "other")]
    [InlineData("ws/proj/docs/imported.md", "instruction")]
    [InlineData("ws/proj/.claude/agents/reviewer.md", "agent")]
    [InlineData("ws/proj/.claude/skills/tidy/SKILL.md", "skill")]
    [InlineData("ws/proj/.claude/skills/tidy/reference.md", "skill")]
    [InlineData("ws/proj/.claude/commands/ship.md", "command")]
    [InlineData("config/.claude/settings.json", "settings")]
    [InlineData("ws/proj/.claude/settings.local.json", "settings")]
    [InlineData("ws/proj/.t3/worktrees/proj/branch/CLAUDE.md", "instruction")]
    [InlineData("ws/proj/.codex/worktrees/b1/.claude/agents/reviewer.md", "agent")]
    public async Task Editable_kinds_are_editable(string relative, string kind)
    {
        var id = Register(relative, relative.EndsWith(".json", StringComparison.Ordinal) ? "{ \"model\": \"opus\" }\n" : "# notes\n", kind);

        Assert.Equal("editable", await ReasonOf(id));
    }

    [Theory]
    [InlineData("ws/proj/README.md", "other")]
    [InlineData("ws/proj/docs/reviewer.md", "agent")]
    [InlineData("ws/proj/.claude/hooks/check.sh", "hookScript")]
    [InlineData("ws/proj/.mcp.json", "settings")]
    [InlineData("ws/proj/.claude/output-styles/terse.md", "outputStyle")]
    [InlineData("ws/proj/config/settings.json", "settings")]
    public async Task Other_kinds_and_non_md_files_are_not_editable(string relative, string kind)
    {
        var id = Register(relative, "{}\n", kind);

        Assert.Equal("not-editable-kind", await ReasonOf(id));
    }

    [Theory]
    [InlineData("ws/managed/.claude/settings.json", "settings")]
    [InlineData("ws/proj/.opencode/commands/ship.md", "command")]
    [InlineData("ws/proj/.codex/AGENTS.md", "instruction")]
    [InlineData("ws/proj/.t3/CLAUDE.md", "instruction")]
    [InlineData("ws/proj/.git/CLAUDE.md", "instruction")]
    public async Task Managed_other_app_and_git_files_are_never_editable(string relative, string kind)
    {
        var id = Register(relative, "{}\n", kind);

        Assert.Equal("not-editable-kind", await ReasonOf(id));
    }

    [Fact]
    public async Task Never_read_paths_are_never_editable()
    {
        var path = tree.Write("ws/proj/.ssh/CLAUDE.md", "# notes\n");
        var file = new RegisteredFile(new FileRef("ssh", PathUtil.ToJson(path), "instruction", 8, 2, false), path);

        var info = await service.GetFileEditAsync(Project, file, CancellationToken.None);

        Assert.Equal("not-editable-kind", info.Reason);
    }

    [Fact]
    public async Task Company_layer_files_are_read_only_by_flag_and_by_path()
    {
        var id = Register("ws/company/CLAUDE.md", "# company\n");
        var path = tree.Write("ws/company/AGENTS.md", "# company\n");
        var unflagged = new RegisteredFile(new FileRef("unflagged", PathUtil.ToJson(path), "instruction", 10, 3, false), path);
        var flaggedPath = tree.Write("ws/proj/.claude/agents/core.md", "# core\n");
        var flagged = new RegisteredFile(new FileRef("flagged", PathUtil.ToJson(flaggedPath), "agent", 10, 3, true), flaggedPath);

        Assert.Equal("company", await ReasonOf(id));
        Assert.Equal("company", (await service.GetFileEditAsync(Project, unflagged, CancellationToken.None)).Reason);
        Assert.Equal("company", (await service.GetFileEditAsync(Project, flagged, CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task Files_outside_the_harness_roots_are_not_editable_but_ancestors_up_to_the_workspace_root_are()
    {
        var outside = Register("elsewhere/CLAUDE.md", "# outside\n");
        var ancestor = Register("ws/CLAUDE.md", "# workspace\n");

        Assert.Equal("outside-harness-roots", await ReasonOf(outside));
        Assert.Equal("editable", await ReasonOf(ancestor));
    }

    [Fact]
    public async Task Size_limit_is_256_KiB_for_the_file_and_the_content()
    {
        var atLimit = Register("ws/proj/CLAUDE.md", new string('a', FileTargets.MaxBytes));
        var over = Register("ws/proj/docs/big.md", new string('a', FileTargets.MaxBytes + 1));

        Assert.Equal("editable", await ReasonOf(atLimit));
        Assert.Equal("too-large", await ReasonOf(over));
        EditFixture.AssertInvalid(await Propose(atLimit, new string('b', FileTargets.MaxBytes + 1)), null, "too-large");
    }

    [Fact]
    public async Task Invalid_utf8_and_nul_bytes_are_not_text()
    {
        var invalid = Register("ws/proj/CLAUDE.md", [0x23, 0x20, 0xC3, 0x28, 0x0A]);
        var nul = Register("ws/proj/docs/nul.md", [0x23, 0x00, 0x0A]);
        var ok = Register("ws/proj/AGENTS.md", "# ok\n");

        Assert.Equal("not-text", await ReasonOf(invalid));
        Assert.Equal("not-text", await ReasonOf(nul));
        EditFixture.AssertInvalid(await Propose(ok, "# a\0b\n"), null, "not-text");
        EditFixture.AssertInvalid(await Propose(ok, "# lone \ud800 surrogate\n"), null, "not-text");
    }

    [Fact]
    public async Task A_file_the_redactor_would_change_is_read_only_and_has_no_sha256()
    {
        var text = Register("ws/proj/CLAUDE.md", $"token {TokenLike}\n");
        var json = Register("ws/proj/.claude/settings.json", "{ \"headers\": { \"X-Trace\": \"abc\" } }\n", "settings");

        var info = await Info(text);

        Assert.Equal(new FileEditInfo(false, "contains-redacted-values", null, null, null), info);
        Assert.Equal("contains-redacted-values", await ReasonOf(json));
    }

    [Fact]
    public async Task A_settings_file_that_fails_the_strict_parse_is_not_editable_so_comments_are_never_served_raw()
    {
        var id = Register("ws/proj/.claude/settings.json", "{\n  // \"env\": { \"OTEL_EXPORTER_OTLP_HEADERS\": \"x-team=abc123\" },\n  \"model\": \"opus\"\n}\n", "settings");

        var info = await Info(id);

        Assert.Equal("parse-error", info.Reason);
        Assert.Null(info.Content);
    }

    [Fact]
    public async Task Editable_info_carries_the_raw_sha256_and_text()
    {
        var bytes = Encoding.UTF8.GetBytes("{\n  \"model\": \"opus\"\n}\n");
        var id = Register("ws/proj/.claude/settings.json", bytes, "settings");

        var info = await Info(id);

        Assert.True(info.Editable);
        Assert.Equal(EditFixture.Sha256(bytes), info.Sha256);
        Assert.Equal("{\n  \"model\": \"opus\"\n}\n", info.Content);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task GitTracked_follows_the_index_check(int exitCode, bool expected)
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        git.ExitCode = exitCode;
        var id = Register("ws/proj/CLAUDE.md", "# notes\n");

        Assert.Equal(expected, (await Info(id)).GitTracked);
    }

    [Fact]
    public async Task A_symlink_to_a_file_outside_the_roots_is_not_editable()
    {
        var outside = tree.Write("elsewhere/notes.md", "# outside\n");
        EditFixture.Link(tree.Path("ws/proj/CLAUDE.md"), outside);
        var id = registry.Register(tree.Path("ws/proj/CLAUDE.md"), "instruction")!.Id;

        Assert.Equal("outside-harness-roots", await ReasonOf(id));
    }

    [Fact]
    public async Task A_markdown_link_to_a_settings_file_is_not_editable()
    {
        var settings = tree.Write("ws/proj/.claude/settings.json", "{}\n");
        EditFixture.Link(tree.Path("ws/proj/CLAUDE.md"), settings);
        var id = registry.Register(tree.Path("ws/proj/CLAUDE.md"), "instruction")!.Id;

        Assert.Equal("not-editable-kind", await ReasonOf(id));
    }

    [Fact]
    public async Task Proposals_reject_marker_strict_json_and_stale_sha()
    {
        var md = Register("ws/proj/CLAUDE.md", "# notes\n");
        var settings = Register("ws/proj/.claude/settings.json", "{}\n", "settings");

        EditFixture.AssertInvalid(await Propose(md, $"key {Redactor.Marker}\n"), null, "redacted-value");
        EditFixture.AssertInvalid(await Propose(settings, "{ \"model\": \"opus\", }\n"), null, "parse-error");
        var stale = await Propose(md, "# changed\n", sha: EditFixture.Sha256(Encoding.UTF8.GetBytes("# other\n")));
        Assert.Equal(EditStatus.Stale, stale.Status);
        Assert.Equal(EditStatus.Ok, (await Propose(md, "# changed\n", sha: EditFixture.Sha256(Encoding.UTF8.GetBytes("# notes\n")))).Status);
    }

    [Fact]
    public async Task Proposals_for_a_non_editable_file_are_rejected_with_its_reason()
    {
        var id = Register("ws/proj/CLAUDE.md", $"token {TokenLike}\n");

        EditFixture.AssertInvalid(await Propose(id, "# clean\n"), null, "contains-redacted-values");
    }

    [Fact]
    public async Task The_assistant_cannot_propose_content_the_redactor_would_hide_or_files_outside_its_view()
    {
        var id = Register("ws/proj/CLAUDE.md", "# notes\n");
        var content = $"token {TokenLike}\n";

        EditFixture.AssertInvalid(await Propose(id, content, assistant: "s1"), null, "would-hide");
        Assert.Equal(EditStatus.Ok, (await Propose(id, content)).Status);
        Assert.Equal(EditStatus.NotFound, (await Propose(id, "# x\n", assistant: "s1", files: new FileRegistry([]))).Status);
    }

    [Fact]
    public async Task The_assistant_cannot_propose_settings_file_edits()
    {
        var id = Register("ws/proj/.claude/settings.json", "{}\n", "settings");

        var result = await Propose(id, "{ \"model\": \"opus\" }\n", assistant: "s1");

        Assert.Equal(EditStatus.Denied, result.Status);
        Assert.Equal("assistant-policy", (string?)result.Body["reason"]);
    }

    [Fact]
    public async Task Line_endings_and_a_missing_trailing_newline_are_kept_as_sent()
    {
        var id = Register("ws/proj/CLAUDE.md", "# a\r\nb\r\n");

        Assert.Equal(EditStatus.Ok, (await Apply(await Propose(id, "# a\r\nc"))).Status);

        Assert.Equal("# a\r\nc"u8.ToArray(), File.ReadAllBytes(tree.Path("ws/proj/CLAUDE.md")));
    }

    [Fact]
    public async Task A_byte_order_mark_is_kept()
    {
        var id = Register("ws/proj/CLAUDE.md", [0xEF, 0xBB, 0xBF, .. "# a\n"u8.ToArray()]);

        Assert.Equal("# a\n", (await Info(id)).Content);
        Assert.Equal(EditStatus.Ok, (await Apply(await Propose(id, "# b\n"))).Status);

        Assert.Equal([0xEF, 0xBB, 0xBF, .. "# b\n"u8.ToArray()], File.ReadAllBytes(tree.Path("ws/proj/CLAUDE.md")));
    }

    [Theory]
    [InlineData("ws/proj/CLAUDE.md", "instruction")]
    [InlineData("ws/proj/.claude/agents/reviewer.md", "agent")]
    public async Task Propose_apply_and_revert_round_trip_with_audit(string relative, string kind)
    {
        var original = Encoding.UTF8.GetBytes("---\nname: x\n---\n# original\n");
        var id = Register(relative, original, kind);
        var path = tree.Path(relative);

        var proposal = await Propose(id, "---\nname: x\n---\n# edited\n", sha: EditFixture.Sha256(original));
        Assert.Equal(EditStatus.Ok, proposal.Status);
        Assert.Equal(original, File.ReadAllBytes(path));
        var file = proposal.Body["files"]![0]!;
        Assert.Equal("file", (string?)file["scope"]);
        Assert.Contains("-# original\n+# edited", EditFixture.Text(file["diff"]), StringComparison.Ordinal);

        var applied = await Apply(proposal);
        Assert.Equal(EditStatus.Ok, applied.Status);
        Assert.Equal("---\nname: x\n---\n# edited\n"u8.ToArray(), File.ReadAllBytes(path));
        var entry = JsonNode.Parse(File.ReadAllLines(AuditPath)[^1])!;
        Assert.True(JsonNode.DeepEquals(new JsonArray(new JsonObject { ["file"] = id, ["action"] = "edit" }), entry["changes"]));

        var revert = await service.RevertAsync(EditFixture.Text(applied.Body["auditId"]), CancellationToken.None);
        Assert.Equal(EditStatus.Ok, revert.Status);
        Assert.Equal(EditStatus.Ok, (await Apply(revert)).Status);
        Assert.Equal(original, File.ReadAllBytes(path));
        var revertEntry = JsonNode.Parse(File.ReadAllLines(AuditPath)[^1])!;
        Assert.Equal("revert", (string?)revertEntry["changes"]![0]!["action"]);
    }

    [Fact]
    public async Task An_assistant_proposal_without_sha256Before_is_stale_when_the_file_changes_before_apply()
    {
        var id = Register("ws/proj/CLAUDE.md", "# notes\n");
        var proposal = await Propose(id, "# assistant\n", assistant: "s1");
        File.WriteAllText(tree.Path("ws/proj/CLAUDE.md"), "# user\n");

        Assert.Equal(EditStatus.Stale, (await Apply(proposal)).Status);
        Assert.Equal("# user\n", File.ReadAllText(tree.Path("ws/proj/CLAUDE.md")));
    }

    [Fact]
    public async Task Reverting_a_file_changed_since_the_apply_is_stale()
    {
        var id = Register("ws/proj/CLAUDE.md", "# notes\n");
        var applied = await Apply(await Propose(id, "# edited\n"));
        File.WriteAllText(tree.Path("ws/proj/CLAUDE.md"), "# later\n");

        Assert.Equal(EditStatus.Stale, (await service.RevertAsync(EditFixture.Text(applied.Body["auditId"]), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_settings_edit_adding_a_secret_shows_a_redacted_diff_of_only_the_change()
    {
        var id = Register("ws/proj/.claude/settings.json", "{\r\n    \"model\": \"opus\"\r\n}\r\n", "settings");

        var proposal = await Propose(id, "{\r\n    \"model\": \"opus\",\r\n    \"headers\": { \"X-Trace\": \"abc\" }\r\n}\r\n");

        var diff = EditFixture.Text(proposal.Body["files"]![0]!["diff"]);
        Assert.DoesNotContain("abc", diff, StringComparison.Ordinal);
        Assert.Single(diff.Split('\n'), l => l.StartsWith('-') && !l.StartsWith("---", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_revert_diff_of_a_settings_edit_redacts_secret_named_keys()
    {
        var id = Register("ws/proj/.claude/settings.json", "{\n  \"model\": \"opus\"\n}\n", "settings");
        var applied = await Apply(await Propose(id, "{\n  \"model\": \"opus\",\n  \"headers\": { \"X-Trace\": \"abc\" }\n}\n"));

        var revert = await service.RevertAsync(EditFixture.Text(applied.Body["auditId"]), CancellationToken.None);

        Assert.Equal(EditStatus.Ok, revert.Status);
        Assert.DoesNotContain("abc", EditFixture.Text(revert.Body["files"]![0]!["diff"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_symlinked_file_is_written_at_its_real_path_and_keeps_the_link()
    {
        var real = tree.Write("config/.claude/CLAUDE.md", "# real\n");
        var link = tree.Path("ws/proj/CLAUDE.md");
        EditFixture.Link(link, real);
        var id = registry.Register(link, "instruction")!.Id;

        var proposal = await Propose(id, "# edited\n");
        Assert.Equal(EditFixture.Real(real), (string?)proposal.Body["files"]![0]!["path"]);
        var applied = await Apply(proposal);
        Assert.Equal(EditStatus.Ok, applied.Status);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("# edited\n", File.ReadAllText(real));

        Assert.Equal(EditStatus.Ok, (await Apply(await service.RevertAsync(EditFixture.Text(applied.Body["auditId"]), CancellationToken.None))).Status);
        Assert.Equal("# real\n", File.ReadAllText(real));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }

    [Fact]
    public async Task Retargeting_the_symlink_after_the_proposal_makes_the_apply_stale()
    {
        var first = tree.Write("config/.claude/CLAUDE.md", "# real\n");
        var second = tree.Write("elsewhere/CLAUDE.md", "# real\n");
        var link = tree.Path("ws/proj/CLAUDE.md");
        EditFixture.Link(link, first);
        var id = registry.Register(link, "instruction")!.Id;
        var proposal = await Propose(id, "# edited\n");

        File.Delete(link);
        File.CreateSymbolicLink(link, second);

        Assert.Equal(EditStatus.Stale, (await Apply(proposal)).Status);
        Assert.Equal("# real\n", File.ReadAllText(first));
        Assert.Equal("# real\n", File.ReadAllText(second));
    }
}
