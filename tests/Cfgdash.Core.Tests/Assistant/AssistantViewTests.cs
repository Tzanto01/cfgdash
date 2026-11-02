using Cfgdash.Core.Infrastructure;
using static Cfgdash.Core.Tests.Assistant.AssistantViewFixture;

namespace Cfgdash.Core.Tests.Assistant;

public sealed class AssistantViewTests : IDisposable
{
    private readonly AssistantViewFixture f = new();

    public void Dispose() => f.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Memory_transcript_and_usage_sections_are_always_dropped(bool inCompany)
    {
        var view = f.View(inCompany);

        var ids = view.Snapshot.Sections.Select(s => s.CollectorId).Concat(view.Snapshot.Collectors.Select(c => c.Id)).ToHashSet();
        Assert.DoesNotContain("engram.memory", ids);
        Assert.DoesNotContain("sessions.transcripts", ids);
        Assert.DoesNotContain("usage.metrics", ids);
        Assert.Contains("settings.effective", ids);
    }

    [Fact]
    public void Outside_the_company_layer_company_rows_nodes_cells_and_sources_are_dropped()
    {
        var view = f.View(false);

        Assert.Equal(["plain", "other-flag"], KvKeys(view, "settings.effective", "kv"));
        Assert.Equal(["plain-row"], RowIds(view, "agents.definitions", "table"));
        Assert.Equal(["plain-node", "plain-child"], NodeIds(view, "instructions.chain", "tree"));
    }

    [Fact]
    public void Outside_the_company_layer_the_company_core_collector_and_layer_path_are_dropped()
    {
        var view = f.View(false);

        Assert.DoesNotContain(view.Snapshot.Sections, s => s.CollectorId == "agents.company-core");
        Assert.DoesNotContain(view.Snapshot.Collectors, c => c.Id == "agents.company-core");
        Assert.DoesNotContain(view.Snapshot.Context.Paths, p => p.Key == PathKeys.CompanyLayer);
        Assert.Contains(view.Snapshot.Context.Paths, p => p.Key == PathKeys.ClaudeConfigDir);
    }

    [Fact]
    public void Inside_the_company_layer_company_items_core_and_layer_path_are_kept()
    {
        var view = f.View(true);

        Assert.Equal(["plain", "flagged", "company-source", "company-contribution", "other-flag"], KvKeys(view, "settings.effective", "kv"));
        Assert.Equal(["plain-row", "badged-row", "company-file-row", "company-cell-row"], RowIds(view, "agents.definitions", "table"));
        Assert.Equal(["plain-node", "plain-child", "badged-node", "badged-child", "company-file-node"], NodeIds(view, "instructions.chain", "tree"));
        Assert.Contains(view.Snapshot.Sections, s => s.CollectorId == "agents.company-core");
        Assert.Contains(view.Snapshot.Context.Paths, p => p.Key == PathKeys.CompanyLayer);
    }

    [Fact]
    public void Files_hold_only_files_referenced_by_kept_sections_outside_the_company_layer()
    {
        var view = f.View(false);

        var expected = new[] { f.Plain.Id, f.Plain2.Id }.Order().ToArray();
        Assert.Equal(expected, view.Files.All.Select(x => x.Ref.Id).Order().ToArray());
        Assert.Equal(expected, view.Snapshot.Files.Select(x => x.Id).Order().ToArray());
        foreach (var id in new[] { f.Orphan.Id, f.Memory.Id, f.CoreOnly.Id, f.BadgedChildFile.Id, "dangling" }.Concat(f.CompanyFiles.Select(c => c.Id)))
        {
            Assert.False(view.Files.TryGet(id, out _), id);
        }
    }

    [Fact]
    public void Inside_the_company_layer_files_include_company_and_core_files_but_still_not_unreferenced_or_excluded_ones()
    {
        var view = f.View(true);

        var expected = new[] { f.Plain.Id, f.Plain2.Id, f.CoreOnly.Id, f.BadgedChildFile.Id }.Concat(f.CompanyFiles.Select(c => c.Id)).Order().ToArray();
        Assert.Equal(expected, view.Files.All.Select(x => x.Ref.Id).Order().ToArray());
        Assert.Equal(expected, view.Snapshot.Files.Select(x => x.Id).Order().ToArray());
    }
}
