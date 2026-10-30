using System.Net;
using System.Net.Security;

namespace Myriagent.Sync;

/// <summary>同步引擎的狀態（docs/sync-protocol.md 第 10 節的狀態機）</summary>
internal enum SyncEngineState
{
    /// <summary>引擎建好、還沒開始連線</summary>
    Idle,

    Connecting,

    /// <summary>Hello／Prove 進行中</summary>
    Handshaking,

    /// <summary>交握完成，正在重送 outbox 與拉取積壓</summary>
    Draining,

    Live,

    /// <summary>斷線，等退避時間到了再重連</summary>
    Backoff,

    /// <summary>伺服器身分換了、沒有可信的輪換聲明：要使用者在本機比對指紋後確認（syncConfirm），確認前不重連</summary>
    NeedsConfirmation,

    /// <summary>使用者暫停同步（系統匣、syncPause）</summary>
    Paused,

    /// <summary>停止、不再重試（原因見 <see cref="SyncStopReason"/>）</summary>
    Stopped,

    /// <summary>
    /// 伺服器判定這台的 app 太舊（app 自我更新 U3；sync-protocol 1.4 的只更新連線）：連線保持、只送心跳與換存取權杖、下載新版；
    /// 不送批次、不拉取，outbox 照常保留到換版之後
    /// </summary>
    UpdateOnly,
}

/// <summary>停止同步的原因（<see cref="SyncEngineState.Stopped"/>；顯示在 sync.detail）</summary>
internal enum SyncStopReason
{
    None,

    /// <summary>HTTP 401：權杖被撤銷或無效，要重新配對</summary>
    Unauthorized,

    /// <summary>HTTP 403（含 forbidden_origin：我們不送 Origin，是用戶端的錯）</summary>
    Forbidden,

    /// <summary>協定版本或 app 版本不支援：請更新 app（U3 起 Hello 的 protocol 不再停止、每小時重試，只留給還沒改的呼叫端）</summary>
    Protocol,

    /// <summary>這台的 sync.db 倒退了（被還原或複製到別台）：重新配對成新的裝置身分</summary>
    SeqRegression,

    /// <summary>伺服器缺了這台已刪掉的事件、又不在對帳模式</summary>
    ServerBehind,

    /// <summary>批次被整批判成格式錯誤（invalid／too_large）</summary>
    InvalidBatch,

    StoreCorrupt,

    StoreTooNew,

    StoreMismatch,

    StoreMissing,

    /// <summary>DPAPI 解不開裝置權杖（換了 Windows 使用者、檔案被竄改）</summary>
    TokenUnreadable,

    /// <summary>CNG 找不到裝置金鑰、公鑰與 device.json 不同，或不認得的金鑰錯誤持續到門檻（<see cref="DeviceKeyErrorRules"/>）</summary>
    KeyMissing,

    /// <summary>名冊裡自己那一筆被標成撤銷</summary>
    SelfRevoked,

    /// <summary>名冊裡沒有自己</summary>
    NotInRegistry,
}

/// <summary>連線失敗的種類（<see cref="SyncFailureRules.Classify"/>）</summary>
internal enum SyncFailureKind
{
    /// <summary>TLS 失敗、5xx、連不上、不認得的代碼：退避重連</summary>
    Transient,

    Unauthorized,

    Forbidden,

    Protocol,

    /// <summary>not_proven，或交握途中連線被關、沒有代碼：退避重連（可能有 TLS 代理、或這台已被撤銷）</summary>
    NotProven,

    /// <summary>hub 的 invalid：用戶端的錯，記錄並告警、退避重連</summary>
    ClientBug,
}

internal sealed record SyncFailure(SyncFailureKind Kind, string? Code, string Detail);

/// <summary>收到 BatchAck 之後怎麼做（<see cref="SendGenerationRules.OnAck"/>）</summary>
internal enum BatchAckAction
{
    /// <summary>交給 <see cref="OutboxRules.ApplyAck"/></summary>
    Apply,

    /// <summary>舊送出世代的 gap：只減在途數，不再 Rewind</summary>
    DropStale,

    /// <summary>seq_regression、invalid、too_large：停止同步</summary>
    Stop,

    /// <summary>不認得的整批錯誤：斷線、照退避重連，由 Prove 重新對齊</summary>
    Reconnect,
}

