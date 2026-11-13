using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Security;
using Newtonsoft.Json.Linq;

namespace Myriagent.Remote;

/// <summary>
/// 遠端桌面輸入的端對端加密（docs/sync-protocol.md 14.16 的「輸入的加密」，功能 <c>deske2e1</c>；2026-11-13 擁有者決定：
/// 鍵盤輸入在看的一方與被看的那台之間加密，伺服器只轉密文）。純函式；桌面（被看的一方）、伺服器（只檢查形狀）共用，
/// 看的一方是網頁的 <c>deskseal.js</c>（同一套演算法，兩邊以同一組固定的向量測）。
/// <list type="bullet">
/// <item>金鑰協商：兩邊各一把這個工作階段才用的 ECDH P-256 金鑰。看的一方的公鑰放在 <c>deskOpen</c> 的 <c>viewerKey</c>，
/// 被看的一方的放在它送的第一個封包 <c>status</c> 的 <c>kx.pub</c>。公鑰是未壓縮的點（65 位元組）的 base64url</item>
/// <item>金鑰：HKDF-SHA256（輸入＝ECDH 的共同秘密〔X 座標 32 位元組〕、salt＝工作階段 id 的 UTF-8、
/// info＝<see cref="Info"/> 的 UTF-8＋0x00＋看的一方的公鑰＋被看的一方的公鑰），取 32 位元組當 AES-256-GCM 的金鑰</item>
/// <item>每包輸入：<c>input</c> 的 <c>e2e {n, ct}</c>。<c>n</c> 從 1 起每包加 1（收的一方只收比上一個大的：重送、亂序的丟掉）；
/// nonce＝4 個 0＋<c>n</c> 的 8 位元組大端；附加驗證資料＝「<see cref="Info"/>|工作階段 id|n」的 UTF-8；
/// 明文＝<c>{"events":[…]}</c> 的 UTF-8；<c>ct</c>＝密文接 16 位元組驗證碼的 base64url</item>
/// </list>
/// 公鑰經伺服器轉送、沒有另外簽章：擋的是伺服器看到或留下輸入的內容（轉送的程式、記錄、記憶體），
/// 不擋主動把公鑰換掉的伺服器——伺服器是擁有者自己的主機，這是這個系統的信任前提
/// </summary>
internal static class DeskSealRules
{
    public const int Version = 1;

    public const string Info = "myriagent-desk-input-v1";

    /// <summary>未壓縮的 P-256 點：0x04＋X（32）＋Y（32）</summary>
    public const int PublicKeyBytes = 65;

    public const int KeyBytes = 32;

    public const int NonceBytes = 12;

    public const int TagBytes = 16;

    /// <summary>一包密文（base64url）的上限：64 個事件、每個文字事件最多 1000 字，留足空間</summary>
    public const int MaxSealedBytes = 400 * 1024;

    /// <summary><c>deskOpen</c> 帶看的一方公鑰的欄位</summary>
    public const string ViewerKeyField = "viewerKey";

    /// <summary>合格的公鑰：正好 65 位元組、開頭 0x04 的 base64url（是不是曲線上的點在匯入時才知道）</summary>
    public static bool IsPublicKey([NotNullWhen(true)] string? publicKey)
        => Base64UrlRules.TryDecode(publicKey, out var bytes, PublicKeyBytes) && bytes.Length == PublicKeyBytes && bytes[0] == 0x04;

    /// <summary>產生這個工作階段用的金鑰</summary>
    public static ECDiffieHellman CreateKey() => ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>公鑰的線上寫法（未壓縮的點的 base64url）</summary>
    public static string ExportPublicKey(ECDiffieHellman key)
    {
        var q = key.ExportParameters(false).Q;
        var raw = new byte[PublicKeyBytes];
        raw[0] = 0x04;
        q.X!.CopyTo(raw, 1 + 32 - q.X.Length);
        q.Y!.CopyTo(raw, 1 + 64 - q.Y.Length);
        return Base64UrlRules.Encode(raw);
    }

