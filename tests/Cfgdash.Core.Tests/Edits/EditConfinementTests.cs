using Cfgdash.Core.Edits;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests.Edits;

public sealed class EditConfinementTests : IDisposable
{
    private readonly EditFixture f = new();

    public void Dispose() => f.Dispose();

    private static async Task AssertRefused(EditFixture fixture)
    {
        var result = await fixture.Propose(EditFixture.Set("model", "project", "opus"));

        EditFixture.AssertInvalid(result, "model", "unsafe-target");
        Assert.False(Directory.Exists(fixture.Cache));
    }

    [LinuxFact]
    public async Task A_settings_file_linked_to_the_claude_json_file_is_refused()
    {
        var target = f.Tree.Write("proj/other/.claude.json", "{}");
        EditFixture.Link(f.ProjectSettings, target);

        await AssertRefused(f);

        Assert.Equal("{}", File.ReadAllText(target));
    }

    [LinuxFact]
    public async Task A_settings_file_linked_to_a_credentials_file_is_refused()
    {
        var target = f.Tree.Write("proj/other/.credentials.json", "{}");
        EditFixture.Link(f.ProjectSettings, target);

        await AssertRefused(f);

        Assert.Equal("{}", File.ReadAllText(target));
    }

    [LinuxFact]
    public async Task A_settings_file_linked_to_a_file_outside_the_scope_root_is_refused()
    {
        var target = f.Tree.Write("elsewhere/settings.json", "{}");
        EditFixture.Link(f.ProjectSettings, target);

        await AssertRefused(f);

        Assert.Equal("{}", File.ReadAllText(target));
    }

    [LinuxFact]
    public async Task A_claude_dir_linked_outside_the_scope_root_is_refused()
    {
        var target = f.Tree.Write("elsewhere/settings.json", "{}");
        Directory.CreateSymbolicLink(f.Tree.Path("proj/.claude"), f.Tree.Path("elsewhere"));

        await AssertRefused(f);

        Assert.Equal("{}", File.ReadAllText(target));
    }

    [LinuxFact]
    public async Task A_settings_file_linked_into_the_managed_dir_is_refused()
    {
        using var g = new EditFixture(managedDir: "proj/managed");
        var target = g.Tree.Write("proj/managed/settings.json", "{}");
        EditFixture.Link(g.ProjectSettings, target);

        await AssertRefused(g);

        Assert.Equal("{}", File.ReadAllText(target));
    }

    [Theory]
    [InlineData(".git/sub")]
    [InlineData(".ssh/keys")]
    public async Task Directories_inside_git_state_or_credential_folders_are_refused(string relative)
    {
        var dir = f.Tree.Dir("proj/" + relative);

        var result = await f.ProposeIn(dir, EditFixture.Set("model", "project", "opus"));

        EditFixture.AssertInvalid(result, "model", "unsafe-target");
        Assert.False(Directory.Exists(Path.Combine(dir, ".claude")));
        Assert.False(Directory.Exists(f.Cache));
    }

    [LinuxFact]
    public async Task A_symlinked_user_settings_file_inside_the_config_dir_is_written_through_and_the_link_survives()
    {
        var shared = f.Tree.Write("config/.claude/shared/settings.json", "{}\n");
        EditFixture.Link(f.UserSettings, shared);
        var proposal = await f.Propose(EditFixture.Set("verbose", "user", true));

        var result = await f.Apply(proposal);

        Assert.Equal(EditStatus.Ok, result.Status);
        Assert.Equal(EditFixture.Real(shared), EditFixture.Text(proposal.Body["files"]![0]!["path"]));
        Assert.NotNull(new FileInfo(f.UserSettings).LinkTarget);
        Assert.Equal("{\n  \"verbose\": true\n}\n", File.ReadAllText(shared));
        Assert.Equal(new[] { "settings.json" }, EditFixture.Names(Path.GetDirectoryName(shared)!));
    }

    [LinuxFact]
    public async Task A_symlinked_project_settings_file_is_refused_even_inside_the_project()
    {
        var shared = f.Tree.Write("proj/shared/settings.json", "{}\n");
        EditFixture.Link(f.ProjectSettings, shared);

        var result = await f.Propose(EditFixture.Set("verbose", "project", true));

        EditFixture.AssertInvalid(result, "verbose", "unsafe-target");
        Assert.Equal("{}\n", File.ReadAllText(shared));
    }
}
