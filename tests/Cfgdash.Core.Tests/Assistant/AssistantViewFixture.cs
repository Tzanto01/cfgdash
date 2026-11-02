using System.Text.Json.Nodes;
using Cfgdash.Core.Assistant;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests.Assistant;

/// <summary>A synthetic snapshot mixing plain, company and excluded-collector items, registered over real temp files.</summary>
internal sealed class AssistantViewFixture : IDisposable
{
    public const string SecretWord = "zebraquartz";

    public AssistantViewFixture()
    {
        var company = Tree.Dir("work/company");
        CompanyPrefix = company;
        OutsideDir = Tree.Dir("work/proj");
        InsideDir = Tree.Dir("work/company/proj");
        Registry = new FileRegistry([company]);
        Plain = File("plain.md", "plain text");
        Plain2 = File("plain2.md", "second plain");
        Orphan = File("orphan.md", "referenced nowhere");
        Memory = File("memory.md", "memory only");
        CoreOnly = File("core.md", "core only");
        CompanyRowFile = File("company-row.md", "c1", true);
        CompanyKvSource = File("company-kv.md", "c2", true);
        CompanyContribution = File("company-contribution.md", "c3", true);
        CompanyCell = File("company-cell.md", "c4", true);
        CompanyNodeFile = File("company-node.md", "c5", true);
        BadgedChildFile = File("badged-child.md", "c6");
        Snapshot = BuildSnapshot();
    }

    public TempTree Tree { get; } = new();

    public FileRegistry Registry { get; }

    public HarnessSnapshot Snapshot { get; }

    public string CompanyPrefix { get; }

    public string OutsideDir { get; }

    public string InsideDir { get; }

    public FileRef Plain { get; }

    public FileRef Plain2 { get; }

    public FileRef Orphan { get; }

    public FileRef Memory { get; }

    public FileRef CoreOnly { get; }

    public FileRef CompanyRowFile { get; }

    public FileRef CompanyKvSource { get; }

    public FileRef CompanyContribution { get; }

    public FileRef CompanyCell { get; }

    public FileRef CompanyNodeFile { get; }

    public FileRef BadgedChildFile { get; }

    public FileRef[] CompanyFiles => [CompanyRowFile, CompanyKvSource, CompanyContribution, CompanyCell, CompanyNodeFile];

    public AssistantView View(bool inCompany) => Create(Snapshot, Registry, inCompany ? InsideDir : OutsideDir);

    public AssistantView Create(HarnessSnapshot snapshot, IFileCatalog files, string dir) =>
        AssistantView.Create(new SnapshotResult(snapshot, files), TestEnvironments.Create(Tree, companyPrefixes: [CompanyPrefix]), dir);

    public static CollectorStatus Status(string id, string view) =>
        new(id, id, "1", view, CollectorState.Active, null, new Requirements(), 0);

    public static HarnessSnapshot Snap(IEnumerable<Section> sections, IReadOnlyList<FileRef>? files = null, IReadOnlyList<ContextPath>? paths = null) =>
        new(1, new MachineInfo("host.example", null, new OsInfo(OsFamily.Linux, "Test OS", "x64"), DateTimeOffset.UnixEpoch, "0.1.0"),
            new SnapshotContext("/home/user/proj", paths ?? []), [.. sections.Select(s => Status(s.CollectorId, s.View))], [.. sections], files ?? []);

    public static KvRow Kv(string key, string? fileId = null, IReadOnlyList<string>? flags = null, IReadOnlyList<Contribution>? contributions = null, string value = "v") =>
        new(key, JsonValue.Create(value), fileId is null ? null : new SourceRef("label", fileId), false, flags, contributions);

    public static TableRow Row(string id, string? fileId = null, IReadOnlyList<string>? flags = null, string? cellFileId = null) =>
        new(id, new Dictionary<string, JsonNode?>
        {
            ["name"] = cellFileId is null ? id : new JsonObject { ["text"] = id, ["fileId"] = cellFileId },
        }, flags, fileId);

