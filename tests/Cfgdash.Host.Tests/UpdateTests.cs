using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Cfgdash.Core;

namespace Cfgdash.Host.Tests;

internal sealed class FakeGh : FakeProcesses
{
    public static readonly string Asset = OperatingSystem.IsWindows() ? "cfgdash-win-x64.zip" : "cfgdash-linux-x64.tar.gz";

    public string Tag { get; set; } = "v9.9.9";

    public bool NotInstalled { get; set; }

    public bool LoggedOut { get; set; }

    public byte[] AssetBytes { get; set; } = [];

    public Func<byte[], string>? Sums { get; set; }

    public static byte[] Archive(string content)
    {
        var data = Encoding.UTF8.GetBytes(content);
        using var memory = new MemoryStream();
        if (OperatingSystem.IsWindows())
        {
            using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var entry = zip.CreateEntry("cfgdash.exe").Open();
                entry.Write(data);
            }
        }
        else
        {
            using var gzip = new GZipStream(memory, CompressionLevel.Fastest, leaveOpen: true);
            using var tar = new TarWriter(gzip, leaveOpen: true);
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "cfgdash") { DataStream = new MemoryStream(data) });
        }

        return memory.ToArray();
    }

    public bool Downloaded => Calls.Any(c => c.Args is ["release", "download", ..]);

    protected override ProcessResult Handle(RecordedCall call)
    {
        switch (call.Args)
        {
            case ["auth", "status"]:
                return NotInstalled ? throw new Win32Exception(2) : LoggedOut ? Fail("not logged in") : Ok();
            case ["release", "view", "--repo", "Tzanto01/cfgdash", "--json", "tagName"]:
                return Ok($$"""{"tagName":"{{Tag}}"}""");
            case ["release", "download", var tag, "--repo", "Tzanto01/cfgdash", "--pattern", var asset, "--pattern", "SHA256SUMS", "--dir", var dir, "--clobber"]:
                Assert.Equal(Tag, tag);
                File.WriteAllBytes(Path.Combine(dir, asset), AssetBytes);
                var sums = Sums?.Invoke(AssetBytes) ?? $"{Convert.ToHexStringLower(SHA256.HashData(AssetBytes))}  {asset}\n";
                File.WriteAllText(Path.Combine(dir, "SHA256SUMS"), sums);
                return Ok();
            default:
                throw new InvalidOperationException($"unexpected gh call: {call.Line}");
        }
    }
}

public sealed class UpdateTests
{
    private static MachineEnvironment WithGh => TestCore.Machine(tools: new Dictionary<string, string> { ["gh"] = "gh-fake" });

    [Fact]
    public async Task Update_with_no_gh_anywhere_exits_2_without_starting_any_process()
    {
        var gh = new FakeGh();
        using var env = new CliEnv(processes: gh);

        var exit = await env.RunAsync("update");

        Assert.Equal(2, exit);
        Assert.Contains("gh is not installed", env.Error.Text);
        Assert.Empty(gh.Calls);
    }

    [Fact]
    public async Task Update_when_the_known_gh_cannot_be_started_exits_2_before_asking_for_a_release()
    {
        var gh = new FakeGh { NotInstalled = true };
        using var env = new CliEnv(WithGh, processes: gh);

        var exit = await env.RunAsync("update");

        Assert.Equal(2, exit);
        Assert.Contains("gh is not installed", env.Error.Text);
        Assert.Single(gh.Calls);
    }

    [Fact]
    public async Task Update_when_gh_is_not_logged_in_exits_2_after_the_auth_check_and_downloads_nothing()
    {
        var gh = new FakeGh { LoggedOut = true };
        using var env = new CliEnv(WithGh, processes: gh);

        var exit = await env.RunAsync("update");

        Assert.Equal(2, exit);
        Assert.Contains("not logged in", env.Error.Text);
        Assert.Equal([["auth", "status"]], gh.Calls.Select(c => c.Args));
    }

