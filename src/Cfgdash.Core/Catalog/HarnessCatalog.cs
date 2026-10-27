using System.Text.Json.Nodes;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;

namespace Cfgdash.Core.Catalog;

/// <summary>
/// One agent, skill, command or output style. <see cref="Kind"/>: <c>agent</c>, <c>skill</c>, <c>command</c>, <c>outputStyle</c>.
/// <see cref="Scope"/>: <c>managed</c>, <c>project</c>, <c>user</c>, <c>plugin</c>, <c>built-in</c>. <see cref="Path"/> is the <c>.md</c> or <c>SKILL.md</c> file (null for built-ins).
/// <see cref="Fields"/> is the unredacted frontmatter. <see cref="LinkTarget"/> is the JSON real path of the entry (skill dir or file) when a junction or symlink is on the way.
/// </summary>
internal sealed record CatalogEntry(string Kind, string Name, string Scope, string? Path, JsonObject? Fields, string? Error, string? LinkTarget, PluginRoot? Plugin, bool Overridden);

/// <summary>Agents, skills, commands and output styles in priority order (docs/contracts/load-and-merge-v1.md). Same name: the first entry wins, the rest are <c>Overridden</c>.</summary>
internal static class HarnessCatalog
{
    public static readonly IReadOnlyList<string> BuiltInOutputStyles = ["Default", "Proactive", "Concise", "Explanatory", "Learning"];

    private const int MaxDepth = 6;
    private const int MaxFiles = 2000;

    private static readonly EnumerationOptions Options = new() { IgnoreInaccessible = true, AttributesToSkip = 0, MatchType = MatchType.Simple };

    private sealed record Root(string Scope, string Dir, PluginRoot? Plugin);

