using System.Text.Json.Nodes;
using Cfgdash.Core.Collectors;
using Cfgdash.Core.Environment;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Settings;
using Cfgdash.Core.Snapshot;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class EnvHarnessCollectorTests(TempTree tree) : IClassFixture<TempTree>
{
    private static readonly string Secret = "sk-" + "ant-" + new string('b', 30);

    private async Task<CollectorResult> CollectAsync(OsFamily os, FakeEnvironmentReader reader)
    {
        var config = tree.Dir("config");
        tree.Write("config/settings.json", """{ "env": { "OTEL_SERVICE_NAME": "svc" } }""");
        var collector = new EnvHarnessCollector(new SettingsLayers(new PlatformLocations(null, null, _ => null)), reader);
        return await collector.CollectAsync(TestContexts.Create(TestContexts.WithConfigDir(tree, config, os), tree.Dir("proj")), CancellationToken.None);
    }

    private static TableBlock Variables(CollectorResult result) => result.Blocks.OfType<TableBlock>().Single(b => b.Id == "variables");

    private static string? Cell(TableBlock table, string name, string column) =>
        table.Rows.Single(r => r.Id == name).Cells.GetValueOrDefault(column)?.GetValue<string>();

    [Fact]
    public async Task Presence_only_credentials_show_set_and_never_their_value()
    {
        var reader = new FakeEnvironmentReader();
        reader.Process["ANTHROPIC_API_KEY"] = Secret;
        reader.User["CLAUDE_CODE_OAUTH_TOKEN"] = Secret;

        var table = Variables(await CollectAsync(OsFamily.Windows, reader));

        Assert.Equal("set", Cell(table, "ANTHROPIC_API_KEY", "process"));
        Assert.Equal("set", Cell(table, "CLAUDE_CODE_OAUTH_TOKEN", "user"));
        Assert.Null(Cell(table, "ANTHROPIC_AUTH_TOKEN", "process"));
        Assert.DoesNotContain(table.Rows.SelectMany(r => r.Cells.Values), c => c?.ToJsonString().Contains(Secret, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Harness_values_are_shown_redacted_and_runtime_names_are_never_listed()
    {
        var reader = new FakeEnvironmentReader();
        reader.Process["CLAUDE_CODE_SUBAGENT_MODEL"] = "sonnet";
        reader.Process["OTEL_EXPORTER_OTLP_HEADERS"] = "x-team=blue";
        reader.Process["ANTHROPIC_BASE_URL"] = "https://proxy.example/?q=" + Secret;
        reader.Process["PATH"] = "/usr/bin";

        var result = await CollectAsync(OsFamily.Windows, reader);
        var table = Variables(result);

        Assert.Equal("sonnet", Cell(table, "CLAUDE_CODE_SUBAGENT_MODEL", "process"));
        Assert.Equal("[redacted]", Cell(table, "OTEL_EXPORTER_OTLP_HEADERS", "process"));
        Assert.Equal("https://proxy.example/?q=[redacted]", Cell(table, "ANTHROPIC_BASE_URL", "process"));
        Assert.Equal([.. HarnessEnvAllowlist.Harness, .. HarnessEnvAllowlist.PresenceOnly], table.Rows.Select(r => r.Id));
        Assert.DoesNotContain(table.Rows, r => HarnessEnvAllowlist.Runtime.Contains(r.Id));
        Assert.Contains("Runtime probe variables are never displayed", result.Notes!);
    }

    [Fact]
    public async Task User_and_machine_columns_exist_only_on_windows()
    {
        var linux = Variables(await CollectAsync(OsFamily.Linux, new FakeEnvironmentReader()));
        var windows = Variables(await CollectAsync(OsFamily.Windows, new FakeEnvironmentReader()));

        Assert.Equal(["name", "kind", "process"], linux.Columns.Select(c => c.Key));
        Assert.Equal(["name", "kind", "process", "user", "machine"], windows.Columns.Select(c => c.Key));
    }

    [Fact]
    public async Task Settings_env_rows_come_from_the_merged_settings_with_their_source()
    {
        var result = await CollectAsync(OsFamily.Linux, new FakeEnvironmentReader());

        var row = Assert.Single(result.Blocks.OfType<KvBlock>().Single(b => b.Id == "settings-env").Rows);
        Assert.Equal(("env.OTEL_SERVICE_NAME", "svc", "user"), (row.Key, row.Value!.GetValue<string>(), row.Source!.Label));
    }
}