/// <summary>
/// 重連的退避（D12）：1, 2, 4, 8, 16 秒、±20% 抖動，在 Live 滿 30 秒後歸零。上限 16 秒而不是協定原本的 60 秒：
/// S5 與第 3 階段演示都要「恢復後 30 秒內補齊」，上限 60 秒＋20% 抖動最慢要 72 秒才會重連（設計 14.5 第 9 列）
/// </summary>
internal static class SyncBackoffRules
{
    public static readonly IReadOnlyList<double> DefaultStepsSeconds = [1, 2, 4, 8, 16];

    public const double JitterFraction = 0.2;

    public static readonly TimeSpan ResetAfterLive = TimeSpan.FromSeconds(30);

    /// <summary>第 attempt 次（從 0 起）的延遲：steps[min(attempt, 最後一個)] ×（0.8～1.2，jitter01 是 0～1 的亂數）</summary>
    public static TimeSpan Delay(int attempt, double jitter01, IReadOnlyList<double> steps)
    {
        if (steps.Count == 0)
            throw new ArgumentException("退避步階不可以是空的", nameof(steps));
        var step = steps[Math.Clamp(attempt, 0, steps.Count - 1)];
        var jitter = double.IsFinite(jitter01) ? Math.Clamp(jitter01, 0, 1) : 0.5;
        var factor = 1 - JitterFraction + 2 * JitterFraction * jitter;
        return TimeSpan.FromSeconds(step * factor);
    }

    public static bool ShouldResetAttempts(TimeSpan liveFor) => liveFor >= ResetAfterLive;

    /// <summary>退避延遲與 notBefore（ServerNotice{shutdown} 的 retryAfterSec）取較晚的</summary>
    public static TimeSpan NextDelay(int attempt, double jitter01, IReadOnlyList<double> steps, DateTimeOffset now, DateTimeOffset? notBefore)
    {
        var delay = Delay(attempt, jitter01, steps);
        if (notBefore is { } at && at - now > delay)
            return at - now;
        return delay;
    }

    /// <summary>
    /// Hello 回 protocol 之後多久再試（app 自我更新 U3，設計 7.5）：這台宣告了 update1，太舊的會拿到只更新的連線，
    /// 還回 protocol 就是伺服器比這台舊（回滾）；伺服器升回來之後不必重開 app 就連得上
    /// </summary>
    public static readonly TimeSpan ProtocolRetry = TimeSpan.FromHours(1);

    /// <summary>這次失敗之後的退避：protocol 一律 <see cref="ProtocolRetry"/>（再乘 0.8～1.2 的抖動），其他照 <see cref="NextDelay"/></summary>
    public static TimeSpan DelayAfter(SyncFailure? failure, int attempt, double jitter01, IReadOnlyList<double> steps, DateTimeOffset now, DateTimeOffset? notBefore)
    {
        if (failure?.Kind != SyncFailureKind.Protocol)
            return NextDelay(attempt, jitter01, steps, now, notBefore);
        var jitter = double.IsFinite(jitter01) ? Math.Clamp(jitter01, 0, 1) : 0.5;
        var delay = ProtocolRetry * (1 - JitterFraction + 2 * JitterFraction * jitter);
        if (notBefore is { } at && at - now > delay)
            return at - now;
        return delay;
    }
}

/// <summary>連線錯誤的分類（docs/sync-protocol.md 第 11 節；依據 HTTP 狀態、hub 錯誤代碼與 TLS）</summary>
internal static class SyncFailureRules
{
    private const string HubExceptionMarker = "HubException: ";

    /// <summary>.NET 用戶端的訊息是「… HubException: {代碼}」：取最後一個 "HubException: " 之後的字串；沒有這個字樣回 null</summary>
    public static string? HubErrorCode(string? message)
    {
        if (message is null)
            return null;
        var index = message.LastIndexOf(HubExceptionMarker, StringComparison.Ordinal);
        if (index < 0)
            return null;
        var code = message[(index + HubExceptionMarker.Length)..].Trim();
        return code.Length == 0 ? null : code;
    }

