using Myriagent.Sync;

namespace MyriagentHub.Ops;

/// <summary>閒置修剪這一次檢查要做什麼</summary>
internal enum IdleTrimAction
{
    /// <summary>不做（還有活動、有請求在處理、靜置不夠久，或沒有東西要修剪）</summary>
    None,

    /// <summary>這段閒置的第一次：釋放已編譯的 DLP 樣式、積極回收</summary>
    Trim,

    /// <summary>同一段閒置裡 GC 已提交的空間又長回來：再積極回收一次（一小時最多一次）</summary>
    Recompact,
}

/// <summary>
/// MyriagentHub 閒置時的修剪（S9「閒置 &lt;150 MB」，設計 14.20；作法照桌面的 <c>DlpCacheTrimRules</c>，WP4.9 步 5、6b）。純函式。
/// 閒置時幾乎不配置、不跑 gen2 GC，負載留下的死物件與空隙一直佔著 GC 已提交的空間；NonBacktracking 的 DLP 樣式快取（約 17 MB）也一直在。
/// 所以伺服器靜置 <see cref="QuietPeriod"/>、沒有請求或 hub 呼叫在處理時，釋放已編譯的 DLP 樣式並做一次阻塞、壓縮的積極回收（含大型物件堆積）。
/// <list type="bullet">
/// <item>活動：HTTP 請求（<see cref="IsBackgroundRequest"/> 以外的；媒體轉送、上傳與下載都是請求，處理中的不能回收）與 hub 呼叫
/// （<see cref="CountsAsActivity"/>：裝置的心跳、用量上傳與換存取權杖不算）。每個請求與呼叫開始、結束各記一次。</item>
/// <item>每段閒置（兩次活動之間）最多修剪一次，而且要有東西可修剪：啟動之後還沒回收過，或 GC 已提交的空間比上一次回收之後多了
/// <see cref="RecompactGrowthBytes"/> 以上（例如媒體轉送）——這兩種靜置滿就做；只有 DLP 樣式已編譯（有對話或指令的內容經過）、已提交沒長到門檻的，
/// 要距上一次回收滿 <see cref="MinDlpOnlyTrimInterval"/> 才做，不到就等到滿（閒置一直持續的話）。擁有者平常使用時回合之間常停超過 30 秒，
/// 只因為樣式就修剪的話幾乎每個回合之後都要一次阻塞的積極回收、下一個項目再花約 35 ms 重建樣式，一小時沒有上限。
/// 只有短請求（版本檢查、登入狀態）的閒置不回收。</item>
/// <item>同一段閒置裡之後只在已提交的空間又多了 <see cref="RecompactGrowthBytes"/> 以上、而且距上一次回收滿 <see cref="RecompactInterval"/> 時再回收
/// （心跳與 keepalive 的配置讓 gen0 慢慢長回來；同桌面步 6b）。</item>
/// </list>
/// 樣式之後第一次遮蔽時重建（輸出逐位元組相同）
/// </summary>
internal static class IdleTrimRules
{
    /// <summary>最後一次活動之後要靜置多久（S9 的閒置量 2 分鐘、取後半的中位數：修剪要在 1 分鐘內做完）</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(30);

    /// <summary>多久檢查一次（只讀幾個計數，幾乎不花 CPU）</summary>
    public static readonly TimeSpan CheckPeriod = TimeSpan.FromSeconds(10);

    /// <summary>同一段閒置裡再回收的最短間隔（距上一次回收）；阻塞的積極回收會停住所有受管理的執行緒</summary>
    public static readonly TimeSpan RecompactInterval = TimeSpan.FromHours(1);

    /// <summary>GC 已提交的空間比上一次回收之後多了這麼多才算有東西可回收（16 MB，同桌面的 <c>DlpCacheTrimRules.RecompactGrowthBytes</c>）</summary>
    public const long RecompactGrowthBytes = 16L << 20;

    /// <summary>
    /// 只因為 DLP 樣式已編譯（已提交沒長到 <see cref="RecompactGrowthBytes"/>）的修剪，距上一次回收至少要隔多久（10 分鐘）：
    /// 一般使用時每小時最多 6 次積極回收與樣式重建，不是每個回合之後一次
    /// </summary>
    public static readonly TimeSpan MinDlpOnlyTrimInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 這一次檢查要做什麼
    /// </summary>
    /// <param name="idleFor">距最後一次活動多久</param>
    /// <param name="inFlight">處理中的請求與 hub 呼叫數</param>
    /// <param name="sameIdlePeriod">上一次回收之後沒有新的活動（同一段閒置裡已經回收過）</param>
    /// <param name="sinceCompaction">距上一次回收多久（這次啟動之後還沒回收過是 null）</param>
    /// <param name="committedGrowthBytes">GC 已提交的空間（<see cref="CommittedEstimate"/>）比上一次回收之後多了多少（還沒回收過時不看）</param>
    /// <param name="dlpCompiled">已編譯的 DLP 樣式在不在</param>
    public static IdleTrimAction Decide(TimeSpan idleFor, int inFlight, bool sameIdlePeriod, TimeSpan? sinceCompaction, long committedGrowthBytes, bool dlpCompiled)
    {
        if (inFlight > 0 || idleFor < QuietPeriod)
            return IdleTrimAction.None;
        if (sinceCompaction is not { } since)
            return IdleTrimAction.Trim;
        if (!sameIdlePeriod)
        {
            if (committedGrowthBytes >= RecompactGrowthBytes)
                return IdleTrimAction.Trim;
            return dlpCompiled && since >= MinDlpOnlyTrimInterval ? IdleTrimAction.Trim : IdleTrimAction.None;
        }
        return since >= RecompactInterval && committedGrowthBytes >= RecompactGrowthBytes ? IdleTrimAction.Recompact : IdleTrimAction.None;
    }

    /// <summary>
    /// 這個 hub 方法算不算活動：裝置的 <c>Heartbeat</c>（每 15 秒）、<c>UsagePublish</c>（負責查詢的那台定期上傳用量）與 <c>RefreshAccessToken</c>
    /// （定期換存取權杖）是背景的往來，不算；其他（發布、即時更新、拉取、指令的結果與輸出、交握，瀏覽器的 <c>Cmd</c>、<c>ClientState</c>）都算
    /// </summary>
    public static bool CountsAsActivity(string? hubMethod) =>
        hubMethod is not (DeviceHubMethods.Heartbeat or DeviceHubMethods.UsagePublish or DeviceHubMethods.RefreshAccessToken);

    /// <summary>
    /// 不算活動的 HTTP 請求：健康檢查（<c>/healthz</c>、<c>/readyz</c>，部署的閘門與監看會一直問）與 SignalR 的路徑 <c>/hubs/…</c>
    /// （WebSocket 連線一直開著；連線上的每一次呼叫由 hub 的過濾器另外記）
    /// </summary>
    public static bool IsBackgroundRequest(string? path) =>
        path is not null
        && (path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/readyz", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/hubs", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/hubs/", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 目前 GC 已提交空間的估計（位元組；同桌面的 <c>DlpCacheTrimRules.CommittedEstimate</c>）：<c>GCMemoryInfo.TotalCommittedBytes</c> 是上一次 GC 結束時的值，
    /// 之後 gen0 的配置提交的新空間要看 <c>GC.GetTotalMemory(false)</c>，兩者取大
    /// </summary>
    public static long CommittedEstimate(long lastGcCommittedBytes, long totalMemoryBytes) => Math.Max(lastGcCommittedBytes, totalMemoryBytes);
}
