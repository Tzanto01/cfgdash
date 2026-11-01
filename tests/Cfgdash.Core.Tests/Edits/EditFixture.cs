using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cfgdash.Core.Edits;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Redaction;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests.Edits;

internal sealed class SettableTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class StubProcessRunner(string standardOutput, int exitCode = 0) : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken, string? workingDirectory = null, string? standardInput = null) =>
        Task.FromResult(new ProcessResult(exitCode, standardOutput, string.Empty, false));
}

/// <summary>An <see cref="EditService"/> over a temp tree: project dir <c>proj</c>, user config dir, config repo, managed dir and cache dir all live inside it.</summary>
internal sealed class EditFixture : IDisposable
{
    public EditFixture(bool userScope = true, bool configRepo = false, string configDir = "config/.claude", string managedDir = "managed",
        IProcessRunner? processes = null, IReadOnlyDictionary<string, string>? tools = null)
    {
        Project = Tree.Dir("proj");
        Config = Tree.Dir(configDir);
        Repo = Tree.Dir("configrepo");
        var managed = Tree.Dir(managedDir);
        var paths = new List<ContextPath>();
        if (userScope)
        {
            paths.Add(TestEnvironments.ContextPath(PathKeys.ClaudeConfigDir, Config));
        }

        if (configRepo)
        {
            paths.Add(TestEnvironments.ContextPath(PathKeys.ConfigRepo, Repo));
        }

        Service = new EditService(
            TestEnvironments.Create(Tree, paths, tools),
            Reader,
            processes ?? new FakeProcessRunner(),
            new Redactor(),
            new PlatformLocations(managed, null, _ => null),
            Cache,
            Time);
    }

    public TempTree Tree { get; } = new();

    public FakeEnvironmentReader Reader { get; } = new();

    public SettableTime Time { get; } = new();

    public string Project { get; }

    public string Config { get; }

    public string Repo { get; }

    public EditService Service { get; }

    public string Cache => Tree.Path("cache");

    public string ProjectSettings => Tree.Path("proj/.claude/settings.json");

    public string UserSettings => Path.Combine(Config, "settings.json");

    public string MachineJson => Path.Combine(Repo, ".claude", "machine.json");

    public string AuditPath => Path.Combine(Cache, "edits", "audit.jsonl");

    public static JsonObject Set(string key, string scope, JsonNode? value) => new() { ["key"] = key, ["scope"] = scope, ["value"] = value };

    public static JsonObject Unset(string key, string scope) => new() { ["key"] = key, ["scope"] = scope, ["unset"] = true };

    public static JsonArray Changes(params JsonNode?[] changes) => new(changes);

    public static string Text(JsonNode? node) => (string?)node ?? throw new InvalidOperationException("Expected a string value.");

    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256File(string path) => Sha256(File.ReadAllBytes(path));

    public static string Real(string path) => PathUtil.ToJson(PathUtil.RealPath(path));

    public static void Link(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);
    }

    public static string[] Names(string dir) =>
        [.. Directory.GetFileSystemEntries(dir).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    public static void AssertInvalid(EditResult result, string? key, string reason)
    {
        Assert.Equal(EditStatus.InvalidChange, result.Status);
        Assert.False(result.Wrote);
        Assert.Equal("invalid-change", (string?)result.Body["error"]);
        Assert.Equal(key, (string?)result.Body["key"]);
        Assert.Equal(reason, (string?)result.Body["reason"]);
    }

    public static JsonObject? Row(JsonObject body, string key) =>
        body["categories"]!.AsArray()
            .SelectMany(c => c!["settings"]!.AsArray())
            .Select(s => s!.AsObject())
            .FirstOrDefault(s => (string?)s["key"] == key);

    public static string? CategoryOf(JsonObject body, string key) =>
        (string?)body["categories"]!.AsArray()
            .FirstOrDefault(c => c!["settings"]!.AsArray().Any(s => (string?)s!["key"] == key))?["id"];

    public string WriteProject(string content) => Tree.Write("proj/.claude/settings.json", content);

    public string WriteUser(string content)
    {
        File.WriteAllText(UserSettings, content);
        return UserSettings;
    }

    public Task<EditResult> Propose(params JsonNode?[] changes) => ProposeIn(Project, changes);

    public Task<EditResult> ProposeIn(string dir, params JsonNode?[] changes) =>
        Service.ProposeAsync(dir, Changes(changes), CancellationToken.None);

    public Task<EditResult> Apply(EditResult proposal) =>
        Service.ApplyAsync(Text(proposal.Body["id"]), Text(proposal.Body["confirm"]), CancellationToken.None);

    public void Dispose() => Tree.Dispose();
}