    [Fact]
    public async Task Update_runs_the_gh_found_on_path_when_the_machine_has_no_known_gh()
    {
        using var bin = new TempDir();
        var ghPath = Path.Combine(bin.Root, OperatingSystem.IsWindows() ? "gh.exe" : "gh");
        File.WriteAllText(ghPath, string.Empty);
        var gh = new FakeGh { Tag = "v0.0.0-dev" };
        using var env = new CliEnv(processes: gh, variables: new Dictionary<string, string> { ["PATH"] = bin.Root });

        var exit = await env.RunAsync("update", "--check");

        Assert.Equal(0, exit);
        Assert.All(gh.Calls, c => Assert.Equal(ghPath, c.Executable));
        Assert.NotEmpty(gh.Calls);
    }

    [Theory]
    [InlineData("v9.9.9", "update available")]
    [InlineData("v0.0.0-dev", "up to date")]
    public async Task Check_reports_the_latest_release_without_downloading(string tag, string expected)
    {
        var gh = new FakeGh { Tag = tag };
        using var env = new CliEnv(WithGh, processes: gh);

        var exit = await env.RunAsync("update", "--check");

        Assert.Equal(0, exit);
        Assert.Contains(expected, env.Out.Text);
        Assert.False(gh.Downloaded);
    }

    [Fact]
    public async Task Update_when_already_current_downloads_nothing()
    {
        var gh = new FakeGh { Tag = $"v{HostDependencies.Version}" };
        using var env = new CliEnv(WithGh, processes: gh);

        var exit = await env.RunAsync("update");

        Assert.Equal(0, exit);
        Assert.False(gh.Downloaded);
        Assert.Equal("OLD", File.ReadAllText(env.Exe));
    }

    [Fact]
    public async Task Update_refuses_while_an_instance_is_running()
    {
        var gh = new FakeGh();
        using var env = new CliEnv(WithGh, processes: gh);
        env.Store.Write(env.LiveRecord(12345, "recorded-token"));

        var exit = await env.RunAsync("update");

        Assert.Equal(1, exit);
        Assert.False(gh.Downloaded);
        Assert.Equal("OLD", File.ReadAllText(env.Exe));
    }

    [Fact]
    public async Task Update_refuses_a_binary_that_is_not_named_cfgdash()
    {
        var gh = new FakeGh();
        using var env = new CliEnv(WithGh, processes: gh);
        env.Exe = Path.Combine(env.Root, "dotnet-host");
        File.WriteAllText(env.Exe, "OLD");

        var exit = await env.RunAsync("update");

        Assert.Equal(1, exit);
        Assert.False(gh.Downloaded);
        Assert.Equal("OLD", File.ReadAllText(env.Exe));
    }

    [UpdateTheory]
    [InlineData("mismatch")]
    [InlineData("no-line")]
    public async Task Update_with_a_bad_checksum_file_changes_nothing(string problem)
    {
        var gh = new FakeGh
        {
            AssetBytes = FakeGh.Archive("NEW"),
            Sums = bytes => problem == "mismatch"
                ? $"{new string('0', 64)}  {FakeGh.Asset}\n"
                : $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  some-other-asset.zip\n",
        };
        using var env = new CliEnv(WithGh, processes: gh);

        var exit = await env.RunAsync("update");

        Assert.Equal(1, exit);
        Assert.Equal("OLD", File.ReadAllText(env.Exe));
        Assert.False(Directory.Exists(Path.Combine(env.Cache, "update")));
        Assert.False(File.Exists(env.Exe + ".old"));
    }

