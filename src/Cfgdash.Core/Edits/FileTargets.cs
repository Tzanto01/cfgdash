using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;

namespace Cfgdash.Core.Edits;

/// <summary>An editable text config file (edits-v2): <see cref="Real"/> is the resolved path that is written; <see cref="Settings"/> marks a Claude Code settings JSON.</summary>
internal sealed record FileTarget(string Logical, string Real, string Kind, bool Settings);

/// <summary>The edits-v2 editability rule by path and registered kind; content rules live in <see cref="EditService"/>.</summary>
internal sealed class FileTargets(MachineEnvironment environment, EditTargets targets)
{
    public const int MaxBytes = 256 * 1024;

    private static readonly string[] InstructionNames = ["CLAUDE.md", "CLAUDE.local.md", "AGENTS.md", "AGENTS.override.md"];
    private static readonly string[] SettingsNames = ["settings.json", "settings.local.json"];
    private static readonly string[] OtherAppSegments = [".codex", ".t3", ".opencode"];
    private static readonly (string Kind, string Dir)[] KindDirs = [("agent", "agents"), ("skill", "skills"), ("command", "commands")];

    /// <summary>
    /// The target for the registered file at <paramref name="path"/> seen from <paramref name="dir"/>; null with the reason
    /// (<c>not-editable-kind</c>, <c>company</c>, <c>outside-harness-roots</c>, <c>unreadable</c>). The logical and the real path must both pass.
    /// </summary>
    public FileTarget? Resolve(string dir, string path, string kind, out string? reason)
    {
        var logical = PathUtil.LongPath(PathUtil.Full(path));
        if (!PathUtil.TryRealPath(logical, out var real) || Directory.Exists(real) || !File.Exists(real))
        {
            reason = "unreadable";
            return null;
        }

        real = PathUtil.LongPath(real);

        reason = Reason(dir, logical, kind) ?? Reason(dir, real, kind)
            ?? (IsSettings(logical) != IsSettings(real) ? "not-editable-kind" : null);
        return reason is null ? new FileTarget(logical, real, kind, IsSettings(real)) : null;
    }

    private string? Reason(string dir, string path, string kind)
    {
        if (PathUtil.IsNetworkPath(path) || FileRegistry.IsNeverRead(path) || EditTargets.HasGitSegment(path) || IsOtherApp(path) || targets.IsManaged(path) || !IsEditableKind(path, kind))
        {
            return "not-editable-kind";
        }

        if (environment.CompanyPrefixes.Any(prefix => PathUtil.IsUnder(path, prefix) || PathUtil.IsUnder(path, PathUtil.RealPath(prefix))))
        {
            return "company";
        }

        return Roots(dir).Any(root => PathUtil.IsUnder(path, root) || PathUtil.IsUnder(path, PathUtil.RealPath(root))) ? null : "outside-harness-roots";
    }

    /// <summary>The config dir, the config repo, the target dir and, when the target is under it, the workspace root (which holds every ancestor up to it).</summary>
    private IEnumerable<string> Roots(string dir)
    {
        var target = PathUtil.Full(dir);
        yield return target;
        if (environment.GetPath(PathKeys.WorkspaceRoot) is { } workspace && PathUtil.IsUnder(target, workspace))
        {
            yield return PathUtil.Full(workspace);
        }

        foreach (var key in new[] { PathKeys.ClaudeConfigDir, PathKeys.ConfigRepo })
        {
            if (environment.GetPath(key) is { } root)
            {
                yield return PathUtil.Full(root);
            }
        }
    }

    private bool IsEditableKind(string path, string kind)
    {
        var name = Path.GetFileName(path);
        if (IsSettings(path))
        {
            var parent = Path.GetDirectoryName(path)!;
            return string.Equals(Path.GetFileName(parent), ".claude", PathUtil.Comparison)
                || (environment.GetPath(PathKeys.ClaudeConfigDir) is { } config && (PathUtil.PathEquals(parent, config) || PathUtil.PathEquals(parent, PathUtil.RealPath(config))));
        }

        if (!name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return InstructionNames.Any(n => string.Equals(n, name, PathUtil.Comparison))
            || kind == "instruction"
            || KindDirs.Any(k => k.Kind == kind && segments.SkipLast(1).Any(s => string.Equals(s, k.Dir, PathUtil.Comparison)));
    }

    private static bool IsSettings(string path) => SettingsNames.Any(n => string.Equals(n, Path.GetFileName(path), PathUtil.Comparison));

    /// <summary>A <c>.codex</c>, <c>.t3</c> or <c>.opencode</c> segment (except their <c>worktrees</c> checkouts) or <c>.config/opencode</c>.</summary>
    private static bool IsOtherApp(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Where((s, i) => OtherAppSegments.Any(o => s.Equals(o, StringComparison.OrdinalIgnoreCase))
                && !(i + 1 < segments.Length - 1 && segments[i + 1].Equals("worktrees", StringComparison.OrdinalIgnoreCase))).Any()
            || segments.Skip(1).Where((s, i) => segments[i].Equals(".config", StringComparison.OrdinalIgnoreCase) && s.Equals("opencode", StringComparison.OrdinalIgnoreCase)).Any();
    }
}
