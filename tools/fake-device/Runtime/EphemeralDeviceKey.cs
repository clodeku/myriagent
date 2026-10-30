using System.Security.Cryptography;
using Myriagent.Security;
using Myriagent.Sync;

namespace FakeDevice;

/// <summary>
/// 只在記憶體的裝置金鑰（WP4.9 步 8，<c>session --ephemeral-key</c>；作法同測試的 InMemoryDeviceSigner）：P-256、KeyStorage＝software，
/// 不碰 CNG 的使用者金鑰存放區（負載工具一次配對很多台，不在共用的金鑰存放區留下或刪除任何檔）。程序結束金鑰就沒了，所以只給
/// 「同一個程序裡配對、跑、解除配對」的 session 用。配對與每個引擎各拿一份 <see cref="Clone"/>（引擎停止時會 Dispose 它拿到的簽章者）
/// </summary>
internal sealed class EphemeralDeviceKey : IDeviceSigner
{
    private readonly ECDsa key;

    public EphemeralDeviceKey()
        : this(ECDsa.Create(ECCurve.NamedCurves.nistP256))
    {
    }

    private EphemeralDeviceKey(ECDsa key)
    {
        this.key = key;
        PublicSpki = P256KeyRules.ExportSpki(key);
    }

    public string PublicSpki { get; }

    public string KeyStorage => PairingRules.KeyStorageSoftware;

    public bool IsDisposed { get; private set; }

    /// <summary>同一把私鑰的另一個實例</summary>
    public EphemeralDeviceKey Clone()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return new EphemeralDeviceKey(ECDsa.Create(key.ExportParameters(includePrivateParameters: true)));
    }

    public string Sign(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return P256KeyRules.Sign(key, data);
    }

    public void Dispose()
    {
        IsDisposed = true;
        key.Dispose();
    }
}
