using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using MyriagentHub.Security;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Security;

/// <summary>SEC-16（伺服器端）：祕密檔一律受保護的 DACL（不繼承、只有 Administrators／SYSTEM／服務身分）＋ DPAPI；Data Protection 金鑰以 DPAPI 加密</summary>
public sealed class SecretFileTests
{
    [Fact]
    public void 祕密檔建立時就是受保護的DACL_覆寫後仍然是()
    {
        var dir = TempDir.Create("myriagenthub-secret");
        try
        {
            var path = Path.Combine(dir, "nested", "secret.txt");
            SecretFiles.WriteAllText(path, "第一版");
            AssertRestricted(path);
            SecretFiles.WriteAllText(path, "第二版");
            AssertRestricted(path);
            File.ReadAllText(path).Should().Be("第二版");
            Directory.GetFiles(Path.GetDirectoryName(path)!).Should().ContainSingle("暫存檔改名後不留下");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public void DPAPI依用途區分_別的用途字串解不開()
    {
        var secret = Encoding.UTF8.GetBytes("-----BEGIN PRIVATE KEY-----");
        var sealedBytes = SecretFiles.Protect(secret, "acme-account");
        Encoding.UTF8.GetString(sealedBytes).Should().NotContain("PRIVATE KEY", "加密後看不到原文");
        SecretFiles.Unprotect(sealedBytes, "acme-account").Should().Equal(secret);
        var wrongPurpose = () => SecretFiles.Unprotect(sealedBytes, "tls-pfx");
        wrongPurpose.Should().Throw<CryptographicException>();
    }

    [Fact]
    public async Task Data_Protection金鑰以DPAPI加密存在資料根()
    {
        await using var owner = await OwnerHost.StartAsync();
        var keys = Directory.GetFiles(owner.Host.Paths.DataProtectionKeysDir, "*.xml");
        keys.Should().NotBeEmpty("登入之後至少產生一把金鑰");
        foreach (var key in keys)
        {
            var xml = await File.ReadAllTextAsync(key);
            xml.Should().Contain("encryptedSecret").And.Contain("DpapiXmlDecryptor");
            xml.Should().NotContain("<masterKey", "金鑰不能以明文存放");
        }
    }

    private static void AssertRestricted(string path)
    {
        var acl = new FileInfo(path).GetAccessControl();
        acl.AreAccessRulesProtected.Should().BeTrue();
        using var me = WindowsIdentity.GetCurrent();
        var allowed = new[] { "S-1-5-32-544", "S-1-5-18", me.User!.Value };
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        rules.Should().OnlyContain(r => !r.IsInherited && r.AccessControlType == AccessControlType.Allow && allowed.Contains(r.IdentityReference.Value));
        rules.Select(r => r.IdentityReference.Value).Should().Contain("S-1-5-32-544").And.Contain("S-1-5-18");
    }
}
