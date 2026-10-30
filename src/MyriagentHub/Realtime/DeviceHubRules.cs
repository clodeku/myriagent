using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Update;
using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>裝置 hub 的純函式規則（sync-protocol 1.4、3、4）：Hello 的版本閘門與格式、呼叫的關卡、連線啟用的功能、心跳、權杖輪換</summary>
internal static class DeviceHubRules
{
    public const int MaxBootIdLength = 64;

    public const int MaxCursors = 16;

    public const int MaxHeartbeatConversations = 1000;

    /// <summary>連上後多久內沒有完成 Prove 就中斷（真實時間）</summary>
    public static readonly TimeSpan ProveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>device_streams 的心跳時間多久寫一次（記憶體裡的每次都更新）</summary>
    public const long HeartbeatPersistIntervalMs = 60_000;

    /// <summary>裝置權杖用了多久就在 Prove 時輪換（設計 8.2.4）；舊的再有效 24 小時</summary>
    public const long TokenRotateAfterMs = 30L * 24 * 60 * 60 * 1000;

    public const long PreviousTokenGraceMs = 24L * 60 * 60 * 1000;

    /// <summary>
    /// Hello 的版本閘門（sync-protocol 1.4），依序：
    /// <list type="number">
    /// <item>協定比伺服器支援的最高版本新（伺服器回滾），或小於 1 → <see cref="HelloGate.Protocol"/>，宣告了 update1 也一樣。</item>
    /// <item>協定比支援的最低版本舊，或 app 版本低於 minAppVersion（版本號解析不了當成太舊）→ 裝置宣告了 <see cref="DeviceFeatureRules.Update1"/>
    /// 就是 <see cref="HelloGate.UpdateOnly"/>，否則 <see cref="HelloGate.Protocol"/>（U3 之前的桌面不宣告：照舊停止重試、提示更新，D4）。</item>
    /// <item>其他 → <see cref="HelloGate.Full"/>（版本夠新時宣告了 update1 也一樣）。</item>
    /// </list>
    /// <paramref name="features"/> 是裝置自己宣告的 <c>HelloRequest.Features</c>，不是協商結果（伺服器一律宣告 update1，兩者等價）。
    /// 配對（<c>PairingServerRules.CheckStart</c>）用同一個協定範圍（<see cref="SyncProtocol.IsSupported"/>），但不開放只更新（D10）
    /// </summary>
    public static HelloGate Gate(int protocolVersion, string? appVersion, IReadOnlyList<string>? features, string minAppVersion,
        int minSupported = SyncProtocol.MinSupportedVersion, int current = SyncProtocol.Version)
    {
        if (protocolVersion > current || protocolVersion < 1)
            return HelloGate.Protocol;
        if (protocolVersion >= minSupported && AppVersionRules.IsAtLeast(appVersion, minAppVersion))
            return HelloGate.Full;
        return DeviceFeatureRules.Has(features, DeviceFeatureRules.Update1) ? HelloGate.UpdateOnly : HelloGate.Protocol;
    }

