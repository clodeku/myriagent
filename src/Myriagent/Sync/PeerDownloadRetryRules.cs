using System.Net;

namespace Myriagent.Sync;

/// <summary>
/// 電腦狀態面板的分段下載（<see cref="ReplicaMediaSource.DownloadAsync"/>）遇到暫時的失敗時要不要重試、等多久。
/// 2026-11-10 使用者回報：9.8 GB 的資料夾下載有一個檔失敗——來源電腦在下載途中自動換版重新啟動（約 13 秒），
/// 正在轉送的那一段被切斷，以前除了 503 之外一律整個檔放棄（一千多段裡斷一次就前功盡棄）。
/// 現在連線中斷、本文提早結束、逾時、502／503／504、這台暫時沒有權杖都從斷掉的位置接著試；
/// 連續失敗（期間完全沒有進度）超過 <see cref="FailBudget"/> 才放棄，有進度就重新計算
/// </summary>
internal static class PeerDownloadRetryRules
{
    /// <summary>連續失敗最多撐多久（要撐過來源電腦換版重新啟動、伺服器部署的停機）</summary>
    public static readonly TimeSpan FailBudget = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
    ];

    /// <summary>之後每次的間隔</summary>
    public static readonly TimeSpan SteadyDelay = TimeSpan.FromSeconds(15);

    /// <summary>伺服器回這些狀態是暫時的：502（來源電腦的轉送失敗）、503（來源離線或忙）、504（來源逾時）</summary>
    public static bool IsTransientStatus(HttpStatusCode status)
        => status is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>
    /// 第 <paramref name="failures"/> 次（從 0 起算）連續失敗之後等多久再試；已經連續失敗了 <paramref name="failingFor"/>，
    /// 再等下去會超過 <see cref="FailBudget"/> 就回 null（放棄）
    /// </summary>
    public static TimeSpan? NextDelay(int failures, TimeSpan failingFor)
    {
        if (failures < 0 || failingFor < TimeSpan.Zero)
            return null;
        var delay = failures < Delays.Length ? Delays[failures] : SteadyDelay;
        return failingFor + delay > FailBudget ? null : delay;
    }
}
