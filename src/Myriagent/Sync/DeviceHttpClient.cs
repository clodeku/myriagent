using System.Net;
using System.Net.Http.Headers;
using Myriagent.Core;

namespace Myriagent.Sync;

/// <summary>
/// 裝置呼叫伺服器 HTTP 端點（<c>/api/v1/device/*</c>）的用戶端（WP4.7 計畫 D24、D25）：
/// <list type="bullet">
/// <item>TLS 只照 <see cref="DevTrustRules.AcceptCertificate"/> 判斷（同 SignalR 的連線；不比 SPKI，憑證續期不影響）。</item>
/// <item><c>Authorization: Bearer cha_</c>；權杖收到之後滿 13 分鐘（<see cref="AccessTokenRules.NeedsRefresh"/>）先請引擎換一次再送。</item>
/// <item>收到 401：權杖還是送出時那一個就請引擎換一次（別的請求已經換過就直接用新的），再送一次；第二次的結果照回（不再試）。</item>
/// </list>
/// 請求以工廠傳入：一個 <see cref="HttpRequestMessage"/> 只能送一次，重送要重新產生（本文也是）。不自動轉址、不帶 cookie。
/// 任何執行緒都可以呼叫；權杖由引擎執行緒寫進 <see cref="AccessTokenHolder"/>
/// </summary>
internal sealed class DeviceHttpClient : IDisposable
{
    private readonly HttpClient client;
    private readonly AccessTokenHolder tokens;
    private readonly Func<CancellationToken, Task<bool>> refresh;
    private readonly TimeProvider time;

    /// <param name="server">要連的伺服器（MYRIAGENT_SYNC_SERVER 覆寫時是它；請求的相對網址以它為準）</param>
    /// <param name="pairedServer">配對的伺服器（開發信任要兩者都是迴路位址）</param>
    /// <param name="refresh">請引擎換一個新的 cha_（換成功回 true；任何執行緒呼叫，同時多個請求共用同一次換新）</param>
    /// <param name="handler">測試換成假的傳輸；null＝正式的 <see cref="SocketsHttpHandler"/></param>
    public DeviceHttpClient(Uri server, Uri pairedServer, bool devTrustEnv, AccessTokenHolder tokens, Func<CancellationToken, Task<bool>> refresh,
        TimeProvider time, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(pairedServer);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(time);
        this.tokens = tokens;
        this.refresh = refresh;
        this.time = time;
        client = new HttpClient(handler ?? CreateHandler(server, pairedServer, devTrustEnv), disposeHandler: true)
        {
            BaseAddress = server,
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>正式的傳輸：不轉址、不帶 cookie、不解壓縮，憑證照開發信任的規則</summary>
    private static SocketsHttpHandler CreateHandler(Uri server, Uri pairedServer, bool devTrustEnv) => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        // 閒置的連線留久一點（伺服器 Kestrel 的 KeepAliveTimeout 預設 130 秒、沒有改）：以前 1 分鐘就關，
        // 瀏覽器隔一陣子才看這台的圖片時，這台回傳原檔（E7）要先重新做 TCP＋TLS 交握
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(110),
        SslOptions =
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                certificate is not null && DevTrustRules.AcceptCertificate(server, pairedServer, errors, devTrustEnv),
        },
    };

    /// <summary>
    /// 送一個請求（<paramref name="request"/> 每次呼叫都要產生新的請求與本文）。沒有可用的權杖（連線斷了、換不到）回 null，不送；
    /// 傳輸失敗照常丟例外（<see cref="HttpRequestException"/>、取消時 <see cref="OperationCanceledException"/>）。回應由呼叫端 Dispose
    /// </summary>
    public async Task<HttpResponseMessage?> SendAsync(Func<HttpRequestMessage> request, HttpCompletionOption option, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var token = await UsableTokenAsync(ct).ConfigureAwait(false);
        if (token is null)
            return null;

        var response = await SendOnceAsync(request, token, option, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        // 401：權杖還是送出時的那一個才換（同時被拒的請求共用引擎的同一次換新）；換不到就照回 401
        if (tokens.Current?.Token == token && !await refresh(ct).ConfigureAwait(false))
            return response;
        if (tokens.Current?.Token is not { } next || next == token)
            return response;

        response.Dispose();
        return await SendOnceAsync(request, next, option, ct).ConfigureAwait(false);
    }

    public void Dispose() => client.Dispose();

    /// <summary>目前的權杖；收到之後滿 13 分鐘（計時器還沒換到，例如剛從睡眠醒來）先換一次，換不到就沿用舊的（伺服器 15 分鐘內照收）</summary>
    private async Task<string?> UsableTokenAsync(CancellationToken ct)
    {
        if (tokens.Current is { } current && !AccessTokenRules.NeedsRefresh(time.GetElapsedTime(current.IssuedTimestamp)))
            return current.Token;
        if (!await refresh(ct).ConfigureAwait(false))
            Log.Info("裝置的存取權杖換不到：沿用目前的（或不送）");
        return tokens.Current?.Token;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> request, string token, HttpCompletionOption option, CancellationToken ct)
    {
        // 請求不在這裡 Dispose：伺服器可能在本文送完之前就回應（401、404），這時本文還在送；本文由呼叫端擁有（不持有要釋放的資源）
        var message = request();
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(message, option, ct).ConfigureAwait(false);
    }
}
