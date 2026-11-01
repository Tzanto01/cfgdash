using System.Globalization;
using System.Text.Json.Nodes;
using Cfgdash.Core.Catalog;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary><c>codex.config</c>: Codex settings, MCP servers, instruction files for the target dir, prompts and skills. Never opens <c>auth.json</c>, <c>history.jsonl</c>, <c>sessions/</c> or <c>log/</c>; <c>${VAR}</c> references are never expanded.</summary>
internal sealed class CodexConfigCollector(IEnvironmentReader environmentReader) : ICollector
{
    private const int MaxRows = 500;
    private const int MaxSkillDepth = 4;
    private const int MaxFrontmatterBytes = 256 * 1024;

    private static readonly IReadOnlyList<TableColumn> McpColumns =
    [
        new("name", "Name"), new("transport", "Transport"), new("target", "Command or URL"), new("env", "Env"),
        new("headers", "Headers"), new("enabled", "Enabled"), new("source", "Source"),
    ];

    private static readonly IReadOnlyList<TableColumn> InstructionColumns = [new("order", "Order"), new("scope", "Scope"), new("file", "File")];

    private static readonly IReadOnlyList<TableColumn> CatalogColumns = [new("name", "Name"), new("description", "Description"), new("file", "File")];

    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase) { "auth.json", "history.jsonl" };

    private static readonly HashSet<string> SkipTopLevel = new(StringComparer.Ordinal) { "mcp_servers" };

    public CollectorManifest Manifest { get; } = new("codex.config", "Codex", "1.0.0", "codex", new Requirements(TargetDir: true));

    public Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var homeDir = context.Environment.HomeDir;
        var configured = environmentReader.Get("CODEX_HOME");
        var home = string.IsNullOrWhiteSpace(configured)
            ? PathUtil.Full(Path.Combine(homeDir, ".codex"))
            : PathUtil.Full(PathUtil.ExpandHome(configured, homeDir));
        if (PathUtil.IsNetworkPath(home) || !Directory.Exists(home))
        {
            return Task.FromResult(CollectorResult.NotPresent("no Codex home (CODEX_HOME or ~/.codex)"));
        }

        return Task.FromResult(Collect(context, home, cancellationToken));
    }

    private static CollectorResult Collect(CollectContext context, string home, CancellationToken cancellationToken)
    {
        var redactor = context.Redactor;
        var notes = new List<string>();
        var blocks = new List<Block>();
        string[] forbiddenRoots = [Path.Combine(home, "sessions"), Path.Combine(home, "log")];

        bool Allowed(string path) =>
            !ForbiddenNames.Contains(Path.GetFileName(path))
            && !ForbiddenNames.Contains(Path.GetFileName(PathUtil.RealPath(path)))
            && !ToolConfig.IsUnderAny(path, forbiddenRoots)
            && !FileRegistry.IsNeverRead(path);

        bool Readable(string path) => Allowed(path) && File.Exists(path);

        string? Redact(string? text) => text is null ? null : redactor.RedactText(text);

        JsonNode? FileCell(string path, string? fileId) => Cell.File(redactor.RedactText(PathUtil.ToJson(path)), fileId);

        List<T> Capped<T>(IEnumerable<T> items, string table)
        {
            var list = items.Take(MaxRows + 1).ToList();
            if (list.Count > MaxRows)
            {
                list.RemoveAt(MaxRows);
                notes.Add(table + ": showing the first 500");
            }

            return list;
        }

        TableRow CatalogRow(string path, string kind, string name, JsonObject? fields)
        {
            var fileId = context.Files.Register(path, kind)?.Id;
            return new TableRow(
                FileRegistry.IdFor(path),
                new Dictionary<string, JsonNode?>
                {
                    ["name"] = redactor.RedactText(name),
                    ["description"] = Redact(CatalogText.Description(fields?["description"])),
                    ["file"] = FileCell(path, fileId),
                },
                FileId: fileId);
        }

        var configPath = Path.Combine(home, "config.toml");
        JsonObject? config = null;
        string? configId = null;
        if (!Readable(configPath))
        {
            notes.Add("no config.toml");
        }
        else
        {
            configId = context.Files.Register(configPath, "settings")?.Id;
            if (ToolConfig.TryReadToml(configPath, out config, out var error))
            {
                blocks.Add(new KvBlock("settings", "config.toml", [.. ToolConfig.Flatten(config, redactor, new SourceRef("config.toml", configId), SkipTopLevel)]));
            }
            else
            {
                notes.Add(redactor.RedactText($"{PathUtil.ToJson(configPath)}: {error}"));
            }
        }

        var servers = config?["mcp_servers"] as JsonObject ?? new JsonObject();
        var mcpRows = servers
            .Where(p => p.Value is JsonObject)
            .Select(p => McpRow(redactor.RedactText(p.Key), (JsonObject)p.Value!, configId, redactor));
        blocks.Add(new TableBlock("mcp", "MCP servers", McpColumns, Capped(mcpRows, "MCP servers")));

        var fallbacks = new List<string>();
        if (config?["project_doc_fallback_filenames"] is JsonArray configuredNames)
        {
            foreach (var item in configuredNames)
            {
                if (ToolConfig.Text(item) is { } name && IsPlainFileName(name))
                {
                    fallbacks.Add(name);
                }
                else
                {
                    notes.Add(redactor.RedactText($"project_doc_fallback_filenames: ignored {item?.ToJsonString() ?? "null"} (not a plain file name)"));
                }
            }
        }

        var instructions = new List<(string Scope, string Path)>();
        if (new[] { "AGENTS.override.md", "AGENTS.md" }.Select(name => Path.Combine(home, name)).FirstOrDefault(Readable) is { } globalFile)
        {
            instructions.Add(("global", globalFile));
        }

        string[] projectNames = ["AGENTS.override.md", "AGENTS.md", .. fallbacks];
        var root = PathUtil.FindGitRoot(context.TargetDir) ?? context.TargetDir;
        foreach (var dir in PathUtil.AncestorsFromRoot(context.TargetDir).Where(d => PathUtil.IsUnder(d, root)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (projectNames.Select(name => Path.Combine(dir, name)).FirstOrDefault(Readable) is { } file)
            {
                instructions.Add(("project", file));
            }
        }

        var instructionRows = Capped(instructions, "Instruction files").Select((entry, index) =>
        {
            var fileId = context.Files.Register(entry.Path, "instruction")?.Id;
            return new TableRow(
                (index + 1).ToString(CultureInfo.InvariantCulture),
                new Dictionary<string, JsonNode?>
                {
                    ["order"] = index + 1,
                    ["scope"] = entry.Scope,
                    ["file"] = FileCell(entry.Path, fileId),
                },
                FileId: fileId);
        }).ToList();
        blocks.Add(new TableBlock("instructions", "Instruction files", InstructionColumns, instructionRows));

        var promptsDir = Path.Combine(home, "prompts");
        var promptFiles = Allowed(promptsDir) && Directory.Exists(promptsDir)
            ? ListFiles(promptsDir, "*.md").Where(Readable).Order(StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        var promptRows = Capped(promptFiles, "Prompts")
            .Select(path => CatalogRow(path, "command", Path.GetFileNameWithoutExtension(path), Fields(path)))
            .ToList();
        blocks.Add(new TableBlock("prompts", "Prompts", CatalogColumns, promptRows));

        var skillsDir = Path.Combine(home, "skills");
        var skillFiles = new List<string>();
        if (Allowed(skillsDir) && Directory.Exists(skillsDir))
        {
            var realSkills = PathUtil.RealPath(skillsDir);
            void Walk(string dir, int depth)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var sub in ListDirs(dir))
                {
                    if (skillFiles.Count > MaxRows)
                    {
                        return;
                    }

                    var skill = Path.Combine(sub, "SKILL.md");
                    if (Readable(skill) && PathUtil.IsUnder(PathUtil.RealPath(skill), realSkills))
                    {
                        skillFiles.Add(skill);
                    }

                    if (depth < MaxSkillDepth)
                    {
                        Walk(sub, depth + 1);
                    }
                }
            }

            Walk(skillsDir, 1);
        }

        var skillRows = Capped(skillFiles.Order(StringComparer.Ordinal), "Skills").Select(path =>
        {
            var fields = Fields(path);
            var name = CatalogText.Value(fields?["name"]) is { Length: > 0 } declared ? declared : Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
            return CatalogRow(path, "skill", name, fields);
        }).ToList();
        blocks.Add(new TableBlock("skills", "Skills", CatalogColumns, skillRows));

        return new CollectorResult(blocks, Notes: notes.Count > 0 ? notes : null);
    }

    private static TableRow McpRow(string name, JsonObject entry, string? configId, IRedactor redactor)
    {
        string? target = null;
        if (ToolConfig.Text(entry["command"]) is { } command)
        {
            var args = (entry["args"] as JsonArray)?.Select(a => ToolConfig.Text(a) ?? a?.ToJsonString() ?? "null") ?? [];
            target = string.Join(' ', [command, .. args]);
        }
        else
        {
            target = ToolConfig.Text(entry["url"]);
        }

        var headers = string.Join("; ", new[] { "http_headers", "env_http_headers" }
            .Select(key => ToolConfig.Pairs(redactor.RedactJson(entry[key], redactAllValues: true)))
            .OfType<string>());
        var env = ToolConfig.Pairs(redactor.RedactJson(entry["env"]));
        return new TableRow(
            name,
            new Dictionary<string, JsonNode?>
            {
                ["name"] = name,
                ["transport"] = entry.ContainsKey("url") ? "http" : entry.ContainsKey("command") ? "stdio" : null,
                ["target"] = target is null ? null : redactor.RedactText(target),
                ["env"] = env is null ? null : redactor.RedactText(env),
                ["headers"] = headers.Length == 0 ? null : redactor.RedactText(headers),
                ["enabled"] = entry["enabled"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) ? JsonValue.Create(enabled) : null,
                ["source"] = Cell.File("config.toml", configId),
            },
            FileId: configId);
    }

    private static bool IsPlainFileName(string name) =>
        !string.IsNullOrWhiteSpace(name)
        && !name.Contains("..", StringComparison.Ordinal)
        && name.IndexOfAny(['/', '\\', ':']) < 0
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !Path.IsPathRooted(name);

    private static JsonObject? Fields(string path) =>
        HarnessFiles.ReadText(path, MaxFrontmatterBytes) is { } text ? Frontmatter.Parse(text).Fields : null;

    private static EnumerationOptions Options => new() { IgnoreInaccessible = true, MatchType = MatchType.Simple };

    private static List<string> ListFiles(string dir, string pattern)
    {
        try
        {
            return [.. Directory.EnumerateFiles(dir, pattern, Options)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> ListDirs(string dir)
    {
        try
        {
            return [.. Directory.EnumerateDirectories(dir, "*", Options).Where(d => new DirectoryInfo(d).LinkTarget is null).Order(StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
