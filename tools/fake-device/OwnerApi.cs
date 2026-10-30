using System.Net;
using System.Net.Http.Json;
using Myriagent.Sync;

namespace FakeDevice;

/// <summary>
/// 擁有者的網頁 API（同 MyriagentHub 網頁的 auth.js）：以帳號密碼登入、核准配對。非 GET 一律帶 <c>Origin</c>（伺服器的 PublicOrigin）與
/// <c>X-Myriagent-Csrf: 1</c>。開發信任（--dev-trust）只在伺服器是迴路位址時接受驗不過的憑證，規則同裝置（DevTrustRules.AcceptCertificate）。
/// 密碼只從環境變數讀進來（不放命令列），不寫進任何記錄或輸出
/// </summary>
internal sealed class OwnerApi : IDisposable
{
    private readonly HttpClient http;

    public OwnerApi(Uri server, string origin, bool devTrust)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, errors) => DevTrustRules.AcceptCertificate(server, server, errors, devTrust) },
        };
        http = new HttpClient(handler) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("Origin", origin);
        http.DefaultRequestHeaders.Add("X-Myriagent-Csrf", "1");
    }

    public Task LoginAsync(string userName, string password) => PostAsync("/api/v1/auth/login", new { userName, password });

    /// <summary>核准（剛登入就算近期驗證過）</summary>
    public Task ApproveAsync(string userCode) => PostAsync("/api/v1/pair/approve", new { userCode });

    public void Dispose() => http.Dispose();

    private async Task PostAsync(string path, object body)
    {
        using var response = await http.PostAsJsonAsync(path, body);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            // 回應內容是伺服器的錯誤代碼（不含密碼）
            var text = await response.Content.ReadAsStringAsync();
            throw new FakeDeviceException($"{path} 回 {(int)response.StatusCode}：{Truncate(text)}");
        }
    }

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300] + "…";
}
