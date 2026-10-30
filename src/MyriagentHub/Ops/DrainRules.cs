namespace MyriagentHub.Ops;

/// <summary>排空是誰要求的</summary>
internal enum DrainSource
{
    /// <summary>管理收件匣根目錄（admin-inbox\，只有本機管理員寫得進去＝擁有者）：永不過期，只有 undrain 解除</summary>
    Admin,

    /// <summary>部署收件匣（admin-inbox\deploy\，runner 帳戶）：Hub:Deploy:DrainMaxSeconds 後自動解除</summary>
    Deploy,
}

/// <summary>
/// 排空狀態的快照（不可變）：旗標、來源、開始時間、期限、重連建議與要求 id 一起換掉，
/// 讀的人不會看到各欄位各自更新到一半的組合（例如「沒有排空卻有期限」）。
/// </summary>
internal sealed record DrainSnapshot(bool Draining, DrainSource? Source, long? Since, long? ExpiresAt, int RetryAfterSec, string? RequestId)
{
    public static readonly DrainSnapshot None = new(false, null, null, null, AdminInboxRules.DefaultRetryAfterSec, null);
}

/// <summary>
/// 排空的純函式規則（docs/hub-setup.md 第 11 節）。部署的 drain 有期限：安裝腳本的停止要求沒送到服務時（Stop() 本身失敗，
/// 例如沒有權限；結束碼 4 stop-failed、sc query 是 RUNNING）服務還在跑，沒有期限就會一直排空到重啟（/readyz 503，第 3 階段起
/// 還會拒收遠端指令）。停止要求送到了、只是逾時前沒停好（STOP_PENDING）時 ApplicationStopping 已觸發，<see cref="ShouldAutoUndrain"/>
/// 不解除：服務本來就在結束，結束之後要管理員啟動。
/// </summary>
internal static class DrainRules
{
    public const int DefaultDeployDrainMaxSeconds = 300;

    /// <summary>下限：要遠大於安裝腳本 drain 之後到停好服務的預算（DrainWaitSec 10＋StopTimeoutSec 30），正常部署在這之前服務早就停了</summary>
    public const int MinDeployDrainMaxSeconds = 60;

    public const int MaxDeployDrainMaxSeconds = 3600;

    /// <summary>Hub:Deploy:DrainMaxSeconds 的驗證（啟動與 admin preflight）：60～3600 秒，否則丟例外</summary>
    public static int ValidateDeployDrainMaxSeconds(int seconds) =>
        seconds is >= MinDeployDrainMaxSeconds and <= MaxDeployDrainMaxSeconds
            ? seconds
            : throw new InvalidOperationException(
                $"Hub:Deploy:DrainMaxSeconds 必須在 {MinDeployDrainMaxSeconds}～{MaxDeployDrainMaxSeconds} 秒之間（預設 {DefaultDeployDrainMaxSeconds}；"
                + $"要遠大於安裝腳本的 DrainWaitSec＋StopTimeoutSec）：{seconds}");

    /// <summary>
    /// 收到 drain 之後的狀態：
    /// <list type="bullet">
    /// <item>擁有者（<see cref="DrainSource.Admin"/>）：沒有期限；原本是部署的排空就換成擁有者的（拿掉期限）。</item>
    /// <item>部署（<see cref="DrainSource.Deploy"/>）：期限＝現在＋<paramref name="deployMaxSeconds"/>；部署的排空中再來一次就重設期限；
    /// 擁有者的排空中維持原狀（不降級、不加期限）。</item>
    /// </list>
    /// 一直在排空中時 <see cref="DrainSnapshot.Since"/> 保留最早開始排空的時間。
    /// </summary>
    public static DrainSnapshot Drain(DrainSnapshot current, DrainSource source, long now, int retryAfterSec, int deployMaxSeconds, string? requestId)
    {
        if (source == DrainSource.Deploy && current.Draining && current.Source == DrainSource.Admin)
            return current;
        var since = current.Draining ? current.Since ?? now : now;
        long? expiresAt = source == DrainSource.Deploy ? now + deployMaxSeconds * 1000L : null;
        return new DrainSnapshot(true, source, since, expiresAt, retryAfterSec, requestId);
    }

    /// <summary>undrain：不論誰要求的排空都解除</summary>
    public static DrainSnapshot Undrain() => DrainSnapshot.None;

    /// <summary>到期：只有部署的排空會到期（現在 ≥ 期限）</summary>
    public static bool IsExpired(DrainSnapshot snapshot, long now) =>
        snapshot.Draining && snapshot.Source == DrainSource.Deploy && snapshot.ExpiresAt is { } expiresAt && now >= expiresAt;

    /// <summary>自動解除：到期，而且服務不在停止中（停止中本來就要結束，維持排空到程序結束）</summary>
    public static bool ShouldAutoUndrain(DrainSnapshot snapshot, long now, bool stopping) => !stopping && IsExpired(snapshot, now);

    /// <summary>營運狀態與稽核裡的來源名稱（與稽核的 actor 同名）</summary>
    public static string? SourceName(DrainSource? source) => source switch
    {
        DrainSource.Admin => "admin",
        DrainSource.Deploy => "deploy",
        _ => null,
    };
}
