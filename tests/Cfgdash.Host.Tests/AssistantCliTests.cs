using System.Text.Json;
using System.Text.Json.Nodes;
using Cfgdash.Core.Snapshot;

namespace Cfgdash.Host.Tests;

public sealed class AssistantCliTests
{
    private static readonly string Secret = "AKIA" + "ABCDEFGHIJKLMNOP";

    private static FakeSnapshots WithSecretSetting() => new()
    {
        Sections =
        [
            new Section("settings.effective", "settings", "Effective settings",
                [new KvBlock("effective", "Effective", [new KvRow("theme", JsonValue.Create("dark"), new SourceRef("project")), new KvRow("awsKey", JsonValue.Create(Secret), new SourceRef("user"))])]),
        ],
    };

    [Fact]
    public async Task Context_json_lists_the_six_sections_in_order_with_secret_looking_values_redacted()
    {
        using var env = new CliEnv(snapshots: WithSecretSetting());

        var exit = await env.RunAsync("context", "--json", "--dir", env.Dir);

        Assert.Equal(0, exit);
        var report = JsonDocument.Parse(env.Out.Text).RootElement;
        Assert.Equal(
            ["instructions", "settings", "agents", "skills", "hooks", "mcp"],
            report.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("id").GetString()));
        Assert.DoesNotContain(Secret, env.Out.Text);
        var settings = report.GetProperty("sections")[1].GetProperty("items").EnumerateArray().ToList();
        Assert.Contains(settings, i => i.GetProperty("label").GetString() == "theme" && i.GetProperty("value").GetString() == "dark");
        Assert.Contains(settings, i => i.GetProperty("label").GetString() == "awsKey" && i.GetProperty("value").GetString() == "[redacted]");
    }

    [Fact]
    public async Task Context_text_mode_prints_the_report_without_the_secret()
    {
        using var env = new CliEnv(snapshots: WithSecretSetting());

        var exit = await env.RunAsync("context", "--dir", env.Dir);

        Assert.Equal(0, exit);
        Assert.Contains("theme", env.Out.Text);
        Assert.Contains("dark", env.Out.Text);
        Assert.DoesNotContain(Secret, env.Out.Text);
    }

    [Fact]
    public async Task Mcp_without_a_port_fails_with_exit_1()
    {
        using var env = new CliEnv();

        var exit = await env.RunAsync("mcp");

        Assert.Equal(1, exit);
        Assert.NotEmpty(env.Error.Text);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("abc")]
    public async Task Mcp_rejects_an_invalid_port_with_exit_1_and_reads_nothing(string port)
    {
        using var env = new CliEnv();

        var exit = await env.RunAsync("mcp", "--port", port);

        Assert.Equal(1, exit);
        Assert.NotEmpty(env.Error.Text);
        Assert.Empty(env.Out.Text);
    }
}
