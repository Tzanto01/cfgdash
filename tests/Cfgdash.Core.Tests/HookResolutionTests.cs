using System.Text.Json.Nodes;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Files;
using Cfgdash.Core.Hooks;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

/// <summary>One build of hooks.registered over project + user settings, an enabled plugin, a user skill and a user agent.</summary>
public sealed class HooksSnapshotFixture : IDisposable
{
    public const string GuardCommand = "pwsh -NoProfile -File \"$CLAUDE_CONFIG_DIR/hooks/guard.ps1\"";

    public HooksSnapshotFixture()
    {
        Secret = "gh" + "p_" + new string('a', 36);
        var target = Tree.Dir("proj");
        var config = Tree.Dir("config/.claude");
        var plugin = Tree.Dir("config/.claude/plugins/cache/tool");
        Guard = Tree.Write("config/.claude/hooks/guard.ps1", "#Requires -Version 7\n<#\nBlocks risky commands.\nSecond line.\n\nDetails.\n#>\n");
        HomeScript = Tree.Write("home/user/scripts/fmt.sh", "#!/bin/sh\n# Formats files.\n");
        var guardGroup = new JsonObject { ["matcher"] = "Bash", ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = GuardCommand }) };
        Project = Tree.Write("proj/.claude/settings.json", new JsonObject { ["hooks"] = new JsonObject { ["PreToolUse"] = new JsonArray(guardGroup.DeepClone()) } }.ToJsonString());
        User = Tree.Write("config/.claude/settings.json", new JsonObject
        {
            ["enabledPlugins"] = new JsonObject { ["tool@mkt"] = true },
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray(guardGroup.DeepClone()),
                ["Stop"] = new JsonArray(new JsonObject
                {
                    ["matcher"] = "",
                    ["hooks"] = new JsonArray(
                        new JsonObject { ["type"] = "command", ["command"] = "pwsh -File \"$CLAUDE_CONFIG_DIR/hooks/gone.ps1\"" },
                        new JsonObject { ["type"] = "command", ["command"] = "echo " + Secret }),
                }),
                ["FutureEvent"] = new JsonArray(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "http", ["url"] = "https://hooks.example/notify", ["timeout"] = 5, ["if"] = "Bash(git *)" }),
                }),
            },
        }.ToJsonString());
        Tree.Write("config/.claude/plugins/installed_plugins.json", new JsonObject
        {
            ["plugins"] = new JsonObject { ["tool@mkt"] = new JsonArray(new JsonObject { ["scope"] = "user", ["installPath"] = plugin }) },
        }.ToJsonString());
        PluginHooks = Tree.Write("config/.claude/plugins/cache/tool/hooks/hooks.json", new JsonObject
        {
            ["hooks"] = new JsonObject { ["PreToolUse"] = new JsonArray(guardGroup.DeepClone()) },
        }.ToJsonString());
        Tree.Write("config/.claude/skills/fmt/SKILL.md", "---\ndescription: formats\nhooks:\n  PostToolUse:\n    - matcher: Edit\n      hooks:\n        - type: command\n          command: sh ~/scripts/fmt.sh\n---\n");
        Tree.Write("config/.claude/agents/checker.md", "---\nname: checker\ndescription: checks\nhooks:\n  Stop:\n    - hooks:\n        - type: prompt\n          prompt: verify the result\n---\n");

        var settings = new SettingsLayers(new PlatformLocations(null, null, _ => null));
        var builder = new SnapshotBuilder(
            TestContexts.WithConfigDir(Tree, config),
            [new HooksRegisteredCollector(settings, new PlatformLocations(null, null, _ => null))],
            new Redactor(),
            new FakeProcessRunner(),
            "0.1.0");
        Snapshot = builder.BuildAsync(target, CancellationToken.None).GetAwaiter().GetResult().Snapshot;
    }

    public TempTree Tree { get; } = new();

    public string Secret { get; }

    public string Guard { get; }

    public string HomeScript { get; }

    public string Project { get; }

    public string User { get; }

    public string PluginHooks { get; }

    public HarnessSnapshot Snapshot { get; }

    public IReadOnlyList<TableRow> Rows =>
        Snapshot.Sections.Single(s => s.CollectorId == "hooks.registered").Blocks.OfType<TableBlock>().Single(b => b.Id == "hooks").Rows;

    public TableRow Row(string id) => Rows.Single(r => r.Id == id);

    public void Dispose() => Tree.Dispose();
}

