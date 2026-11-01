using System.Text.Json.Nodes;
using Cfgdash.Core.Catalog;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary>
/// <c>opencode.config</c>: merged OpenCode settings with the file each comes from, providers, agents, commands, MCP servers and instruction files.
/// Never opens <c>auth.json</c> or anything under OpenCode's data dir; <c>{env:...}</c> and <c>{file:...}</c> substitutions are shown as written.
/// </summary>
internal sealed class OpenCodeConfigCollector(IEnvironmentReader environmentReader) : ICollector
{
    private const int MaxRows = 500;
    private const string Json = "opencode.json";
    private const string Jsonc = "opencode.jsonc";
    private const string Outside = "not opened (outside project and config dirs)";
    private static readonly HashSet<string> NonSettings = new(StringComparer.Ordinal) { "provider", "agent", "command", "mcp", "instructions", "$schema" };
    private static readonly char[] GlobChars = ['*', '?', '[', '{'];
    private static readonly HashSet<string> ConcatArrays = new(StringComparer.Ordinal) { "instructions", "plugin" };

    private static readonly IReadOnlyList<TableColumn> ProviderColumns =
    [
        new("provider", "Provider"), new("name", "Name"), new("package", "Package"), new("baseUrl", "Base URL"), new("models", "Models"), new("options", "Options"),
    ];

    private static readonly IReadOnlyList<TableColumn> AgentColumns =
    [
        new("name", "Name"), new("mode", "Mode"), new("model", "Model"), new("description", "Description"), new("source", "Source"),
    ];

    private static readonly IReadOnlyList<TableColumn> CommandColumns =
    [
        new("name", "Name"), new("description", "Description"), new("agent", "Agent"), new("model", "Model"), new("source", "Source"),
    ];

    private static readonly IReadOnlyList<TableColumn> McpColumns =
    [
        new("name", "Name"), new("transport", "Transport"), new("target", "Command or URL"), new("env", "Env"), new("headers", "Headers"),
        new("enabled", "Enabled"), new("source", "Source"),
    ];

    private static readonly IReadOnlyList<TableColumn> InstructionColumns =
    [
        new("order", "Order"), new("scope", "Scope"), new("entry", "Entry"), new("status", "Status"),
    ];

    private sealed record Source(int Index, string Label, string Path, string? FileId, JsonObject Root);

    private sealed record Setter(Source Source, JsonNode? Value);

    public CollectorManifest Manifest { get; } = new("opencode.config", "OpenCode", "1.0.0", "opencode", new Requirements(TargetDir: true));

