using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MyriagentHub.Hosting;
using MyriagentHub.Security;

namespace MyriagentHub.Tls;

/// <summary>
/// TLS 憑證的存放（9.6）。SChannel 要寫入磁碟的私鑰（暫時金鑰交握會失敗 0x8009030E），所以：
/// <list type="bullet">
/// <item>store（正式）：每張憑證只匯入一次到服務身分的 CurrentUser\My（PersistKeySet），之後每次啟動以指紋載入，
/// 不會像原型那樣每次啟動都留下一個孤兒金鑰檔；換新後 24 小時刪舊憑證與它的私鑰。</item>
/// <item>file（開發與測試）：PFX 以 DPAPI 加密存在 secrets\tls\，每次啟動匯入記憶體（Dispose 時刪暫存金鑰），不碰使用者的憑證存放區。</item>
/// </list>
/// </summary>
internal interface ICertificateStorage
{
    string Kind { get; }

    /// <summary>依指紋載入（找不到、沒有私鑰回 null）</summary>
    X509Certificate2? Load(string thumbprint);

    /// <summary>保存並回傳可給 SChannel 用、帶私鑰的憑證</summary>
    X509Certificate2 Import(byte[] pfx);

    /// <summary>刪除憑證與它的私鑰（不存在就算了）</summary>
    void Remove(string thumbprint);
}

internal sealed class WindowsStoreCertificateStorage(ILogger<WindowsStoreCertificateStorage> log) : ICertificateStorage
{
    public string Kind => "store";

    public X509Certificate2? Load(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        var match = found.FirstOrDefault(c => c.HasPrivateKey);
        foreach (var other in found.Where(c => !ReferenceEquals(c, match)))
            other.Dispose();
        return match;
    }

    public X509Certificate2 Import(byte[] pfx)
    {
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
        log.LogInformation("TLS 憑證已匯入 CurrentUser\\My：{Thumbprint}（{Name}，到期 {NotAfter:O}）", certificate.Thumbprint, TlsCertificates.DisplayName(certificate), certificate.NotAfter);
        return certificate;
    }

    public void Remove(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
        {
            try
            {
                using (var ecdsa = certificate.GetECDsaPrivateKey())
                {
                    if (ecdsa is ECDsaCng cng)
                        cng.Key.Delete();
                }
                using (var rsa = certificate.GetRSAPrivateKey())
                {
                    if (rsa is RSACng cng)
                        cng.Key.Delete();
                }
            }
            catch (CryptographicException ex)
            {
                log.LogWarning(ex, "舊憑證的私鑰刪不掉：{Thumbprint}", thumbprint);
            }
            store.Remove(certificate);
            certificate.Dispose();
            log.LogInformation("已刪除舊 TLS 憑證與私鑰：{Thumbprint}", thumbprint);
        }
    }
}

internal sealed class FileCertificateStorage(HubPaths paths, ILogger<FileCertificateStorage> log) : ICertificateStorage
{
    private const string Purpose = "tls-pfx";

    public string Kind => "file";

    private string Dir => Path.Combine(paths.SecretsDir, "tls");

    public X509Certificate2? Load(string thumbprint)
    {
        var path = FileFor(thumbprint);
        if (!File.Exists(path))
            return null;
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12(SecretFiles.Unprotect(File.ReadAllBytes(path), Purpose), null);
            if (certificate.HasPrivateKey && string.Equals(certificate.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                return certificate;
            certificate.Dispose();
        }
        catch (CryptographicException ex)
        {
            log.LogWarning(ex, "TLS 憑證檔解不開（換機或金鑰遺失）：{Path}", path);
        }
        return null;
    }

    public X509Certificate2 Import(byte[] pfx)
    {
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, null);
        Directory.CreateDirectory(Dir);
        SecretFiles.WriteProtected(FileFor(certificate.Thumbprint), pfx, Purpose);
        return certificate;
    }

    public void Remove(string thumbprint)
    {
        var path = FileFor(thumbprint);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string FileFor(string thumbprint) => Path.Combine(Dir, thumbprint.ToUpperInvariant() + ".pfx.dpapi");
}

/// <summary>憑證工具</summary>
internal static class TlsCertificates
{
    /// <summary>自簽憑證（還沒有正式憑證時先用）：ECDSA P-256、SAN＝網域＋127.0.0.1＋::1、伺服器驗證用途，回傳 PFX</summary>
    public static byte[] CreateSelfSignedPfx(string dnsName, TimeSpan lifetime, DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        var notBefore = now.AddMinutes(-5);
        using var certificate = request.CreateSelfSigned(notBefore, notBefore + lifetime);
        return certificate.Export(X509ContentType.Pkcs12);
    }

    /// <summary>
    /// 憑證的顯示名稱：主體的 CN；沒有 CN（Let's Encrypt 的短效憑證與 Pebble 簽的憑證主體是空的，名稱只在 SAN）時
    /// 用 SAN 的 DNS 名稱；都沒有時用 SAN 的 IP，最後才是原樣的主體（可能是空字串）。
    /// </summary>
    public static string DisplayName(X509Certificate2 certificate)
    {
        if (CommonName(certificate.SubjectName) is { Length: > 0 } cn)
            return cn;
        var dns = DnsNames(certificate);
        if (dns.Count > 0)
            return string.Join(", ", dns);
        var ips = SubjectAlternativeNames(certificate).SelectMany(e => e.EnumerateIPAddresses()).Select(ip => ip.ToString()).ToList();
        return ips.Count > 0 ? string.Join(", ", ips) : certificate.Subject;
    }

    /// <summary>主體裡的 CN（OID 2.5.4.3；多值的 RDN 不算）；沒有回 null</summary>
    public static string? CommonName(X500DistinguishedName name)
    {
        foreach (var rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (!rdn.HasMultipleElements && rdn.GetSingleElementType().Value == "2.5.4.3")
                return rdn.GetSingleElementValue();
        }
        return null;
    }

    /// <summary>SAN 裡的 DNS 名稱</summary>
    public static IReadOnlyList<string> DnsNames(X509Certificate2 certificate) =>
        SubjectAlternativeNames(certificate).SelectMany(e => e.EnumerateDnsNames()).ToList();

    private static IEnumerable<X509SubjectAlternativeNameExtension> SubjectAlternativeNames(X509Certificate2 certificate) =>
        certificate.Extensions
            .Where(e => e.Oid?.Value == "2.5.29.17")
            .Select(e => e as X509SubjectAlternativeNameExtension ?? new X509SubjectAlternativeNameExtension(e.RawData, e.Critical));

    /// <summary>PEM 裡的所有憑證（中繼憑證鏈）</summary>
    public static X509Certificate2Collection ParseChain(string? pem)
    {
        var collection = new X509Certificate2Collection();
        if (!string.IsNullOrWhiteSpace(pem))
            collection.ImportFromPem(pem);
        return collection;
    }
}