    /// <param name="httpStatus">WebSocket 升級的 HTTP 狀態（ClientWebSocket.HttpStatusCode）；沒有回應時 null</param>
    /// <param name="hubErrorCode">hub 方法失敗時的代碼（<see cref="HubErrorCode"/>）</param>
    /// <param name="tlsFailure">TLS 交握失敗（憑證驗不過）</param>
    /// <param name="closedDuringHandshake">Hello／Prove 進行中連線被關、沒有代碼：伺服器在撤銷、SPKI 不符、簽章錯時都是先 Abort 再丟例外</param>
    public static SyncFailure Classify(int? httpStatus, string? hubErrorCode, bool tlsFailure, bool closedDuringHandshake = false)
    {
        if (httpStatus == 401)
            return new SyncFailure(SyncFailureKind.Unauthorized, "401", "權杖已撤銷，請解除配對後重新配對");
        if (httpStatus == 403)
            return new SyncFailure(SyncFailureKind.Forbidden, "403", "伺服器拒絕這台的連線（HTTP 403）");
        switch (hubErrorCode)
        {
            case SyncErrorCodes.Protocol:
                // 這台宣告了 update1（U3）：太舊時伺服器給只更新的連線，還回 protocol 就是伺服器比這台舊
                return new SyncFailure(SyncFailureKind.Protocol, hubErrorCode, "伺服器的版本比這個 app 舊（不支援這個協定）");
            case SyncErrorCodes.Draining:
                // 伺服器排空中（部署換版，sync-protocol 第 11 節）：不是錯誤，照退避稍後再連
                return new SyncFailure(SyncFailureKind.Transient, hubErrorCode, "伺服器正在更新");
            case SyncErrorCodes.UpdateRequired:
                return new SyncFailure(SyncFailureKind.Transient, hubErrorCode, "這台的 app 版本太舊，只能下載更新");
            case SyncErrorCodes.NotProven:
                return NotProven(hubErrorCode);
            case SyncErrorCodes.Invalid:
                return new SyncFailure(SyncFailureKind.ClientBug, hubErrorCode, "伺服器判定這台送出的請求格式錯誤（invalid）");
        }

        if (tlsFailure)
            return new SyncFailure(SyncFailureKind.Transient, "tls", "伺服器的憑證驗證失敗");
        if (hubErrorCode is not null)
            return new SyncFailure(SyncFailureKind.Transient, hubErrorCode, $"伺服器回應不認得的錯誤（{hubErrorCode}）");
        if (closedDuringHandshake)
            return NotProven(null);
        if (httpStatus is >= 500 and <= 599)
            return new SyncFailure(SyncFailureKind.Transient, httpStatus.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), $"伺服器錯誤（HTTP {httpStatus}）");
        if (httpStatus is { } status)
            return new SyncFailure(SyncFailureKind.Transient, status.ToString(System.Globalization.CultureInfo.InvariantCulture), $"伺服器回應 HTTP {status}");
        return new SyncFailure(SyncFailureKind.Transient, null, "伺服器連不上");
    }

    public const string ConnectTimeoutCode = "connect_timeout";

    /// <summary>
    /// 建立連線失敗（<c>HubConnection.StartAsync</c> 丟例外）的分類：升級的 HTTP 狀態與 TLS 失敗照 <see cref="Classify"/>；
    /// 都沒有、而且是 <see cref="SyncTimingRules.ConnectTimeout"/> 到了才中斷的，記成連線逾時（<see cref="ConnectTimeoutCode"/>，暫時性、照退避重連）
    /// </summary>
    public static SyncFailure ClassifyConnect(int? httpStatus, bool tlsFailure, bool timedOut, TimeSpan timeout)
    {
        var failure = Classify(httpStatus, null, tlsFailure);
        if (!timedOut || failure.Code is not null)
            return failure;
        var seconds = timeout.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        return new SyncFailure(SyncFailureKind.Transient, ConnectTimeoutCode, $"連線伺服器 {seconds} 秒沒有完成（伺服器可能正在重新啟動）");
    }

    /// <summary>停止、不再重試的種類：401、403（protocol 在 U3 起改成每小時重試，<see cref="SyncBackoffRules.ProtocolRetry"/>）</summary>
    public static bool IsTerminal(SyncFailureKind kind) => kind is SyncFailureKind.Unauthorized or SyncFailureKind.Forbidden;

    public static SyncStopReason StopReasonFor(SyncFailureKind kind) => kind switch
    {
        SyncFailureKind.Unauthorized => SyncStopReason.Unauthorized,
        SyncFailureKind.Forbidden => SyncStopReason.Forbidden,
        SyncFailureKind.Protocol => SyncStopReason.Protocol,
        _ => SyncStopReason.None,
    };

    /// <summary>OutboxRules 的判決要不要停止：Continue／Reconnect 回 null</summary>
    public static SyncStopReason? StopReasonFor(OutboxVerdict verdict) => verdict switch
    {
        OutboxVerdict.StopSeqRegression => SyncStopReason.SeqRegression,
        OutboxVerdict.StopServerBehind => SyncStopReason.ServerBehind,
        OutboxVerdict.StopInvalidBatch => SyncStopReason.InvalidBatch,
        OutboxVerdict.Continue or OutboxVerdict.Reconnect => null,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "不認得的 outbox 判決"),
    };

    private static SyncFailure NotProven(string? code) =>
        new(SyncFailureKind.NotProven, code, "伺服器看到的憑證與本機不同（可能有 TLS 代理），或這台已被撤銷");
}

