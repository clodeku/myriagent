using System.IO.Compression;
using System.Security.Cryptography;
using Myriagent.Sync;

namespace Myriagent.Update;

/// <summary>整包驗證失敗的步驟代碼：伺服器收件的回執直接沿用（只有 ASCII，U2 設計 D19）</summary>
internal static class ReleasePackageSteps
{
    public const string Signature = "signature";
    public const string Manifest = "manifest";
    public const string Policy = "policy";
    public const string Protocol = "protocol";
    public const string ZipHash = "zip_hash";
    public const string ZipEntries = "zip_entries";
}

/// <summary>
/// manifest 與簽章的驗證結果。Step 是 null 表示通過（Manifest、Signature 都有值）；
/// 失敗時 Step 是 <see cref="ReleasePackageSteps"/> 的代碼、Error 是中文原因，已經解析出來的部分照樣帶出（收件的服務記錄寫 kid、release 用）
/// </summary>
internal sealed record ManifestVerification(ReleaseManifest? Manifest, ReleaseSignature? Signature, string? Step, string? Error)
{
    public bool Ok => Step is null;
}

/// <summary>整包（簽章、manifest、zip）的驗證結果，欄位的意思同 <see cref="ManifestVerification"/></summary>
internal sealed record PackageVerification(ReleaseManifest? Manifest, ReleaseSignature? Signature, string? Step, string? Error)
{
    public bool Ok => Step is null;
}

/// <summary>
/// 發佈套件的整包驗證、發佈政策與檔名（U2 設計 T2、D11）：發佈工具 myriagent-release 的自我驗證與伺服器收件共用，U3 的桌面以 <see cref="VerifyOffer"/>
/// 沿用簽章、格式與政策的部分（不看協定版本）。
/// 順序固定：簽章（對 manifest 原文位元組，在解析之前）→ manifest 格式 → 發佈政策 → 協定版本 → zip 雜湊 → zip 逐項。
/// 前一步沒過就不做後面的：簽章不對就不解析 manifest，zip 雜湊不對就不開 zip（壓縮炸彈到不了解壓那一步）
/// </summary>
internal static class ReleasePackageRules
{
    /// <summary>D11：entry 一律是它（桌面備妥的版本缺 exe 時會一直重試）</summary>
    public const string EntryExecutable = "Myriagent.exe";

    /// <summary>D11：桌面自己寫在版本資料夾根目錄的檔（AppUpdateManager.ManifestFileName、SignatureFileName），套件不得帶同名的檔</summary>
    public static IReadOnlyList<string> ReservedRootFiles { get; } = ["release.manifest.json", "release.manifest.sig"];

    /// <summary>
    /// 套件的三個檔名（設計 6.2）：myriagent-&lt;release&gt;.zip、.manifest.json、.manifest.sig。
    /// release 必須通過 <see cref="ReleaseIdentity.TryParseRelease"/>，否則丟 <see cref="ArgumentException"/>（不讓呼叫端拿沒驗證過的字串組路徑）
    /// </summary>
    public static (string Zip, string Manifest, string Signature) FileNames(string release)
    {
        if (!ReleaseIdentity.TryParseRelease(release, out _))
            throw new ArgumentException($"不是合法的 release 名稱：{release}", nameof(release));
        var stem = $"myriagent-{release}";
        return ($"{stem}.zip", $"{stem}.manifest.json", $"{stem}.manifest.sig");
    }