    public static string[] KvKeys(AssistantView view, string collectorId, string blockId) =>
        [.. view.Snapshot.Sections.Where(s => s.CollectorId == collectorId).SelectMany(s => s.Blocks).OfType<KvBlock>().Where(b => b.Id == blockId).SelectMany(b => b.Rows).Select(r => r.Key)];

    public static string[] RowIds(AssistantView view, string collectorId, string blockId) =>
        [.. view.Snapshot.Sections.Where(s => s.CollectorId == collectorId).SelectMany(s => s.Blocks).OfType<TableBlock>().Where(b => b.Id == blockId).SelectMany(b => b.Rows).Select(r => r.Id)];

    public static string[] NodeIds(AssistantView view, string collectorId, string blockId)
    {
        static IEnumerable<string> Flat(IEnumerable<TreeNode> nodes) => nodes.SelectMany(n => Flat(n.Children).Prepend(n.Id));
        return [.. Flat(view.Snapshot.Sections.Where(s => s.CollectorId == collectorId).SelectMany(s => s.Blocks).OfType<TreeBlock>().Where(b => b.Id == blockId).SelectMany(b => b.Nodes))];
    }

    public void Dispose() => Tree.Dispose();

    private FileRef File(string name, string content, bool company = false) =>
        Registry.Register(Tree.Write("work/files/" + name, content), "other", company)!;

    private HarnessSnapshot BuildSnapshot()
    {
        var contributions = new[] { new Contribution(JsonValue.Create("x"), new SourceRef("c", CompanyContribution.Id), ContributionStatus.Applied) };
        var sections = new[]
        {
            new Section("settings.effective", "settings", "Settings",
            [
                new KvBlock("kv", "KV",
                [
                    Kv("plain", Plain.Id),
                    Kv("flagged", flags: ["company"]),
                    Kv("company-source", CompanyKvSource.Id),
                    Kv("company-contribution", contributions: contributions),
                    Kv("other-flag", flags: ["missing-script"]),
                ]),
            ]),
            new Section("agents.definitions", "agents", "Agents",
            [
                new TableBlock("table", "Table", [new TableColumn("name", "Name")],
                [
                    Row("plain-row", Plain2.Id),
                    Row("badged-row", flags: ["company"]),
                    Row("company-file-row", CompanyRowFile.Id),
                    Row("company-cell-row", cellFileId: CompanyCell.Id),
                ]),
            ]),
            new Section("instructions.chain", "instructions", "Instructions",
            [
                new TreeBlock("tree", "Tree",
                [
                    new TreeNode("plain-node", "plain", [new TreeNode("plain-child", "child", [])]),
                    new TreeNode("badged-node", "badged", [new TreeNode("badged-child", "child", [], BadgedChildFile.Id)], Badges: ["company"]),
                    new TreeNode("company-file-node", "cf", [], CompanyNodeFile.Id),
                ]),
            ]),
            new Section("agents.company-core", "agents", "Company core", [new KvBlock("core", "Core", [Kv("core-row", CoreOnly.Id)])]),
            new Section("engram.memory", "memory", "Memory", [new KvBlock("mem", "Mem", [Kv("memory-row", Memory.Id, value: SecretWord)])]),
            new Section("sessions.transcripts", "sessions", "Transcripts", [new KvBlock("tr", "Tr", [Kv("transcript-row", value: SecretWord)])]),
            new Section("usage.metrics", "usage", "Usage", [new KvBlock("us", "Us", [Kv("usage-row")])]),
        };
        var paths = new[]
        {
            new ContextPath(PathKeys.CompanyLayer, PathUtil.ToJson(CompanyPrefix), "test", true),
            new ContextPath(PathKeys.ClaudeConfigDir, PathUtil.ToJson(Tree.Dir("config")), "test", true),
        };
        return Snap(sections, [.. Registry.All.Select(f => f.Ref), new FileRef("dangling", "/home/user/gone.md", "other", 0, 0, false)], paths);
    }
}
