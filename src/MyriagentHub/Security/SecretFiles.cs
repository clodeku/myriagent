using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace MyriagentHub.Security;

/// <summary>
/// 祕密檔（伺服器身分金鑰、ACME 帳戶金鑰、憑證…）：先以「不繼承、只有 Administrators、SYSTEM 與目前程序身分」的 DACL 建立空檔再寫內容，
/// 中間沒有任何一刻是繼承上層 ACL 的；金鑰類另以 DPAPI（LocalMachine＋用途 entropy）加密（9.7、SEC-16）。
/// 換到別台電腦解不開是預期行為（重建伺服器時重新產生）。
/// </summary>
internal static class SecretFiles
{
    public static void WriteAllText(string path, string content) => WriteAllBytes(path, Encoding.UTF8.GetBytes(content));

    public static void WriteAllBytes(string path, byte[] content)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, CreateSecurity()))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            // 同一磁碟內改名保留檔案自己的（受保護的）DACL
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>受保護（不繼承）的 DACL：Administrators、SYSTEM、目前程序身分（服務是 NT SERVICE\MyriagentHub）完全控制</summary>
    public static FileSecurity CreateSecurity()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in AllowedSids())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    public static IReadOnlyList<SecurityIdentifier> AllowedSids()
    {
        var list = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            new(WellKnownSidType.LocalSystemSid, null),
        };
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is { } user && !list.Contains(user))
            list.Add(user);
        return list;
    }

    /// <summary>DPAPI LocalMachine 加密；entropy 依用途區分，同一台電腦上的其他程式拿到檔案也要知道用途字串才解得開</summary>
    public static byte[] Protect(byte[] data, string purpose) =>
        ProtectedData.Protect(data, Entropy(purpose), DataProtectionScope.LocalMachine);

    public static byte[] Unprotect(byte[] data, string purpose) =>
        ProtectedData.Unprotect(data, Entropy(purpose), DataProtectionScope.LocalMachine);

    public static void WriteProtected(string path, byte[] data, string purpose) => WriteAllBytes(path, Protect(data, purpose));

    public static byte[]? ReadProtected(string path, string purpose) =>
        File.Exists(path) ? Unprotect(File.ReadAllBytes(path), purpose) : null;

    // 改名（MyriagentHub → MyriagentHub，2026-10-30）之前的值：伺服器身分金鑰、TLS 憑證、ACME 帳戶金鑰都以它加密，改了就解不開（裝置要全部重新配對）
    private static byte[] Entropy(string purpose) => Encoding.UTF8.GetBytes("MyriagentHub/" + purpose);
}
