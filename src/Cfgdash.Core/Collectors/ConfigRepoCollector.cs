using System.Text.Json.Nodes;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary>
/// <c>overview.config-repo</c>: branch, upstream, ahead/behind, dirty files and last commit of the config repo (read-only git).
/// Git runs only against the config repo's own <c>.git</c> (no repository discovery) with fsmonitor, hooks and optional locks off; never in the target dir.
/// </summary>
internal sealed class ConfigRepoCollector : ICollector
{
    public const int MaxRows = 500;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);

    public CollectorManifest Manifest { get; } = new(
        "overview.config-repo", "Config repo", "1.0.0", "overview",
        new Requirements(Tools: ["git"], Paths: [PathKeys.ConfigRepo]));

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var git = context.Environment.Tools["git"];
        var repo = PathUtil.Full(context.Environment.GetPath(PathKeys.ConfigRepo)!);
        var redactor = context.Redactor;
        string[] pinned = ["--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=", "--git-dir=" + Path.Combine(repo, ".git"), "--work-tree=" + repo];

        var status = await context.Processes.RunAsync(git, [.. pinned, "status", "--porcelain=v2", "--branch", "-z"], GitTimeout, cancellationToken);
        if (status.TimedOut || status.ExitCode != 0)
        {
            var firstLine = status.TimedOut ? "timed out" : status.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"exit code {status.ExitCode}";
            return CollectorResult.Unavailable(redactor.RedactText("git status failed: " + firstLine));
        }

        var parsed = ParseStatus(status.StandardOutput);
        var log = await context.Processes.RunAsync(git, [.. pinned, "log", "--no-show-signature", "-1", "--format=%H%x1f%cI%x1f%s"], GitTimeout, cancellationToken);
        var commit = !log.TimedOut && log.ExitCode == 0 ? log.StandardOutput.TrimEnd('\r', '\n').Split('\u001f') : [];

        var source = new SourceRef("git");
        JsonNode? Text(string? value) => value is null ? null : redactor.RedactText(value);
        var rows = new List<KvRow>
        {
            new("branch", Text(parsed.Branch), source),
            new("upstream", Text(parsed.Upstream), source),
            new("ahead", parsed.Ahead, source),
            new("behind", parsed.Behind, source),
            new("dirtyCount", parsed.Dirty.Count, source),
            new("lastCommitHash", Text(commit.Length == 3 ? commit[0] : null), source),
            new("lastCommitDate", Text(commit.Length == 3 ? commit[1] : null), source),
            new("lastCommitSubject", Text(commit.Length == 3 ? commit[2] : null), source),
        };

        var tableRows = parsed.Dirty.Take(MaxRows)
            .Select(d => new TableRow(d.Path, new Dictionary<string, JsonNode?> { ["path"] = Text(d.Path), ["status"] = d.Status }))
            .ToList();
        IReadOnlyList<string>? notes = parsed.Dirty.Count > MaxRows ? [$"showing {MaxRows} of {parsed.Dirty.Count} dirty files"] : null;

        return new CollectorResult(
            [
                new KvBlock("repo", "Repository", rows),
                new TableBlock("dirty", "Uncommitted changes", [new TableColumn("path", "Path"), new TableColumn("status", "Status")], tableRows),
            ],
            Notes: notes);
    }

    internal sealed record DirtyEntry(string Path, string Status);

    internal sealed record GitStatus(string? Branch, string? Upstream, int? Ahead, int? Behind, IReadOnlyList<DirtyEntry> Dirty);

    internal static GitStatus ParseStatus(string output)
    {
        string? branch = null, upstream = null;
        int? ahead = null, behind = null;
        var dirty = new List<DirtyEntry>();
        var entries = output.TrimEnd('\r', '\n').Split('\0');
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            if (entry.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = entry["# branch.head ".Length..];
            }
            else if (entry.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = entry["# branch.upstream ".Length..];
            }
            else if (entry.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = entry["# branch.ab ".Length..].Split(' ');
                if (parts.Length == 2 && int.TryParse(parts[0].TrimStart('+'), out var a) && int.TryParse(parts[1].TrimStart('-'), out var b))
                {
                    ahead = a;
                    behind = b;
                }
            }
            else if (entry.StartsWith("1 ", StringComparison.Ordinal))
            {
                Add(entry, 9);
            }
            else if (entry.StartsWith("2 ", StringComparison.Ordinal))
            {
                Add(entry, 10);
                i++;
            }
            else if (entry.StartsWith("u ", StringComparison.Ordinal))
            {
                Add(entry, 11);
            }
            else if (entry.StartsWith("? ", StringComparison.Ordinal))
            {
                dirty.Add(new DirtyEntry(entry[2..], "untracked"));
            }
        }

        void Add(string entry, int fields)
        {
            var parts = entry.Split(' ', fields);
            if (parts.Length == fields)
            {
                dirty.Add(new DirtyEntry(parts[^1], parts[1]));
            }
        }

        return new GitStatus(branch, upstream, ahead, behind, dirty);
    }
}
