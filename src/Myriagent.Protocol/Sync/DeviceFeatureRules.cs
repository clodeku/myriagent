namespace Myriagent.Sync;

/// <summary>
/// 裝置 hub 的功能宣告（docs/sync-protocol.md 14.1、設計 14.11 D45、D46）：Hello 與 HelloResponse 各帶自己支援的功能，雙方實際啟用的是交集。
/// 沒宣告（null，第 3 階段的裝置與 WP4.1 的桌面）＝第 3 階段的行為：伺服器不送第 14 節的任何訊息、不對它路由遠端指令，裝置不發布對話事件。
/// 功能不在 hubSig／deviceSig 的簽章範圍內，只決定「送不送」；任何安全檢查都在裝置端重做、不依賴它
/// </summary>
internal static class DeviceFeatureRules
{
    /// <summary>對話事件與依串流的拉取（14.2）</summary>
    public const string Conv1 = "conv1";

    /// <summary>即時更新與觀看（14.3）</summary>
    public const string Live1 = "live1";

    /// <summary>
    /// 即時更新改送增量（14.3；docs/plans/2026-10-23-live-delta-and-replica-live.md 第 3 節）：協商出它的連線，裝置可以送 <c>LiveDelta</c>、
    /// 伺服器會送 <c>LiveResync</c>，即時更新的上限是 <see cref="LiveRules.MaxDeltaItemBytes"/>、頻率照 <see cref="SyncLimits.MaxLiveDeltaHz"/>。
    /// 只在也協商出 <see cref="Live1"/> 時有作用
    /// </summary>
    public const string Live2 = "live2";

    /// <summary>遠端指令（14.4～14.6）</summary>
    public const string Cmd1 = "cmd1";

    /// <summary>媒體與檔案轉送（14.3）</summary>
    public const string Media1 = "media1";

    /// <summary>
    /// 只更新模式（app 自我更新 U2；docs/sync-protocol.md 1.4、14.1）：裝置宣告它，表示 app 版本太舊時接受只更新的連線
    /// （只換存取權杖、心跳、下載新版，認得 <c>updateOnly</c> 與 <c>update_required</c>）。伺服器判斷時看裝置自己宣告的
    /// <c>features</c>；不對應任何 hub 方法（<see cref="RequiredFor"/> 不變）
    /// </summary>
    public const string Update1 = "update1";

    /// <summary>
    /// 桌面遠端操作（docs/sync-protocol.md 14.7；擁有者 2026-10-22 決定「已配對就能操作」）：協商出它的連線可以呼叫 <c>PeerCmd</c>、收 <c>PeerReply</c>，
    /// 而且接受另一台桌面發起（<c>issuer.kind = device</c>）的信封。只在也協商出 <see cref="Cmd1"/> 時有作用
    /// </summary>
    public const string Peer1 = "peer1";

    /// <summary>
    /// Grok、Antigravity 也同步（設計 docs/plans/2026-10-22-grok-antigravity-sync.md 第 3 節）：伺服器宣告它，表示目錄、對話事件、
    /// cli.state 收四種代理。桌面只在目前這條連線協商出它時才發布這兩種的帳戶、對話與 CLI 狀態（舊伺服器或回滾時照舊只同步 Claude、Codex，
    /// 不會被拒收或封鎖）。不對應任何 hub 方法（<see cref="RequiredFor"/> 不變）
    /// </summary>
    public const string Prov1 = "prov1";

    /// <summary>
    /// 各電腦的即時狀態（docs/sync-protocol.md 14.8）：協商出它的連線可以呼叫 <c>StatsPublish</c>（CPU、記憶體、磁碟、網路與延遲）、
    /// 收 <c>StatsUpdate</c>（其他電腦的狀態）。給人看的即時資料，不寫持久事件
    /// </summary>
    public const string Stat1 = "stat1";

