using Myriagent.Core;
using Myriagent.Update;

namespace Myriagent.Sync;

/// <summary>引擎給協調者的 app 自我更新功能（U3；<c>SyncEngine</c> 實作，協調者以 <c>engine as ISyncEngineReleases</c> 取得）</summary>
internal interface ISyncEngineReleases
{
    /// <summary>
    /// 下載新版用的 HTTP 用戶端（任何執行緒）：和媒體轉送共用這條連線的 <c>cha_</c>（沒有權杖時 <see cref="DeviceHttpClient.SendAsync"/> 回 null）；
    /// 引擎停止之後是 null
    /// </summary>
    DeviceHttpClient? ReleaseHttp { get; }
}

/// <summary>引擎 → 協調者：伺服器說有新的桌面版（協調者的 EnginePort 實作；引擎以 <c>args.Host as IAppReleasePort</c> 取得，沒有就不回報）。任何執行緒呼叫</summary>
internal interface IAppReleasePort
{
    /// <summary><paramref name="release"/> 是合法的 release 名稱（<see cref="ReleaseIdentity.TryParseRelease"/>）；要不要比這台新由更新管理員判斷</summary>
    void ReportAppRelease(string release);
}

// app 自我更新 U3（設計 7.4～7.6、8.4；docs/sync-protocol.md 1.4、4、11、15）：
// - Hello 宣告 update1（有 IAppReleasePort 的宿主，也就是正式的協調者）：版本太舊時伺服器給只更新的連線，不回 protocol。
// - 下載用的 HTTP 用戶端：和 WP4.7 的媒體轉送共用同一個存取權杖（AccessTokenHolder 與 RefreshAccessTokenForHttpAsync）；
//   協商出 media1 或 update1 的連線都保存權杖（SyncEngine.Media.cs 的 OnProvenForMedia）。
// - 伺服器說有新版：交握驗過伺服器身分之後的 Hello latestApp，與 ServerNotice{appRelease}（交握完成前到的照舊暫存）經 IAppReleasePort 告訴協調者。
// - Prove 回 updateOnly：狀態 UpdateOnly，只開心跳與權杖換新；不送批次、不拉取、不拉名冊、不上傳用量（各處以 conn.UpdateOnly 擋），outbox 留到換版之後。
// - Hello 回 draining：照停機通知的規則（30 秒 ±20%）延後下一次連線。
// 狀態只在引擎執行緒改（用戶端的建立加鎖，HubReleaseSource 在執行緒集區讀）
internal sealed partial class SyncEngine : ISyncEngineReleases
{
    private readonly object releaseHttpGate = new();
    private DeviceHttpClient? releaseHttp;
    private bool releaseHttpClosed;

    private IAppReleasePort? ReleasePort => args.Host as IAppReleasePort;

    public DeviceHttpClient? ReleaseHttp
    {
        get
        {
            lock (releaseHttpGate)
            {
                if (releaseHttpClosed)
                    return null;
                return releaseHttp ??= new DeviceHttpClient(server, pairedServer, args.DevTrustEnv, accessTokens, RefreshAccessTokenForHttpAsync, time, MediaHttpHandlerForTest);
            }
        }
    }

    /// <summary>引擎停止（CloseMedia）：收掉下載用的用戶端，之後不再建</summary>
    private void CloseReleaseHttp()
    {
        DeviceHttpClient? closing;
        lock (releaseHttpGate)
        {
            releaseHttpClosed = true;
            closing = releaseHttp;
            releaseHttp = null;
        }

        closing?.Dispose();
    }

    /// <summary>宿主收得到「伺服器說有新版」才宣告 update1（測試的宿主沒有就照舊，Hello 不變）</summary>
    private void ContributeReleaseFeatures(List<string> into)
    {
        if (ReleasePort is not null)
            into.Add(DeviceFeatureRules.Update1);
    }

    /// <summary>
    /// Prove 回 updateOnly（sync-protocol 1.4）：交握到此為止。採用交握中收到的通知（shutdown、appRelease）、清掉用量指派、開心跳、
    /// 保存存取權杖（下載新版用）；功能只留 update1，第 4 階段與名冊都不碰
    /// </summary>
    private void CompleteUpdateOnlyHandshake(SyncConnection conn, ProveResponse prove)
    {
        conn.InHandshake = false;
        conn.Proven = true;
        conn.UpdateOnly = true;
        conn.Features = conn.Features.Where(f => f == DeviceFeatureRules.Update1).ToList();
        lastFailure = null;
        pullWarning = null;
        noticeDetail = null;
        foreach (var notice in conn.EarlyNotices)
            OnServerNotice(conn, notice);
        conn.EarlyNotices.Clear();
        conn.EarlyAssigns.Clear();
        conn.EarlyTrustRev = null;
        // 伺服器不把只更新的連線當用量候選（指派一律是空的）：表裡的 On 全部改 Off，這台照本機原本的頻率查
        assignments = UsageSyncRules.FromProve(assignments, prove.UsageAssignments);
        sendGeneration++;
        inFlight.Clear();
        lastBatchAt = null;
        liveSince = time.GetUtcNow();
        Log.Warn("伺服器判定這台的 app 版本太舊：只提供更新（不送、不拉，換到新版之後恢復同步）");
        SetState(SyncEngineState.UpdateOnly);
        StartHeartbeat(conn);
        AfterProveForMedia(conn, prove);
    }

    /// <summary>Hello 回 draining（伺服器排空中、沒有停機通知可看）：下一次連線不早於 30 秒 ×（1 ± 20%），和已經記下的取較晚的</summary>
    private void DelayForDraining()
    {
        var at = SyncTimingRules.NoticeNotBefore(time.GetUtcNow(), null, options.Jitter());
        if (notBefore is not { } existing || existing < at)
            notBefore = at;
        Log.Info($"伺服器排空中（draining）：{notBefore:O} 之後再重連");
    }

    /// <summary>ServerNotice{appRelease}：伺服器收了新版（交握完成之後才會到這裡）</summary>
    partial void HandleServerNoticeExtension(SyncConnection conn, ServerNotice notice, ref bool handled)
    {
        if (notice.Kind != ServerNoticeKinds.AppRelease)
            return;
        handled = true;
        ReportAppRelease(notice.Release, "伺服器通知");
    }

    /// <summary>Hello 的 latestApp（伺服器身分驗過之後）：伺服器目前提供的版本</summary>
    private void NoteLatestApp(AppReleaseInfo? latest)
    {
        if (latest is not null)
            ReportAppRelease(latest.Release, "Hello");
    }

    private void ReportAppRelease(string? release, string from)
    {
        if (ReleasePort is not { } port || !ReleaseIdentity.TryParseRelease(release, out _))
            return;
        try
        {
            port.ReportAppRelease(release!);
        }
        catch (Exception ex)
        {
            Log.Error($"回報伺服器的新版（{from}）失敗", ex);
        }
    }
}
