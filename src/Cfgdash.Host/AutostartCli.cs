using System.Text.Json.Nodes;
using Cfgdash.Core.Config;

namespace Cfgdash.Host;

internal static partial class CfgdashCli
{
    private static async Task<int> AutostartAsync(CliOptions options, HostDependencies dependencies)
    {
        var core = await dependencies.CreateCore(CancellationToken.None);
        var cacheDir = PrivateFiles.EnsureDirectory(dependencies.CacheDir(core.EnvironmentReader));
        var config = new CfgdashConfig(dependencies.ConfigDir(core.EnvironmentReader), cacheDir, dependencies.TimeProvider);
        var autostart = dependencies.Autostart(core, dependencies.ExePath);
        var state = await autostart.GetAsync(CancellationToken.None);
        if (state.Actual == "unsupported")
        {
            dependencies.Error.WriteLine($"cfgdash: {state.Detail}");
            return ExitPrerequisite;
        }

        if (options.Action == "status")
        {
            var configured = config.Read().StartAtLogin;
            dependencies.Out.WriteLine(state.Detail is not null || (state.Actual == "on") != configured ? "mismatch" : state.Actual);
            return ExitOk;
        }

        var on = options.Action == "on";
        var (status, body) = await new CfgdashSettingsService(config, autostart).UpdateAsync(new JsonObject { ["startAtLogin"] = on }, "cli", CancellationToken.None);
        if (status == 200)
        {
            dependencies.Out.WriteLine($"start at login: {(on ? "on" : "off")}");
            return ExitOk;
        }

        dependencies.Error.WriteLine($"cfgdash: {(body["reason"] ?? body["error"])?.GetValue<string>()}");
        return ExitError;
    }
}
