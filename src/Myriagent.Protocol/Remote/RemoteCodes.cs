namespace Myriagent.Remote;

/// <summary>遠端指令結果的狀態（docs/sync-protocol.md 14.6 的 <c>CommandResult.state</c>、web-protocol 的 <c>commandResult.state</c>）</summary>
internal static class RemoteResultStates
{
    /// <summary>電腦的中樞已接受（例如 send 已建立項目或排進佇列），不是代理這一輪結束</summary>
    public const string Done = "done";

    public const string Rejected = "rejected";

    /// <summary>30 秒內沒有完成信號，或執行中當機：指令可能已執行，瀏覽器不得自動重送（D21）</summary>
    public const string Unknown = "unknown";

    public static IReadOnlyList<string> All { get; } = [Done, Rejected, Unknown];
}

/// <summary>
/// 遠端指令被拒的代碼（web-protocol 第一節 <c>commandResult</c> 的表；電腦與伺服器共用）。
/// 瀏覽器看到的就是這些；閘門停在哪一步另記 <see cref="RemoteGateSteps"/>（只進遠端操作紀錄）
/// </summary>
internal static class RemoteResultCodes
{
    /// <summary>目標電腦不在線（遠端指令不排隊，D16）</summary>
    public const string Offline = "offline";

    /// <summary>那台的本機政策不允許（不是 Control、遠端已暫停、要完全自動但沒有允許），或對話的同步政策不允許</summary>
    public const string Policy = "policy";

    /// <summary>要先重新輸入密碼（伺服器的工作階段；目前兩個時間窗都是 null，不會發生，Q2）</summary>
    public const string StepUpRequired = "step_up_required";

    /// <summary>卡片已被處理、內容變了（rd1 摘要不符），或對話的程序已重開</summary>
    public const string Stale = "stale";

    /// <summary>那台已不是這個對話的擁有者（交接、租約換了），或對話是其他電腦的副本</summary>
    public const string NotOwner = "not_owner";

    public const string RateLimited = "rate_limited";

    /// <summary>電腦剛連上、還在交握，暫時收不下更多指令（早到緩衝滿了，D49）</summary>
    public const string Busy = "busy";

    /// <summary>指令在途中過期（伺服器時間的界線，或電腦的時間推算不可用，D4）</summary>
    public const string Expired = "expired";

    /// <summary>同一個指令 id 已經執行過不同的內容（D17）</summary>
    public const string Conflict = "conflict";

    /// <summary>形狀不對（不認得的鍵、型別、數值列舉、超過上限）、或不是遠端可以做的指令</summary>
    public const string Invalid = "invalid";

    /// <summary>那台電腦的程式還不支援這個遠端指令（WP4.1 的預設授權者一律如此，WP4.4 之前）</summary>
    public const string Unsupported = "unsupported";

    public static IReadOnlyList<string> All { get; } =
        [Offline, Policy, StepUpRequired, Stale, NotOwner, RateLimited, Busy, Expired, Conflict, Invalid, Unsupported];
}

/// <summary>
/// 電腦端中樞閘門 <c>ExecuteRemoteAsync</c> 的各步（設計 8.11、docs/sync-protocol.md 14.6 的表）：檢查順序固定，
/// 失敗時以 <c>Reject(code, message, step)</c> 結束，<c>step</c> 只進遠端操作紀錄
/// </summary>
internal static class RemoteGateSteps
{
    /// <summary>5a 型錄的路由是 Owner／Device 執行</summary>
    public const string Route = "route";

    /// <summary>5b 鍵白名單、型別、數值列舉一律拒絕（RemoteCommandShapeRules）</summary>
    public const string Shape = "shape";

    /// <summary>5c 對話存在、不是草稿或副本、擁有者是這台、epoch 相符、同步政策是 full</summary>
    public const string Conversation = "conversation";

    /// <summary>5d 依這台的事實重算層級（RemoteTierRules），不會拒絕</summary>
    public const string Tier = "tier";

    /// <summary>5e 本機政策（RemotePolicyRules）</summary>
    public const string Policy = "policy";

    /// <summary>5f 驗證時間（RemoteAuthRules.IsFresh）</summary>
    public const string Fresh = "fresh";

    /// <summary>5g 預設密碼（Q5-B：不拒絕，只記錄與警告）</summary>
    public const string DefaultPassword = "defaultPassword";

    /// <summary>5h 卡片摘要與 RequestKey（RemoteCardRules）</summary>
    public const string Card = "card";

    /// <summary>5i 登記的授權者（WP4.1 的預設一律 unsupported）</summary>
    public const string Authorize = "authorize";

    /// <summary>5j 交給處理者、等完成信號</summary>
    public const string Handler = "handler";

    /// <summary>閘門的順序</summary>
    public static IReadOnlyList<string> Order { get; } = [Route, Shape, Conversation, Tier, Policy, Fresh, DefaultPassword, Card, Authorize, Handler];
}
