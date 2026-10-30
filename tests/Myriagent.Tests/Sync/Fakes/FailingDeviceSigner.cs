using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>簽章時丟指定例外的裝置金鑰（公鑰照 <paramref name="inner"/>；TPM 在簽章時才忙碌、金鑰在開著時被刪的情境）</summary>
internal sealed class FailingDeviceSigner(IDeviceSigner inner, Exception error) : IDeviceSigner
{
    public string PublicSpki => inner.PublicSpki;

    public string KeyStorage => inner.KeyStorage;

    public int SignAttempts { get; private set; }

    public bool IsDisposed { get; private set; }

    public string Sign(ReadOnlySpan<byte> data)
    {
        SignAttempts++;
        throw error;
    }

    public void Dispose()
    {
        IsDisposed = true;
        inner.Dispose();
    }
}