    /// <summary>
    /// 各帳戶的 token 計數（docs/sync-protocol.md 14.9）：協商出它的連線可以呼叫 <c>TokensPublish</c>（這台依邏輯帳戶與本機日期的計數），
    /// 伺服器存起來給瀏覽器版的帳戶卡顯示各電腦的合計。只供顯示，不寫持久事件
    /// </summary>
    public const string Tok1 = "tok1";

    /// <summary>
    /// 其他電腦的 token 計數（docs/sync-protocol.md 14.10；2026-10-23）：協商出它的連線收 <c>TokensUpdate</c>（其他電腦依邏輯帳戶與日期的合計），
    /// 桌面的帳戶卡加上這台自己的計數，顯示所有電腦的合計。只供顯示，不寫持久事件
    /// </summary>
    public const string Tok2 = "tok2";

    /// <summary>
    /// 各電腦的排程（排程頁的 S3；docs/sync-protocol.md 14.12；2026-10-26）：協商出它的連線可以呼叫 <c>SchedulesPublish</c>（這台排程頁的摘要）、
    /// 收 <c>SchedulesUpdate</c>（其他電腦最後回報的摘要）；遠端的 scheduleAction、scheduleAgent 也要目標有它。只供顯示，不寫持久事件
    /// </summary>
    public const string Sched1 = "sched1";

    /// <summary>
    /// 逐字稿與檔案日誌（第 5 階段，docs/sync-protocol.md 第 16 節）：協商出它的連線可以用 <c>/api/v1/device/logs</c> 上傳與下載 CLI 紀錄、附件與預設工作區，
    /// 收 <c>LogRepair</c>。伺服器只在 <c>Hub:Logs:Declare</c> 打開時宣告、桌面只在 <c>SyncPhase5Switches.Log1</c> 打開時宣告（WP5.1 起兩邊預設都關）
    /// </summary>
    public const string Log1 = "log1";

    /// <summary>
    /// 租約、交接與強制接手（第 5 階段，docs/sync-protocol.md 第 16 節）：協商出它的連線可以呼叫 <c>Handoff*</c>、收 <c>HandoffRequest</c>／<c>HandoffUpdate</c>，
    /// 擁有者換手以伺服器寫的 <c>conv.lease</c> 告知。只在也協商出 <see cref="Log1"/> 時有效（<see cref="Negotiate"/> 拿掉沒有 log1 的 hand1）；
    /// 伺服器只在 <c>Hub:Handoff:Declare</c> 與 <c>Hub:Logs:Declare</c> 都打開時宣告
    /// </summary>
    public const string Hand1 = "hand1";

    /// <summary>
    /// 遠端執行指令（docs/sync-protocol.md 14.11；2026-10-25）：協商出它的連線收 <c>remoteExec</c>（被操作的一方，輸出以 <c>ExecOutput</c> 送、
    /// 收 <c>ExecAbort</c>），也能發起（<c>ExecCancel</c>、收 <c>ExecReply</c>）。只在也協商出 <see cref="Cmd1"/>、<see cref="Peer1"/> 時有作用
    /// </summary>
    public const string Exec1 = "exec1";

    /// <summary>
    /// 多人協作（docs/sync-protocol.md 14.14；2026-10-30）：協商出它的連線接受協作人發起（<c>issuer.kind = collaborator</c>）的信封、
    /// 可以呼叫 <c>CollabCmd</c>（擁有者在桌面管理協作人與對話的分享）、收 <c>CollabUpdate</c>（協作人清單與分享）。只在也協商出 <see cref="Cmd1"/> 時收信封
    /// </summary>
    public const string Collab1 = "collab1";

    /// <summary>
    /// 電腦狀態面板的檔案傳輸（2026-11-09）：協商出它的連線收 <c>deviceBrowse</c>、
    /// <c>deviceFetch</c>、<c>devicePut</c>（舊版的桌面在閘門 5a 回 invalid，伺服器先擋下、說明那台要先更新）。不對應任何 hub 方法
    /// </summary>
    public const string Xfer1 = "xfer1";

