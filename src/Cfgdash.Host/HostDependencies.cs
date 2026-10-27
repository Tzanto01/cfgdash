using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Cfgdash.Core;
using Microsoft.Extensions.FileProviders;

namespace Cfgdash.Host;

/// <summary>The seams of the cfgdash executable. Tests replace members with fakes; production uses <see cref="CreateDefault"/>.</summary>
internal sealed class HostDependencies
{
    public static readonly string Version =
        typeof(HostDependencies).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";

    public Func<CancellationToken, Task<CoreServices>> CreateCore { get; init; } = ct => CfgdashCore.CreateAsync(Version, ct);

    public Func<IEnvironmentReader, string> CacheDir { get; init; } = CfgdashPaths.CacheDir;

    public TextWriter Out { get; init; } = Console.Out;

    public TextWriter Error { get; init; } = Console.Error;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public Action<string> OpenBrowser { get; init; } = OpenDefaultBrowser;

    public string? ExePath { get; init; } = Environment.ProcessPath;

    /// <summary>The UI files served at <c>/</c>; null when the UI was not embedded at build time.</summary>
    public IFileProvider? UiFiles { get; init; } = LoadEmbeddedUi();

    public static HostDependencies CreateDefault() => new();

    private static IFileProvider? LoadEmbeddedUi()
    {
        var assembly = typeof(HostDependencies).Assembly;
        try
        {
            return new ManifestEmbeddedFileProvider(assembly).GetFileInfo("wwwroot/index.html").Exists
                ? new ManifestEmbeddedFileProvider(assembly, "wwwroot")
                : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void OpenDefaultBrowser(string url)
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(url) { UseShellExecute = true }
            : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { url } };
        try
        {
            using var process = Process.Start(startInfo);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
        }
    }
}
