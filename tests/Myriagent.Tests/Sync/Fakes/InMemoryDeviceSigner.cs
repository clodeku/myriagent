using System.Security.Cryptography;
using Myriagent.Security;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>測試用的裝置金鑰（記憶體裡的 P-256，KeyStorage＝software）：不碰 CNG 的金鑰存放區</summary>
internal sealed class InMemoryDeviceSigner : IDeviceSigner
{
    private readonly ECDsa key;

    public InMemoryDeviceSigner()
        : this(ECDsa.Create(ECCurve.NamedCurves.nistP256))
    {
    }

    private InMemoryDeviceSigner(ECDsa key)
    {
        this.key = key;
        PublicSpki = P256KeyRules.ExportSpki(key);
    }

    public string PublicSpki { get; }

    public string KeyStorage => PairingRules.KeyStorageSoftware;

    public bool IsDisposed { get; private set; }

    /// <summary>同一把私鑰的另一個實例（引擎停止時會 Dispose 它拿到的簽章者，測試要讓下一個引擎沿用同一把）</summary>
    public InMemoryDeviceSigner Clone()
    {
        var copy = ECDsa.Create(key.ExportParameters(includePrivateParameters: true));
        return new InMemoryDeviceSigner(copy);
    }

    /// <summary>私鑰的 PKCS#8（base64）：交給子程序探針用同一把金鑰</summary>
    public string ExportPkcs8() => Convert.ToBase64String(key.ExportPkcs8PrivateKey());

    public static InMemoryDeviceSigner FromPkcs8(string base64)
    {
        var imported = ECDsa.Create();
        imported.ImportPkcs8PrivateKey(Convert.FromBase64String(base64), out _);
        return new InMemoryDeviceSigner(imported);
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