    /// <summary>整個資料夾的傳輸（2026-11-09）：協商出它的連線收 <c>deviceTree</c>、<c>deviceFetchMany</c> 與帶 <c>subfolder</c> 的 <c>devicePut</c>。不對應任何 hub 方法</summary>
    public const string Xfer2 = "xfer2";

    /// <summary>
    /// 遠端桌面（docs/sync-protocol.md 14.16；2026-11-09）：協商出它的連線收 <c>deskOpen</c>（被看的一方）、可以發起（看的一方），
    /// 之後的影格、輸入、結束走 <c>DeskUp</c>／<c>DeskDown</c>
    /// </summary>
    public const string Desk1 = "desk1";

    /// <summary>
    /// 輸入框的草稿（docs/sync-protocol.md 14.17；2026-11-10）：協商出它的連線可以呼叫 <c>ComposerDraftPublish</c>（這台輸入框打到一半的字）、
    /// 收 <c>ComposerDraftUpdate</c>（其他電腦與瀏覽器版的草稿）
    /// </summary>
    public const string Draft1 = "draft1";

    /// <summary>
    /// 臨時對話（2026-11-11）：協商出它的連線收帶 <c>ephemeral</c> 的 <c>conversationCreate</c>（瀏覽器版右上角的對話鈕開的；不列在清單、關掉就刪掉）。不對應任何 hub 方法
    /// </summary>
    public const string Quick1 = "quick1";

    /// <summary>
    /// 網頁上傳的附件可以移除（2026-11-13）：協商出它的連線收 <c>removeAttachment</c> 的 <c>id</c> 是上傳 id（網頁的晶片只知道上傳 id，
    /// 不知道那台的附件 id）——還在下載或匯入的也不再加進輸入區。不對應任何 hub 方法；舊版的桌面找不到這個 id 會當成沒事，伺服器不送給它們
    /// </summary>
    public const string Att1 = "att1";

    /// <summary>
    /// Myriagent 的定時訊息可以從其他電腦與瀏覽器版設定（2026-11-13）：協商出它的連線收 <c>timedSave</c>、<c>timedAction</c>。
    /// 不對應任何 hub 方法；桌面跟著 sched1 宣告，伺服器只把這兩個指令送給協商出它的電腦
    /// </summary>
    public const string Timed1 = "timed1";

    /// <summary>這版認得的功能（不認得的名稱照收、不啟用：交集裡不會有它）</summary>
    public static IReadOnlyList<string> Known { get; } = [Conv1, Live1, Live2, Cmd1, Media1, Update1, Peer1, Prov1, Stat1, Tok1, Tok2, Log1, Hand1, Exec1, Sched1, Collab1, Xfer1, Xfer2, Desk1, Draft1, Quick1, Att1, Timed1];

    public const int MaxFeatures = 32;

    public const int MaxNameLength = 32;

    private const string NameChars = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>1～32 個 <c>[a-z0-9]</c></summary>
    public static bool IsValidName(string? name)
        => name is { Length: > 0 and <= MaxNameLength } && name.AsSpan().IndexOfAnyExcept(NameChars) < 0;

