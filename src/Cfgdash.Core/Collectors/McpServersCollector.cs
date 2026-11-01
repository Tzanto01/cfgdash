using System.Text.Json.Nodes;
using Cfgdash.Core.Environment;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary><c>mcp.servers</c>: MCP servers by scope (managed-mcp.json, local, project, user, plugin). <c>${VAR}</c> references are never expanded.</summary>
internal sealed class McpServersCollector(SettingsLayers settings, PlatformLocations locations, IEnvironmentReader environmentReader) : ICollector
{
    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new("name", "Name"), new("scope", "Scope"), new("transport", "Transport"), new("target", "Command or URL"),
        new("env", "Env"), new("headers", "Headers"), new("source", "Source"), new("status", "Status"),
    ];

    private sealed record Server(string Id, string Name, string Scope, JsonObject Entry, JsonNode? Source, string? FileId);

    public CollectorManifest Manifest { get; } = new("mcp.servers", "MCP servers", "1.0.0", "mcp-permissions", new Requirements(TargetDir: true));

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var stack = await settings.LoadAsync(context, cancellationToken);
        var environment = context.Environment;
        var redactor = context.Redactor;
        var notes = new List<string>();
        var servers = new List<Server>();

        void Add(string idPrefix, string scope, JsonObject? entries, Func<JsonNode?> source, string? fileId)
        {
            if (entries is null)
            {
                return;
            }

            foreach (var (name, entry) in entries)
            {
                var id = idPrefix + name;
                if (entry is JsonObject obj && !servers.Any(s => s.Id == id))
                {
                    servers.Add(new Server(id, name, scope, obj, source(), fileId));
                }
            }
        }

        void AddFile(string idPrefix, string scope, string path, string kind, bool rootFallback = false)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var fileId = context.Files.Register(path, kind)?.Id;
            if (!HarnessFiles.TryReadStrictJson(path, out var root, out var error))
            {
                notes.Add(redactor.RedactText($"{PathUtil.ToJson(path)}: {error}"));
                return;
            }

            var entries = root!["mcpServers"] as JsonObject ?? (rootFallback ? root : null);
            Add(idPrefix, scope, entries, () => Cell.File(redactor.RedactText(PathUtil.ToJson(path)), fileId), fileId);
        }

        var managedFile = locations.ManagedDir is null ? null : Path.Combine(locations.ManagedDir, "managed-mcp.json");
        var managed = managedFile is not null && File.Exists(managedFile);
        if (managed)
        {
            AddFile("managed:", "managed", managedFile!, "mcp");
            notes.Add("managed-mcp.json is present: it replaces all other MCP servers");
        }

        var configDir = environment.Paths.FirstOrDefault(p => p.Key == PathKeys.ClaudeConfigDir);
        var claudeJson = ClaudeJson.Read(ClaudeJson.Locate(configDir?.Value, configDir?.Source, environment.HomeDir));
        foreach (var project in claudeJson?.Projects.Where(p =>
                     !string.IsNullOrWhiteSpace(p.Path) && Path.IsPathRooted(p.Path) && p.Path.IndexOfAny(Path.GetInvalidPathChars()) < 0
                     && PathUtil.PathEquals(p.Path, stack.TargetDir)) ?? [])
        {
            Add("local:", "local", project.McpServers, () => Cell.Text(".claude.json (local)"), null);
        }

        AddFile("project:", "project", Path.Combine(stack.ProjectRoot, ".mcp.json"), "mcp");
        Add("user:", "user", claudeJson?.UserMcpServers, () => Cell.Text(".claude.json (user)"), null);

        var plugins = PluginLocator.Locate(stack, environment);
        foreach (var plugin in plugins)
        {
            var prefix = $"plugin:{plugin.Key}:";
            AddFile(prefix, "plugin", Path.Combine(plugin.Dir, ".mcp.json"), "mcp");
            var manifestPath = Path.Combine(plugin.Dir, ".claude-plugin", "plugin.json");
            if (!File.Exists(manifestPath) || !HarnessFiles.TryReadStrictJson(manifestPath, out var manifest, out _))
            {
                continue;
            }

            switch (manifest!["mcpServers"])
            {
                case JsonObject inline:
                    var manifestId = context.Files.Register(manifestPath, "other")?.Id;
                    Add(prefix, "plugin", inline, () => Cell.File(redactor.RedactText(PathUtil.ToJson(manifestPath)), manifestId), manifestId);
                    break;
                case JsonValue reference when reference.TryGetValue<string>(out var relative):
                    var referenced = PathUtil.Full(Path.Combine(plugin.Dir, relative));
                    if (PathUtil.IsUnder(referenced, plugin.Dir) && !FileRegistry.IsNeverRead(referenced))
                    {
                        AddFile(prefix, "plugin", referenced, "mcp", rootFallback: true);
                    }

                    break;
            }
        }

        if (plugins.Count > 0)
        {
            notes.Add(PluginLocator.CacheDirNote);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = servers.Select(server =>
        {
            var overridden = (managed && server.Scope != "managed") | !seen.Add(server.Name);
            var flags = new List<string>();
            if (overridden)
            {
                flags.Add("overridden");
            }

            if (MissingCommand(server.Entry, environment.Os))
            {
                flags.Add("missing-command");
            }

            return new TableRow(
                server.Id,
                new Dictionary<string, JsonNode?>
                {
                    ["name"] = server.Name,
                    ["scope"] = server.Scope,
                    ["transport"] = Text(server.Entry["type"]) ?? (Text(server.Entry["command"]) is null ? null : "stdio"),
                    ["target"] = Target(server.Entry) is { } target ? redactor.RedactText(target) : null,
                    ["env"] = Pairs(redactor.RedactJson(server.Entry["env"])),
                    ["headers"] = Pairs(redactor.RedactJson(server.Entry["headers"] as JsonObject, redactAllValues: true)),
                    ["source"] = server.Source?.DeepClone(),
                    ["status"] = overridden ? "overridden" : "applied",
                },
                flags.Count > 0 ? flags : null,
                server.FileId);
        }).ToList();

        return new CollectorResult([new TableBlock("servers", "MCP servers", Columns, rows)], Notes: notes.Count > 0 ? notes : null);
    }

    private bool MissingCommand(JsonObject entry, OsFamily os)
    {
        if (Text(entry["command"]) is not { } command || Text(entry["type"]) is not (null or "stdio")
            || PathUtil.IsNetworkPath(command) || command.Contains("${", StringComparison.Ordinal))
        {
            return false;
        }

        return Path.IsPathFullyQualified(command) ? !File.Exists(command) : ToolProbe.FindOnPath(command, environmentReader, os) is null;
    }

    private static string? Target(JsonObject entry)
    {
        if (Text(entry["command"]) is { } command)
        {
            var args = (entry["args"] as JsonArray)?.Select(a => Text(a) ?? a?.ToJsonString() ?? "null") ?? [];
            return string.Join(' ', [command, .. args]);
        }

        return Text(entry["url"]);
    }

    private static string? Pairs(JsonNode? node) =>
        node is JsonObject { Count: > 0 } obj
            ? string.Join("; ", obj.Select(p => p.Key + "=" + (Text(p.Value) ?? p.Value?.ToJsonString() ?? "null")))
            : null;

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
