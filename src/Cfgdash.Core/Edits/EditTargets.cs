using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;

namespace Cfgdash.Core.Edits;

/// <summary>A confined write target: <see cref="Real"/> is the resolved path that is written (a symlinked settings file keeps its link).</summary>
internal sealed record EditTarget(string Scope, string Real)
{
    /// <summary>Set for an edits-v2 file target (scope <c>file</c>).</summary>
    public FileTarget? File { get; init; }
}

/// <summary>Maps scopes to their settings files and confines writes (edits-v1 "Scope", plan resolution 3).</summary>
internal sealed class EditTargets(MachineEnvironment environment, PlatformLocations locations)
{
    public static readonly string[] Scopes = ["user", "project", "local", "machine"];

    /// <summary>The target of <paramref name="scope"/> for <paramref name="dir"/>; null with <c>scope-unavailable</c> or <c>unsafe-target</c>.</summary>
    public EditTarget? Resolve(string scope, string dir, out string? error)
    {
        var target = PathUtil.Full(dir);
        var root = scope switch
        {
            "user" => environment.GetPath(PathKeys.ClaudeConfigDir),
            "project" => target,
            "local" => PathUtil.IsNetworkPath(target) ? target : PathUtil.FindGitRoot(target) ?? target,
            "machine" => environment.GetPath(PathKeys.ConfigRepo),
            _ => null,
        };
        if (root is null)
        {
            error = "scope-unavailable";
            return null;
        }

        error = "unsafe-target";
        root = PathUtil.Full(root);
        var name = scope switch
        {
            "local" => "settings.local.json",
            "machine" => "machine.json",
            _ => "settings.json",
        };
        var logical = scope == "user" ? Path.Combine(root, name) : Path.Combine(root, ".claude", name);
        if (PathUtil.IsNetworkPath(root) || FileRegistry.IsNeverRead(logical))
        {
            return null;
        }

        var realRoot = PathUtil.RealPath(root);
        var real = PathUtil.RealPath(logical);
        if (Path.GetDirectoryName(real) is not { } parent
            || !Directory.Exists(realRoot)
            || !string.Equals(Path.GetFileName(real), name, PathUtil.Comparison)
            || FileRegistry.IsNeverRead(real)
            || HasGitSegment(logical) || HasGitSegment(real)
            || !PathUtil.IsUnder(real, realRoot)
            || IsManaged(logical) || IsManaged(real)
            || Directory.Exists(real)
            || new FileInfo(real).LinkTarget is not null
            || (scope == "user" && Directory.Exists(parent) ? !PathUtil.IsUnder(parent, realRoot) : !PathUtil.PathEquals(parent, Path.Combine(realRoot, ".claude"))))
        {
            return null;
        }

        error = null;
        return new EditTarget(scope, real);
    }

    internal bool IsManaged(string path) =>
        new[] { locations.ManagedDir, locations.IgnoredManagedDir }.Any(dir => dir is not null && (PathUtil.IsUnder(path, dir) || PathUtil.IsUnder(path, PathUtil.RealPath(dir))));

    internal static bool HasGitSegment(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase));
}
