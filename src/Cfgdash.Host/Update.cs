using System.ComponentModel;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cfgdash.Core;

namespace Cfgdash.Host;

internal static partial class CfgdashCli
{
    private const string ReleaseRepo = "Tzanto01/cfgdash";
    private const UnixFileMode ExecutableMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private static readonly TimeSpan GhTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    /// <summary><c>cfgdash update [--check]</c> (api-v1: update): latest GitHub release via gh, SHA256SUMS-verified, replaces the running binary.</summary>
    private static async Task<int> UpdateAsync(CliOptions options, HostDependencies dependencies)
    {
        var core = await dependencies.CreateCore(CancellationToken.None);
        var gh = core.Environment.Tools.GetValueOrDefault("gh") ?? "gh";
        ProcessResult auth;
        try
        {
            auth = await core.Processes.RunAsync(gh, ["auth", "status"], GhTimeout, CancellationToken.None);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            dependencies.Error.WriteLine("cfgdash: gh is not installed (https://cli.github.com).");
            return ExitPrerequisite;
        }

        if (auth.TimedOut || auth.ExitCode != 0)
        {
            dependencies.Error.WriteLine("cfgdash: gh is not logged in (run gh auth login).");
            return ExitPrerequisite;
        }

        var view = await core.Processes.RunAsync(gh, ["release", "view", "--repo", ReleaseRepo, "--json", "tagName"], GhTimeout, CancellationToken.None);
        if (view.TimedOut || view.ExitCode != 0)
        {
            dependencies.Error.WriteLine($"cfgdash: no release found: {ShortLine(view.TimedOut ? "timed out" : view.StandardError)}");
            return ExitError;
        }

        var tag = ReadTag(view.StandardOutput);
        if (tag is null || !TryParseVersion(tag, out var latest) || !TryParseVersion(HostDependencies.Version, out var current))
        {
            dependencies.Error.WriteLine("cfgdash: could not read the latest release version.");
            return ExitError;
        }

        var newer = CompareVersions(latest, current) > 0;
        if (options.Check)
        {
            dependencies.Out.WriteLine($"cfgdash {HostDependencies.Version}, latest {tag}: {(newer ? "update available" : "up to date")}");
            return ExitOk;
        }

        if (!newer)
        {
            dependencies.Out.WriteLine($"cfgdash {HostDependencies.Version} is up to date (latest {tag}).");
            return ExitOk;
        }

        var cacheDir = PrivateFiles.EnsureDirectory(dependencies.CacheDir(core.EnvironmentReader));
        if (new RunStateStore(cacheDir).Read() is { } state && RunStateStore.IsLive(state))
        {
            dependencies.Error.WriteLine("cfgdash: cfgdash is running; run `cfgdash stop` first.");
            return ExitError;
        }

        var exe = dependencies.ExePath;
        var exeName = Path.GetFileName(exe);
        if (string.IsNullOrEmpty(exe)
            || !(string.Equals(exeName, "cfgdash", StringComparison.Ordinal) || string.Equals(exeName, "cfgdash.exe", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            dependencies.Error.WriteLine("cfgdash: update only works for an installed cfgdash binary.");
            return ExitError;
        }

        var asset = RuntimeInformation.OSArchitecture != Architecture.X64 ? null
            : OperatingSystem.IsWindows() ? "cfgdash-win-x64.zip"
            : OperatingSystem.IsLinux() ? "cfgdash-linux-x64.tar.gz"
            : null;
        if (asset is null)
        {
            dependencies.Error.WriteLine($"cfgdash: no release asset for {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}.");
            return ExitError;
        }

        var updateDir = Path.Combine(cacheDir, "update");
        try
        {
            DeleteDirectory(updateDir);
            PrivateFiles.EnsureDirectory(updateDir);
            var download = await core.Processes.RunAsync(gh, ["release", "download", tag, "--repo", ReleaseRepo, "--pattern", asset, "--pattern", "SHA256SUMS", "--dir", updateDir, "--clobber"], DownloadTimeout, CancellationToken.None);
            if (download.TimedOut || download.ExitCode != 0)
            {
                dependencies.Error.WriteLine($"cfgdash: download failed: {ShortLine(download.TimedOut ? "timed out" : download.StandardError)}");
                return ExitError;
            }

            var assetPath = Path.Combine(updateDir, asset);
            if (VerifyChecksum(assetPath, Path.Combine(updateDir, "SHA256SUMS"), asset) is { } checksumProblem)
            {
                dependencies.Error.WriteLine($"cfgdash: {checksumProblem}; nothing was changed.");
                return ExitError;
            }

            if (OperatingSystem.IsWindows())
            {
                var extracted = Path.Combine(updateDir, "cfgdash.exe");
                if (!ExtractZipEntry(assetPath, "cfgdash.exe", extracted))
                {
                    dependencies.Error.WriteLine($"cfgdash: {asset} has no cfgdash.exe; nothing was changed.");
                    return ExitError;
                }

                ReplaceWindows(exe, extracted);
            }
            else
            {
                var temp = Path.Combine(Path.GetDirectoryName(exe)!, $".cfgdash.new-{Guid.NewGuid():N}");
                try
                {
                    if (!ExtractTarEntry(assetPath, temp))
                    {
                        dependencies.Error.WriteLine($"cfgdash: {asset} has no cfgdash binary; nothing was changed.");
                        return ExitError;
                    }

                    File.SetUnixFileMode(temp, ExecutableMode);
                    File.Move(temp, exe, overwrite: true);
                }
                finally
                {
                    File.Delete(temp);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
        {
            dependencies.Error.WriteLine($"cfgdash: update failed ({e.GetType().Name}); {exe} was not replaced.");
            return ExitError;
        }
        finally
        {
            try
            {
                DeleteDirectory(updateDir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        dependencies.Out.WriteLine($"updated {HostDependencies.Version} -> {tag}");
        return ExitOk;
    }

    private static string? ReadTag(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("tagName", out var tag) && tag.ValueKind == JsonValueKind.String
                ? tag.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>[v]X.Y.Z[-pre][+build]</c>; exactly three numeric parts.</summary>
    internal static bool TryParseVersion(string text, out (Version Number, string? Prerelease) version)
    {
        version = default;
        var value = text.StartsWith('v') ? text[1..] : text;
        var plus = value.IndexOf('+');
        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-');
        var prerelease = dash >= 0 ? value[(dash + 1)..] : null;
        if (dash >= 0)
        {
            value = value[..dash];
        }

        if (!Version.TryParse(value, out var number) || number.Build < 0 || number.Revision >= 0 || prerelease is { Length: 0 })
        {
            return false;
        }

        version = (number, prerelease);
        return true;
    }

    /// <summary>Numeric compare; with equal numbers a prerelease is older than the release, two prereleases compare ordinally.</summary>
    internal static int CompareVersions((Version Number, string? Prerelease) left, (Version Number, string? Prerelease) right)
    {
        var numbers = left.Number.CompareTo(right.Number);
        if (numbers != 0)
        {
            return numbers;
        }

        return (left.Prerelease, right.Prerelease) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            _ => string.CompareOrdinal(left.Prerelease, right.Prerelease),
        };
    }

    /// <summary>Null when the asset's SHA-256 matches its <c>SHA256SUMS</c> line (<c>&lt;64 hex&gt;  &lt;name&gt;</c> or <c>&lt;64 hex&gt; *&lt;name&gt;</c>); otherwise the problem.</summary>
    private static string? VerifyChecksum(string assetPath, string sumsPath, string assetName)
    {
        if (!File.Exists(assetPath) || !File.Exists(sumsPath))
        {
            return "the release asset or SHA256SUMS was not downloaded";
        }

        var expected = File.ReadLines(sumsPath)
            .Select(line => ChecksumLine().Match(line))
            .FirstOrDefault(match => match.Success && string.Equals(match.Groups["name"].Value, assetName, StringComparison.Ordinal))
            ?.Groups["hash"].Value;
        if (expected is null)
        {
            return $"SHA256SUMS has no line for {assetName}";
        }

        using var stream = File.OpenRead(assetPath);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"checksum mismatch for {assetName}";
    }

    private static bool ExtractZipEntry(string zipPath, string entryName, string target)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, entryName, StringComparison.Ordinal));
        if (entry is null)
        {
            return false;
        }

        entry.ExtractToFile(target, overwrite: true);
        return true;
    }

    private static bool ExtractTarEntry(string tarGzPath, string target)
    {
        using var file = File.OpenRead(tarGzPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile && entry.Name is "cfgdash" or "./cfgdash")
            {
                entry.ExtractToFile(target, overwrite: false);
                return true;
            }
        }

        return false;
    }

    private static void ReplaceWindows(string exe, string replacement)
    {
        var old = exe + ".old";
        File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(replacement, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [GeneratedRegex("^(?<hash>[0-9A-Fa-f]{64}) [ *](?<name>.+)$")]
    private static partial Regex ChecksumLine();
}
