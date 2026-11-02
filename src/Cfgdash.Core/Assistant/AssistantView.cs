using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Assistant;

/// <summary>
/// The snapshot and files the config assistant may see (docs/contracts/assistant-v1.md): without <c>engram.memory</c>,
/// <c>sessions.transcripts</c> and <c>usage.metrics</c>, and, unless the dir is under the company layer, without company items.
/// </summary>
public sealed class AssistantView
{
    private static readonly HashSet<string> Excluded = new(["engram.memory", "sessions.transcripts", "usage.metrics"], StringComparer.Ordinal);
    private const string CompanyCore = "agents.company-core";
    private const string CompanyFlag = "company";

    private AssistantView(HarnessSnapshot snapshot, IFileCatalog files)
    {
        Snapshot = snapshot;
        Files = files;
    }

    /// <summary>The filtered snapshot; its <see cref="HarnessSnapshot.Files"/> holds only <see cref="Files"/>.</summary>
    public HarnessSnapshot Snapshot { get; }

    /// <summary>Files referenced by the kept sections and registered in the source catalog.</summary>
    public IFileCatalog Files { get; }

    public static AssistantView Create(SnapshotResult result, MachineEnvironment env, string dir)
    {
        var source = result.Snapshot;
        var inCompany = env.CompanyPrefixes.Any(p => PathUtil.IsUnder(dir, p));
        var companyFiles = inCompany
            ? new HashSet<string>(StringComparer.Ordinal)
            : source.Files.Where(f => f.Company).Select(f => f.Id)
                .Concat(result.Files.All.Where(f => f.Ref.Company).Select(f => f.Ref.Id))
                .ToHashSet(StringComparer.Ordinal);
        var unreadable = new HashSet<string>(companyFiles, StringComparer.Ordinal);
        var markers = inCompany ? [] : CompanyMarkers(env);

        bool Dropped(string collectorId) => Excluded.Contains(collectorId) || (!inCompany && collectorId == CompanyCore);
        bool CompanyFile(string? fileId) => fileId is not null && companyFiles.Contains(fileId);
        bool Flagged(IReadOnlyList<string>? flags) => !inCompany && flags?.Contains(CompanyFlag, StringComparer.Ordinal) == true;
        bool Mentions(JsonNode? node) => Strings(node).Any(text => markers.Any(m => text.Contains(m, PathUtil.Comparison)));
        IEnumerable<TreeNode> FlaggedNodes(IEnumerable<TreeNode> nodes) =>
            nodes.SelectMany(n => Flagged(n.Badges) ? [n] : FlaggedNodes(n.Children));

        foreach (var block in source.Sections.SelectMany(s => s.Blocks))
        {
            Block? flagged = block switch
            {
                KvBlock kv => kv with { Rows = [.. kv.Rows.Where(r => Flagged(r.Flags))] },
                TableBlock table => table with { Rows = [.. table.Rows.Where(r => Flagged(r.Flags))] },
                TreeBlock tree => tree with { Nodes = [.. FlaggedNodes(tree.Nodes)] },
                _ => null,
            };
            if (flagged is not null)
            {
                unreadable.UnionWith(FileIds(flagged));
            }
        }

        TreeNode? Node(TreeNode node) =>
            Flagged(node.Badges) || CompanyFile(node.FileId) || Mentions(node.Label) || (node.Meta?.Values ?? []).Any(Mentions)
                ? null
                : node with { Children = [.. node.Children.Select(Node).OfType<TreeNode>()] };

        Block Filter(Block block) => block switch
        {
            KvBlock kv => kv with
            {
                Rows =
                [
                    .. kv.Rows
                        .Where(r => !Flagged(r.Flags) && !CompanyFile(r.Source?.FileId) && !(r.Contributions ?? []).Any(c => CompanyFile(c.Source.FileId))
                            && !Mentions(r.Key) && !Mentions(r.Value))
                        .Select(r => r.Contributions is null ? r : r with { Contributions = [.. r.Contributions.Where(c => !Mentions(c.Value))] }),
                ],
            },
            TableBlock table => table with
            {
                Rows = [.. table.Rows.Where(r => !Flagged(r.Flags) && !CompanyFile(r.FileId) && !r.Cells.Values.Any(c => CompanyFile(CellFileId(c)) || Mentions(c)))],
            },
            TreeBlock tree => tree with { Nodes = [.. tree.Nodes.Select(Node).OfType<TreeNode>()] },
            _ => block,
        };

        var sections = source.Sections.Where(s => !Dropped(s.CollectorId)).Select(s => s with { Blocks = [.. s.Blocks.Select(Filter)] }).ToList();
        var referenced = sections.SelectMany(s => s.Blocks).SelectMany(FileIds).ToHashSet(StringComparer.Ordinal);
        var files = new Dictionary<string, RegisteredFile>(StringComparer.Ordinal);
        foreach (var id in referenced)
        {
            if (!unreadable.Contains(id) && result.Files.TryGet(id, out var file) && (inCompany || !file.Ref.Company))
            {
                files[id] = file;
            }
        }

        var snapshot = source with
        {
            Context = inCompany ? source.Context : source.Context with { Paths = [.. source.Context.Paths.Where(p => p.Key != PathKeys.CompanyLayer)] },
            Collectors = [.. source.Collectors.Where(c => !Dropped(c.Id))],
            Sections = sections,
            Files = [.. source.Files.Where(f => files.ContainsKey(f.Id))],
        };
        return new AssistantView(snapshot, new Catalog(files));
    }