    [UpdateFact]
    public async Task Update_replaces_the_binary_with_the_verified_release_and_cleans_up()
    {
        var gh = new FakeGh { AssetBytes = FakeGh.Archive("NEW-BINARY") };
        using var env = new CliEnv(WithGh, processes: gh);

        var exit = await env.RunAsync("update");

        Assert.Equal(0, exit);
        Assert.Contains("v9.9.9", env.Out.Text);
        Assert.Equal("NEW-BINARY", File.ReadAllText(env.Exe));
        Assert.False(Directory.Exists(Path.Combine(env.Cache, "update")));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("OLD", File.ReadAllText(env.Exe + ".old"));
        }
        else
        {
            Assert.True(File.GetUnixFileMode(env.Exe).HasFlag(UnixFileMode.UserExecute));
            Assert.Empty(Directory.GetFiles(env.Root, ".cfgdash.new-*"));
        }
    }

    [WindowsFact]
    public async Task Next_start_removes_the_old_binary_left_by_an_update()
    {
        using var env = new CliEnv();
        File.WriteAllText(env.Exe + ".old", "OLD");

        await env.RunAsync("stop");

        Assert.False(File.Exists(env.Exe + ".old"));
    }

    [Theory]
    [InlineData("0.0.0-dev", "v0.1.0")]
    [InlineData("v1.2.3-rc.1", "v1.2.3")]
    [InlineData("v1.9.0", "v1.10.0")]
    [InlineData("1.2.3-alpha", "1.2.3-beta")]
    public void Versions_order_by_number_then_prerelease(string older, string newer)
    {
        Assert.True(CfgdashCli.TryParseVersion(older, out var low));
        Assert.True(CfgdashCli.TryParseVersion(newer, out var high));

        Assert.True(CfgdashCli.CompareVersions(high, low) > 0);
        Assert.True(CfgdashCli.CompareVersions(low, high) < 0);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("v1.2.3-")]
    public void Malformed_versions_do_not_parse(string text)
    {
        Assert.False(CfgdashCli.TryParseVersion(text, out _));
    }
}

public sealed class FindOnPathTests
{
    private static readonly string FileName = OperatingSystem.IsWindows() ? "gh.exe" : "gh";

    private static FakeEnv PathVar(params string[] entries) =>
        new(new Dictionary<string, string> { ["PATH"] = string.Join(Path.PathSeparator, entries) });

    [Fact]
    public void A_fully_qualified_path_entry_with_the_tool_resolves_to_its_absolute_path()
    {
        using var temp = new TempDir();
        var bin = temp.Sub("bin");
        File.WriteAllText(Path.Combine(bin, FileName), string.Empty);

        var found = HostDependencies.FindOnPath("gh", PathVar(temp.Sub("empty"), bin));

        Assert.Equal(Path.Combine(bin, FileName), found);
    }

    [Fact]
    public void Relative_entries_are_ignored_even_when_they_contain_the_tool()
    {
        var relative = "cfgdash-path-test-" + Guid.NewGuid().ToString("N");
        var full = Path.Combine(Directory.GetCurrentDirectory(), relative);
        Directory.CreateDirectory(full);
        try
        {
            File.WriteAllText(Path.Combine(full, FileName), string.Empty);

            Assert.Null(HostDependencies.FindOnPath("gh", PathVar(relative, ".", "." + Path.DirectorySeparatorChar + relative)));
        }
        finally
        {
            Directory.Delete(full, recursive: true);
        }
    }

    [Fact]
    public void A_quoted_entry_with_spaces_is_resolved()
    {
        using var temp = new TempDir();
        var bin = temp.Sub("my tools");
        File.WriteAllText(Path.Combine(bin, FileName), string.Empty);

        var found = HostDependencies.FindOnPath("gh", PathVar($"\"{bin}\""));

        Assert.Equal(Path.Combine(bin, FileName), found);
    }

    [Fact]
    public void No_tool_on_the_path_or_no_path_at_all_resolves_to_null()
    {
        using var temp = new TempDir();

        Assert.Null(HostDependencies.FindOnPath("gh", PathVar(temp.Root)));
        Assert.Null(HostDependencies.FindOnPath("gh", new FakeEnv()));
    }
}

internal sealed class UpdateTheoryAttribute : TheoryAttribute
{
    public UpdateTheoryAttribute()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.X64
            || !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
        {
            Skip = "No release asset for this platform";
        }
    }
}
