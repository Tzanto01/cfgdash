using System.Text.Json;

namespace Cfgdash.Host.Tests;

public sealed class AssistantConfigTests
{
    private static Task<AssistantHost> StartWithConfigAsync() => AssistantHost.StartAsync(new AssistantHostOptions
    {
        Prepare = paths =>
        {
            var configDir = Directory.CreateDirectory(Path.Combine(paths.Root, "config")).FullName;
            File.WriteAllText(Path.Combine(configDir, "config.json"), """{ "assistant": { "model": "opus", "effort": "high", "maxBudgetUsd": 3.5 } }""");
        },
    });

    [LinuxFact]
    public async Task A_session_without_model_or_effort_takes_model_effort_and_budget_from_config_json()
    {
        await using var host = await StartWithConfigAsync();
        host.Claude.Configure();

        await host.CreateSessionAsync();
        var argv = (await host.Claude.WaitRunAsync(1)).Argv;

        Assert.Contains("--model=opus", argv);
        Assert.Contains("--effort=high", argv);
        Assert.Contains("--max-budget-usd=3.5", argv);
    }

    [LinuxFact]
    public async Task Request_values_override_config_json_but_the_budget_stays_from_config()
    {
        await using var host = await StartWithConfigAsync();
        host.Claude.Configure();

        await host.CreateSessionAsync(body: JsonSerializer.Serialize(new { dir = host.Project, model = "haiku", effort = "low" }));
        var argv = (await host.Claude.WaitRunAsync(1)).Argv;

        Assert.Contains("--model=haiku", argv);
        Assert.Contains("--effort=low", argv);
        Assert.Contains("--max-budget-usd=3.5", argv);
    }
}
