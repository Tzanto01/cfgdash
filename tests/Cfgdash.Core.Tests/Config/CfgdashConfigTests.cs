using System.Text.Json.Nodes;
using Cfgdash.Core.Config;
using Cfgdash.Core.Tests.Edits;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests.Config;

public sealed class CfgdashConfigTests : IDisposable
{
    private readonly TempTree tree = new();
    private readonly CfgdashConfig config;

    public CfgdashConfigTests() => config = new CfgdashConfig(tree.Path("cfg"), tree.Path("cache"), new SettableTime());

    public void Dispose() => tree.Dispose();

    private string AuditPath => tree.Path("cache/edits/audit.jsonl");

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void A_missing_file_reads_as_the_documented_defaults()
    {
        var settings = config.Read();

        Assert.Equal(CfgdashSettings.Defaults, settings);
        Assert.Equal(2.0, settings.AssistantMaxBudgetUsd);
        Assert.Equal(("system", "sonnet", "medium"), (settings.Theme, settings.AssistantModel, settings.AssistantEffort));
        Assert.False(settings.StartAtLogin || settings.Tailnet || settings.FleetEnabled);
        Assert.Null(settings.DefaultDir);
    }

    [Fact]
    public void Read_falls_back_per_key_for_wrong_types_bad_values_and_a_missing_default_dir()
    {
        var missing = tree.Path("missing").Replace('\\', '/');
        tree.Write("cfg/config.json", new JsonObject
        {
            ["theme"] = 5,
            ["startAtLogin"] = "yes",
            ["tailnet"] = true,
            ["defaultDir"] = missing,
            ["assistant"] = new JsonObject { ["effort"] = "bogus", ["maxBudgetUsd"] = "x", ["model"] = "opus" },
        }.ToJsonString());

        var settings = config.Read();

        Assert.Equal("system", settings.Theme);
        Assert.False(settings.StartAtLogin);
        Assert.True(settings.Tailnet);
        Assert.Null(settings.DefaultDir);
        Assert.Equal("medium", settings.AssistantEffort);
        Assert.Equal(2.0, settings.AssistantMaxBudgetUsd);
        Assert.Equal("opus", settings.AssistantModel);
    }

    [Fact]
    public void ToJson_nests_fleet_and_assistant_and_keeps_an_unset_default_dir_as_an_explicit_null()
    {
        var json = CfgdashSettings.Defaults.ToJson();

        Assert.True(json.ContainsKey("defaultDir"));
        Assert.Null(json["defaultDir"]);
        Assert.False((bool)json["fleet"]!["enabled"]!);
        Assert.Equal("sonnet", (string?)json["assistant"]!["model"]);
        Assert.Equal("medium", (string?)json["assistant"]!["effort"]);
        Assert.Equal(2.0, (double)json["assistant"]!["maxBudgetUsd"]!);
        Assert.Equal("system", (string?)json["theme"]);
    }

    [Fact]
    public void Write_keeps_unknown_keys_and_order_merges_nested_partials_and_leaves_no_temp_file()
    {
        tree.Write("cfg/config.json", """{"zeta":1,"theme":"dark","extra":{"a":1},"assistant":{"model":"opus","note":"x"}}""");

        var error = config.Write(Obj("""{"assistant":{"effort":"high"},"tailnet":true}"""), "ui");

        Assert.Null(error);
        var text = File.ReadAllText(config.FilePath);
        Assert.StartsWith("{\n  \"zeta\": 1,\n", text, StringComparison.Ordinal);
        Assert.Contains("\n    \"model\": \"opus\"", text, StringComparison.Ordinal);
        Assert.EndsWith("}\n", text, StringComparison.Ordinal);
        var root = Obj(text);
        Assert.Equal(["zeta", "theme", "extra", "assistant", "tailnet"], root.Select(p => p.Key));
        Assert.Equal(["model", "note", "effort"], root["assistant"]!.AsObject().Select(p => p.Key));
        Assert.Equal("opus", config.Read().AssistantModel);
        Assert.Equal("high", config.Read().AssistantEffort);
        Assert.Equal(["config.json"], EditFixture.Names(tree.Path("cfg")));
    }

    [Fact]
    public void Write_creates_the_file_and_never_touches_overrides_json()
    {
        tree.Write("cfg/overrides.json", "{ }");

        Assert.Null(config.Write(Obj("""{"theme":"light"}"""), "ui"));

        Assert.Equal("light", config.Read().Theme);
        Assert.Equal("{ }", File.ReadAllText(config.OverridesPath));
    }