    public static IReadOnlyList<CatalogEntry> Agents(CollectContext context, SettingsStack stack, PlatformLocations locations)
    {
        var roots = Roots(context, stack, locations, "agents", managedFirst: true, userBeforeProject: false);
        var entries = new List<CatalogEntry>();
        foreach (var root in roots)
        {
            foreach (var file in MarkdownFiles(root.Dir, recursive: true))
            {
                var entry = Load(context, "agent", root, file, file, System.IO.Path.GetFileNameWithoutExtension(file), useNameField: true);
                if (entry is not null && entry.Error is null && (Text(entry.Fields?["name"]) is null || Text(entry.Fields?["description"]) is null))
                {
                    entry = entry with { Error = "frontmatter must set name and description" };
                }

                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }

        return Resolve(entries);
    }

    public static IReadOnlyList<CatalogEntry> Skills(CollectContext context, SettingsStack stack, PlatformLocations locations)
    {
        var entries = new List<CatalogEntry>();
        foreach (var root in Roots(context, stack, locations, "skills", managedFirst: true, userBeforeProject: true))
        {
            foreach (var dir in TopDirectories(root.Dir))
            {
                var file = System.IO.Path.Combine(dir, "SKILL.md");
                if (File.Exists(file) && Load(context, "skill", root, file, dir, System.IO.Path.GetFileName(dir), useNameField: true) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return Resolve(entries);
    }

    public static IReadOnlyList<CatalogEntry> Commands(CollectContext context, SettingsStack stack, PlatformLocations locations)
    {
        var entries = new List<CatalogEntry>();
        foreach (var root in Roots(context, stack, locations, "commands", managedFirst: false, userBeforeProject: true))
        {
            foreach (var file in MarkdownFiles(root.Dir, recursive: true))
            {
                var relative = System.IO.Path.GetRelativePath(root.Dir, file);
                var name = relative[..^System.IO.Path.GetExtension(relative).Length].Replace('\\', ':').Replace('/', ':');
                if (Load(context, "command", root, file, file, name, useNameField: false) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return Resolve(entries);
    }

    public static IReadOnlyList<CatalogEntry> OutputStyles(CollectContext context, SettingsStack stack, PlatformLocations locations)
    {
        var entries = BuiltInOutputStyles.Select(name => new CatalogEntry("outputStyle", name, "built-in", null, null, null, null, null, false)).ToList();
        foreach (var root in Roots(context, stack, locations, "output-styles", managedFirst: true, userBeforeProject: true))
        {
            foreach (var file in MarkdownFiles(root.Dir, recursive: false))
            {
                if (Load(context, "outputStyle", root, file, file, System.IO.Path.GetFileNameWithoutExtension(file), useNameField: true) is { } entry)
                {
                    entries.Add(entry);
                }
            }
        }

        return Resolve(entries);
    }

    private static List<Root> Roots(CollectContext context, SettingsStack stack, PlatformLocations locations, string kindDir, bool managedFirst, bool userBeforeProject)
    {
        var roots = new List<Root>();
        if (managedFirst && locations.ManagedDir is { } managedDir)
        {
            roots.Add(new Root("managed", System.IO.Path.Combine(managedDir, ".claude", kindDir), null));
        }

        var project = PathUtil.AncestorsFromRoot(stack.TargetDir)
            .Where(dir => PathUtil.IsUnder(dir, stack.ProjectRoot))
            .Reverse()
            .Select(dir => new Root("project", System.IO.Path.Combine(dir, ".claude", kindDir), null))
            .ToList();
        var configDir = context.Environment.GetPath(PathKeys.ClaudeConfigDir);
        var user = configDir is null ? null : new Root("user", System.IO.Path.Combine(configDir, kindDir), null);
        if (!userBeforeProject)
        {
            roots.AddRange(project);
        }

        if (user is not null)
        {
            roots.Add(user);
        }

        if (userBeforeProject)
        {
            roots.AddRange(project);
        }

        roots.AddRange(PluginLocator.Locate(stack, context.Environment).Select(p => new Root("plugin", System.IO.Path.Combine(p.Dir, kindDir), p)));
        var seen = new HashSet<string>(PathUtil.Comparer);
        return [.. roots.Where(r => seen.Add(PathUtil.RealPath(r.Dir)))];
    }

    private static CatalogEntry? Load(CollectContext context, string kind, Root root, string file, string entryPath, string name, bool useNameField)
    {
        if (FileRegistry.IsNeverRead(file))
        {
            return null;
        }

        context.Files.Register(file, kind);
        var text = HarnessFiles.ReadText(file);
        var frontmatter = text is null ? new FrontmatterResult(null, string.Empty, "unreadable") : Frontmatter.Parse(text);
        if (useNameField && Text(frontmatter.Fields?["name"]) is { Length: > 0 } declared)
        {
            name = declared;
        }

        if (root.Plugin is not null && kind is "skill" or "command")
        {
            name = root.Plugin.Name + ":" + name;
        }

        var real = PathUtil.RealPath(entryPath);
        var link = PathUtil.PathEquals(real, entryPath) ? null : PathUtil.ToJson(real);
        return new CatalogEntry(kind, name, root.Scope, PathUtil.Full(file), frontmatter.Fields, frontmatter.Error, link, root.Plugin, false);
    }

    private static List<CatalogEntry> Resolve(List<CatalogEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. entries.Select(e => seen.Add(e.Name) ? e : e with { Overridden = true })];
    }

    private static List<string> MarkdownFiles(string root, bool recursive)
    {
        var result = new List<string>();

        void Visit(string dir, int depth)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.md", Options))
                {
                    if (result.Count >= MaxFiles)
                    {
                        return;
                    }

                    if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(file);
                    }
                }

                if (!recursive || depth >= MaxDepth)
                {
                    return;
                }

                foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories("*", Options).OrderBy(d => d.Name, StringComparer.Ordinal))
                {
                    if (!sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        Visit(sub.FullName, depth + 1);
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        if (Directory.Exists(root))
        {
            Visit(root, 1);
        }

        return [.. result.Order(StringComparer.Ordinal)];
    }

    private static List<string> TopDirectories(string root)
    {
        try
        {
            return Directory.Exists(root)
                ? [.. Directory.EnumerateDirectories(root, "*", Options).Take(MaxFiles).Order(StringComparer.Ordinal)]
                : [];
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
