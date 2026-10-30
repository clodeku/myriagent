using System.Security.Cryptography.X509Certificates;

namespace MyriagentTools.AcmeE2e;

internal sealed record KeyFileEntry(string Name, DateTime CreatedLocal, long Length);

internal sealed record StoreCertificate(string Store, string Subject, string Issuer, string Thumbprint);

/// <summary>
/// 這個測試在主機上可能留下的副作用（設計 9.6）：
/// <list type="bullet">
/// <item>%APPDATA%\Microsoft\Crypto\Keys：PFX 以預設旗標匯入時寫的 CNG 金鑰檔；程序正常結束會刪，被強制結束就變孤兒。</item>
/// <item>CurrentUser\CA：<c>SslStreamCertificateContext.Create(葉, 中繼)</c> 會把中繼憑證寫進 CA 存放區
/// （LocalMachine 沒權限時退到 CurrentUser），每次執行 Pebble 都產生新的中繼 CA，所以每次都會多一張。</item>
/// </list>
/// 執行前後各拍一次快照比對；CA 存放區裡這次執行新增的 Pebble 憑證由本工具移除，金鑰檔只回報不刪（同機其他程序也會建金鑰檔）。
/// </summary>
internal static class HostSideEffects
{
    public static string KeysDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Crypto", "Keys");

    public static Dictionary<string, KeyFileEntry> SnapshotKeys()
    {
        var result = new Dictionary<string, KeyFileEntry>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(KeysDir))
            return result;
        foreach (var file in new DirectoryInfo(KeysDir).EnumerateFiles())
            result[file.Name] = new KeyFileEntry(file.Name, file.CreationTime, file.Length);
        return result;
    }

    /// <summary>之後才出現、現在還在的金鑰檔</summary>
    public static List<KeyFileEntry> NewKeyFiles(IReadOnlyDictionary<string, KeyFileEntry> before)
    {
        var now = SnapshotKeys();
        return now.Values.Where(k => !before.ContainsKey(k.Name)).OrderBy(k => k.CreatedLocal).ToList();
    }

    public static List<StoreCertificate> SnapshotStore(StoreName name, StoreLocation location)
    {
        var label = $"{location}\\{name}";
        using var store = new X509Store(name, location);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var list = new List<StoreCertificate>();
        foreach (var certificate in store.Certificates)
        {
            list.Add(new StoreCertificate(label, certificate.Subject, certificate.Issuer, certificate.Thumbprint));
            certificate.Dispose();
        }
        return list;
    }

    public static List<StoreCertificate> PebbleCertificates(IEnumerable<StoreCertificate> certificates) =>
        certificates.Where(c => c.Subject.Contains("Pebble", StringComparison.OrdinalIgnoreCase) || c.Issuer.Contains("Pebble", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>移除 CurrentUser\CA 裡這次執行才出現的 Pebble 憑證（執行前就在的不動），回傳移除的清單</summary>
    public static List<StoreCertificate> RemoveNewPebbleCertificates(IReadOnlyCollection<StoreCertificate> before)
    {
        var known = before.Select(c => c.Thumbprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = new List<StoreCertificate>();
        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
        foreach (var certificate in store.Certificates)
        {
            using (certificate)
            {
                if (known.Contains(certificate.Thumbprint))
                    continue;
                if (!certificate.Subject.Contains("Pebble", StringComparison.OrdinalIgnoreCase) && !certificate.Issuer.Contains("Pebble", StringComparison.OrdinalIgnoreCase))
                    continue;
                store.Remove(certificate);
                removed.Add(new StoreCertificate("CurrentUser\\CA", certificate.Subject, certificate.Issuer, certificate.Thumbprint));
            }
        }
        return removed;
    }
}