    [Fact]
    public void Write_refuses_an_unparseable_file_and_keeps_its_bytes_and_writes_no_audit_line()
    {
        var path = tree.Write("cfg/config.json", "{ not json");
        var before = File.ReadAllBytes(path);

        var error = config.Write(Obj("""{"theme":"light"}"""), "ui");

        Assert.Equal("parse-error", error);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(AuditPath));
    }

    [Fact]
    public void Write_appends_one_audit_line_with_hashes_and_key_names_but_no_values()
    {
        Assert.Null(config.Write(Obj("""{"assistant":{"model":"canary-model-x"}}"""), "ui"));
        var firstHash = EditFixture.Sha256File(config.FilePath);
        Assert.Null(config.Write(Obj("""{"theme":"dark"}"""), "ui"));

        var lines = File.ReadAllLines(AuditPath);
        Assert.Equal(2, lines.Length);
        var first = Obj(lines[0]);
        var second = Obj(lines[1]);
        Assert.Equal("cfgdash-settings", (string?)first["kind"]);
        Assert.Equal("ui", (string?)first["listener"]);
        Assert.Null(first["files"]![0]!["sha256Before"]);
        Assert.Equal(firstHash, (string?)first["files"]![0]!["sha256After"]);
        Assert.Equal(firstHash, (string?)second["files"]![0]!["sha256Before"]);
        Assert.Equal(EditFixture.Sha256File(config.FilePath), (string?)second["files"]![0]!["sha256After"]);
        var change = first["changes"]!.AsArray().Single()!;
        Assert.Equal(("assistant.model", "cfgdash", "set"), ((string?)change["key"], (string?)change["scope"], (string?)change["action"]));
        Assert.DoesNotContain("canary-model-x", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("dark", lines[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"theme":"dark"}""", null, null)]
    [InlineData("""{"assistant":{"maxBudgetUsd":0.1}}""", null, null)]
    [InlineData("""{"assistant":{"maxBudgetUsd":50}}""", null, null)]
    [InlineData("""{"defaultDir":null}""", null, null)]
    [InlineData("""{"assistant":{"model":"claude-x.1:beta"},"fleet":{"enabled":true}}""", null, null)]
    [InlineData("""{"nope":1}""", "nope", "unknown-key")]
    [InlineData("""{"assistant":{"nope":1}}""", "assistant.nope", "unknown-key")]
    [InlineData("""{"fleet":{"enabled":"yes"}}""", "fleet.enabled", "bad-type")]
    [InlineData("""{"fleet":true}""", "fleet", "bad-type")]
    [InlineData("""{"theme":3}""", "theme", "bad-type")]
    [InlineData("""{"theme":"purple"}""", "theme", "bad-value")]
    [InlineData("""{"assistant":{"effort":"extreme"}}""", "assistant.effort", "bad-value")]
    [InlineData("""{"assistant":{"model":"-bad"}}""", "assistant.model", "bad-value")]
    [InlineData("""{"assistant":{"model":"a b"}}""", "assistant.model", "bad-value")]
    [InlineData("""{"assistant":{"maxBudgetUsd":0.09}}""", "assistant.maxBudgetUsd", "bad-value")]
    [InlineData("""{"assistant":{"maxBudgetUsd":50.01}}""", "assistant.maxBudgetUsd", "bad-value")]
    [InlineData("""{"assistant":{"maxBudgetUsd":"2"}}""", "assistant.maxBudgetUsd", "bad-type")]
    [InlineData("""{"startAtLogin":1}""", "startAtLogin", "bad-type")]
    [InlineData("""{"defaultDir":7}""", "defaultDir", "bad-type")]
    [InlineData("""{"defaultDir":"relative/dir"}""", "defaultDir", "bad-value")]
    [InlineData("{}", null, "bad-request")]
    [InlineData("[]", null, "bad-request")]
    public void Validate_accepts_documented_partials_and_names_the_dotted_key_and_reason_of_a_problem(string json, string? key, string? reason)
    {
        var result = config.Validate(JsonNode.Parse(json));

        Assert.Equal(reason, result?.Reason);
        Assert.Equal(key, result?.Key);
    }

    [Fact]
    public void Validate_requires_an_existing_default_dir_and_accepts_one_that_exists()
    {
        var existing = tree.Dir("work");

        Assert.Null(config.Validate(new JsonObject { ["defaultDir"] = existing }));
        Assert.Equal(("defaultDir", "bad-value"), config.Validate(new JsonObject { ["defaultDir"] = Path.Combine(tree.Root, "missing") }));
    }

    [Fact]
    public void Validate_rejects_a_missing_body()
    {
        Assert.Equal((null, "bad-request"), config.Validate(null));
    }
}
