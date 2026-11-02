using System.Diagnostics;
using System.Runtime.InteropServices;
using Cfgdash.Core.Files;
using Cfgdash.Core.Infrastructure;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class WindowsPathSecurityTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathName(string longPath, [Out] char[]? shortPath, uint bufferLength);

    private static string ShortPath(string path)
    {
        var size = GetShortPathName(path, null, 0);
        var buffer = new char[size];
        var length = GetShortPathName(path, buffer, size);
        return new string(buffer, 0, (int)length);
    }

    [WindowsFact]
    public void A_credential_path_spelled_with_8_3_short_names_is_never_read()
    {
        using var tree = new TempTree();
        var config = tree.Write("a-long-directory-name/.ssh/config", "Host x");
        var shortPath = ShortPath(config);
        Assert.NotEqual(config, shortPath);
        Assert.DoesNotContain(".ssh", shortPath, StringComparison.OrdinalIgnoreCase);

        Assert.True(FileRegistry.IsNeverRead(shortPath));
    }

    [WindowsFact]
    public void A_link_to_a_unc_target_is_never_read_and_is_not_probed_over_the_network()
    {
        using var tree = new TempTree();
        var link = tree.Path("link.json");
        File.CreateSymbolicLink(link, @"\\192.0.2.1\share\x.json");
        var watch = Stopwatch.StartNew();

        var resolved = PathUtil.TryRealPath(link, out var real);
        var never = FileRegistry.IsNeverRead(link);

        watch.Stop();
        Assert.False(resolved);
        Assert.True(never);
        Assert.False(PathUtil.IsNetworkPath(real));
        Assert.True(PathUtil.PathEquals(link, real));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
    }
}
