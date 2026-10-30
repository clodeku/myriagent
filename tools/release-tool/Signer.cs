using System.Security.Cryptography;
using System.Text;
using Myriagent.Security;
using Myriagent.Update;

namespace MyriagentTools.ReleaseTool;

/// <summary>簽章的結果（輸出值交給 workflow 的下一個步驟）</summary>
internal sealed record SignOutput(string SigPath, string SigSha256, string Kid);

/// <summary>
/// 簽 manifest（規格 6.3、6.4）。私鑰從環境變數 DESKTOP_RELEASE_KEY（PKCS#8 的 base64：GitHub secret）或開發機的金鑰檔
/// （<see cref="LocalKeyFile"/>，DPAPI 加密的 PKCS#8）讀，解碼後的位元組用完就清掉；
/// 金鑰必須在正式清單（ReleaseKeys.Production）裡：還沒提交進 Production 的新金鑰在這裡就失敗——新金鑰要先把公鑰提交進 Production、
/// 部署伺服器，才能用它簽章（伺服器收件也只認 Production，U2 設計 D1）。簽完立即以同一份清單自我驗證。任何訊息都不含私鑰的內容
/// </summary>
internal static class Signer
{
    public const string KeyVariable = "DESKTOP_RELEASE_KEY";

    /// <summary>私鑰取自環境變數 <see cref="KeyVariable"/></summary>
    public static Task<(SignOutput? Output, string? Error)> SignAsync(string manifestPath, string sigPath, string? privateKeyBase64,
        IReadOnlyList<ReleaseKey> production, CancellationToken ct)
        => SignCoreAsync(manifestPath, sigPath, KeyVariable,
            key => string.IsNullOrWhiteSpace(privateKeyBase64) ? $"沒有設定環境變數 {KeyVariable}" : ImportPrivateKey(key, privateKeyBase64),
            production, ct);

    /// <summary>私鑰取自開發機的金鑰檔（keygen --dpapi-file 寫的；不讀環境變數）</summary>
    public static Task<(SignOutput? Output, string? Error)> SignWithKeyFileAsync(string manifestPath, string sigPath, string keyFile,
        IReadOnlyList<ReleaseKey> production, CancellationToken ct)
        => SignCoreAsync(manifestPath, sigPath, "金鑰檔", key => ImportKeyFile(key, keyFile), production, ct);

    /// <summary>manifest 與檔名都對了才讀私鑰（<paramref name="import"/> 匯入成功回 null）；<paramref name="source"/> 是錯誤訊息裡私鑰來源的名稱</summary>
    private static async Task<(SignOutput? Output, string? Error)> SignCoreAsync(string manifestPath, string sigPath, string source,
        Func<ECDsa, string?> import, IReadOnlyList<ReleaseKey> production, CancellationToken ct)
    {
        var manifestFull = Path.GetFullPath(manifestPath);
        if (!File.Exists(manifestFull))
            return (null, $"找不到 manifest：{manifestFull}");
        if (new FileInfo(manifestFull).Length > ReleaseManifestRules.MaxManifestBytes)
            return (null, $"manifest 超過 {ReleaseManifestRules.MaxManifestBytes} 位元組");
        var manifestBytes = await File.ReadAllBytesAsync(manifestFull, ct);
        if (!ReleaseManifestRules.TryParse(manifestBytes, out var manifest, out var parseError))
            return (null, $"manifest 不合格：{parseError}");
        var names = ReleasePackageRules.FileNames(manifest.Identity.Release);
        if (Path.GetFileName(manifestFull) != names.Manifest)
            return (null, $"manifest 的檔名應該是 {names.Manifest}");
        var sigFull = Path.GetFullPath(sigPath);
        if (Path.GetFileName(sigFull) != names.Signature)
            return (null, $"簽章檔的檔名應該是 {names.Signature}");
        using var key = ECDsa.Create();
        if (import(key) is { } importError)
            return (null, importError);
        var spki = P256KeyRules.ExportSpki(key);
        if (!P256KeyRules.IsValidSpki(spki))
            return (null, $"{source} 不是 P-256 的金鑰");
        var kid = ReleaseManifestRules.ComputeKid(spki);
        if (!production.Any(k => k.Kid == kid))
        {
            return (null, production.Count == 0
                ? "ReleaseKeys.Production 是空的：先以 tools/release/New-DesktopReleaseKey.ps1 產生正式金鑰，並把公鑰提交進 ReleaseKeys.Production"
                : $"簽章金鑰 {kid} 不在 ReleaseKeys.Production：新金鑰要先把公鑰提交進 ReleaseKeys.Production、部署伺服器，才能用它簽章");
        }

        var signature = ReleaseManifestRules.Sign(manifestBytes, key);
        var json = ReleaseManifestRules.SerializeSignature(signature);
        var bytes = new UTF8Encoding(false).GetBytes(json);
        if (bytes.Length > ReleaseManifestRules.MaxSignatureBytes)
            return (null, $"簽章檔超過 {ReleaseManifestRules.MaxSignatureBytes} 位元組");
        // 自我驗證：同一份正式清單、撤銷清單一律是空的（D1）；VerifyManifest 依序跑 signature（TryParseSignature＋VerifySignature）、manifest、policy、protocol
        var verification = ReleasePackageRules.VerifyManifest(manifestBytes, json, production, Array.Empty<string>());
        if (!verification.Ok)
            return (null, $"自我驗證失敗（{verification.Step}）：{verification.Error}");
        await File.WriteAllBytesAsync(sigFull, bytes, ct);
        return (new SignOutput(sigFull, ReleaseManifestRules.Sha256Hex(bytes), kid), null);
    }

    /// <summary>匯入 PKCS#8；錯誤訊息是固定的文字，不帶例外訊息也不帶輸入內容</summary>
    private static string? ImportPrivateKey(ECDsa key, string privateKeyBase64)
    {
        byte[] der;
        try
        {
            der = Convert.FromBase64String(privateKeyBase64);
        }
        catch (FormatException)
        {
            return $"{KeyVariable} 不是 base64（內容不顯示）";
        }
        try
        {
            key.ImportPkcs8PrivateKey(der, out var read);
            return read == der.Length ? null : $"{KeyVariable} 的 PKCS#8 後面有多餘的位元組（內容不顯示）";
        }
        catch (CryptographicException)
        {
            return $"{KeyVariable} 不是 PKCS#8 格式的 ECDSA 私鑰（內容不顯示）";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
        }
    }

    /// <summary>解開金鑰檔再匯入 PKCS#8；錯誤訊息同上，不帶內容</summary>
    private static string? ImportKeyFile(ECDsa key, string keyFile)
    {
        var (der, error) = LocalKeyFile.Read(keyFile);
        if (der == null)
            return error;
        try
        {
            key.ImportPkcs8PrivateKey(der, out var read);
            return read == der.Length ? null : "金鑰檔的 PKCS#8 後面有多餘的位元組（內容不顯示）";
        }
        catch (CryptographicException)
        {
            return "金鑰檔解開了，但不是 PKCS#8 格式的 ECDSA 私鑰（內容不顯示）";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(der);
        }
    }
}
