using Cfgdash.Core.Edits;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests.Edits;

public sealed class EditApplyGuardTests : IDisposable
{
    private const string Sonnet = "{\n  \"model\": \"sonnet\"\n}\n";

    private readonly EditFixture f = new();

    public void Dispose() => f.Dispose();

    private static void AssertError(EditResult result, EditStatus status, string error)
    {
        Assert.Equal(status, result.Status);
        Assert.False(result.Wrote);
        Assert.Equal(error, (string?)result.Body["error"]);
    }

    [Fact]
    public async Task An_unknown_id_is_not_found()
    {
        var result = await f.Service.ApplyAsync("0123456789abcdef0123456789abcdef", "anything", CancellationToken.None);

        AssertError(result, EditStatus.NotFound, "not-found");
    }

    [Theory]
    [InlineData(599, EditStatus.Ok)]
    [InlineData(601, EditStatus.NotFound)]
    public async Task A_proposal_is_usable_for_ten_minutes(int secondsLater, EditStatus expected)
    {
        f.WriteProject(Sonnet);
        var proposal = await f.Propose(EditFixture.Set("model", "project", "opus"));
        f.Time.Now = f.Time.Now.AddSeconds(secondsLater);

        var result = await f.Apply(proposal);

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected == EditStatus.Ok, File.Exists(f.AuditPath));
        Assert.Equal(expected == EditStatus.Ok ? "{\n  \"model\": \"opus\"\n}\n" : Sonnet, File.ReadAllText(f.ProjectSettings));
    }

    [Fact]
    public async Task A_missing_or_wrong_confirm_is_refused_and_the_proposal_stays_usable()
    {
        f.WriteProject(Sonnet);
        var proposal = await f.Propose(EditFixture.Set("model", "project", "opus"));
        var id = EditFixture.Text(proposal.Body["id"]);

        var missing = await f.Service.ApplyAsync(id, null, CancellationToken.None);
        var wrong = await f.Service.ApplyAsync(id, new string('0', 64), CancellationToken.None);

        AssertError(missing, EditStatus.ConfirmMismatch, "confirm-mismatch");
        AssertError(wrong, EditStatus.ConfirmMismatch, "confirm-mismatch");
        Assert.Equal(Sonnet, File.ReadAllText(f.ProjectSettings));
        Assert.False(File.Exists(f.AuditPath));
        var applied = await f.Apply(proposal);
        Assert.Equal(EditStatus.Ok, applied.Status);
    }

    [Fact]
    public async Task A_proposal_can_be_applied_only_once()
    {
        f.WriteProject(Sonnet);
        var proposal = await f.Propose(EditFixture.Set("model", "project", "opus"));
        await f.Apply(proposal);

        var second = await f.Apply(proposal);

        AssertError(second, EditStatus.NotFound, "not-found");
        Assert.Single(File.ReadAllLines(f.AuditPath));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("appeared")]
    [InlineData("vanished")]
    public async Task Apply_is_stale_and_writes_nothing_when_the_target_changed_appeared_or_vanished(string mutation)
    {
        if (mutation != "appeared")
        {
            f.WriteProject(Sonnet);
        }

        var proposal = await f.Propose(EditFixture.Set("model", "project", "opus"));
        string? expected = null;
        switch (mutation)
        {
            case "changed":
                expected = "{\n  \"model\": \"haiku\"\n}\n";
                File.WriteAllText(f.ProjectSettings, expected);
                break;
            case "appeared":
                expected = "{}\n";
                f.WriteProject(expected);
                break;
            default:
                File.Delete(f.ProjectSettings);
                break;
        }

        var result = await f.Apply(proposal);

        AssertError(result, EditStatus.Stale, "stale");
        Assert.Equal(expected is not null, File.Exists(f.ProjectSettings));
        if (expected is not null)
        {
            Assert.Equal(expected, File.ReadAllText(f.ProjectSettings));
        }

        Assert.False(File.Exists(f.AuditPath));
    }

    [Fact]
    public async Task One_stale_file_among_several_leaves_every_file_untouched()
    {
        f.WriteUser(Sonnet);
        f.WriteProject("{}\n");
        var proposal = await f.Propose(EditFixture.Set("model", "user", "opus"), EditFixture.Set("verbose", "project", true));
        File.WriteAllText(f.ProjectSettings, "{\n  \"theme\": \"dark\"\n}\n");

        var result = await f.Apply(proposal);

        AssertError(result, EditStatus.Stale, "stale");
        Assert.Equal(Sonnet, File.ReadAllText(f.UserSettings));
        Assert.Equal("{\n  \"theme\": \"dark\"\n}\n", File.ReadAllText(f.ProjectSettings));
        Assert.False(File.Exists(f.AuditPath));
    }

    [LinuxFact]
    public async Task Apply_is_stale_when_a_symlinked_settings_file_is_retargeted()
    {
        var first = f.Tree.Write("proj/shared/a/settings.json", Sonnet);
        var second = f.Tree.Write("proj/shared/b/settings.json", Sonnet);
        EditFixture.Link(f.ProjectSettings, first);
        var proposal = await f.Propose(EditFixture.Set("model", "project", "opus"));
        File.Delete(f.ProjectSettings);
        File.CreateSymbolicLink(f.ProjectSettings, second);

        var result = await f.Apply(proposal);

        AssertError(result, EditStatus.Stale, "stale");
        Assert.Equal(Sonnet, File.ReadAllText(first));
        Assert.Equal(Sonnet, File.ReadAllText(second));
        Assert.False(File.Exists(f.AuditPath));
    }

    [Fact]
    public async Task Concurrent_applies_of_one_proposal_succeed_exactly_once()
    {
        f.WriteProject(Sonnet);
        var proposal = await f.Propose(EditFixture.Set("model", "project", "opus"));

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => f.Apply(proposal))));

        Assert.Single(results, r => r.Status == EditStatus.Ok);
        Assert.Equal(7, results.Count(r => r.Status == EditStatus.NotFound));
        Assert.Single(File.ReadAllLines(f.AuditPath));
        Assert.Equal("{\n  \"model\": \"opus\"\n}\n", File.ReadAllText(f.ProjectSettings));
    }
}
