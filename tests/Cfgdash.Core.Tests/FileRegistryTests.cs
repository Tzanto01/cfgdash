using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Cfgdash.Core.Files;
using Cfgdash.Core.Tests.Support;

namespace Cfgdash.Core.Tests;

public sealed class FileRegistryTests(TempTree tree) : IClassFixture<TempTree>
{
    [Fact]
    public void Id_is_the_documented_sha256_prefix_and_ignores_separator_style()
    {
        var path = tree.Write("ids/Dir/Notes.md", "x");
        var json = path.Replace('\\', '/');
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? json.ToLowerInvariant() : json)))[..16];
        var registry = new FileRegistry([]);

        var first = registry.Register(path, "other");
        var second = registry.Register(json, "other");

        Assert.Equal(expected, first!.Id);
        Assert.Equal(first.Id, second!.Id);
        Assert.Equal(json, first.Path);
        Assert.Single(registry.All);
        Assert.True(registry.TryGet(expected, out var found));
        Assert.Equal(Path.GetFullPath(path), found.FullPath);
    }

    [Fact]
    public void Id_folds_case_on_windows_only()
    {
        var path = tree.Write("ids/Case/File.md", "x");

        var same = FileRegistry.IdFor(path) == FileRegistry.IdFor(path.ToUpperInvariant());

        Assert.Equal(OperatingSystem.IsWindows(), same);
    }

    [Theory]
    [InlineData(".credentials.json")]
    [InlineData("server.pem")]
    [InlineData("tls.KEY")]
    [InlineData("cert.pfx")]
    [InlineData("cert.p12")]
    [InlineData("vault.kdbx")]
    [InlineData("id_rsa")]
    [InlineData("id_rsa.pub")]
    [InlineData("id_ecdsa")]
    [InlineData("id_ed25519")]
    [InlineData(".env")]
    [InlineData(".ENV")]
    [InlineData(".env.local")]
    [InlineData(".netrc")]
    [InlineData("_netrc")]
    [InlineData(".git-credentials")]
    [InlineData(".claude.json")]
    [InlineData(".claude.json.backup")]
    [InlineData(".ssh/config")]
    [InlineData("nested/.gnupg/pubring.kbx")]
    [InlineData(".aws/config")]
    [InlineData(".kube/config")]
    [InlineData(".config/gh/hosts.yml")]
    [InlineData(".npmrc")]
    [InlineData("credentials")]
    [InlineData("putty.ppk")]
    [InlineData("infra/prod.tfstate.backup")]
    public void Register_refuses_never_read_files(string relative)
    {
        var path = tree.Write($"never/{Guid.NewGuid():N}/{relative}", "content");
        var registry = new FileRegistry([]);

        Assert.Null(registry.Register(path, "other"));
        Assert.True(FileRegistry.IsNeverRead(path));
        Assert.Empty(registry.All);
    }

    [Fact]
    public void Register_accepts_an_ordinary_file_and_estimates_tokens()
    {
        var path = tree.Write("ordinary/settings.json", "0123456789");
        var registry = new FileRegistry([]);

        var file = registry.Register(path, "settings");

        Assert.NotNull(file);
        Assert.Equal(10, file.SizeBytes);
        Assert.Equal(3, file.Tokens);
        Assert.Equal("settings", file.Kind);
        Assert.False(file.Company);
    }

    [Fact]
    public void Register_refuses_missing_files_and_directories()
    {
        var registry = new FileRegistry([]);

        Assert.Null(registry.Register(tree.Path("missing/none.md"), "other"));
        Assert.Null(registry.Register(tree.Dir("missing/dir"), "other"));
    }

    [Fact]
    public void Register_refuses_a_file_reached_through_a_link_into_ssh()
    {
        tree.Write("linked/real/.ssh/config", "Host example");
        var target = tree.Path("linked/real/.ssh");
        var link = tree.Path("linked/alias");
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo("cmd", ["/c", "mklink", "/J", link, target]) { CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }

        var registry = new FileRegistry([]);

        Assert.Null(registry.Register(Path.Combine(link, "config"), "other"));
    }

    [WindowsFact]
    public void Register_refuses_an_alternate_data_stream_path()
    {
        var path = tree.Write("ads/notes.md", "x");

        Assert.True(FileRegistry.IsNeverRead(path + "::$DATA"));
        Assert.Null(new FileRegistry([]).Register(path + "::$DATA", "other"));
    }

    [WindowsFact]
    public void Network_paths_are_never_read()
    {
        Assert.True(FileRegistry.IsNeverRead(@"\\203.0.113.7\share\notes.md"));
        Assert.True(FileRegistry.IsNeverRead("//203.0.113.7/share/notes.md"));
        Assert.True(FileRegistry.IsNeverRead(@"\\?\UNC\203.0.113.7\share\notes.md"));
        Assert.Null(new FileRegistry([]).Register("//203.0.113.7/share/notes.md", "instruction"));
    }

    [LinuxFact]
    public void Proc_files_are_never_registered_directly_or_through_a_link()
    {
        var link = tree.Path("proc-link/notes.md");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, "/proc/self/environ");
        var registry = new FileRegistry([]);

        Assert.Null(registry.Register("/proc/self/environ", "instruction"));
        Assert.Null(registry.Register(link, "instruction"));
        Assert.Empty(registry.All);
    }

    [Fact]
    public void Company_flag_follows_a_link_into_a_company_prefix()
    {
        var companyDir = tree.Dir("company-link/acme");
        tree.Write("company-link/acme/rules.md", "x");
        var link = tree.Path("company-link/alias");
        CatalogSnapshotFixture.CreateDirectoryLink(link, companyDir);

        Assert.True(new FileRegistry([companyDir]).Register(Path.Combine(link, "rules.md"), "instruction")!.Company);
    }

    [Fact]
    public void Company_flag_comes_from_prefixes_unless_given_explicitly()
    {
        var companyDir = tree.Dir("company-root/acme");
        var inside = tree.Write("company-root/acme/rules.md", "x");
        var sibling = tree.Write("company-root/acme-other/rules.md", "x");
        var registry = new FileRegistry([companyDir.Replace('\\', '/')]);

        Assert.True(registry.Register(inside, "instruction")!.Company);
        Assert.False(registry.Register(sibling, "instruction")!.Company);
        Assert.False(new FileRegistry([companyDir]).Register(inside, "instruction", company: false)!.Company);
    }
}