    /// <summary>D11 的發佈政策：entry 必須是 <see cref="EntryExecutable"/>；清單不得含 <see cref="ReservedRootFiles"/>（只看根目錄、不分大小寫）。通過回 null，否則回原因</summary>
    public static string? CheckPublishPolicy(ReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!string.Equals(manifest.Entry, EntryExecutable, StringComparison.Ordinal))
            return $"entry 必須是 {EntryExecutable}（manifest 寫的是 {manifest.Entry}）";
        var reserved = manifest.Files.FirstOrDefault(f => ReservedRootFiles.Contains(f.Path, StringComparer.OrdinalIgnoreCase));
        return reserved == null ? null : $"清單不得含桌面保留的檔名：{reserved.Path}";
    }

    /// <summary>
    /// 桌面驗伺服器給的新版（U3 的 AppUpdateManager；PROTO-1）：依序 signature（TryParseSignature＋VerifySignature，對原文位元組）→ manifest（TryParse）→ policy。
    /// 不看 protocolVersion：新版的協定比執行中的這一版新是正常的，那是伺服器收件與發佈工具的規則（<see cref="VerifyManifest"/>）。
    /// 撤銷清單是桌面 updater-state.json 的 RevokedKids（U2 設計 D1 之後一直是空的，照樣讀它，不分大小寫比對）。
    /// 呼叫端讀檔之前要先比過大小（MaxManifestBytes、MaxSignatureBytes）
    /// </summary>
    public static ManifestVerification VerifyOffer(byte[] manifestBytes, string? signatureJson,
        IEnumerable<ReleaseKey> trusted, IReadOnlyCollection<string> revokedKids)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        ArgumentNullException.ThrowIfNull(trusted);
        ArgumentNullException.ThrowIfNull(revokedKids);
        if (!ReleaseManifestRules.TryParseSignature(signatureJson, out var signature))
            return new ManifestVerification(null, null, ReleasePackageSteps.Signature, "簽章檔不存在或格式不對");
        if (ReleaseManifestRules.VerifySignature(manifestBytes, signature, trusted, revokedKids) is { } signatureError)
            return new ManifestVerification(null, signature, ReleasePackageSteps.Signature, signatureError);
        if (!ReleaseManifestRules.TryParse(manifestBytes, out var manifest, out var parseError))
            return new ManifestVerification(null, signature, ReleasePackageSteps.Manifest, parseError);
        if (CheckPublishPolicy(manifest) is { } policyError)
            return new ManifestVerification(manifest, signature, ReleasePackageSteps.Policy, policyError);
        return new ManifestVerification(manifest, signature, null, null);
    }

    /// <summary>
    /// 伺服器收件（T4）與發佈工具（T10）驗 manifest 與簽章：<see cref="VerifyOffer"/> 通過之後再看 protocol（SyncProtocol.IsSupported）。
    /// 信任清單只有內建的 ReleaseKeys.Production，撤銷清單一律傳空的（U2 設計 D1）；參數留著是因為 U1 的 VerifySignature 本來就收它。
    /// protocol 看的是執行這段程式的這一版支援的範圍：桌面一律用 <see cref="VerifyOffer"/>，不要用這個（ReleasePackageRulesTests 掃桌面的原始碼）。
    /// 呼叫端讀檔之前要先比過大小（MaxManifestBytes、MaxSignatureBytes）
    /// </summary>
    public static ManifestVerification VerifyManifest(byte[] manifestBytes, string? signatureJson,
        IEnumerable<ReleaseKey> trusted, IReadOnlyCollection<string> revokedKids)
    {
        var offer = VerifyOffer(manifestBytes, signatureJson, trusted, revokedKids);
        if (!offer.Ok)
            return offer;
        var manifest = offer.Manifest!;
        if (!SyncProtocol.IsSupported(manifest.ProtocolVersion))
            return new ManifestVerification(manifest, offer.Signature, ReleasePackageSteps.Protocol,
                $"protocolVersion {manifest.ProtocolVersion} 不在支援範圍（{SyncProtocol.MinSupportedVersion}～{SyncProtocol.Version}）");
        return offer;
    }

    /// <summary>
    /// 驗 zip：先從頭算整檔 SHA-256 比對 manifest 的 zipSha256，相符才把位置設回開頭、開 ZipArchive 逐項驗證（<see cref="ReleaseZipRules.VerifyEntries"/>）。
    /// 通過回 null；失敗回（步驟, 原因），步驟是 zip_hash 或 zip_entries（zip 格式壞掉也算 zip_entries）。
    /// zip 必須可讀、可定位，否則丟 <see cref="ArgumentException"/>：不可定位的串流會讓 ZipArchive 把整個檔複製進記憶體。串流不會被關閉
    /// </summary>
    public static async Task<(string Step, string Error)?> VerifyZipAsync(ReleaseManifest manifest, Stream zip, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        RequireSeekable(zip);
        zip.Position = 0;
        var sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(zip, cancellationToken));
        if (!string.Equals(sha, manifest.ZipSha256, StringComparison.Ordinal))
            return (ReleasePackageSteps.ZipHash, "zip 的 SHA-256 與 manifest 不符");
        zip.Position = 0;
        try
        {
            using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
            if (ReleaseZipRules.VerifyEntries(archive, manifest, cancellationToken) is { } entriesError)
                return (ReleasePackageSteps.ZipEntries, entriesError);
            return null;
        }
        catch (InvalidDataException ex)
        {
            // 雜湊與簽過的 manifest 相符、zip 本身卻壞掉：發佈就是壞的（同桌面的 VerifyAndExtractAsync）
            return (ReleasePackageSteps.ZipEntries, $"zip 格式不對：{ex.Message}");
        }
    }

    /// <summary>整包驗證：<see cref="VerifyManifest"/> 通過才 <see cref="VerifyZipAsync"/>。zip 不可定位時一開始就丟 <see cref="ArgumentException"/></summary>
    public static async Task<PackageVerification> VerifyAsync(byte[] manifestBytes, string? signatureJson, Stream zip,
        IEnumerable<ReleaseKey> trusted, IReadOnlyCollection<string> revokedKids, CancellationToken cancellationToken)
    {
        RequireSeekable(zip);
        var checkedManifest = VerifyManifest(manifestBytes, signatureJson, trusted, revokedKids);
        if (!checkedManifest.Ok)
            return new PackageVerification(checkedManifest.Manifest, checkedManifest.Signature, checkedManifest.Step, checkedManifest.Error);
        var zipFailure = await VerifyZipAsync(checkedManifest.Manifest!, zip, cancellationToken);
        return new PackageVerification(checkedManifest.Manifest, checkedManifest.Signature, zipFailure?.Step, zipFailure?.Error);
    }

    private static void RequireSeekable(Stream zip)
    {
        ArgumentNullException.ThrowIfNull(zip);
        if (!zip.CanRead || !zip.CanSeek)
            throw new ArgumentException("zip 串流必須可讀、可定位（不可定位時 ZipArchive 會把整個檔複製進記憶體）", nameof(zip));
    }
}
