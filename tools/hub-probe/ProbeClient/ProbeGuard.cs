using System.Security.Cryptography.X509Certificates;
using FakeDevice;

namespace MyriagentHub.ProbeClient;

/// <summary>正式站守門拒絕（訊息給使用者看；結束碼 <see cref="Program.ExitGuard"/>）</summary>
internal sealed class ProbeGuardException(string message) : Exception(message);

/// <summary>
/// ProbeClient 的正式站守門（WP4.9 計畫 D18）：所有模式連伺服器之前先過 <see cref="LocalHubTargetRules.CheckUrl"/>（只收 https＋迴路、
/// 拒絕任何 example.com），再以匿名的本機迴路身分讀一次 <c>/readyz</c>、記下 TLS 握手看到的憑證，過 <see cref="LocalHubTargetRules.CheckTarget"/>
/// （Windows 服務、hub.example.com 的憑證或公開 CA 簽發一律拒絕）。規則與 fake-device 共用同一份（以連結編譯 LocalHubTargetRules.cs），
/// 網路這一半照 fake-device 的 LocalHubTarget：讀不到 /readyz、或內容沒有 environment 一律拒絕（fail closed）
/// </summary>
internal static class ProbeGuard
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <param name="handler">測試用：換掉網路（沒有 TLS，就只有 /readyz 的事實）；null＝真的連線、記下伺服器憑證</param>
    public static async Task<LocalHubTargetFacts> VerifyAsync(string baseUrl, TimeSpan timeout, HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        if (LocalHubTargetRules.CheckUrl(baseUrl) is { } urlProblem)
            throw new ProbeGuardException($"伺服器網址不合格（{baseUrl}）：{urlProblem}");
        var readyz = new Uri(new Uri(baseUrl), "/readyz");

        X509Certificate2? seen = null;
        var owned = handler is null;
        handler ??= new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            SslOptions =
            {
                // 這個請求不帶任何憑證或權杖：接受任何伺服器憑證只為了看它
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is not null)
                    {
                        seen?.Dispose();
                        seen = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    }

                    return true;
                },
            },
        };
        try
        {
            using var http = new HttpClient(handler, disposeHandler: owned) { Timeout = timeout };
            string body;
            int status;
            try
            {
                using var response = await http.GetAsync(readyz, ct);
                status = (int)response.StatusCode;
                body = await response.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                throw new ProbeGuardException($"讀不到 {readyz}（守門要先確認不是正式站）：{ex.Message}");
            }

            var facts = LocalHubTargetRules.FactsFromCertificate(body, seen);
            if (facts.Environment is null)
                throw new ProbeGuardException($"{readyz}（HTTP {status}）沒有 environment：不確定是不是正式站，不連");
            if (LocalHubTargetRules.CheckTarget(facts) is { } targetProblem)
                throw new ProbeGuardException($"{baseUrl}：{targetProblem}");
            return facts;
        }
        finally
        {
            seen?.Dispose();
        }
    }
}
