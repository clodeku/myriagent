using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace MyriagentHub.Tls;

/// <summary>目前使用中的伺服器憑證（含中繼憑證的 SslStreamCertificateContext）</summary>
internal sealed class CertificateEntry
{
    public CertificateEntry(X509Certificate2 certificate, X509Certificate2Collection chain, bool selfSigned)
    {
        Certificate = certificate;
        Chain = chain;
        SelfSigned = selfSigned;
        // offline：不在交握時上網抓 AIA；建立時 .NET 會把中繼憑證寫進系統的 CA 存放區（預期的副作用，9.6）
        Context = SslStreamCertificateContext.Create(certificate, chain, offline: true);
    }

    public X509Certificate2 Certificate { get; }

    public X509Certificate2Collection Chain { get; }

    public SslStreamCertificateContext Context { get; }

    public bool SelfSigned { get; }
}

/// <summary>
/// 目前使用中的伺服器憑證。Kestrel 的 OnAuthenticate 每條新連線讀一次，所以換憑證（ACME 續期）不必重啟服務；
/// 已建立的連線（含 WebSocket）不受影響。啟動步驟（TLS）在開始聽連接埠之前放入第一張。
/// </summary>
internal sealed class CertificateHolder : IDisposable
{
    private readonly List<CertificateEntry> retired = [];
    private volatile CertificateEntry? current;
    private int swapCount;

    public CertificateEntry? Current => current;

    public SslStreamCertificateContext CurrentContext =>
        current?.Context ?? throw new InvalidOperationException("TLS 憑證尚未載入");

    public int SwapCount => swapCount;

    /// <summary>換上新憑證；舊的不立即 Dispose（可能還有交握正在使用），服務結束時一起釋放</summary>
    public CertificateEntry? Swap(X509Certificate2 certificate, X509Certificate2Collection? chain, bool selfSigned)
    {
        var previous = current;
        current = new CertificateEntry(certificate, chain ?? [], selfSigned);
        Interlocked.Increment(ref swapCount);
        if (previous is not null)
        {
            lock (retired)
                retired.Add(previous);
        }
        return previous;
    }

    public void Dispose()
    {
        lock (retired)
        {
            foreach (var entry in retired)
                entry.Certificate.Dispose();
            retired.Clear();
        }
        current?.Certificate.Dispose();
    }
}
