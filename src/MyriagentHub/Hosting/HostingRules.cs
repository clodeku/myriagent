using System.Net;

namespace MyriagentHub.Hosting;

/// <summary>主機設定的純函式規則（資料根、來源、監聽位址、各功能的預設啟用）</summary>
internal static class HostingRules
{
    /// <summary>部署腳本停止服務時等 30 秒（Install-MyriagentHubRelease.ps1 的 StopTimeoutSec）</summary>
    public static readonly TimeSpan ServiceStopBudget = TimeSpan.FromSeconds(30);

    /// <summary>主機等各服務停止的上限：比停止預算少 5 秒，程序本身結束（WAL 檢查點、釋放憑證）也在 30 秒內</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(25);

    /// <summary>
    /// 瀏覽器看到的來源：有設定 PublicOrigin 就用（去掉結尾斜線、轉小寫），否則 https://網域[:埠]（443 與 0 不寫埠）。
    /// </summary>
    public static string PublicOrigin(HubOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PublicOrigin))
            return NormalizeOrigin(options.PublicOrigin) ?? throw new InvalidOperationException($"Hub:PublicOrigin 格式不正確：{options.PublicOrigin}");
        var host = options.ServerDomain.Trim().ToLowerInvariant();
        return options.HttpsPort is 443 or 0 ? $"https://{host}" : $"https://{host}:{options.HttpsPort}";
    }

    /// <summary>來源正規化：scheme://host[:port]，scheme 與 host 小寫、預設埠省略；格式不對回 null</summary>
    public static string? NormalizeOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) || !Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath is not ("/" or "") || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        var host = uri.IdnHost.ToLowerInvariant();
        if (uri.HostNameType == UriHostNameType.IPv6)
            host = "[" + host.Trim('[', ']') + "]";
        return uri.IsDefaultPort ? $"{uri.Scheme}://{host}" : $"{uri.Scheme}://{host}:{uri.Port}";
    }

    /// <summary>
    /// 資料根：--data 優先，其次 Hub:DataDir；都沒有時正式環境用 %ProgramData%\MyriagentHub，其他環境用內容根目錄下的 data。
    /// </summary>
    public static string ResolveDataDir(string? commandLineData, string? configuredDataDir, bool isProduction, string contentRoot, string programData)
    {
        if (!string.IsNullOrWhiteSpace(commandLineData))
            return Path.GetFullPath(commandLineData.Trim());
        if (!string.IsNullOrWhiteSpace(configuredDataDir))
            return Path.GetFullPath(configuredDataDir.Trim());
        return isProduction ? Path.Combine(programData, "MyriagentHub") : Path.Combine(contentRoot, "data");
    }

    /// <summary>監聽位址：any、localhost 或明確 IP；格式不對丟例外（設定錯誤要在啟動時就暴露）</summary>
    public static (BindKind Kind, IPAddress? Address) ParseBindAddress(string? value)
    {
        var v = (value ?? "any").Trim();
        if (v.Length == 0 || v.Equals("any", StringComparison.OrdinalIgnoreCase))
            return (BindKind.Any, null);
        if (v.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return (BindKind.Localhost, null);
        if (IPAddress.TryParse(v, out var ip))
            return (BindKind.Address, ip);
        throw new InvalidOperationException($"Hub:BindAddress 必須是 any、localhost 或 IP：{value}");
    }

    /// <summary>TLS 憑證存放方式：有設定照設定；否則正式環境 store、其他 file</summary>
    public static bool UseCertificateStore(string? configured, bool isProduction) =>
        string.IsNullOrWhiteSpace(configured)
            ? isProduction
            : configured.Trim().ToLowerInvariant() switch
            {
                "store" => true,
                "file" => false,
                _ => throw new InvalidOperationException($"Hub:Tls:CertStore 必須是 store 或 file：{configured}"),
            };

    /// <summary>唯讀隔離自檢：有設定照設定；否則只有以 Windows 服務執行時啟用</summary>
    public static bool SelfTestEnabled(bool? configured, bool isWindowsService) => configured ?? isWindowsService;
}

internal enum BindKind
{
    Any,
    Localhost,
    Address,
}
