using System.Security.Cryptography;
using Myriagent.Security;

namespace Myriagent.Sync;

/// <summary>
/// 裝置金鑰（ECDSA P-256）：Hello／Prove 的持有證明。正式環境是 CNG 的持久金鑰（<see cref="DeviceKey"/>），
/// 測試用記憶體裡的金鑰。私鑰永不匯出，對外只有公鑰（SPKI base64url）與簽章（P1363）。
/// </summary>
internal interface IDeviceSigner : IDisposable
{
    /// <summary>公鑰的 SPKI（<see cref="P256KeyRules.ExportSpki"/>；只匯出公鑰）</summary>
    string PublicSpki { get; }

    /// <summary><c>tpm</c> 或 <c>software</c>（<see cref="PairingRules.KeyStorageTpm"/>、<see cref="PairingRules.KeyStorageSoftware"/>）</summary>
    string KeyStorage { get; }

    /// <summary>以私鑰簽（SHA-256、IEEE P1363 64 位元組，base64url）</summary>
    string Sign(ReadOnlySpan<byte> data);
}

/// <summary>
/// CNG 的持久裝置金鑰（使用者金鑰存放區）。名稱是 <c>Myriagent.Device.{26 字亂數}</c>，記在 device.json：
/// 產生金鑰時（/pair/start 之前）還沒有 deviceId（設計 14.5 第 4 列）。重新配對一律產生新金鑰——伺服器拒收配對過或撤銷過的公鑰。
/// <list type="bullet">
/// <item>先試 TPM（Microsoft Platform Crypto Provider；TPM 金鑰本來就不可匯出，不設 ExportPolicy），失敗才用軟體 KSP（ExportPolicy=None）。</item>
/// <item>建金鑰之前先寫 <c>sync\pending-key.json</c>，當機留下的孤兒金鑰由 <see cref="PairingRecovery"/> 清掉。</item>
/// </list>
/// </summary>
internal sealed class DeviceKey : IDeviceSigner
{
    public const string NamePrefix = "Myriagent.Device.";

    private readonly CngKey key;
    private readonly ECDsaCng ecdsa;
    private bool disposed;

    private DeviceKey(CngKey key, string name, string keyStorage)
    {
        this.key = key;
        Name = name;
        KeyStorage = keyStorage;
        ecdsa = new ECDsaCng(key);
        PublicSpki = P256KeyRules.ExportSpki(ecdsa);
    }

    public string Name { get; }

    public string KeyStorage { get; }

    public string PublicSpki { get; }

    /// <summary>新的金鑰名稱：前綴＋16 位元組亂數的 Crockford Base32（26 字）</summary>
    public static string NewKeyName(string prefix = NamePrefix) => prefix + CrockfordBase32.Encode(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// 建新的持久金鑰：<paramref name="tryTpm"/> 時先試 TPM，失敗才用軟體 KSP；退回時以 <paramref name="log"/> 記原因。
    /// 兩個都失敗時丟 <see cref="CryptographicException"/>
    /// </summary>
    public static DeviceKey Create(string keyName, bool tryTpm, Action<string>? log = null) =>
        Create(
            keyName,
            tryTpm ? [CngProvider.MicrosoftPlatformCryptoProvider, CngProvider.MicrosoftSoftwareKeyStorageProvider] : [CngProvider.MicrosoftSoftwareKeyStorageProvider],
            log);

    /// <summary>依序試 <paramref name="providers"/>（測試注入不存在的提供者，驗證退回軟體 KSP）</summary>
    internal static DeviceKey Create(string keyName, IReadOnlyList<CngProvider> providers, Action<string>? log)
    {
        if (string.IsNullOrWhiteSpace(keyName))
            throw new ArgumentException("金鑰名稱不可以是空的", nameof(keyName));
        CryptographicException? last = null;
        foreach (var provider in providers)
        {
            var storage = StorageOf(provider);
            try
            {
                var parameters = new CngKeyCreationParameters
                {
                    Provider = provider,
                    KeyUsage = CngKeyUsages.Signing,
                    KeyCreationOptions = CngKeyCreationOptions.None,
                };
                // TPM 金鑰本來就不可匯出，設 ExportPolicy 反而可能讓建立失敗；軟體 KSP 明確設成不可匯出
                if (storage == PairingRules.KeyStorageSoftware)
                    parameters.ExportPolicy = CngExportPolicies.None;
                var cng = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, parameters);
                try
                {
                    return new DeviceKey(cng, keyName, storage);
                }
                catch
                {
                    TryDelete(cng);
                    throw;
                }
            }
            catch (CryptographicException ex)
            {
                last = ex;
                log?.Invoke($"裝置金鑰無法建在 {provider.Provider}（{ex.Message}），改用下一個提供者");
            }
        }

        throw new CryptographicException("無法建立裝置金鑰", last);
    }

    /// <summary>
    /// 開既有的金鑰：存放位置不認得、名稱是空的、或金鑰不存在（CNG 回 NTE_BAD_KEYSET）回 null。其他錯誤（TPM 忙碌、TBS 服務還沒起來、
    /// 提供者開不了……）照樣丟 <see cref="CryptographicException"/>（HResult 是 CNG 的錯誤碼），由引擎以 <see cref="DeviceKeyErrorRules"/>
    /// 分類：暫時性的退避重試，不當成金鑰不見了
    /// </summary>
    public static DeviceKey? Open(string keyName, string keyStorage) =>
        ProviderOf(keyStorage) is { } provider ? Open(keyName, provider, keyStorage) : null;

    /// <summary>以指定的提供者開（測試注入不存在的提供者，驗證錯誤照樣丟出、不變成 null）</summary>
    internal static DeviceKey? Open(string keyName, CngProvider provider, string keyStorage)
    {
        if (string.IsNullOrWhiteSpace(keyName))
            return null;
        if (!CngKey.Exists(keyName, provider))
            return null;
        var cng = CngKey.Open(keyName, provider);
        try
        {
            return new DeviceKey(cng, keyName, keyStorage);
        }
        catch
        {
            cng.Dispose();
            throw;
        }
    }

    /// <summary>刪掉持久金鑰；刪了回 true，不存在或刪不掉回 false</summary>
    public static bool Delete(string keyName, string keyStorage)
    {
        if (ProviderOf(keyStorage) is not { } provider || string.IsNullOrWhiteSpace(keyName))
            return false;
        try
        {
            if (!CngKey.Exists(keyName, provider))
                return false;
            using var cng = CngKey.Open(keyName, provider);
            cng.Delete();
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public static bool Exists(string keyName, string keyStorage)
    {
        if (ProviderOf(keyStorage) is not { } provider || string.IsNullOrWhiteSpace(keyName))
            return false;
        try
        {
            return CngKey.Exists(keyName, provider);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    public string Sign(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return P256KeyRules.Sign(ecdsa, data);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        ecdsa.Dispose();
        key.Dispose();
    }

    private static string StorageOf(CngProvider provider) =>
        provider == CngProvider.MicrosoftPlatformCryptoProvider ? PairingRules.KeyStorageTpm : PairingRules.KeyStorageSoftware;

    private static CngProvider? ProviderOf(string? keyStorage) => keyStorage switch
    {
        PairingRules.KeyStorageTpm => CngProvider.MicrosoftPlatformCryptoProvider,
        PairingRules.KeyStorageSoftware => CngProvider.MicrosoftSoftwareKeyStorageProvider,
        _ => null,
    };

    private static void TryDelete(CngKey cng)
    {
        try
        {
            cng.Delete();
        }
        catch (CryptographicException)
        {
            cng.Dispose();
        }
    }
}