    /// <summary>
    /// 以自己的金鑰與對方的公鑰算出這個工作階段的 AES 金鑰；對方的公鑰格式不對、不在曲線上回 null。
    /// <paramref name="viewerPublic"/>、<paramref name="targetPublic"/> 是兩邊線上的公鑰（都放進 info：兩邊算的一定是同一組）
    /// </summary>
    public static byte[]? DeriveKey(ECDiffieHellman own, string? peerPublic, string sessionId, string viewerPublic, string targetPublic)
    {
        if (!IsPublicKey(peerPublic) || !IsPublicKey(viewerPublic) || !IsPublicKey(targetPublic))
            return null;
        try
        {
            Base64UrlRules.TryDecode(peerPublic, out var raw, PublicKeyBytes);
            using var peer = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = raw[1..33], Y = raw[33..65] },
            });
            var shared = own.DeriveRawSecretAgreement(peer.PublicKey);
            try
            {
                Base64UrlRules.TryDecode(viewerPublic, out var viewer, PublicKeyBytes);
                Base64UrlRules.TryDecode(targetPublic, out var target, PublicKeyBytes);
                var prefix = Encoding.UTF8.GetBytes(Info);
                var info = new byte[prefix.Length + 1 + PublicKeyBytes * 2];
                prefix.CopyTo(info, 0);
                viewer.CopyTo(info, prefix.Length + 1);
                target.CopyTo(info, prefix.Length + 1 + PublicKeyBytes);
                return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, KeyBytes, Encoding.UTF8.GetBytes(sessionId), info);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(shared);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException or ArgumentException)
        {
            // 不在曲線上的點：Windows CNG 丟 PlatformNotSupportedException 或 CryptographicException
            return null;
        }
    }

    public static byte[] Nonce(long n)
    {
        var nonce = new byte[NonceBytes];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), n);
        return nonce;
    }

    public static byte[] AssociatedData(string sessionId, long n)
        => Encoding.UTF8.GetBytes(Info + "|" + sessionId + "|" + n.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>加密一包（桌面不當看的一方；給測試與之後的用途）：回 <c>ct</c></summary>
    public static string Seal(byte[] key, string sessionId, long n, ReadOnlySpan<byte> plaintext)
    {
        var sealedBytes = new byte[plaintext.Length + TagBytes];
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(Nonce(n), plaintext, sealedBytes.AsSpan(0, plaintext.Length), sealedBytes.AsSpan(plaintext.Length), AssociatedData(sessionId, n));
        return Base64UrlRules.Encode(sealedBytes);
    }

    /// <summary>解開一包；驗證碼不對（金鑰、工作階段、n 任何一個對不上，或內容被動過）、格式不對回 null</summary>
    public static byte[]? Open(byte[] key, string sessionId, long n, string? ct)
    {
        if (n < 1 || !Base64UrlRules.TryDecode(ct, out var sealedBytes, MaxSealedBytes) || sealedBytes.Length < TagBytes)
            return null;
        var plaintext = new byte[sealedBytes.Length - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(Nonce(n), sealedBytes.AsSpan(0, plaintext.Length), sealedBytes.AsSpan(plaintext.Length), plaintext, AssociatedData(sessionId, n));
            return plaintext;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // ---- 線上的形狀 ----

    /// <summary>被看的一方第一個封包（<c>status</c>）裡的 <c>kx</c></summary>
    public static JObject KeyExchange(string targetPublic) => new() { ["v"] = Version, ["pub"] = targetPublic };

    /// <summary><c>status</c> 的內容裡被看的一方的公鑰；沒有、版本不認得、格式不對回 null</summary>
    public static string? TargetPublic(JObject? statusBody)
        => statusBody?["kx"] is JObject kx
           && kx["v"] is JValue { Type: JTokenType.Integer, Value: long v } && v == Version
           && kx["pub"] is JValue { Type: JTokenType.String, Value: string pub } && IsPublicKey(pub)
            ? pub
            : null;

    /// <summary>
    /// <c>input</c> 的內容裡加密的那一包（<c>e2e {n, ct}</c>）。沒有 <c>e2e</c> 回 null、<paramref name="malformed"/> 是 false；
    /// 有但格式不對（n 不是正整數、ct 不是 base64url 或太大）回 null、<paramref name="malformed"/> 是 true
    /// </summary>
    public static (long N, string Ct)? SealedOf(JObject? inputBody, out bool malformed)
    {
        malformed = false;
        if (inputBody?["e2e"] is null or { Type: JTokenType.Null })
            return null;
        if (inputBody["e2e"] is JObject e
            && e["n"] is JValue { Type: JTokenType.Integer, Value: long n } && n >= 1
            && e["ct"] is JValue { Type: JTokenType.String, Value: string ct } && ct.Length >= 22 && Base64UrlRules.IsCanonical(ct, MaxSealedBytes))
            return (n, ct);
        malformed = true;
        return null;
    }
}
