using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cfgdash.Core.Catalog;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary>
/// <c>instructions.chain</c>: instruction files for the target dir in load order (docs/contracts/load-and-merge-v1.md),
/// @imports as children, referenced Markdown files, lazy subdirectory files, main-thread agent and output style.
/// </summary>
internal sealed partial class InstructionChainCollector(SettingsLayers settings, PlatformLocations locations) : ICollector
{
    private const int MaxHops = 4;
    private const int LazyDepth = 4;
    private const int MaxLazyDirs = 2000;
    private const int MaxLazyFiles = 100;

    private static readonly HashSet<string> SkippedDirs = new(["node_modules", "bin", "obj", "dist", "build", "out", "target", "vendor", "packages"], StringComparer.OrdinalIgnoreCase);
    private static readonly EnumerationOptions RuleOptions = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8, MatchType = MatchType.Simple };
    private static readonly EnumerationOptions DirOptions = new() { IgnoreInaccessible = true, MatchType = MatchType.Simple };

    public CollectorManifest Manifest { get; } = new("instructions.chain", "Instruction chain", "1.0.0", "instructions", new Requirements(TargetDir: true));

    /// <summary>The existence probe for imported and referenced files; it only ever sees paths that passed the never-read rules.</summary>
    internal Func<string, bool> ReferenceExists { get; init; } = File.Exists;

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var stack = await settings.LoadAsync(context, cancellationToken);
        var chain = new Chain(context, stack, ReferenceExists);
        var target = stack.TargetDir;

        if (locations.ManagedDir is { } managedDir)
        {
            chain.AddRoot(Path.Combine(managedDir, "CLAUDE.md"), ["managed"], project: false);
        }

        if (context.Environment.GetPath(PathKeys.ClaudeConfigDir) is { } configDir)
        {
            chain.AddRoot(Path.Combine(configDir, "CLAUDE.md"), ["user"], project: false);
            chain.AddRules(Path.Combine(configDir, "rules"), "user", project: false);
        }

        var anyClaude = false;
        foreach (var dir in PathUtil.AncestorsFromRoot(target))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var main = Path.Combine(dir, "CLAUDE.md");
            var nested = Path.Combine(dir, ".claude", "CLAUDE.md");
            if (File.Exists(main) && File.Exists(nested))
            {
                chain.Notes.Add($"Both CLAUDE.md and .claude/CLAUDE.md exist in {PathUtil.ToJson(dir)}; loading both is not documented");
            }

            foreach (var (path, badge) in new[] { (main, "project"), (nested, "project"), (Path.Combine(dir, "CLAUDE.local.md"), "local") })
            {
                anyClaude |= chain.AddRoot(path, [badge], project: true);
            }
        }

        chain.AddRules(Path.Combine(target, ".claude", "rules"), "project", project: true);

        if (!anyClaude && chain.AddRoot(Path.Combine(target, "AGENTS.md"), ["project", "agents-md"], project: true))
        {
            chain.Notes.Add("AGENTS.md is read natively by Claude Code 2.1.277+ only");
        }

        chain.AddAgent(locations);
        chain.AddOutputStyle(locations);
        chain.AddLazy(cancellationToken);

        return new CollectorResult([chain.Block()], Notes: chain.Notes.Count > 0 ? chain.Notes : null);
    }

    private sealed class Node(string label)
    {
        public string Label { get; } = label;

        public FileRef? File { get; set; }

        public List<string> Badges { get; } = [];

        public string? Error { get; set; }

        public string? Paths { get; set; }

        public bool Counted { get; set; }

        public bool Lazy { get; set; }

        public List<Node> Children { get; } = [];
    }

    private sealed record Import(string Raw, string Path);

    private sealed class Chain(CollectContext context, SettingsStack stack, Func<string, bool> referenceExists)
    {
        private readonly List<Node> top = [];
        private readonly HashSet<string> shown = new(PathUtil.Comparer);

        public List<string> Notes { get; } = [];

        public bool AddRoot(string path, IReadOnlyList<string> badges, bool project)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var node = FileNode(PathUtil.ToJson(path), path, "instruction");
            node.Badges.AddRange(badges);
            top.Add(node);
            var real = PathUtil.RealPath(path);
            if (node.File is null || !shown.Add(real))
            {
                node.Badges.Add("duplicate");
                return true;
            }

            node.Badges.Add("loaded");
            node.Counted = true;
            Expand(node, path, [real], 0, project);
            return true;
        }

        public void AddRules(string dir, string scope, bool project)
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            List<string> files;
            try
            {
                files = [.. Directory.EnumerateFiles(dir, "*.md", RuleOptions).Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }

            foreach (var file in files.Where(f => !FileRegistry.IsNeverRead(f)))
            {
                var paths = Frontmatter.Parse(HarnessFiles.ReadText(file) ?? string.Empty).Fields?["paths"];
                if (paths is null)
                {
                    AddRoot(file, [scope, "rule"], project);
                    continue;
                }

                var node = FileNode(PathUtil.ToJson(file), file, "instruction");
                node.Badges.AddRange([scope, "rule", "conditional"]);
                node.Paths = CatalogText.Value(paths);
                top.Add(node);
            }
        }

        public void AddAgent(PlatformLocations locations)
        {
            if (Text(stack.Get("agent")?.Value) is not { } name)
            {
                return;
            }

            var entry = HarnessCatalog.Agents(context, stack, locations).FirstOrDefault(e => !e.Overridden && e.Name == name);
            var node = entry?.Path is { } path ? FileNode("Agent: " + name, path, "agent") : new Node("Agent: " + name);
            node.Badges.Add("agent");
            if (node.File is null)
            {
                node.Badges.Add("error");
                node.Error = "agent not found";
            }
            else
            {
                node.Badges.Add("loaded");
                node.Counted = true;
            }

            top.Add(node);
        }

        public void AddOutputStyle(PlatformLocations locations)
        {
            if (Text(stack.Get("outputStyle")?.Value) is not { } name)
            {
                return;
            }

            var entry = HarnessCatalog.OutputStyles(context, stack, locations).FirstOrDefault(e => !e.Overridden && e.Name == name);
            var label = "Output style: " + name;
            Node node;
            if (entry is null)
            {
                node = new Node(label) { Error = "output style not found" };
                node.Badges.AddRange(["output-style", "error"]);
            }
            else if (entry.Path is null)
            {
                node = new Node(label);
                node.Badges.AddRange(["built-in", "loaded"]);
            }
            else
            {
                node = FileNode(label, entry.Path, "outputStyle");
                node.Badges.AddRange(["output-style", "loaded"]);
                node.Counted = node.File is not null;
            }

            top.Add(node);
        }

        public void AddLazy(CancellationToken cancellationToken)
        {
            var visited = 0;
            var found = 0;
            var stopped = false;

            void Visit(string dir, int depth)
            {
                List<DirectoryInfo> subs;
                try
                {
                    subs = [.. new DirectoryInfo(dir).EnumerateDirectories("*", DirOptions).OrderBy(d => d.Name, StringComparer.Ordinal)];
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return;
                }

                foreach (var sub in subs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (stopped || sub.Name.StartsWith('.') || SkippedDirs.Contains(sub.Name) || sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (++visited > MaxLazyDirs)
                    {
                        Notes.Add($"Lazy scan stopped after {MaxLazyDirs} directories");
                        stopped = true;
                        return;
                    }

                    foreach (var file in new[] { Path.Combine(sub.FullName, "CLAUDE.md"), Path.Combine(sub.FullName, ".claude", "CLAUDE.md") })
                    {
                        if (!File.Exists(file))
                        {
                            continue;
                        }

                        if (++found > MaxLazyFiles)
                        {
                            Notes.Add($"Lazy scan stopped after {MaxLazyFiles} files");
                            stopped = true;
                            return;
                        }

                        var node = FileNode(PathUtil.ToJson(file), file, "instruction");
                        node.Badges.Add("lazy");
                        node.Lazy = true;
                        top.Add(node);
                    }

                    if (depth < LazyDepth)
                    {
                        Visit(sub.FullName, depth + 1);
                    }
                }
            }

            Visit(stack.TargetDir, 1);
        }

        public TreeBlock Block()
        {
            var counted = Flatten(top).Where(n => n.Counted && n.File is not null).Select(n => n.File!).ToList();
            var order = 0;
            var nodes = top.Select((n, i) => ToTree(n, "n" + (i + 1), n.Lazy ? null : ++order)).ToList();
            return new TreeBlock(
                "chain",
                context.Redactor.RedactText("Load order for " + PathUtil.ToJson(stack.TargetDir)),
                nodes,
                new TreeSummary(counted.Count, counted.Sum(f => f.SizeBytes), counted.Sum(f => f.Tokens)),
                "Token counts are estimates (characters / 4).");
        }

        private void Expand(Node node, string path, IReadOnlyList<string> chain, int hop, bool project)
        {
            if (HarnessFiles.ReadText(path) is not { } text)
            {
                return;
            }

            var dir = Path.GetDirectoryName(path)!;
            var (imports, references) = Scan(text);
            var importTargets = new HashSet<string>(PathUtil.Comparer);
            foreach (var import in imports)
            {
                var target = Resolve(import.Path, dir, context.Environment.HomeDir);
                var network = target is not null && PathUtil.IsNetworkPath(target);
                var neverRead = target is not null && !network && FileRegistry.IsNeverRead(target);
                Node child;
                if (target is null || network || neverRead || !referenceExists(target))
                {
                    child = new Node("@" + import.Raw) { Error = network ? "network path not read" : neverRead ? "not readable" : "missing" };
                    child.Badges.AddRange(["import", "error"]);
                }
                else
                {
                    importTargets.Add(target);
                    child = FileNode("@" + import.Raw, target, "instruction");
                    child.Badges.Add("import");
                    var real = PathUtil.RealPath(target);
                    if (child.File is null)
                    {
                        child.Error = "not readable";
                        child.Badges.Add("error");
                    }
                    else if (chain.Contains(real, PathUtil.Comparer))
                    {
                        child.Error = "cycle";
                        child.Badges.Add("error");
                    }
                    else if (hop + 1 > MaxHops)
                    {
                        child.Error = "depth";
                        child.Badges.Add("error");
                    }
                    else if (!shown.Add(real))
                    {
                        child.Badges.Add("duplicate");
                    }
                    else
                    {
                        child.Badges.Add("loaded");
                        child.Counted = true;
                        Expand(child, target, [.. chain, real], hop + 1, project);
                    }

                    if (project && !PathUtil.IsUnder(target, stack.ProjectRoot))
                    {
                        child.Badges.Add("external");
                    }
                }

                node.Children.Add(child);
            }

            var seen = new HashSet<string>(PathUtil.Comparer);
            foreach (var raw in references)
            {
                var hash = raw.IndexOf('#');
                var reference = hash < 0 ? raw : raw[..hash];
                var target = Resolve(reference, dir, null);
                if (target is null || !target.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || PathUtil.IsNetworkPath(target) || FileRegistry.IsNeverRead(target) || !referenceExists(target) || importTargets.Contains(target) || PathUtil.RealPath(target) is var real && (chain.Contains(real, PathUtil.Comparer) || !seen.Add(real)))
                {
                    continue;
                }

                var child = FileNode(reference, target, "instruction");
                child.Badges.Add("referenced");
                node.Children.Add(child);
            }
        }

        private Node FileNode(string label, string path, string kind) => new(label) { File = context.Files.Register(path, kind) };

        private TreeNode ToTree(Node node, string id, int? order)
        {
            var redactor = context.Redactor;
            var meta = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            if (order is { } value)
            {
                meta["order"] = value;
            }

            if (node.File is { } file)
            {
                meta["sizeBytes"] = file.SizeBytes;
                meta["tokens"] = file.Tokens;
            }

            if (node.Error is not null)
            {
                meta["error"] = node.Error;
            }

            if (node.Paths is not null)
            {
                meta["paths"] = redactor.RedactText(node.Paths);
            }

            return new TreeNode(
                id,
                redactor.RedactText(node.Label),
                [.. node.Children.Select((c, i) => ToTree(c, $"{id}.{i + 1}", null))],
                node.File?.Id,
                node.Badges,
                meta.Count > 0 ? meta : null);
        }

        private static IEnumerable<Node> Flatten(IEnumerable<Node> nodes) => nodes.SelectMany(n => Flatten(n.Children).Prepend(n));

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    }

    private static string? Resolve(string path, string dir, string? homeDir)
    {
        try
        {
            if (homeDir is not null && path.StartsWith("~/", StringComparison.Ordinal))
            {
                return PathUtil.Full(PathUtil.ExpandHome(path, homeDir));
            }

            return PathUtil.Full(Path.Combine(dir, path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static (List<Import> Imports, List<string> References) Scan(string text)
    {
        var imports = new List<Import>();
        var references = new List<string>();
        var fence = '\0';
        var fenceLength = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (fence != '\0')
            {
                var run = trimmed.TakeWhile(c => c == fence).Count();
                if (run >= fenceLength && trimmed[run..].Trim().Length == 0)
                {
                    fence = '\0';
                }

                continue;
            }

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                fence = trimmed[0];
                fenceLength = trimmed.TakeWhile(c => c == fence).Count();
                continue;
            }

            var code = CodeSpans(line);
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] != '@' || code[i] || (i > 0 && !char.IsWhiteSpace(line[i - 1])))
                {
                    continue;
                }

                var raw = new StringBuilder();
                var path = new StringBuilder();
                var j = i + 1;
                while (j < line.Length)
                {
                    if (line[j] == '\\' && j + 1 < line.Length && line[j + 1] == ' ')
                    {
                        raw.Append("\\ ");
                        path.Append(' ');
                        j += 2;
                        continue;
                    }

                    if (char.IsWhiteSpace(line[j]))
                    {
                        break;
                    }

                    raw.Append(line[j]);
                    path.Append(line[j]);
                    j++;
                }

                if (path.Length > 0 && path[0] is not ('"' or '\''))
                {
                    imports.Add(new Import(raw.ToString(), path.ToString()));
                }

                i = j;
            }

            foreach (Match match in MarkdownLink().Matches(line))
            {
                var target = match.Groups[1].Value;
                if (!code[match.Index] && !target.StartsWith('#') && !target.Contains("://", StringComparison.Ordinal) && !target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                {
                    references.Add(target);
                }
            }

            foreach (Match match in PlainMarkdownPath().Matches(line))
            {
                if (!match.Value.Contains("://", StringComparison.Ordinal))
                {
                    references.Add(match.Value);
                }
            }
        }

        return (imports, references);
    }

    private static bool[] CodeSpans(string line)
    {
        var mask = new bool[line.Length];
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] != '`')
            {
                i++;
                continue;
            }

            var start = i;
            var length = Run(line, i);
            i += length;
            var close = -1;
            for (var k = i; k < line.Length;)
            {
                if (line[k] != '`')
                {
                    k++;
                    continue;
                }

                var run = Run(line, k);
                if (run == length)
                {
                    close = k;
                    break;
                }

                k += run;
            }

            if (close < 0)
            {
                continue;
            }

            Array.Fill(mask, true, start, close + length - start);
            i = close + length;
        }

        return mask;
    }

    private static int Run(string line, int index)
    {
        var end = index;
        while (end < line.Length && line[end] == '`')
        {
            end++;
        }

        return end - index;
    }

    [GeneratedRegex("""\[[^\]]*\]\(\s*<?([^)\s>]+)>?(?:\s+"[^"]*")?\s*\)""")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex("""(?<![^\s`'"(\[<*,;|])[^\s`'"()\[\]<>*,;|@#][^\s`'"()\[\]<>*,;|#]*\.md(?![^\s`'"()\[\]<>*,;|.!?:])""", RegexOptions.IgnoreCase)]
    private static partial Regex PlainMarkdownPath();
}
