using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Cfgdash.Host.Tests;

public sealed class GeneratedFileAclTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("cfgdash-acl-").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [WindowsFact]
    [SupportedOSPlatform("windows")]
    public void Generated_settings_and_mcp_files_grant_only_the_current_user_with_inheritance_off()
    {
        var user = WindowsIdentity.GetCurrent().User!;
        foreach (var name in new[] { "settings-test-1.json", "mcp-test-1.json" })
        {
            var path = Path.Combine(dir, name);
            AssistantProcess.WriteFile(path, "{}");

            var acl = new FileInfo(path).GetAccessControl();
            var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

            Assert.True(acl.AreAccessRulesProtected);
            var rule = Assert.Single(rules);
            Assert.Equal(user, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.False(rule.IsInherited);
        }
    }
}