public sealed class HookResolutionTests(HooksSnapshotFixture fixture) : IClassFixture<HooksSnapshotFixture>
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "cfgdash-hooks");
    private static readonly string ConfigDir = Path.Combine(Base, "cfg");
    private static readonly string TargetDir = Path.Combine(Base, "proj");
    private static readonly string PluginDir = Path.Combine(Base, "plug");
    private static readonly string HomeDir = Path.Combine(Base, "home");

    private static string? Text(JsonNode? cell) => cell is JsonObject obj ? obj["text"]?.GetValue<string>() : cell?.GetValue<string>();

    private static string? FileId(JsonNode? cell) => (cell as JsonObject)?["fileId"]?.GetValue<string>();

    [Theory]
    [InlineData("$CLAUDE_CONFIG_DIR/hooks/a.ps1", "cfg/hooks/a.ps1")]
    [InlineData("${CLAUDE_CONFIG_DIR}/hooks/a.ps1", "cfg/hooks/a.ps1")]
    [InlineData("%CLAUDE_CONFIG_DIR%\\hooks\\a.ps1", "cfg/hooks/a.ps1")]
    [InlineData("$CLAUDE_PROJECT_DIR/.claude/b.sh", "proj/.claude/b.sh")]
    [InlineData("${CLAUDE_PROJECT_DIR}/b.sh", "proj/b.sh")]
    [InlineData("$env:CLAUDE_CONFIG_DIR\\hooks\\a.ps1", "cfg/hooks/a.ps1")]
    [InlineData("$env:CLAUDE_PROJECT_DIR/b.ps1", "proj/b.ps1")]
    [InlineData("${CLAUDE_PLUGIN_ROOT}/scripts/c.mjs", "plug/scripts/c.mjs")]
    [InlineData("~/scripts/d.py", "home/scripts/d.py")]
    [InlineData("scripts/e.sh", "proj/scripts/e.sh")]
    public void Expand_resolves_variables_home_and_relative_paths(string input, string expected)
    {
        var resolved = HookScripts.Expand(input, ConfigDir, TargetDir, PluginDir, HomeDir);

        Assert.Equal(PathUtil.ToJson(Path.Combine(Base, expected)), PathUtil.ToJson(resolved));
    }

    [Theory]
    [InlineData("pwsh -NoProfile -File \"$CLAUDE_CONFIG_DIR/hooks/my hook.ps1\" -Mode x.sh", "$CLAUDE_CONFIG_DIR/hooks/my hook.ps1")]
    [InlineData("pwsh -file '~/hooks/a.ps1'", "~/hooks/a.ps1")]
    [InlineData("pwsh -File hooks/a.ps1", "hooks/a.ps1")]
    [InlineData("node --no-warnings \"${CLAUDE_PLUGIN_ROOT}/run.mjs\" other.py", "${CLAUDE_PLUGIN_ROOT}/run.mjs")]
    [InlineData("echo done", null)]
    public void ExtractScript_prefers_the_File_argument_then_the_first_script_token(string command, string? expected)
    {
        Assert.Equal(expected, HookScripts.ExtractScript(command));
    }

    [Theory]
    [InlineData("a.ps1", "#Requires -Version 7\n\n<# Guards the shell.\n   Second line.\n\n   More detail.\n#>\nparam()", "Guards the shell. Second line.")]
    [InlineData("b.ps1", "#Requires -Version 7\n# Writes a checkpoint.\n# Before compaction.\n#\n# Usage notes.\nparam()", "Writes a checkpoint. Before compaction.")]
    [InlineData("c.sh", "#!/usr/bin/env bash\n# Formats staged files.\nset -e", "Formats staged files.")]
    [InlineData("d.mjs", "// Sends a notification.\nimport x from 'y';", "Sends a notification.")]
    [InlineData("e.ps1", "param()\n# not a header", null)]
    public void Purpose_is_the_first_header_comment_paragraph(string name, string content, string? expected)
    {
        var path = fixture.Tree.Write("purpose/" + name, content);

        Assert.Equal(expected, HookScripts.Purpose(path));
    }

    [Fact]
    public void Purpose_is_capped_at_300_chars()
    {
        var path = fixture.Tree.Write("purpose/long.sh", "# " + new string('x', 400));

        Assert.Equal(300, HookScripts.Purpose(path)!.Length);
    }

    [Fact]
    public void Settings_hook_resolves_its_script_purpose_and_source()
    {
        var row = fixture.Row("project:PreToolUse-0-0");

        Assert.Equal("Bash", Text(row.Cells["matcher"]));
        Assert.Equal(FileRegistry.IdFor(fixture.Guard), FileId(row.Cells["script"]));
        Assert.Equal("Blocks risky commands. Second line.", Text(row.Cells["purpose"]));
        Assert.Equal("project settings", Text(row.Cells["source"]));
        Assert.Equal(FileRegistry.IdFor(fixture.Project), row.FileId);
        Assert.Contains(fixture.Snapshot.Files, f => f.Id == FileRegistry.IdFor(fixture.Guard) && f.Kind == "hookScript");
    }

    [Fact]
    public void Identical_handler_in_a_second_settings_file_is_a_duplicate_but_the_plugin_copy_is_not()
    {
        var copies = fixture.Rows.Where(r => Text(r.Cells["command"]) == HooksSnapshotFixture.GuardCommand).ToList();

        Assert.Equal(["project:PreToolUse-0-0", "user:PreToolUse-0-0", "plugin:tool@mkt:PreToolUse-0-0"], copies.Select(r => r.Id));
        Assert.Equal(["user:PreToolUse-0-0"], copies.Where(r => r.Flags?.Contains("duplicate") == true).Select(r => r.Id));
        Assert.Equal(FileRegistry.IdFor(fixture.PluginHooks), copies[2].FileId);
    }

    [Fact]
    public void Missing_script_is_flagged_and_counted_in_a_note()
    {
        var row = fixture.Row("user:Stop-0-0");

        Assert.Equal("*", Text(row.Cells["matcher"]));
        Assert.Equal(["missing-script"], row.Flags!);
        Assert.EndsWith("/hooks/gone.ps1", Text(row.Cells["script"]));
        Assert.Contains("1 hook script not found", fixture.Snapshot.Collectors.Single().Notes!);
    }

    [Fact]
    public void Secret_in_a_hook_command_is_redacted()
    {
        var command = Text(fixture.Row("user:Stop-0-1").Cells["command"])!;

        Assert.DoesNotContain(fixture.Secret, command);
        Assert.Contains("[redacted]", command);
    }

    [Fact]
    public void Unknown_event_and_http_handler_are_shown_with_details()
    {
        var row = fixture.Row("user:FutureEvent-0-0");

        Assert.Equal("http", Text(row.Cells["type"]));
        Assert.Equal("https://hooks.example/notify", Text(row.Cells["command"]));
        Assert.Equal("timeout: 5; if: Bash(git *)", Text(row.Cells["details"]));
        Assert.Null(row.Cells["script"]);
    }

    [Fact]
    public void Skill_and_agent_frontmatter_hooks_are_listed()
    {
        var skill = fixture.Row("skill:fmt:PostToolUse-0-0");
        var agent = fixture.Row("agent:checker:Stop-0-0");

        Assert.Equal(FileRegistry.IdFor(fixture.HomeScript), FileId(skill.Cells["script"]));
        Assert.Equal("Formats files.", Text(skill.Cells["purpose"]));
        Assert.Equal("verify the result", Text(agent.Cells["command"]));
        Assert.Contains(fixture.Snapshot.Collectors.Single().Notes!, n => n.StartsWith("Skill hooks run only", StringComparison.Ordinal));
    }
}
