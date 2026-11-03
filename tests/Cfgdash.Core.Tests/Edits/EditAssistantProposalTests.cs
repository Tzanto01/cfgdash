using System.Text.Json.Nodes;
using Cfgdash.Core.Edits;

namespace Cfgdash.Core.Tests.Edits;

public sealed class EditAssistantProposalTests : IDisposable
{
    private static readonly string HighConfidenceToken = "gh" + "p_" + new string('z', 36);
    private readonly EditFixture f = new();

    public void Dispose() => f.Dispose();

    private Task<EditResult> Assistant(JsonNode change, string session = "session-1") =>
        f.Service.ProposeForAssistantAsync(f.Project, EditFixture.Changes(change.DeepClone()), session, false, CancellationToken.None);

    [Fact]
    public async Task An_auto_proposal_whose_file_changed_before_apply_answers_stale_and_writes_nothing()
    {
        f.WriteProject("""{"model":"haiku"}""");
        var proposal = await f.Service.ProposeForAssistantAsync(f.Project, EditFixture.Changes(EditFixture.Set("model", "project", "opus")), "session-1", true, CancellationToken.None);
        f.WriteProject("""{"model":"sonnet"}""");

        var applied = await f.Apply(proposal);

        Assert.Equal(EditStatus.Stale, applied.Status);
        Assert.Equal("""{"model":"sonnet"}""", File.ReadAllText(f.ProjectSettings));
        Assert.False(File.Exists(f.AuditPath));
    }

    [Fact]
    public async Task The_redacted_marker_is_rejected_for_a_string_value_by_both_entry_points()
    {
        var change = EditFixture.Set("model", "project", "[redacted]");

        EditFixture.AssertInvalid(await f.Propose(change.DeepClone()), "model", "redacted-value");
        EditFixture.AssertInvalid(await Assistant(change), "model", "redacted-value");
    }

    [Fact]
    public async Task The_redacted_marker_is_rejected_inside_a_list_by_both_entry_points()
    {
        var change = EditFixture.Set("permissions.deny", "project", new JsonArray("Read", "[redacted]"));

        EditFixture.AssertInvalid(await f.Propose(change.DeepClone()), "permissions.deny", "redacted-value");
        EditFixture.AssertInvalid(await Assistant(change), "permissions.deny", "redacted-value");
    }

    [Fact]
    public async Task A_value_the_redactor_would_change_is_rejected_for_the_assistant_only()
    {
        var change = EditFixture.Set("model", "project", HighConfidenceToken);

        var publicResult = await f.Propose(change.DeepClone());
        var assistantResult = await Assistant(change);

        Assert.Equal(EditStatus.Ok, publicResult.Status);
        EditFixture.AssertInvalid(assistantResult, "model", "would-hide");
    }

    [Fact]
    public async Task Eight_assistant_proposals_are_open_and_the_ninth_answers_quota_while_public_proposals_are_unaffected()
    {
        for (var i = 0; i < 8; i++)
        {
            Assert.Equal(EditStatus.Ok, (await Assistant(EditFixture.Set("model", "project", "opus"))).Status);
        }

        var ninth = await Assistant(EditFixture.Set("model", "project", "opus"));
        var publicResult = await f.Propose(EditFixture.Set("model", "project", "opus"));

        EditFixture.AssertInvalid(ninth, null, "quota");
        Assert.Equal(EditStatus.Ok, publicResult.Status);
    }

    [Fact]
    public async Task Expired_assistant_proposals_free_the_quota()
    {
        for (var i = 0; i < 8; i++)
        {
            await Assistant(EditFixture.Set("model", "project", "opus"));
        }

        f.Time.Now += TimeSpan.FromHours(1);
        var result = await Assistant(EditFixture.Set("model", "project", "opus"));

        Assert.Equal(EditStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Applying_an_assistant_proposal_audits_origin_and_session_id()
    {
        var proposal = await Assistant(EditFixture.Set("model", "project", "opus"), "session-abc");

        var applied = await f.Apply(proposal);

        Assert.Equal(EditStatus.Ok, applied.Status);
        var entry = JsonNode.Parse(Assert.Single(File.ReadAllLines(f.AuditPath)))!.AsObject();
        Assert.Equal("assistant", (string?)entry["origin"]);
        Assert.Equal("session-abc", (string?)entry["sessionId"]);
    }

    [Fact]
    public async Task Applying_a_public_proposal_audits_neither_origin_nor_session_id()
    {
        var proposal = await f.Propose(EditFixture.Set("model", "project", "opus"));

        await f.Apply(proposal);

        var entry = JsonNode.Parse(Assert.Single(File.ReadAllLines(f.AuditPath)))!.AsObject();
        Assert.False(entry.ContainsKey("origin"));
        Assert.False(entry.ContainsKey("sessionId"));
    }
}
