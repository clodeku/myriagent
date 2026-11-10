using Myriagent.Agents;
using Myriagent.Remote;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 送出時挑的電腦（<see cref="DraftCommandRules.DefaultDevice"/>）：挑到的電腦，或立即拒絕（<see cref="RouteRejection.CmdError"/> 一律是 false）
/// </summary>
internal sealed record DraftDeviceChoice(string? DeviceId, RouteRejection? Rejection);

/// <summary>
/// 伺服器草稿上的遠端指令（WP4.5 計畫步 9；D33、K8；web-protocol 第六節第 9 點）。純函式：
/// <list type="bullet">
/// <item><see cref="Apply"/>：<c>setOption</c>、<c>setAccount</c>、<c>setFolder</c>、<c>resetFolder</c>、<c>rename</c> 改草稿（同桌面草稿的語意：模型、強度、
/// 權限模式的「預設」是 null；換到另一家代理時模型、強度、權限模式回到預設；資料夾只收本機磁碟的完整路徑；標題去掉前後空白、最多 80 字，空的不改）。
/// 形狀照型錄（草稿 id 不是線上 id，以合格的佔位 id 代入後套同一份 <see cref="RemoteCommandShapeRules.Check"/>）。草稿上的其他指令回 invalid。</item>
/// <item><see cref="ConversationCreate"/>：第一次 send 組成的 <c>conversationCreate</c>（帳戶是草稿的邏輯帳戶，送出前由路由換成那台的本機 id；
/// 沒選權限模式是每步詢問，經代理種類的正規化；沒有的欄位省略；<c>text</c> 是空字串時省略）。</item>
/// <item><see cref="DefaultDevice"/>：草稿沒指定電腦時挑哪台。</item>
/// </list>
/// </summary>
internal static class DraftCommandRules
{
    public const string SetOption = "setOption", SetAccount = "setAccount", SetFolder = "setFolder", ResetFolder = "resetFolder", Rename = "rename";

    /// <summary>草稿已送出、新對話還在建立中時又送 send 的說明（立即拒絕 busy，D33）</summary>
    public const string CreatingMessage = "新對話還在建立中，請稍候再送";

    /// <summary>草稿的帳戶已從目錄刪除（立即拒絕 invalid）</summary>
    public const string AccountGoneMessage = "這個帳戶已從目錄刪除，請換一個帳戶再送";

    /// <summary>草稿標題的上限（同桌面 AgentHub.Rename）</summary>
    public const int MaxTitleLength = 80;

    /// <summary>草稿上可以改的指令（其他指令回 invalid）</summary>
    public static IReadOnlySet<string> EditCommands { get; } = new HashSet<string>(StringComparer.Ordinal) { SetOption, SetAccount, SetFolder, ResetFolder, Rename };

    /// <summary>形狀檢查時代入的線上對話 id（草稿 id 不是線上 id 的形狀）</summary>
    private const string PlaceholderConversationId = "00000000000000000000000000000000";

    private const string ConversationIdField = "conversationId";

    /// <summary>
    /// 套用一個草稿上的修改指令；回傳改好的草稿（<c>with</c> 的複本），或 <see cref="BrowserErrorCodes.Invalid"/>（形狀不合格、不是可以改的指令、
    /// 帳戶不在 <paramref name="accounts"/>、權限模式不是這個代理的、資料夾不是本機磁碟的完整路徑）
    /// </summary>
    /// <param name="accounts">閘道最後一份帳戶快照（<see cref="BrowserGateway.DraftAccounts"/>）</param>
    public static (BrowserDraft? Draft, string? Error) Apply(BrowserDraft draft, string name, JObject cmd, IReadOnlyList<DraftAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(accounts);
        if (!EditCommands.Contains(name) || RemoteCommandCatalog.Find(name) is not { } spec)
            return Invalid;
        var projected = RemoteCommandCatalog.ApplyLimits(CommandRouteRules.Project(cmd, spec));
        projected[ConversationIdField] = PlaceholderConversationId;
        if (RemoteCommandShapeRules.Check(projected) is not null)
            return Invalid;

        switch (name)
        {
            case SetOption:
            {
                var key = (string)projected["key"]!;
                var value = Normalize((string)projected["value"]!);
                switch (key)
                {
                    case "model":
                        return (draft with { Model = value }, null);
                    case "effort":
                        return (draft with { Effort = value }, null);
                    case "permission":
                        if (value is null)
                            return (draft with { PermissionMode = null }, null);
                        // 型錄已確認是權限模式的名稱；只收這個代理有的模式（網頁的選單照 header.permissionModes）
                        if (!AgentPermissionModes.For(draft.Provider).Any(o => string.Equals(o.Mode.ToString(), value, StringComparison.Ordinal)))
                            return Invalid;
                        return (draft with { PermissionMode = value }, null);
                    default:
                        return Invalid;
                }
            }
            case SetAccount:
            {
                var accountId = (string)projected["accountId"]!;
                if (accounts.FirstOrDefault(a => string.Equals(a.Id, accountId, StringComparison.Ordinal)) is not { } account)
                    return Invalid;
                // 同桌面：換到另一家代理時模型、強度、權限模式回到預設（新對話的預設在那台電腦上）
                return account.Provider == draft.Provider
                    ? (draft with { AccountId = account.Id }, null)
                    : (draft with { AccountId = account.Id, Provider = account.Provider, Model = null, Effort = null, PermissionMode = null }, null);
            }
            case SetFolder:
            {
                var path = ((string)projected["path"]!).Trim();
                if (!AgentAttachmentRules.IsLocalDrivePath(path))
                    return Invalid;
                return (draft with { WorkingDirectory = path, CustomFolder = true }, null);
            }
            case ResetFolder:
                return (draft with { WorkingDirectory = null, CustomFolder = false }, null);
            case Rename:
            {
                var title = ((string)projected["title"]!).Trim();
                if (title.Length == 0)
                    return (draft, null);
                return (draft with { Title = title.Length <= MaxTitleLength ? title : title[..MaxTitleLength] }, null);
            }
            default:
                return Invalid;
        }
    }