/// <summary>
/// 送出世代（D10）：每次 Rewind（gap）或重連時加一，每批記下送出時的世代。最多 4 批在途時，一次 gap 會讓後面幾批也各回一次 gap，
/// 每次 Rewind 都會把剛重送、正在途中的列改回 Retry（OutboxRules.Rewind）——舊世代的 gap 只減在途數、不再 Rewind
/// </summary>
internal static class SendGenerationRules
{
    /// <param name="batchGeneration">這批送出時的送出世代</param>
    /// <param name="currentGeneration">目前的送出世代</param>
    public static BatchAckAction OnAck(int batchGeneration, int currentGeneration, BatchAck ack)
    {
        if (ack.Error is not { } error)
            return BatchAckAction.Apply;
        return error.Code switch
        {
            SyncErrorCodes.SeqRegression or SyncErrorCodes.Invalid or SyncErrorCodes.TooLarge => BatchAckAction.Stop,
            SyncErrorCodes.Gap => batchGeneration == currentGeneration ? BatchAckAction.Apply : BatchAckAction.DropStale,
            _ => BatchAckAction.Reconnect,
        };
    }
}

/// <summary>引擎的計時常數（D12、第 3.2 節）：閒置 Live 時只剩心跳</summary>
internal static class SyncTimingRules
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(SyncProtocol.HeartbeatSeconds);

    /// <summary>有在途批次時才計時：這麼久沒收到 BatchAck 就斷線重連</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 建立連線（TCP、TLS、WebSocket 升級加 SignalR 交握；SignalR 用戶端的交握逾時 15 秒在這之內）最多等這麼久，超過就斷線、照退避重連。
    /// 2026-10-22 實測：伺服器部署換版的那幾秒建立的連線 TCP 連得上、之後一直沒有回應，伺服器也不關它，沒有上限時要等到第 330 秒才失敗
    /// </summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    /// <summary>批次之間至少隔這麼久（sync-protocol 5.1）</summary>
    public static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 閒置後第一批等這麼久再送：中樞同一次合併刷新寫的幾筆（項目＋標頭＋清單）各自排進引擎執行緒，稍等一下才會進同一批
    /// </summary>
    public static readonly TimeSpan BatchCoalesce = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// 排下一次送出要等多久：距上一批不足 <paramref name="interval"/> 就等到滿，否則只等 <paramref name="coalesce"/>。
    /// 以前一律等滿 interval（判斷式寫成「剩餘時間大於 interval」永遠不成立），閒置後每一筆持久事件都多等 100 ms
    /// </summary>
    public static TimeSpan SendDelay(DateTimeOffset? lastBatchAt, DateTimeOffset now, TimeSpan interval, TimeSpan coalesce)
    {
        if (lastBatchAt is { } last && last + interval - now > coalesce)
            return last + interval - now;
        return coalesce;
    }

    /// <summary>只有待送筆數改變時，sync 訊息最多每這麼久送一次（尾端一定送出）</summary>
    public static readonly TimeSpan StatusThrottle = TimeSpan.FromMilliseconds(500);

    /// <summary>devices 訊息的節流（web-protocol）</summary>
    public static readonly TimeSpan DevicesThrottle = TimeSpan.FromMilliseconds(500);

    /// <summary>ServerNotice{shutdown} 沒帶 retryAfterSec 時（伺服器的預設，AdminInbox）</summary>
    public const int DefaultShutdownRetrySeconds = 30;

    public const int MinShutdownRetrySeconds = 1;

    public const int MaxShutdownRetrySeconds = 600;

    /// <summary>ServerNotice{shutdown} 之後最早的重連時間：retryAfterSec 夾在 1～600 秒，沒帶用 30 秒</summary>
    public static DateTimeOffset NoticeNotBefore(DateTimeOffset now, int? retryAfterSec) =>
        now.AddSeconds(Math.Clamp(retryAfterSec ?? DefaultShutdownRetrySeconds, MinShutdownRetrySeconds, MaxShutdownRetrySeconds));

    /// <summary>
    /// 同上再乘 0.8～1.2 的抖動（app 自我更新 U3，設計 7.6）：伺服器換版時各電腦不要在同一刻一起重連。
    /// Hello 回 draining（沒有通知可看）時也用它，retryAfterSec 是 null（30 秒）
    /// </summary>
    public static DateTimeOffset NoticeNotBefore(DateTimeOffset now, int? retryAfterSec, double jitter01)
    {
        var seconds = Math.Clamp(retryAfterSec ?? DefaultShutdownRetrySeconds, MinShutdownRetrySeconds, MaxShutdownRetrySeconds);
        var jitter = double.IsFinite(jitter01) ? Math.Clamp(jitter01, 0, 1) : 0.5;
        return now.AddSeconds(seconds * (1 - SyncBackoffRules.JitterFraction + 2 * SyncBackoffRules.JitterFraction * jitter));
    }
}

