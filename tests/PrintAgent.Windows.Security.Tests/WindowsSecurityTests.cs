using System.Security.AccessControl;
using System.Security.Principal;
using PrintAgent.Common.Helpers;
using PrintAgent.UI.Services;
using Xunit;

namespace PrintAgent.Windows.Security.Tests;

public class WindowsSecurityTests
{
    [Theory]
    [InlineData("Other Product", "2.0.0", "2.0.0")]
    [InlineData("NuxPrint Agent", "1.9.4", "2.0.0")]
    [InlineData("NuxPrint Agent", "2.0.0", "3.0.0")]
    [InlineData("NuxPrint Agent", "invalid", "2.0.0")]
    public void Signed_wrong_product_or_version_cannot_be_used_as_an_update(string product, string signed, string requested) =>
        Assert.Throws<IOException>(() => SecureUpdater.ValidateSignedMetadata(product, signed, requested));

    [Fact]
    public void Matching_signed_product_and_version_are_accepted() => SecureUpdater.ValidateSignedMetadata("NuxPrint Agent", "2.0.0.0", "2.0.0");

    [Theory]
    [InlineData("http://nuxprint.com/setup.exe", "2.0.0")]
    [InlineData("https://evil.example/setup.exe", "2.0.0")]
    [InlineData("https://nuxprint.com@evil.example/setup.exe", "2.0.0")]
    [InlineData("https://nuxprint.com:444/setup.exe", "2.0.0")]
    [InlineData("https://nuxprint.com/setup.exe", "1.9.4")]
    [InlineData("https://nuxprint.com/setup.exe", "1.9.5 & calc")]
    public void Unsafe_update_metadata_is_rejected(string url, string version) => Assert.Throws<ArgumentException>(() => SecureUpdater.ValidateTarget(url, version));

    [Fact]
    public void Trusted_https_origin_and_new_version_are_accepted() => Assert.Equal("nuxprint.com", SecureUpdater.ValidateTarget("https://nuxprint.com/setup.exe", "2.0.0").Host);

    [Fact]
    public void User_writable_helper_is_never_trusted() => Assert.Throws<IOException>(() => ExecutableTrust.RequireInstalledPath(Path.Combine(Path.GetTempPath(), "SumatraPDF.exe")));

    [Fact]
    public void User_data_directory_has_only_current_user_and_system_permissions()
    {
        var path = Path.Combine(Path.GetTempPath(), "np-acl-" + Guid.NewGuid().ToString("N"));
        try
        {
            UserDataDirectory.Ensure(path);
            var acl = new DirectoryInfo(path).GetAccessControl(); Assert.True(acl.AreAccessRulesProtected);
            var current = WindowsIdentity.GetCurrent().User!.Value;
            var allowed = new[] { current, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value };
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            Assert.Equal(2, rules.Length);
            Assert.All(rules, rule => Assert.Contains(rule.IdentityReference.Value, allowed));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path); }
    }
}
