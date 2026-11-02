namespace Cfgdash.Host.Tests;

public sealed class AutostartServerTests
{
    [Fact]
    public async Task Autostart_mode_never_shuts_down_for_idleness()
    {
        await using var host = await ServerHost.StartAsync(autostartMode: true);
        var stopped = host.Server.WaitForShutdownAsync();

        host.Time.Advance(TimeSpan.FromMinutes(45));
        await Task.WhenAny(stopped, Task.Delay(500));
        host.Time.Advance(TimeSpan.FromMinutes(45));
        await Task.WhenAny(stopped, Task.Delay(300));

        Assert.False(stopped.IsCompleted);
        Assert.Equal(200, (await host.GetAsync("/api/v1/health")).Status);
    }

    [Fact]
    public async Task Warm_start_builds_the_start_dir_snapshot_once_and_the_first_request_reuses_it()
    {
        await using var host = await ServerHost.StartAsync(autostartMode: true);
        Assert.Equal(0, host.Snapshots.CountFor(host.StartDir));

        host.Server.WarmStartSnapshot();

        Assert.True(await Poll.UntilAsync(() => host.Snapshots.CountFor(host.StartDir) == 1));
        Assert.Equal(200, (await host.GetAsync("/api/v1/snapshot")).Status);
        Assert.Equal(1, host.Snapshots.CountFor(host.StartDir));
    }
}
