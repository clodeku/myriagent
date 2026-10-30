using System.Security.Cryptography;

namespace Myriagent.Security;

/// <summary>
/// 裝置金鑰與伺服器身分金鑰（ECDSA P-256）的公鑰與簽章規則。公鑰以 SPKI（DER）的 base64url 傳遞；
/// 簽章是 IEEE P1363 的 64 位元組（r‖s）——CNG 的 ECDsa 預設就是這個格式（不是 DER）。
/// </summary>
internal static class P256KeyRules
{
    public const string CurveOid = "1.2.840.10045.3.1.7";

    public const int SignatureBytes = 64;

    public const int MaxSpkiBytes = 512;

    /// <summary>匯入 SPKI：必須剛好是一個 P-256 公鑰（多餘的位元組、其他曲線、RSA 都不收）</summary>
    public static bool TryImport(string? spki, out ECDsa? key)
    {
        key = null;
        if (!Base64UrlRules.TryDecode(spki, out var bytes, MaxSpkiBytes))
            return false;
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(bytes, out var read);
            var curve = ecdsa.ExportParameters(false).Curve;
            if (read != bytes.Length || ecdsa.KeySize != 256 || !curve.IsNamed || curve.Oid.Value != CurveOid)
            {
                ecdsa.Dispose();
                return false;
            }

            key = ecdsa;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
        {
            // 曲線外的點：Windows CNG 丟 PlatformNotSupportedException
            ecdsa.Dispose();
            return false;
        }
    }

    public static bool IsValidSpki(string? spki)
    {
        if (!TryImport(spki, out var key))
            return false;
        key!.Dispose();
        return true;
    }

    /// <summary>以 SPKI 公鑰驗 P1363 簽章（格式錯誤一律回 false）</summary>
    public static bool Verify(string? spki, ReadOnlySpan<byte> data, string? signature)
    {
        if (!Base64UrlRules.TryDecode(signature, out var sig, SignatureBytes) || sig.Length != SignatureBytes || !TryImport(spki, out var key))
            return false;
        using (key)
        {
            return key!.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }

    /// <summary>
    /// 以 SPKI 公鑰驗「SHA-256 雜湊」的 P1363 簽章：給大內容（內容另外以串流算好雜湊，不必整份放進記憶體）。
    /// 對同一份內容與 <see cref="Verify"/> 等價——<paramref name="sha256"/> 是 SHA-256(data) 時，<see cref="Sign"/>(data) 的簽章兩邊都驗得過。
    /// 雜湊不是 32 位元組、格式錯誤一律回 false
    /// </summary>
    public static bool VerifyHash(string? spki, ReadOnlySpan<byte> sha256, string? signature)
    {
        if (sha256.Length != SHA256.HashSizeInBytes)
            return false;
        if (!Base64UrlRules.TryDecode(signature, out var sig, SignatureBytes) || sig.Length != SignatureBytes || !TryImport(spki, out var key))
            return false;
        using (key)
        {
            return key!.VerifyHash(sha256, sig, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }

    /// <summary>以私鑰簽（伺服器身分金鑰、測試；桌面的 CNG 裝置金鑰同樣輸出 P1363）</summary>
    public static string Sign(ECDsa key, ReadOnlySpan<byte> data) =>
        Base64UrlRules.Encode(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static string ExportSpki(ECDsa key) => Base64UrlRules.Encode(key.ExportSubjectPublicKeyInfo());

    /// <summary>裝置金鑰的指紋（8 字）</summary>
    public static string Fingerprint(string spki) =>
        Base64UrlRules.TryDecode(spki, out var bytes, MaxSpkiBytes) ? FingerprintRules.Of(bytes) : throw new ArgumentException("SPKI 不是 base64url", nameof(spki));

    /// <summary>伺服器身分金鑰的指紋（16 字，本機確認新身分時與伺服器主控台比對）</summary>
    public static string AnchorFingerprint(string spki) =>
        Base64UrlRules.TryDecode(spki, out var bytes, MaxSpkiBytes) ? FingerprintRules.Anchor(bytes) : throw new ArgumentException("SPKI 不是 base64url", nameof(spki));
}
