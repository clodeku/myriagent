using System.Net;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>可以撥快的時鐘（工作階段閒置、近期驗證過期、退避都靠它測）；本機時區固定用 UTC，結果不隨執行環境改變</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private long ticks = start.UtcTicks;

    public ManualTimeProvider()
        : this(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);

    public long NowMs => GetUtcNow().ToUnixTimeMilliseconds();
}

/// <summary>
/// 每個測試自己的資料根（測試結束刪除；刪不掉就留著，不影響結果）。伺服器的 SQLite 連線不用池（見 SqliteSetup），
/// 連線釋放後檔案就關了，不必清池；短暫的刪除失敗（防毒或索引服務剛好開著檔）重試幾次。
/// </summary>
internal static class TempDir
{
    public static string Create(string prefix = "myriagenthub-test")
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void TryDelete(string dir)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>repo 裡的檔案（測試讀部署腳本這類介面的另一邊）</summary>
internal static class RepoFiles
{
    /// <summary>repo 根目錄：從測試輸出資料夾往上找 Myriagent.slnx</summary>
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Myriagent.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("找不到 repo 根目錄（Myriagent.slnx）");
    }

    public static string Combine(params string[] parts) => Path.Combine([Root(), .. parts]);
}

/// <summary>測試用的 cookie 容器（TestServer 的 HttpClient 不會自己處理 cookie）</summary>
internal sealed partial class CookieHandler(CookieContainer container) : DelegatingHandler
{
    public CookieContainer Container { get; } = container;

    /// <summary>最後一次回應的 Set-Cookie 原文（檢查 cookie 旗標用）</summary>
    public List<string> SetCookieHeaders { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var header = Container.GetCookieHeader(request.RequestUri!);
        if (!string.IsNullOrEmpty(header))
            request.Headers.TryAddWithoutValidation("Cookie", header);
        var response = await base.SendAsync(request, cancellationToken);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var value in values)
            {
                SetCookieHeaders.Add(value);
                Container.SetCookies(request.RequestUri!, WithoutExpiry(value));
            }
        }
        return response;
    }

    /// <summary>
    /// 去掉有值的 cookie 的 expires／max-age 再放進容器：伺服器以注入的時鐘（ManualTimeProvider，從 2026-10-11 起算）算到期，
    /// CookieContainer 卻以真實時間判斷，真實日期超過「假時鐘的起點＋絕對期限 7 天」之後每個 cookie 一進容器就被丟掉、所有登入後的請求都 401。
    /// 到期與否交給伺服器（工作階段存放區照假時鐘驗）；值是空的（登出、刪除用的 cookie）照原樣，讓容器照舊刪掉
    /// </summary>
    internal static string WithoutExpiry(string setCookie)
    {
        var first = setCookie.Split(';', 2)[0];
        var eq = first.IndexOf('=');
        if (eq < 0 || eq == first.Length - 1)
            return setCookie;
        return ExpiryAttribute().Replace(setCookie, string.Empty);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@";\s*(expires|max-age)=[^;]*", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ExpiryAttribute();
}

/// <summary>TestServer 沒有遠端 IP：以 X-Test-Ip 標頭指定（只在測試主機裡；正式程式絕不信任任何轉送標頭）</summary>
internal sealed class TestIpStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Ip";
    public const string DefaultIp = "203.0.113.10";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            var value = context.Request.Headers[Header].ToString();
            context.Connection.RemoteIpAddress = IPAddress.Parse(string.IsNullOrEmpty(value) ? DefaultIp : value);
            await nextMiddleware(context);
        });
        next(app);
    };
}
