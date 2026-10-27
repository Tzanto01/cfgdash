namespace Cfgdash.Host.Tests;

public sealed class IdleShutdownTests
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Server_stops_with_reason_idle_after_30_minutes_without_authenticated_requests()
    {
        await using var host = await ServerHost.StartAsync();
        var stopped = host.Server.WaitForShutdownAsync();

        host.Time.Advance(20 * Minute);
        await host.GetAsync("/api/v1/health", bearer: null);
        host.Time.Advance(11 * Minute);

        Assert.Same(stopped, await Task.WhenAny(stopped, Task.Delay(10_000)));
        Assert.Equal("idle", await stopped);
    }

    [Fact]
    public async Task Authenticated_request_restarts_the_idle_window()
    {
        await using var host = await ServerHost.StartAsync();
        var stopped = host.Server.WaitForShutdownAsync();

        host.Time.Advance(20 * Minute);
        await host.GetAsync("/api/v1/health");
        host.Time.Advance(20 * Minute);
        await host.GetAsync("/api/v1/health", bearer: null);
        await Task.WhenAny(stopped, Task.Delay(300));
        var stillRunning = !stopped.IsCompleted;
        host.Time.Advance(11 * Minute);

        Assert.True(stillRunning);
        Assert.Same(stopped, await Task.WhenAny(stopped, Task.Delay(10_000)));
        Assert.Equal("idle", await stopped);
    }
}
