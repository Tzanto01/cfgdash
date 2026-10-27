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

    public Action<string, IEnvironmentReader> OpenBrowser { get; init; } = OpenDefaultBrowser;

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

    /// <summary>The absolute path of <paramref name="name"/> (<c>&lt;name&gt;.exe</c> on Windows) in the first fully qualified <c>PATH</c> entry that has it; relative entries such as <c>.</c> are skipped. Null when not found.</summary>
    internal static string? FindOnPath(string name, IEnvironmentReader environment)
    {
        var fileName = OperatingSystem.IsWindows() ? name + ".exe" : name;
        foreach (var entry in (environment.Get("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = entry.Trim('"');
            if (!Path.IsPathFullyQualified(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void OpenDefaultBrowser(string url, IEnvironmentReader environment)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo(url) { UseShellExecute = true };
        }
        else if (FindOnPath(OperatingSystem.IsMacOS() ? "open" : "xdg-open", environment) is { } opener)
        {
            startInfo = new ProcessStartInfo(opener) { ArgumentList = { url } };
        }
        else
        {
            return;
        }

        try
        {
            using var process = Process.Start(startInfo);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
        }
    }
}