    /// <summary>
    /// Hello 的 <c>features</c> 合不合格（伺服器的 <c>DeviceHubRules.CheckHello</c>：不合格整個 Hello 回 <c>invalid</c>）：
    /// null 可以（沒宣告）；否則 ≤<see cref="MaxFeatures"/> 項、每項都是 <see cref="IsValidName"/>（重複的照收，協商時去掉）
    /// </summary>
    public static bool IsValid(IReadOnlyList<string>? features)
    {
        if (features is null)
            return true;
        if (features.Count > MaxFeatures)
            return false;
        foreach (var name in features)
        {
            if (!IsValidName(name))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 雙方實際啟用的功能：裝置宣告、伺服器也宣告的，依裝置宣告的順序、去重；不合格的名稱略過，最多 <see cref="MaxFeatures"/> 項。
    /// 任一方是 null（沒宣告）就是空的。<see cref="Hand1"/> 要有 <see cref="Log1"/>：交集裡沒有 log1 時拿掉 hand1（交接要靠日誌驗證逐字稿，
    /// 兩邊的宣告本來就照這個條件，這裡是保險）
    /// </summary>
    public static IReadOnlyList<string> Negotiate(IReadOnlyList<string>? device, IReadOnlyList<string>? server)
    {
        if (device is null || server is null)
            return [];
        var offered = server.Where(IsValidName).ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var name in device)
        {
            if (result.Count == MaxFeatures)
                break;
            if (IsValidName(name) && offered.Contains(name) && !result.Contains(name, StringComparer.Ordinal))
                result.Add(name);
        }

        if (!result.Contains(Log1, StringComparer.Ordinal))
            result.Remove(Hand1);
        return result;
    }

    /// <summary>協商出的功能裡有沒有這一項（區分大小寫）</summary>
    public static bool Has(IReadOnlyList<string>? negotiated, string feature)
        => negotiated is not null && negotiated.Contains(feature, StringComparer.Ordinal);

    /// <summary>
    /// 裝置 hub 的方法要協商出哪個功能才能送（docs/sync-protocol.md 14.3、14.6）：第 3 階段的方法回 null（一律可以）；
    /// 兩邊都照它決定送不送，裝置收到沒協商的訊息也照它略過。不是 <see cref="DeviceHubMethods"/> 的名稱丟例外（新方法一定要決定功能）
    /// </summary>
    public static string? RequiredFor(string method) => method switch
    {
        DeviceHubMethods.Hello or DeviceHubMethods.Prove or DeviceHubMethods.PublishBatch or DeviceHubMethods.Pull or DeviceHubMethods.Heartbeat
            or DeviceHubMethods.UsagePublish or DeviceHubMethods.GetTrust or DeviceHubMethods.RefreshAccessToken
            or DeviceHubMethods.BatchAck or DeviceHubMethods.Notify or DeviceHubMethods.UsageAssign or DeviceHubMethods.TrustChanged
            or DeviceHubMethods.ServerNotice => null,
        DeviceHubMethods.SnapshotRequest => Conv1,
        DeviceHubMethods.LiveRelay or DeviceHubMethods.WatchChanged => Live1,
        DeviceHubMethods.LiveDelta or DeviceHubMethods.LiveResync => Live2,
        DeviceHubMethods.CommandDelivery or DeviceHubMethods.CommandResult or DeviceHubMethods.CommandOutput => Cmd1,
        DeviceHubMethods.MediaRequest => Media1,
        DeviceHubMethods.PeerCmd or DeviceHubMethods.PeerReply => Peer1,
        DeviceHubMethods.StatsPublish or DeviceHubMethods.StatsUpdate => Stat1,
        DeviceHubMethods.TokensPublish => Tok1,
        DeviceHubMethods.HandoffPreview or DeviceHubMethods.HandoffStart or DeviceHubMethods.HandoffCancel or DeviceHubMethods.HandoffProgress
            or DeviceHubMethods.HandoffReady or DeviceHubMethods.HandoffRequest or DeviceHubMethods.HandoffUpdate => Hand1,
        DeviceHubMethods.LogRepair => Log1,
        DeviceHubMethods.TokensUpdate => Tok2,
        DeviceHubMethods.SchedulesPublish or DeviceHubMethods.SchedulesUpdate => Sched1,
        DeviceHubMethods.ExecOutput or DeviceHubMethods.ExecCancel or DeviceHubMethods.ExecReply or DeviceHubMethods.ExecAbort => Exec1,
        DeviceHubMethods.CollabCmd or DeviceHubMethods.CollabUpdate => Collab1,
        DeviceHubMethods.DeskUp or DeviceHubMethods.DeskDown => Desk1,
        DeviceHubMethods.ComposerDraftPublish or DeviceHubMethods.ComposerDraftUpdate => Draft1,
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "不認得的裝置 hub 方法"),
    };
}
