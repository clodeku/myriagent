using System.Security.Cryptography;
using System.Text;
using Myriagent.Remote;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.RemoteDesk;

/// <summary>
/// 被看的一方這個工作階段的輸入解密（<see cref="DeskSealRules"/>，docs/sync-protocol.md 14.16）。看的一方在 <c>deskOpen</c> 給了公鑰時建立：
/// 產生自己的金鑰、算出這個工作階段的 AES 金鑰（私鑰算完就丟），公鑰由工作階段放在第一個封包回給看的一方。
/// 每包的編號只收比上一包大的（重送、亂序的丟掉）。收封包的執行緒呼叫，自己加鎖
/// </summary>
internal sealed class DeskInputCipher : IDisposable
{
    private readonly object gate = new();
    private readonly string sessionId;
    private readonly byte[] key;
    private long lastN;
    private bool disposed;

    private DeskInputCipher(string sessionId, byte[] key, string publicKey)
    {
        this.sessionId = sessionId;
        this.key = key;
        PublicKey = publicKey;
    }

    /// <summary>看的一方沒給公鑰、或給的不合格（不是曲線上的點）回 null：這個工作階段照舊收明文的輸入</summary>
    public static DeskInputCipher? TryCreate(string sessionId, string? viewerKey)
    {
        if (!DeskSealRules.IsPublicKey(viewerKey))
            return null;
        using var own = DeskSealRules.CreateKey();
        var publicKey = DeskSealRules.ExportPublicKey(own);
        var key = DeskSealRules.DeriveKey(own, viewerKey, sessionId, viewerKey, publicKey);
        return key is null ? null : new DeskInputCipher(sessionId, key, publicKey);
    }

    /// <summary>這台這個工作階段的公鑰（放在第一個封包的 <c>kx.pub</c>）</summary>
    public string PublicKey { get; }

    /// <summary>已經收過解得開的一包：看的一方確實有金鑰，之後明文的輸入一律不收</summary>
    public bool Established
    {
        get
        {
            lock (gate)
                return lastN > 0;
        }
    }

    /// <summary>解開一包輸入，回裡面的事件；解不開（驗證碼不對、編號沒有比上一包大、內容不是合格的事件）回 null</summary>
    public IReadOnlyList<DeskInputEvent>? Open(long n, string ct)
    {
        byte[]? plain;
        lock (gate)
        {
            if (disposed || n <= lastN)
                return null;
            plain = DeskSealRules.Open(key, sessionId, n, ct);
            if (plain is null)
                return null;
            lastN = n;
        }

        try
        {
            using var reader = new JsonTextReader(new StringReader(Encoding.UTF8.GetString(plain))) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 };
            // 裡面只能是明文的事件：不收又包一層 e2e 的
            return JToken.ReadFrom(reader) is JObject body && body["e2e"] is null ? DeskRules.ParseInput(body) : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