    /// <summary>
    /// The texts that mark a company-layer path: each prefix with either separator and, below the home dir, also its
    /// <c>~</c>, <c>$HOME</c>, <c>${HOME}</c> and <c>%USERPROFILE%</c> forms.
    /// </summary>
    private static string[] CompanyMarkers(MachineEnvironment env)
    {
        var home = PathUtil.Full(env.HomeDir).TrimEnd('/', '\\');
        var markers = new List<string>();
        foreach (var prefix in env.CompanyPrefixes)
        {
            var full = PathUtil.Full(prefix).TrimEnd('/', '\\');
            List<string> forms = [full];
            if (home.Length > 0 && full.Length > home.Length && PathUtil.IsUnder(full, home))
            {
                var rest = full[home.Length..];
                forms.AddRange(["~" + rest, "$HOME" + rest, "${HOME}" + rest, "%USERPROFILE%" + rest]);
            }

            markers.AddRange(forms.SelectMany(f => new[] { f.Replace('\\', '/'), f.Replace('/', '\\') }));
        }

        return [.. markers.Where(m => m.Length > 0).Distinct(StringComparer.Ordinal)];
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => [value.GetValue<string>()],
        JsonObject obj => obj.SelectMany(p => Strings(p.Value)),
        JsonArray array => array.SelectMany(Strings),
        _ => [],
    };

    private static string? CellFileId(JsonNode? cell) =>
        cell is JsonObject obj && obj["fileId"] is JsonValue id && id.TryGetValue<string>(out var fileId) ? fileId : null;

    internal static IEnumerable<string> FileIds(Block block)
    {
        IEnumerable<string?> ids = block switch
        {
            KvBlock kv => kv.Rows.SelectMany(r => (r.Contributions ?? []).Select(c => c.Source.FileId).Prepend(r.Source?.FileId)),
            TableBlock table => table.Rows.SelectMany(r => r.Cells.Values.Select(CellFileId).Prepend(r.FileId)),
            TreeBlock tree => tree.Nodes.SelectMany(NodeFileIds),
            _ => [],
        };
        return ids.OfType<string>();
    }

    private static IEnumerable<string?> NodeFileIds(TreeNode node) => node.Children.SelectMany(NodeFileIds).Prepend(node.FileId);

    private sealed class Catalog(Dictionary<string, RegisteredFile> files) : IFileCatalog
    {
        public IReadOnlyList<RegisteredFile> All { get; } = [.. files.Values.OrderBy(f => f.Ref.Path, StringComparer.Ordinal)];

        public bool TryGet(string id, [MaybeNullWhen(false)] out RegisteredFile file) => files.TryGetValue(id, out file);
    }
}
