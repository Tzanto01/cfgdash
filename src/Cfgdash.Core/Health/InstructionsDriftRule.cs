using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Health;

/// <summary>health-v1 <c>instructions-drift</c>: a project instruction file one present tool loads for the target dir and another does not.</summary>
internal static class InstructionsDriftRule
{
    private static readonly string[] Names = ["CLAUDE.md", "AGENTS.md", "AGENTS.override.md"];

    private static readonly (string CollectorId, string Tool)[] Tools =
        [("instructions.chain", "Claude Code"), ("codex.config", "Codex"), ("opencode.config", "OpenCode")];

    public static IEnumerable<Finding> Evaluate(HarnessSnapshot snapshot, IRedactor redactor)
    {
        var present = Tools.Where(t => McpDriftRule.IsPresent(snapshot, t.CollectorId)).ToList();
        if (present.Count < 2)
        {
            yield break;
        }

        var comparison = snapshot.Machine.Os.Family == OsFamily.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var target = Normalize(snapshot.Context.TargetDir);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in snapshot.Files)
        {
            var path = Normalize(file.Path);
            var slash = path.LastIndexOf('/');
            var parent = slash < 0 ? string.Empty : path[..slash];
            if (slash >= 0 && Names.Contains(path[(slash + 1)..], StringComparer.Ordinal)
                && (string.Equals(target, parent, comparison) || target.StartsWith(parent + "/", comparison)))
            {
                paths.TryAdd(file.Id, file.Path);
            }
        }

        var loaded = new List<(string CollectorId, string Tool, Dictionary<string, (string BlockId, string ItemId, string View)> Files)>();
        foreach (var (collectorId, tool) in present)
        {
            var files = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal);
            void Add(string? fileId, string blockId, string itemId, string view)
            {
                if (fileId is not null && paths.ContainsKey(fileId))
                {
                    files.TryAdd(fileId, (blockId, itemId, view));
                }
            }

            foreach (var section in snapshot.Sections.Where(s => s.CollectorId == collectorId))
            {
                foreach (var block in section.Blocks)
                {
                    if (collectorId == "instructions.chain" && block is TreeBlock tree)
                    {
                        void Walk(IReadOnlyList<TreeNode> nodes)
                        {
                            foreach (var node in nodes)
                            {
                                if (node.Badges?.Contains("loaded") == true)
                                {
                                    Add(node.FileId, tree.Id, node.Id, section.View);
                                }

                                Walk(node.Children);
                            }
                        }

                        Walk(tree.Nodes);
                    }
                    else if (collectorId != "instructions.chain" && block is TableBlock { Id: "instructions" } table)
                    {
                        foreach (var row in table.Rows.Where(r => collectorId != "opencode.config" || McpDriftRule.CellText(r, "status") == "loaded"))
                        {
                            Add(row.FileId, table.Id, row.Id, section.View);
                        }
                    }
                }
            }

            loaded.Add((collectorId, tool, files));
        }

        foreach (var fileId in loaded.SelectMany(l => l.Files.Keys).Distinct(StringComparer.Ordinal))
        {
            var loaders = loaded.Where(l => l.Files.ContainsKey(fileId)).ToList();
            var missing = loaded.Where(l => !l.Files.ContainsKey(fileId)).ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            var first = loaders[0];
            var (blockId, itemId, view) = first.Files[fileId];
            var detail = $"{paths[fileId]} is loaded by {string.Join(", ", loaders.Select(l => l.Tool))} but not by {string.Join(", ", missing.Select(l => l.Tool))}.";
            yield return new Finding(
                $"instructions-drift:{first.CollectorId}:{blockId}:{itemId}", "instructions-drift", FindingSeverity.Info, "Instruction file not shared",
                redactor.RedactText(detail), new FindingEvidence(first.CollectorId, blockId, itemId, FileId: fileId), view);
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}