    /// <summary>
    /// 第一次 send 組成的 <c>conversationCreate</c>（D33、契約 C17）：鍵照型錄的順序；<c>accountId</c> 是草稿的帳戶（邏輯帳戶 id，路由換成那台的本機 id）、
    /// <c>permissionMode</c> 沒選時是每步詢問（經 <see cref="ConversationViewRules.Permission"/>，同標頭顯示的值）、<c>model</c>／<c>effort</c>／<c>folder</c>／<c>title</c>
    /// 沒有就省略、<c>text</c> 是空字串時省略（電腦把空字串與省略都當成沒有文字）。<c>deviceId</c> 不寫（型錄沒有這個欄位，目標只在信封的 <c>dev</c>）
    /// </summary>
    public static JObject ConversationCreate(BrowserDraft draft, string text)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(text);
        var cmd = new JObject
        {
            ["cmd"] = RemoteCommandCatalog.ConversationCreate,
            ["accountId"] = draft.AccountId,
            ["permissionMode"] = ConversationViewRules.Permission(draft.Provider, draft.PermissionMode).ToString(),
        };
        if (draft.Model is { } model)
            cmd["model"] = model;
        if (draft.Effort is { } effort)
            cmd["effort"] = effort;
        if (draft.WorkingDirectory is { } folder)
            cmd["folder"] = folder;
        if (draft.Title is { } title)
            cmd["title"] = title;
        if (text.Length > 0)
            cmd["text"] = text;
        // 臨時對話（quickChat 開的草稿）：那台建成不列在清單、關掉就刪掉的對話
        if (draft.Ephemeral)
            cmd["ephemeral"] = true;
        return cmd;
    }

    /// <summary>
    /// 草稿沒指定電腦時的預設（D33；設計 4.9.2「該邏輯帳戶 Ready 而且在線、最近用過的那台」）：<paramref name="logicalAccountId"/> 連結狀態是 ready、
    /// 未撤銷、有已證明而且協商出 cmd1 的連線的電腦裡，連線的 <see cref="ConnectionFact.Order"/> 最大的那台（同值依裝置 id）。都沒有時立即拒絕：
    /// <list type="bullet">
    /// <item>沒有任何未撤銷的連結：invalid（<see cref="CommandRouteRules.NoDevicesMessage"/>）。</item>
    /// <item>有在線（已證明）而沒有協商出 cmd1 的電腦：unsupported（要先更新）。</item>
    /// <item>有在線、協商出 cmd1，但連結不是 ready 的電腦：invalid（<see cref="CommandRouteRules.NoLinkMessage"/>：那台電腦沒有登入這個帳戶）。</item>
    /// <item>其他：offline。</item>
    /// </list>
    /// </summary>
    public static DraftDeviceChoice DefaultDevice(string logicalAccountId, IReadOnlyList<AccountLinkFact> links,
        Func<string, DeviceFact?> device, Func<string, IReadOnlyList<ConnectionFact>> connections)
    {
        ArgumentNullException.ThrowIfNull(logicalAccountId);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(connections);
        var linked = links
            .Where(l => string.Equals(l.LogicalAccountId, logicalAccountId, StringComparison.Ordinal) && device(l.DeviceId) is { Revoked: false })
            .OrderBy(l => l.DeviceId, StringComparer.Ordinal)
            .ToList();
        if (linked.Count == 0)
            return Reject(RemoteResultCodes.Invalid, CommandRouteRules.NoDevicesMessage);

        string? best = null;
        var bestOrder = long.MinValue;
        var onlineWithoutCmd1 = false;
        var onlineNotReady = false;
        foreach (var link in linked)
        {
            var proven = connections(link.DeviceId).Where(c => c.Proven).ToList();
            var negotiated = proven.Where(c => c.Cmd1).ToList();
            if (negotiated.Count == 0)
            {
                onlineWithoutCmd1 |= proven.Count > 0;
                continue;
            }
            if (!string.Equals(link.Status, UsageRules.LinkReady, StringComparison.Ordinal))
            {
                onlineNotReady = true;
                continue;
            }
            var order = negotiated.Max(c => c.Order);
            if (best is null || order > bestOrder)
                (best, bestOrder) = (link.DeviceId, order);
        }
        if (best is not null)
            return new DraftDeviceChoice(best, null);
        if (onlineWithoutCmd1)
            return Reject(RemoteResultCodes.Unsupported);
        if (onlineNotReady)
            return Reject(RemoteResultCodes.Invalid, CommandRouteRules.NoLinkMessage);
        return Reject(RemoteResultCodes.Offline);
    }

    private static (BrowserDraft? Draft, string? Error) Invalid => (null, BrowserErrorCodes.Invalid);

    /// <summary>模型、強度、權限模式的值：空白、空字串與「default」是 null（帳戶預設），其他去掉前後空白（同桌面 AgentHub.SetOption）</summary>
    private static string? Normalize(string value) =>
        string.IsNullOrWhiteSpace(value) || value == ConversationViewRules.DefaultValue ? null : value.Trim();

    private static DraftDeviceChoice Reject(string code, string? message = null) => new(null, new RouteRejection(code, CmdError: false, message));
}
