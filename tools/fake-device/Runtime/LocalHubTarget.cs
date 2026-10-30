using System.Security.Cryptography.X509Certificates;

namespace FakeDevice;

/// <summary>
/// 正式站守門的網路那一半（WP4.9 計畫 D18）：配對、連線之前先過 <see cref="LocalHubTargetRules.CheckUrl"/>，再以匿名的本機迴路身分讀一次
/// <c>/readyz</c>、記下 TLS 握手看到的憑證，過 <see cref="LocalHubTargetRules.CheckTarget"/>。這個請求不帶任何憑證或權杖，所以接受任何
/// 伺服器憑證（只為了看它）；讀不到 /readyz、或內容沒有 environment 一律當成「不確定」而拒絕（fail closed）
/// </summary>
internal static class LocalHubTarget
{
    /// <summary>確認是本機自己啟動的 MyriagentHub：可以用就回傳看到的事實，否則丟 <see cref="DeviceRuntimeException"/>（訊息給使用者看）</summary>
    public static async Task<LocalHubTargetFacts> VerifyAsync(Uri server, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (LocalHubTargetRules.CheckUrl(server.ToString()) is { } urlProblem)
            throw new DeviceRuntimeException($"伺服器網址不合格（{server}）：{urlProblem}");

        X509Certificate2? seen = null;
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is not null)
                        seen = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    return true;
                },
            },
        };
        using var http = new HttpClient(handler) { Timeout = timeout };
        string body;
        int status;
        try
        {
            using var response = await http.GetAsync(new Uri(server, "/readyz"), ct);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new DeviceRuntimeException($"讀不到 {new Uri(server, "/readyz")}（守門要先確認不是正式站）：{ex.Message}");
        }

        try
        {
            var facts = LocalHubTargetRules.FactsFromCertificate(body, seen);
            if (facts.Environment is null)
                throw new DeviceRuntimeException($"/readyz（HTTP {status}）沒有 environment：不確定是不是正式站，不連");
            if (LocalHubTargetRules.CheckTarget(facts) is { } targetProblem)
                throw new DeviceRuntimeException($"{server}：{targetProblem}");
            return facts;
        }
        finally
        {
            seen?.Dispose();
        }
    }
}