    /// <summary>
    /// Hello 的格式（不含版本與身分：版本由 <see cref="Gate"/> 判斷、回 protocol，其他回 invalid）；null＝合格。
    /// 宣告了 update1 的舊版會走到 Prove（版本號解析不了也一樣），所以 appVersion 另外擋空字串、長度與控制字元：它會寫進 device_streams、device.upsert 與版本頁
    /// </summary>
    public static string? CheckHello(HelloRequest request)
    {
        if (request.BootId is not { Length: > 0 and <= MaxBootIdLength } boot
            || boot.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") >= 0)
            return "bootId 不正確";
        if (!CatalogRules.IsOptionalText(request.MachineName, CatalogRules.MaxShortTextLength) || !CatalogRules.IsOptionalText(request.WindowsUser, CatalogRules.MaxShortTextLength))
            return "電腦或使用者名稱不正確";
        if (!HandshakeRules.IsNonce(request.NonceD) || !HandshakeRules.IsSpkiSha256(request.ObservedSpkiSha256))
            return "nonceD 或 observedSpkiSha256 不正確";
        if (request.Outbox is not { } outbox || !IsOutbox(outbox))
            return "outbox 不正確";
        if (request.Cursors is null || request.Cursors.Count > MaxCursors || request.Cursors.Values.Any(v => v < 0))
            return "cursors 不正確";
        if (request.Policy is not { } policy || policy.RemoteControlLevel is not { Length: > 0 and <= 32 } level
            || level.AsSpan().IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz") >= 0)
            return "policy 不正確";
        // 第 4 階段的功能宣告（sync-protocol 14.1）：≤32 項、每項 1～32 個 [a-z0-9]；不認得的名稱照收、不啟用
        if (!DeviceFeatureRules.IsValid(request.Features))
            return "features 不正確";
        if (request.AppVersion is not { Length: > 0 } || !CatalogRules.IsOptionalText(request.AppVersion, AppVersionRules.MaxLength))
            return "appVersion 不正確";
        return null;
    }

    /// <summary>
    /// 這條連線啟用的功能（Hello 時記下、Prove 時交給 <see cref="DeviceConnection"/>）：一般連線是協商結果（<see cref="DeviceFeatureRules.Negotiate"/>）；
    /// 只更新的連線只留 update1，第 4 階段的功能一律不啟用——伺服器送第 14 節的訊息之前問的 <see cref="DeviceConnection.Allows"/> 都是 false（sync-protocol 1.4）
    /// </summary>
    public static IReadOnlyList<string> ConnectionFeatures(HelloGate gate, IReadOnlyList<string> negotiated) =>
        gate == HelloGate.UpdateOnly ? negotiated.Where(name => name == DeviceFeatureRules.Update1).ToList() : negotiated;

    /// <summary>
    /// HelloResponse 給這條連線的上限：<see cref="SyncLimits.MaxLiveDeltaHz"/> 只在協商出 live2 時帶（<paramref name="features"/> 是
    /// <see cref="ConnectionFeatures"/> 的結果）。沒協商出的連線（舊桌面、只更新的連線）線上省略，回應和第 3 階段逐位元組相同（sync-protocol 14.1）
    /// </summary>
    public static SyncLimits HelloLimits(SyncLimits limits, IReadOnlyList<string> features) =>
        features.Contains(DeviceFeatureRules.Live2, StringComparer.Ordinal) ? limits : limits with { MaxLiveDeltaHz = null };

    private static readonly HashSet<string> DeviceToServer = new(DeviceHubMethods.DeviceToServer, StringComparer.Ordinal);

    /// <summary>
    /// 裝置 hub 的一次呼叫能不能進到方法（<see cref="DeviceHubFilter"/>；sync-protocol 1.4、3、4）：null＝可以，否則是 HubException 的代碼。
    /// 方法要在允許清單 <see cref="DeviceHubMethods.DeviceToServer"/> 裡（partial 檔新增的公開方法不會自動開放，要一起改清單），
    /// 不在清單的一律 <c>invalid</c>；Prove 成功之前只能呼叫 Hello、Prove，其他回 <c>not_proven</c>；
    /// 只更新的連線（<paramref name="updateOnly"/>）Prove 之後只放行 RefreshAccessToken 與 Heartbeat，其他回 <c>update_required</c>
    /// （白名單：交握方法與第 4 階段的方法也擋）
    /// </summary>
    public static string? CheckInvocation(string? method, bool proven, bool updateOnly = false)
    {
        if (method is null || !DeviceToServer.Contains(method))
            return SyncErrorCodes.Invalid;
        if (!proven && method is not (DeviceHubMethods.Hello or DeviceHubMethods.Prove))
            return SyncErrorCodes.NotProven;
        if (proven && updateOnly && method is not (DeviceHubMethods.RefreshAccessToken or DeviceHubMethods.Heartbeat))
            return SyncErrorCodes.UpdateRequired;
        return null;
    }

    /// <summary>outbox 位置：next ≥ 1；最小未確認、最小保留都在 next 之前，保留的不晚於未確認的</summary>
    public static bool IsOutbox(OutboxPosition outbox) =>
        outbox.NextDeviceSeq >= 1
        && outbox.OldestUnackedDeviceSeq is null or >= 1 && (outbox.OldestUnackedDeviceSeq ?? 0) < outbox.NextDeviceSeq
        && outbox.OldestRetainedDeviceSeq is null or >= 1 && (outbox.OldestRetainedDeviceSeq ?? 0) < outbox.NextDeviceSeq
        && (outbox.OldestUnackedDeviceSeq is not { } unacked || outbox.OldestRetainedDeviceSeq is { } retained && retained <= unacked);

    /// <summary>
    /// Prove 把連線加進通知群組之後，補送 Hello 之後才發生的通知（HUB-1）：Hello 在排空中就回 draining，所以 Prove 時看到排空中＝排空在 Hello 之後才開始，
    /// 而群組在 Prove 才加入，那時的 ServerNotice{shutdown} 廣播送不到這條連線；Hello 回應的 latestApp 之後才收進更新的版本，appRelease 也一樣。
    /// 加入群組之後才看（廣播與這裡至少有一邊送得到，最多重複一則，裝置照樣處理）。最新版只在版本比 Hello 回報的新時才送（撤回讓最新版變舊時不送）
    /// </summary>
    public static IReadOnlyList<ServerNotice> CatchUpNotices(bool draining, int retryAfterSec, AppReleaseInfo? latestAtHello, AppReleaseInfo? latestNow)
    {
        var notices = new List<ServerNotice>(2);
        if (draining)
            notices.Add(new ServerNotice(ServerNoticeKinds.Shutdown, retryAfterSec));
        if (latestNow is not null && !string.Equals(latestNow.Release, latestAtHello?.Release, StringComparison.Ordinal)
            && ReleaseVersion.TryParse(latestNow.Version, out var now)
            && (latestAtHello is null || !ReleaseVersion.TryParse(latestAtHello.Version, out var before) || now > before))
            notices.Add(new ServerNotice(ServerNoticeKinds.AppRelease, Release: latestNow.Release));
        return notices;
    }

    /// <summary>
    /// Prove 時要不要換發裝置權杖：用輪換後 24 小時內的舊權杖連上（裝置沒收到或沒存好新的）→ 再發一次、舊的期限不延長；
    /// 目前的權杖用了 30 天以上 → 發新的、目前的變成舊的（再有效 24 小時）
    /// </summary>
    public static TokenRotation RotationFor(bool usedPreviousToken, long issuedAt, long now) =>
        usedPreviousToken ? TokenRotation.ReissueKeepPrevious
        : now - issuedAt >= TokenRotateAfterMs ? TokenRotation.Rotate
        : TokenRotation.None;
}

/// <summary>Hello 版本閘門的結果（<see cref="DeviceHubRules.Gate"/>，sync-protocol 1.4）</summary>
internal enum HelloGate
{
    /// <summary>一般連線</summary>
    Full,

    /// <summary>
    /// 只更新的連線：照常交握；裝置 hub 只放行換存取權杖與心跳（<see cref="DeviceHubRules.CheckInvocation"/>），
    /// 裝置的 REST 端點（<c>/api/v1/device/*</c>，含下載新版）跟其他已配對的裝置一樣能用
    /// </summary>
    UpdateOnly,

    /// <summary>回 protocol：桌面提示更新 app（協定比伺服器新時是伺服器版本較舊）</summary>
    Protocol,
}

internal enum TokenRotation
{
    None,
    Rotate,
    ReissueKeepPrevious,
}
