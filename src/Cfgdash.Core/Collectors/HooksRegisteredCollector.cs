using System.Text.Json.Nodes;
using Cfgdash.Core.Catalog;
using Cfgdash.Core.Files;
using Cfgdash.Core.Hooks;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Core.Collectors;

/// <summary><c>hooks.registered</c>: hook handlers from settings files, enabled plugins, skills and agents, with resolved scripts and their header purpose.</summary>
internal sealed class HooksRegisteredCollector(SettingsLayers settings, PlatformLocations locations) : ICollector
{
    private static readonly string[] TextKeys = ["type", "command", "url", "tool", "prompt"];

    private static readonly IReadOnlyList<TableColumn> Columns =
    [
        new("event", "Event"), new("matcher", "Matcher"), new("type", "Type"), new("command", "Command"), new("script", "Script"),
        new("source", "Source"), new("purpose", "Purpose"), new("details", "Details"),
    ];

    private sealed record Source(string Key, JsonObject Hooks, JsonNode? Cell, FileRef? File, string? PluginDir, bool IsSettings);

    public CollectorManifest Manifest { get; } = new("hooks.registered", "Hooks", "1.0.0", "hooks", new Requirements(Paths: [PathKeys.ClaudeConfigDir], TargetDir: true));

    public async Task<CollectorResult> CollectAsync(CollectContext context, CancellationToken cancellationToken)
    {
        var stack = await settings.LoadAsync(context, cancellationToken);
        var environment = context.Environment;
        var redactor = context.Redactor;
        var notes = new List<string>();
        var sources = new List<Source>();
        foreach (var layer in stack.Contributing)
        {
            if (layer.Root!["hooks"] is JsonObject hooks)
            {
                var label = layer.FileId is null ? layer.Label : layer.Label + " settings";
                var file = layer.Path is null ? null : context.Files.Register(layer.Path, "settings");
                sources.Add(new Source(layer.Id, hooks, Cell.File(label, layer.FileId), file, null, true));
            }
        }

        var plugins = PluginLocator.Locate(stack, environment);
        foreach (var plugin in plugins)
        {
            var path = Path.Combine(plugin.Dir, "hooks", "hooks.json");
            if (!File.Exists(path))
            {
                continue;
            }

            var file = context.Files.Register(path, "other");
            if (!HarnessFiles.TryReadStrictJson(path, out var root, out var error))
            {
                notes.Add(redactor.RedactText($"{PathUtil.ToJson(path)}: {error}"));
                continue;
            }

            var hooks = root!["hooks"] as JsonObject ?? root;
            sources.Add(new Source("plugin:" + plugin.Key, hooks, Cell.File("plugin " + plugin.Key, file?.Id), file, plugin.Dir, false));
        }

        var frontmatterHooks = false;
        foreach (var (kind, entries) in new[] { ("skill", HarnessCatalog.Skills(context, stack, locations)), ("agent", HarnessCatalog.Agents(context, stack, locations)) })
        {
            foreach (var entry in entries.Where(e => !e.Overridden && !(kind == "agent" && e.Scope == "plugin")))
            {
                if (entry.Fields?["hooks"] is JsonObject hooks)
                {
                    var file = context.Files.Register(entry.Path!, kind);
                    sources.Add(new Source($"{kind}:{entry.Name}", hooks, Cell.File($"{kind} {redactor.RedactText(entry.Name)}", file?.Id), file, entry.Plugin?.Dir, false));
                    frontmatterHooks = true;
                }
            }
        }

        var configDir = environment.GetPath(PathKeys.ClaudeConfigDir);
        var seenHandlers = new List<(string Event, string Matcher, JsonNode Handler)>();
        var missing = 0;
        var rows = new List<TableRow>();
        foreach (var source in sources)
        {
            foreach (var (eventName, groupsNode) in source.Hooks)
            {
                if (groupsNode is not JsonArray groups)
                {
                    continue;
                }

                for (var g = 0; g < groups.Count; g++)
                {
                    if (groups[g] is not JsonObject group || group["hooks"] is not JsonArray handlers)
                    {
                        continue;
                    }

                    var matcher = Text(group["matcher"]) is { Length: > 0 } m && m != "*" ? m : "*";
                    for (var h = 0; h < handlers.Count; h++)
                    {
                        if (handlers[h] is not JsonObject handler)
                        {
                            continue;
                        }

                        var flags = new List<string>();
                        if (source.IsSettings)
                        {
                            if (seenHandlers.Any(s => s.Event == eventName && s.Matcher == matcher && JsonNode.DeepEquals(s.Handler, handler)))
                            {
                                flags.Add("duplicate");
                            }
                            else
                            {
                                seenHandlers.Add((eventName, matcher, handler));
                            }
                        }

                        var company = source.File?.Company == true;
                        JsonNode? script = null;
                        string? purpose = null;
                        var command = Text(handler["command"]);
                        if (Text(handler["type"]) == "command" && command is not null && HookScripts.ExtractScript(command) is { } extracted)
                        {
                            var path = HookScripts.Expand(extracted, configDir, stack.TargetDir, source.PluginDir, environment.HomeDir);
                            if (PathUtil.IsNetworkPath(path))
                            {
                                script = redactor.RedactText(PathUtil.ToJson(path));
                                flags.Add("network-path");
                            }
                            else if (File.Exists(path))
                            {
                                var scriptFile = context.Files.Register(path, "hookScript");
                                script = Cell.File(redactor.RedactText(PathUtil.ToJson(path)), scriptFile?.Id);
                                purpose = scriptFile is null ? null : HookScripts.Purpose(path) is { } p ? redactor.RedactText(p) : null;
                                company |= scriptFile?.Company == true;
                            }
                            else
                            {
                                script = redactor.RedactText(PathUtil.ToJson(path));
                                flags.Add("missing-script");
                                missing++;
                            }
                        }

                        if (company)
                        {
                            flags.Add("company");
                        }

                        var redacted = redactor.RedactJson(handler) as JsonObject ?? new JsonObject();
                        var details = redacted.Where(p => !TextKeys.Contains(p.Key)).Select(p => $"{p.Key}: {CatalogText.Value(p.Value) ?? "null"}").ToList();
                        rows.Add(new TableRow(
                            $"{source.Key}:{eventName}-{g}-{h}",
                            new Dictionary<string, JsonNode?>
                            {
                                ["event"] = redactor.RedactText(eventName),
                                ["matcher"] = redactor.RedactText(matcher),
                                ["type"] = Cell.Text(Text(redacted["type"])),
                                ["command"] = Cell.Text(TextKeys.Skip(1).Select(k => Text(redacted[k])).FirstOrDefault(t => t is not null)),
                                ["script"] = script,
                                ["source"] = source.Cell?.DeepClone(),
                                ["purpose"] = purpose,
                                ["details"] = details.Count > 0 ? string.Join("; ", details) : null,
                            },
                            flags.Count > 0 ? flags : null,
                            source.File?.Id));
                    }
                }
            }
        }

        if (missing > 0)
        {
            notes.Add(missing == 1 ? "1 hook script not found" : $"{missing} hook scripts not found");
        }

        if (frontmatterHooks)
        {
            notes.Add("Skill hooks run only while the skill is active; agent hooks only while that agent runs");
        }

        if (plugins.Count > 0)
        {
            notes.Add(PluginLocator.CacheDirNote);
        }

        return new CollectorResult([new TableBlock("hooks", "Registered hooks", Columns, rows)], Notes: notes.Count > 0 ? notes : null);
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