    public Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken) =>
        Task.Run(() => Collect(context), cancellationToken);

    private CollectorResult Collect(CollectContext context)
    {
        var redactor = context.Redactor;
        var home = context.Environment.HomeDir;
        var notes = new List<string>();
        string? R(string? text) => text is null ? null : redactor.RedactText(text);
        string PathText(string path) => redactor.RedactText(PathUtil.ToJson(path));

        var xdg = environmentReader.Get("XDG_CONFIG_HOME");
        var globalDir = PathUtil.Full(!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg) && !PathUtil.IsNetworkPath(xdg)
            ? Path.Combine(xdg, "opencode")
            : Path.Combine(home, ".config", "opencode"));
        var dataDir = PathUtil.Full(Path.Combine(home, ".local", "share", "opencode"));
        var target = PathUtil.Full(context.TargetDir);
        var projectRoot = PathUtil.FindGitRoot(target) ?? target;
        var projectDirs = PathUtil.AncestorsFromRoot(target).Where(d => PathUtil.IsUnder(d, projectRoot)).ToList();

        bool Forbidden(string path) =>
            PathUtil.IsNetworkPath(path)
            || IsAuthJson(path)
            || ToolConfig.IsUnderAny(path, [dataDir])
            || IsAuthJson(PathUtil.RealPath(path))
            || FileRegistry.IsNeverRead(path);

        string? configDir = null;
        var configDirValue = environmentReader.Get("OPENCODE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(configDirValue))
        {
            var dir = PathUtil.IsNetworkPath(configDirValue) ? null : PathUtil.Full(PathUtil.ExpandHome(configDirValue, home));
            if (dir is null || Forbidden(dir))
            {
                notes.Add("OPENCODE_CONFIG_DIR: not opened");
            }
            else if (Directory.Exists(dir))
            {
                configDir = dir;
            }
        }

        var candidates = new List<(string Label, string Path)>
        {
            ("global/" + Json, Path.Combine(globalDir, Json)),
            ("global/" + Jsonc, Path.Combine(globalDir, Jsonc)),
        };
        var configValue = environmentReader.Get("OPENCODE_CONFIG");
        if (!string.IsNullOrWhiteSpace(configValue))
        {
            if (PathUtil.IsNetworkPath(configValue))
            {
                notes.Add("OPENCODE_CONFIG: not opened");
            }
            else
            {
                candidates.Add(("OPENCODE_CONFIG", PathUtil.Full(PathUtil.ExpandHome(configValue, home))));
            }
        }

        foreach (var dir in projectDirs)
        {
            foreach (var name in new[] { Json, Jsonc, ".opencode/" + Json, ".opencode/" + Jsonc })
            {
                var path = PathUtil.Full(Path.Combine(dir, name));
                candidates.Add(("project/" + Path.GetRelativePath(projectRoot, path).Replace('\\', '/'), path));
            }
        }

        if (configDir is not null)
        {
            candidates.Add(("OPENCODE_CONFIG_DIR/" + Json, Path.Combine(configDir, Json)));
            candidates.Add(("OPENCODE_CONFIG_DIR/" + Jsonc, Path.Combine(configDir, Jsonc)));
        }

        var sources = new List<Source>();
        var present = configDir is not null;
        foreach (var (label, path) in candidates)
        {
            if (Forbidden(path))
            {
                notes.Add(redactor.RedactText(label + ": not opened"));
                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            present = true;
            var fileId = context.Files.Register(path, "settings")?.Id;
            if (!ToolConfig.TryReadJsonc(path, out var root, out var error))
            {
                notes.Add(redactor.RedactText($"{PathUtil.ToJson(path)}: {error}"));
                continue;
            }

            sources.Add(new Source(sources.Count, label, path, fileId, root!));
        }

        if (!present)
        {
            return CollectorResult.NotPresent("no OpenCode config (OPENCODE_CONFIG, OPENCODE_CONFIG_DIR, ~/.config/opencode/opencode.json[c] or project opencode.json[c])");
        }

        var merged = new JsonObject();
        var setters = new Dictionary<string, List<Setter>>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            Merge(merged, source.Root, string.Empty, source, setters);
        }

        SourceRef Ref(Source source) => new(redactor.RedactText(source.Label), source.FileId);
        JsonNode? Display(JsonNode? value, bool secret) => secret && value is not null ? JsonValue.Create(Redactor.Marker) : redactor.RedactJson(value);
        Source? Winner(string section, string name) => sources.LastOrDefault(s => s.Root[section] is JsonObject obj && obj.ContainsKey(name));
        JsonObject MergedSection(string key) => merged[key] as JsonObject ?? new JsonObject();

        var truncated = new HashSet<string>(StringComparer.Ordinal);
        bool Room(List<TableRow> rows, string block)
        {
            if (rows.Count < MaxRows)
            {
                return true;
            }

            if (truncated.Add(block))
            {
                notes.Add($"{block}: more than {MaxRows} rows, list truncated");
            }

            return false;
        }

        var settingsRows = new List<KvRow>();
        void Walk(JsonObject node, string prefix)
        {
            foreach (var (key, child) in node)
            {
                if (prefix.Length == 0 && NonSettings.Contains(key))
                {
                    continue;
                }

                var path = prefix.Length == 0 ? key : prefix + "." + key;
                var isSecret = redactor.IsSecretName(key);
                if (child is JsonObject inner && !isSecret)
                {
                    Walk(inner, path);
                    continue;
                }

                var value = Display(child, isSecret);
                var changed = !JsonNode.DeepEquals(value, child);
                var by = isSecret
                    ? setters.Where(p => p.Key == path || p.Key.StartsWith(path + ".", StringComparison.Ordinal))
                        .SelectMany(p => p.Value).Select(s => s.Source).Distinct().OrderBy(s => s.Index)
                        .Select(s => new Setter(s, JsonValue.Create(Redactor.Marker))).ToList()
                    : setters.GetValueOrDefault(path)?.Select(s => new Setter(s.Source, Display(s.Value, false))).ToList() ?? new List<Setter>();
                settingsRows.Add(new KvRow(
                    redactor.RedactText(path),
                    value,
                    by.Count > 0 ? Ref(by[^1].Source) : null,
                    changed,
                    changed ? ["redacted"] : null,
                    by.Count > 1
                        ? by.Select((s, i) => new Contribution(s.Value, Ref(s.Source), ConcatArrays.Contains(path) ? ContributionStatus.Merged : i == by.Count - 1 ? ContributionStatus.Applied : ContributionStatus.Overridden)).ToList()
                        : null));
            }
        }

        Walk(merged, string.Empty);

        var providers = new List<TableRow>();
        foreach (var (id, node) in MergedSection("provider"))
        {
            if (node is not JsonObject provider)
            {
                continue;
            }

            if (!Room(providers, "providers"))
            {
                break;
            }

            var options = provider["options"] as JsonObject;
            var shown = redactor.RedactJson(options) as JsonObject;
            shown?.Remove("baseURL");
            providers.Add(new TableRow(
                "provider:" + redactor.RedactText(id),
                new Dictionary<string, JsonNode?>
                {
                    ["provider"] = R(id),
                    ["name"] = R(ToolConfig.Text(provider["name"])),
                    ["package"] = R(ToolConfig.Text(provider["npm"])),
                    ["baseUrl"] = R(ToolConfig.Text(options?["baseURL"])),
                    ["models"] = provider["models"] is JsonObject { Count: > 0 } models ? R(string.Join(", ", models.Select(m => m.Key))) : null,
                    ["options"] = R(ToolConfig.Pairs(shown)),
                },
                null,
                Winner("provider", id)?.FileId));
        }

        var folders = new List<string> { globalDir };
        folders.AddRange(projectDirs.Select(d => Path.Combine(d, ".opencode")));
        if (configDir is not null)
        {
            folders.Add(configDir);
        }

        folders = folders.Select(PathUtil.Full).Distinct(PathUtil.Comparer).ToList();

        void AddMarkdown(List<TableRow> rows, string block, string kind, string[] subdirs, Func<JsonObject?, Dictionary<string, JsonNode?>> cells)
        {
            foreach (var folder in folders)
            {
                foreach (var sub in subdirs)
                {
                    var dir = Path.Combine(folder, sub);
                    if (Forbidden(dir) || !Directory.Exists(dir))
                    {
                        continue;
                    }

                    string[] files;
                    try
                    {
                        files = Directory.GetFiles(dir, "*.md", SearchOption.TopDirectoryOnly);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    Array.Sort(files, StringComparer.Ordinal);
                    foreach (var file in files)
                    {
                        if (Forbidden(file))
                        {
                            notes.Add(PathText(file) + ": not opened");
                            continue;
                        }

                        if (!Room(rows, block))
                        {
                            return;
                        }

                        var fileId = context.Files.Register(file, kind)?.Id;
                        var parsed = HarnessFiles.ReadText(file) is { } text ? Frontmatter.Parse(text) : null;
                        var row = cells(parsed?.Fields);
                        row["name"] = R(Path.GetFileNameWithoutExtension(file));
                        row["source"] = Cell.File(PathText(file), fileId);
                        if (parsed?.Error is { } error)
                        {
                            row["description"] ??= R("error: " + error);
                        }

                        rows.Add(new TableRow("file:" + FileRegistry.IdFor(file), row, parsed?.Error is null ? null : ["error"], fileId));
                    }
                }
            }
        }

        var agents = new List<TableRow>();
        foreach (var (name, node) in MergedSection("agent"))
        {
            if (node is not JsonObject agent)
            {
                continue;
            }

            if (!Room(agents, "agents"))
            {
                break;
            }

            agents.Add(new TableRow(
                "config:" + redactor.RedactText(name),
                new Dictionary<string, JsonNode?>
                {
                    ["name"] = R(name),
                    ["mode"] = R(ToolConfig.Text(agent["mode"])),
                    ["model"] = R(ToolConfig.Text(agent["model"])),
                    ["description"] = R(CatalogText.Description(agent["description"])),
                    ["source"] = "config",
                },
                null,
                Winner("agent", name)?.FileId));
        }

        AddMarkdown(agents, "agents", "agent", ["agent", "agents"], fields => new Dictionary<string, JsonNode?>
        {
            ["mode"] = R(CatalogText.Value(fields?["mode"])),
            ["model"] = R(CatalogText.Value(fields?["model"])),
            ["description"] = R(CatalogText.Description(fields?["description"])),
        });

        var commands = new List<TableRow>();
        foreach (var (name, node) in MergedSection("command"))
        {
            if (node is not JsonObject command)
            {
                continue;
            }

            if (!Room(commands, "commands"))
            {
                break;
            }

            commands.Add(new TableRow(
                "config:" + redactor.RedactText(name),
                new Dictionary<string, JsonNode?>
                {
                    ["name"] = R(name),
                    ["description"] = R(CatalogText.Description(command["description"])),
                    ["agent"] = R(ToolConfig.Text(command["agent"])),
                    ["model"] = R(ToolConfig.Text(command["model"])),
                    ["source"] = "config",
                },
                null,
                Winner("command", name)?.FileId));
        }

        AddMarkdown(commands, "commands", "command", ["command", "commands"], fields => new Dictionary<string, JsonNode?>
        {
            ["description"] = R(CatalogText.Description(fields?["description"])),
            ["agent"] = R(CatalogText.Value(fields?["agent"])),
            ["model"] = R(CatalogText.Value(fields?["model"])),
        });

        var mcp = new List<TableRow>();
        foreach (var (name, node) in MergedSection("mcp"))
        {
            if (node is not JsonObject server)
            {
                continue;
            }

            if (!Room(mcp, "mcp"))
            {
                break;
            }

            var mcpTarget = server["command"] is JsonArray command
                ? string.Join(' ', command.Select(a => ToolConfig.Text(a) ?? a?.ToJsonString() ?? "null"))
                : ToolConfig.Text(server["command"]) ?? ToolConfig.Text(server["url"]);
            var winner = Winner("mcp", name);
            mcp.Add(new TableRow(
                "mcp:" + redactor.RedactText(name),
                new Dictionary<string, JsonNode?>
                {
                    ["name"] = R(name),
                    ["transport"] = R(ToolConfig.Text(server["type"])),
                    ["target"] = R(mcpTarget),
                    ["env"] = R(ToolConfig.Pairs(redactor.RedactJson(server["environment"]))),
                    ["headers"] = R(ToolConfig.Pairs(redactor.RedactJson(server["headers"] as JsonObject, redactAllValues: true))),
                    ["enabled"] = redactor.RedactJson(server["enabled"]),
                    ["source"] = winner is null ? null : Cell.File(PathText(winner.Path), winner.FileId),
                },
                null,
                winner?.FileId));
        }

        var instructions = new List<TableRow>();
        void AddInstruction(string scope, JsonNode? entry, string status, string? fileId)
        {
            if (!Room(instructions, "instructions"))
            {
                return;
            }

            var order = instructions.Count + 1;
            instructions.Add(new TableRow(
                "instruction:" + order,
                new Dictionary<string, JsonNode?> { ["order"] = order, ["scope"] = scope, ["entry"] = entry, ["status"] = status },
                null,
                fileId));
        }

        void AddAgentsFile(string scope, string path)
        {
            if (Forbidden(path) || !File.Exists(path))
            {
                return;
            }

            var fileId = context.Files.Register(path, "instruction")?.Id;
            AddInstruction(scope, Cell.File(PathText(path), fileId), "loaded", fileId);
        }

        AddAgentsFile("global", Path.Combine(globalDir, "AGENTS.md"));
        foreach (var dir in Enumerable.Reverse(projectDirs))
        {
            AddAgentsFile("project", Path.Combine(dir, "AGENTS.md"));
        }

        var roots = new List<string> { projectRoot, globalDir };
        if (configDir is not null)
        {
            roots.Add(configDir);
        }

        foreach (var item in merged["instructions"] as JsonArray ?? new JsonArray())
        {
            if (ToolConfig.Text(item) is not { } entry)
            {
                continue;
            }

            var text = redactor.RedactText(entry);
            if (entry.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || entry.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                AddInstruction("config", text, "url (not fetched)", null);
                continue;
            }

            if (entry.IndexOfAny(GlobChars) >= 0)
            {
                AddInstruction("config", text, "glob (not expanded)", null);
                continue;
            }

            var path = Resolve(entry, home, projectRoot);
            if (path is null || !roots.Any(r => PathUtil.IsUnder(path, r)))
            {
                AddInstruction("config", text, Outside, null);
            }
            else if (Forbidden(path))
            {
                AddInstruction("config", text, "not opened", null);
            }
            else if (!ToolConfig.IsUnderAny(PathUtil.RealPath(path), roots))
            {
                AddInstruction("config", text, Outside, null);
            }
            else if (!File.Exists(path))
            {
                AddInstruction("config", text, "missing", null);
            }
            else
            {
                var fileId = context.Files.Register(path, "instruction")?.Id;
                AddInstruction("config", Cell.File(text, fileId), "loaded", fileId);
            }
        }

        return new CollectorResult(
        [
            new KvBlock("settings", "Effective settings", settingsRows),
            new TableBlock("providers", "Providers and models", ProviderColumns, providers),
            new TableBlock("agents", "Agents", AgentColumns, agents),
            new TableBlock("commands", "Commands", CommandColumns, commands),
            new TableBlock("mcp", "MCP servers", McpColumns, mcp),
            new TableBlock("instructions", "Instruction files", InstructionColumns, instructions),
        ],
        Notes: notes.Count > 0 ? notes : null);
    }

    private static void Merge(JsonObject into, JsonObject from, string prefix, Source source, Dictionary<string, List<Setter>> setters)
    {
        foreach (var (key, value) in from)
        {
            var path = prefix + key;
            if (value is JsonObject obj)
            {
                if (into[key] is not JsonObject existing)
                {
                    existing = new JsonObject();
                    into[key] = existing;
                }

                Merge(existing, obj, path + ".", source, setters);
                continue;
            }

            into[key] = prefix.Length == 0 && ConcatArrays.Contains(key) && value is JsonArray added && into[key] is JsonArray existingItems
                ? new JsonArray([.. existingItems.Concat(added).DistinctBy(n => n?.ToJsonString()).Select(n => n?.DeepClone())])
                : value?.DeepClone();
            if (!setters.TryGetValue(path, out var list))
            {
                setters[path] = list = [];
            }

            list.Add(new Setter(source, value));
        }
    }

    private static string? Resolve(string entry, string home, string projectRoot)
    {
        if (PathUtil.IsNetworkPath(entry))
        {
            return null;
        }

        try
        {
            var expanded = PathUtil.ExpandHome(entry, home);
            var path = PathUtil.Full(Path.IsPathRooted(expanded) ? expanded : Path.Combine(projectRoot, expanded));
            return PathUtil.IsNetworkPath(path) ? null : path;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }

    private static bool IsAuthJson(string path) => string.Equals(Path.GetFileName(path), "auth.json", StringComparison.OrdinalIgnoreCase);
}