/// <summary>
/// 伺服器網址與開發用的憑證信任：正式環境一律走 WebPKI；本機測試的 MyriagentHub（自簽憑證）只在設了
/// <c>MYRIAGENT_SYNC_DEV_TRUST=1</c>、而且連到的與配對時的伺服器都是迴路位址時才接受（覆寫網址不能把正式配對導去別處）
/// </summary>
internal static class DevTrustRules
{
    public const string DefaultServer = "https://hub.example.com";

    public const string ServerOverrideVariable = "MYRIAGENT_SYNC_SERVER";

    public const string DevTrustVariable = "MYRIAGENT_SYNC_DEV_TRUST";

    /// <summary>127.0.0.0/8、::1、localhost</summary>
    public static bool IsLoopback(Uri server)
    {
        if (!server.IsAbsoluteUri)
            return false;
        var host = server.DnsSafeHost;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    /// <summary>驗不過的憑證只在「設了 MYRIAGENT_SYNC_DEV_TRUST=1、連到的與配對時的伺服器都是迴路位址」時接受；驗得過的一律接受</summary>
    public static bool AcceptCertificate(Uri connected, Uri paired, SslPolicyErrors errors, bool devTrustEnv)
    {
        if (errors == SslPolicyErrors.None)
            return true;
        return devTrustEnv && IsLoopback(connected) && IsLoopback(paired);
    }

    /// <summary>
    /// syncPair.server 與 MYRIAGENT_SYNC_SERVER 的檢查（網址可能來自網頁）：只收 https、沒有 userinfo、query、fragment，路徑是空的或「/」；
    /// 合格回只有 scheme、host、port 的網址，不合格回 null
    /// </summary>
    public static Uri? ValidateServerUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var text = value.Trim();
        // 原文就擋：Uri 會把空的 query（「?」）、空的 fragment 正規化掉，看不出來
        if (text.IndexOfAny(['@', '?', '#', '\\']) >= 0)
            return null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.AbsolutePath != "/"
            || string.IsNullOrEmpty(uri.Host))
            return null;
        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>要連的伺服器：覆寫（MYRIAGENT_SYNC_SERVER）合格就用它並標示開發模式，否則用配對時的網址</summary>
    public static (Uri Server, bool Overridden) ResolveServer(string? overrideUrl, string pairedUrl)
    {
        if (ValidateServerUrl(overrideUrl) is { } overridden)
            return (overridden, true);
        return (new Uri(pairedUrl), false);
    }
}
