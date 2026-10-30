namespace Myriagent.Sync;

/// <summary>配對中的進度（web-protocol SyncStatus.pairing）</summary>
internal sealed record PairingStatus(string UserCode, string VerificationUri, string VerificationUriComplete, long ExpiresAt, string? Error);

/// <summary>引擎與協調者發布給宿主的同步狀態（web-protocol 的 SyncStatus 加上 deviceId；由 SyncViewRules.StatusView 轉成 JSON）</summary>
internal sealed record SyncStatusSnapshot(
    SyncEngineState State,
    SyncStopReason StopReason,
    string? Detail,
    string Server,
    int Pending,
    long? LastSyncAt,
    long? ClockSkewMs,
    string? DeviceId,
    string? DeviceFingerprint,
    string? HubFingerprint,
    PairingStatus? Pairing,
    string? ConfirmFingerprint,
    bool DevMode);

/// <summary>這次狀態改變要不要送 sync 訊息</summary>
internal abstract record StatusPublish
{
    public sealed record Now : StatusPublish;

    /// <summary>尾端送出：排在這個時間（距上次送出滿 <see cref="SyncTimingRules.StatusThrottle"/>）</summary>
    public sealed record At(DateTimeOffset Time) : StatusPublish;

    public sealed record Skip : StatusPublish;
}

/// <summary>同步狀態給網頁的對應與節流（web-protocol 的 SyncStatus）</summary>
internal static class SyncStatusRules
{
    public const string DevModeNote = "開發模式：伺服器網址已覆寫（MYRIAGENT_SYNC_SERVER）";

    /// <summary>只更新的連線（app 自我更新 U3）的說明</summary>
    public const string UpdateOnlyText = "這台的 app 版本太舊：伺服器只提供更新，換到新版之後恢復同步";

    /// <summary>引擎狀態 → 網頁的 state（pairing／connecting／online／offline／paused／stopped）；配對中一律 pairing</summary>
    public static string WebState(SyncEngineState state, bool pairing)
    {
        if (pairing)
            return "pairing";
        return state switch
        {
            SyncEngineState.Idle or SyncEngineState.Connecting or SyncEngineState.Handshaking or SyncEngineState.Draining => "connecting",
            SyncEngineState.Live => "online",
            SyncEngineState.Backoff or SyncEngineState.UpdateOnly => "offline",
            SyncEngineState.Paused => "paused",
            SyncEngineState.Stopped or SyncEngineState.NeedsConfirmation => "stopped",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "不認得的同步狀態"),
        };
    }

    /// <summary>給人看的說明（sync.detail）：停止的原因、要本機確認、退避時的錯誤與剩餘秒數；開發模式另外加註</summary>
    public static string? Detail(SyncEngineState state, SyncStopReason reason, SyncFailure? failure, TimeSpan? retryIn, bool devMode)
    {
        var text = state switch
        {
            SyncEngineState.Stopped => StopText(reason, failure),
            SyncEngineState.NeedsConfirmation => "伺服器身分換了：請到伺服器主控台（MyriagentHub.exe admin status）比對指紋後確認，不要拿網頁上的比",
            SyncEngineState.Backoff => BackoffText(failure, retryIn),
            SyncEngineState.UpdateOnly => UpdateOnlyText,
            SyncEngineState.Paused => "同步已暫停",
            _ => null,
        };
        if (!devMode)
            return text;
        return text is null ? DevModeNote : $"{text}（{DevModeNote}）";
    }

    /// <summary>
    /// 要不要送：第一次、或 state、detail、confirm、pairing、時差等待送筆數以外的欄位改變→立刻送；
    /// 只有 pending 改變→距上次未滿 500 ms 時排在上次＋500 ms（尾端一定送出），滿了就立刻送；完全沒變→不送
    /// </summary>
    public static StatusPublish Decide(SyncStatusSnapshot? lastSent, SyncStatusSnapshot next, DateTimeOffset lastSentAt, DateTimeOffset now)
    {
        if (lastSent is null)
            return new StatusPublish.Now();
        if (lastSent == next)
            return new StatusPublish.Skip();
        if (next with { Pending = lastSent.Pending } != lastSent)
            return new StatusPublish.Now();
        var due = lastSentAt + SyncTimingRules.StatusThrottle;
        return now >= due ? new StatusPublish.Now() : new StatusPublish.At(due);
    }

    private static string StopText(SyncStopReason reason, SyncFailure? failure) => reason switch
    {
        SyncStopReason.Unauthorized => "權杖已撤銷，請解除配對後重新配對",
        SyncStopReason.Forbidden => $"伺服器拒絕這台的連線（{failure?.Code ?? "403"}），請解除配對後重新配對",
        SyncStopReason.Protocol => "伺服器不支援這個版本，請更新 app",
        SyncStopReason.SeqRegression => "同步資料庫倒退了（被還原或複製到別台），請解除配對後重新配對",
        SyncStopReason.ServerBehind => "伺服器缺少這台已刪除的事件，同步已停止，請聯絡伺服器管理者",
        SyncStopReason.InvalidBatch => "伺服器拒收這台送出的資料（格式錯誤），同步已停止，請更新 app",
        SyncStopReason.StoreCorrupt => "本機同步資料庫損毀，請解除配對後重新配對",
        SyncStopReason.StoreTooNew => "本機同步資料庫是較新版的 app 建立的，請更新 app",
        SyncStopReason.StoreMismatch => "本機同步資料庫屬於另一台裝置，請解除配對後重新配對",
        SyncStopReason.StoreMissing => "本機同步資料庫不見了，請解除配對後重新配對",
        SyncStopReason.TokenUnreadable => "讀不到裝置權杖（換了 Windows 使用者或檔案損毀），請解除配對後重新配對",
        // 不認得的金鑰錯誤持續到門檻才停的（DeviceKeyErrorRules）：金鑰可能還在，說明照實際的錯誤
        SyncStopReason.KeyMissing => failure is { Code: DeviceKeyErrorRules.LostCode } ? failure.Detail : "找不到這台的裝置金鑰，請解除配對後重新配對",
        SyncStopReason.SelfRevoked => "這台已被撤銷，請解除配對後重新配對",
        SyncStopReason.NotInRegistry => "伺服器的裝置名冊裡沒有這台，請解除配對後重新配對",
        SyncStopReason.None => failure?.Detail ?? "同步已停止",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "不認得的停止原因"),
    };

    private static string BackoffText(SyncFailure? failure, TimeSpan? retryIn)
    {
        var what = failure?.Detail ?? "伺服器連不上";
        if (retryIn is not { } wait)
            return what;
        var seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        // protocol 一小時才重試一次（U3）：兩分鐘以上改用分鐘
        if (seconds >= 120)
            return $"{what}，{(int)Math.Ceiling(seconds / 60.0)} 分鐘後重試";
        return $"{what}，{seconds} 秒後重試";
    }
}
