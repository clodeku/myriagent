using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>帳戶連結的事實（sync.db <c>account_links</c> 的一列）：邏輯帳戶在某台電腦上的本機帳戶 id 與連結狀態</summary>
internal sealed record AccountLinkFact(string LogicalAccountId, string DeviceId, string LocalAccountId, string Status);

/// <summary>裝置目錄的事實：名稱（給 toast 用）與是否已撤銷（撤銷的不當目標）</summary>
internal sealed record DeviceFact(string DeviceId, string Name, bool Revoked);

/// <summary>
/// 路由判斷的拒絕。<paramref name="CmdError"/>＝true：當成 <c>Cmd</c> 的 error 回（<see cref="BrowserErrorCodes"/> 的 invalid、conflict、
/// not_available，D2）；false：立即拒絕（<c>Cmd</c> 回 accepted，接著送 <c>commandResult rejected</c>，代碼是 <see cref="RemoteResultCodes"/>，D3）
/// </summary>
/// <param name="Message">給人看的說明（立即拒絕時當 toast 與 <c>commandResult.message</c>）；null＝用代碼的文字</param>
internal sealed record RouteRejection(string Code, bool CmdError, string? Message = null);

/// <summary>Device 路由的目標種類（D5）</summary>
internal enum DeviceTargetKind
{
    /// <summary>一台電腦（非 durable）</summary>
    Single,

    /// <summary>所有電腦登出：連結的每台未撤銷電腦各一列（durable，D26）</summary>
    FanOut,
}

/// <summary>Device 路由的目標（D5）；<see cref="Rejection"/> 不是 null 時 <see cref="DeviceIds"/> 是空的</summary>
internal sealed record DeviceTargets(DeviceTargetKind Kind, IReadOnlyList<string> DeviceIds, RouteRejection? Rejection);

/// <summary>目標電腦一條裝置連線的快照（<c>DeviceConnection</c>）：<paramref name="CanSign"/> 是 <c>CanSignEnvelopes(HubIdentity.Spki)</c></summary>
internal sealed record ConnectionFact(string ConnectionId, long Order, bool Proven, bool Cmd1, bool CanSign);

/// <summary>
/// 選連線的結果（D7）：要送的連線，或立即拒絕的代碼（<see cref="RemoteResultCodes.Offline"/>／<see cref="RemoteResultCodes.Unsupported"/>）；
/// <see cref="Abort"/> 是要中斷的連線（協商出 cmd1 但 Hello 時的身分公鑰不是目前的，不論有沒有選到連線都要中斷）
/// </summary>
internal sealed record ConnectionChoice(string? ConnectionId, string? RejectCode, IReadOnlyList<string> Abort);

/// <summary>
/// 瀏覽器遠端指令的路由規則（WP4.5 計畫 D1、D4～D8）：哪些指令轉給電腦、投影到型錄欄位、對話與 Device 路由的目標、
/// 改寫目標與帳戶 id、選連線。純函式，不碰資料庫與連線；流程在 <c>CommandRouter</c>（步 6）
/// </summary>
internal static class CommandRouteRules
{
    /// <summary>MapAccounts 找不到那台的連結時的說明（立即拒絕 invalid，D6）</summary>
    public const string NoLinkMessage = "那台電腦沒有登入這個帳戶";

    /// <summary>所有電腦登出時沒有任何未撤銷的電腦連結到這個帳戶（立即拒絕 invalid，D26）</summary>
    public const string NoDevicesMessage = "沒有任何電腦連結到這個帳戶";

    /// <summary>型錄唯一 <see cref="RemoteCommandSpec.DurableAllowed"/> 的指令：沒帶 deviceId 時是所有電腦登出（D5（b））</summary>
    public const string AccountLogout = "accountLogout";

    private const string CmdKey = "cmd";

    private const string DeviceIdField = "deviceId";

    /// <summary>
    /// 延後的指令（D1）：照舊回 not_available、不轉給電腦。WP4.5 合併時是 WP4.7 的上傳、列檔、@ 提示、取檔四個；
    /// WP4.7 合併時拿掉了（WP4.7 計畫步 13），現在是空的。之後的工作包要先合併伺服器、晚一點才開放的指令時加在這裡
    /// </summary>
    public static IReadOnlySet<string> Deferred { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>由伺服器轉給電腦：型錄的路由是 Owner／Device，而且不在 <see cref="Deferred"/>（伺服器自己處理的、本機專屬的、沒列名的都是 false）</summary>
    public static bool Routes(string? name)
        => RemoteCommandCatalog.Find(name) is { Route: RemoteRoute.Owner or RemoteRoute.Device } && !Deferred.Contains(name!);

    /// <summary>
    /// 只留 <c>cmd</c> 與型錄欄位（鍵 Ordinal 比對；順序是 <c>cmd</c> 再照型錄），值深複製、不改原物件。
    /// 網頁的 cmd 物件「不認得的欄位忽略」（例如 <c>paneId</c>），電腦的形狀檢查卻是嚴格的（D8）
    /// </summary>
    public static JObject Project(JObject cmd, RemoteCommandSpec spec)
    {
        var projected = new JObject();
        if (cmd.Property(CmdKey, StringComparison.Ordinal) is { } name)
            projected.Add(CmdKey, name.Value.DeepClone());
        foreach (var field in spec.Fields)
        {
            if (cmd.Property(field.Name, StringComparison.Ordinal) is { } property)
                projected.Add(field.Name, property.Value.DeepClone());
        }

        return projected;
    }

    /// <summary>
    /// 指令參照的邏輯帳戶（路由要先查它存不存在，<see cref="ResolveDevices"/> 的 <c>logicalAccountExists</c>）：
    /// 種類 AccountId 而且值是 <c>la_…</c> 的欄位，或 LogicalAccountId 欄位的值；都沒有回 null
    /// </summary>
    public static string? LogicalAccountRef(RemoteCommandSpec spec, JObject cmd)
    {
        foreach (var field in spec.Fields)
        {
            if (field.Kind is not (RemoteFieldKind.AccountId or RemoteFieldKind.LogicalAccountId))
                continue;
            if (RemoteCommandShapeRules.IsString(cmd[field.Name], out var id) && SyncIds.IsLogicalAccountId(id))
                return id;
        }

        return null;
    }

    /// <summary>
    /// Owner 路由的對話檢查（D4）：對話不存在、已刪除或沒有擁有者是 <c>Cmd conflict</c>；擁有者裝置不存在或已撤銷是立即拒絕 not_owner；
    /// 同步政策不是 <c>full</c>（不認得的值也算）時只收 <c>stop</c>，其他立即拒絕 policy（M9）。null＝可以轉給擁有者
    /// </summary>
    /// <param name="ownerDevice">裝置目錄裡 <see cref="ConversationOwnership.OwnerDeviceId"/> 那台（不在目錄是 null）</param>
    public static RouteRejection? CheckConversation(ConversationOwnership? owner, DeviceFact? ownerDevice, string name)
    {
        if (owner is null)
            return new RouteRejection(BrowserErrorCodes.Conflict, CmdError: true);
        if (ownerDevice is null || ownerDevice.Revoked || !string.Equals(ownerDevice.DeviceId, owner.OwnerDeviceId, StringComparison.Ordinal))
            return new RouteRejection(RemoteResultCodes.NotOwner, CmdError: false);
        if (!string.Equals(owner.SyncPolicy, ConversationSyncPolicies.Full, StringComparison.Ordinal)
            && !string.Equals(name, RemoteCommandCatalog.Stop, StringComparison.Ordinal))
            return new RouteRejection(RemoteResultCodes.Policy, CmdError: false);
        return null;
    }

    /// <summary>
    /// Device 路由的目標（D5），依序：
    /// <list type="number">
    /// <item>參照的邏輯帳戶不存在或已刪除：<c>Cmd conflict</c>（不論哪一種）。</item>
    /// <item>有 deviceId：那台要在目錄裡而且未撤銷（否則 <c>Cmd invalid</c>），就是那台（非 durable）。</item>
    /// <item>所有電腦登出（<see cref="AccountLogout"/>、沒有 deviceId、accountId 是存在的 <c>la_</c>）：連結的每台未撤銷電腦，依 id 排序；一台都沒有是立即拒絕 invalid。</item>
    /// <item>沒有 deviceId、accountId 是存在的 <c>la_</c>、只連結到一台未撤銷的電腦：就是那台。</item>
    /// <item>其他（沒有帳戶、<c>accountRefresh {accountId:null}</c>、本機 id、連結到兩台以上、<c>accountAdd</c> 沒有 deviceId）：<c>Cmd not_available</c>，和第 3 階段相同。</item>
    /// </list>
    /// 連結的狀態不影響目標（電腦自己判斷帳戶的狀態）
    /// </summary>
    /// <param name="cmd">投影後的指令（<see cref="Project"/>）</param>
    /// <param name="logicalAccountExists">null＝指令沒有 <c>la_</c> 參照（<see cref="LogicalAccountRef"/> 是 null）；false＝不存在或已刪除</param>
    /// <param name="links">這個邏輯帳戶的連結（別的帳戶的連結會被略過）</param>
    /// <exception cref="ArgumentException">路由不是 Device，或有 <c>la_</c> 參照卻沒查它存不存在</exception>
    public static DeviceTargets ResolveDevices(RemoteCommandSpec spec, JObject cmd, bool? logicalAccountExists,
        IReadOnlyList<AccountLinkFact> links, Func<string, DeviceFact?> device)
    {
        if (spec.Route != RemoteRoute.Device)
            throw new ArgumentException($"{spec.Name} 的路由不是 Device", nameof(spec));
        var reference = LogicalAccountRef(spec, cmd);
        if (reference is not null && logicalAccountExists is null)
            throw new ArgumentException("指令參照了邏輯帳戶，要先查它存不存在", nameof(logicalAccountExists));
        if (reference is not null && logicalAccountExists == false)
            return Reject(BrowserErrorCodes.Conflict, cmdError: true);

        if (HasField(spec, DeviceIdField) && RemoteCommandShapeRules.IsString(cmd[DeviceIdField], out var deviceId))
        {
            return device(deviceId) is { Revoked: false } target && string.Equals(target.DeviceId, deviceId, StringComparison.Ordinal)
                ? new DeviceTargets(DeviceTargetKind.Single, [deviceId], null)
                : Reject(BrowserErrorCodes.Invalid, cmdError: true);
        }

        var accountField = spec.Fields.FirstOrDefault(f => f.Kind == RemoteFieldKind.AccountId);
        if (accountField is null || !RemoteCommandShapeRules.IsString(cmd[accountField.Name], out var accountId) || !SyncIds.IsLogicalAccountId(accountId))
            return Reject(BrowserErrorCodes.NotAvailable, cmdError: true);

        var linked = links
            .Where(l => string.Equals(l.LogicalAccountId, accountId, StringComparison.Ordinal))
            .Select(l => l.DeviceId)
            .Distinct(StringComparer.Ordinal)
            .Where(id => device(id) is { Revoked: false })
            .Order(StringComparer.Ordinal)
            .ToList();
        if (string.Equals(spec.Name, AccountLogout, StringComparison.Ordinal))
        {
            return linked.Count == 0
                ? Reject(RemoteResultCodes.Invalid, cmdError: false, NoDevicesMessage)
                : new DeviceTargets(DeviceTargetKind.FanOut, linked, null);
        }

        return linked.Count == 1
            ? new DeviceTargets(DeviceTargetKind.Single, linked, null)
            : Reject(BrowserErrorCodes.NotAvailable, cmdError: true);
    }

    /// <summary>
    /// 型錄有 deviceId 欄位的才寫入 <c>deviceId</c>＝目標（D8 第 4 步；<c>conversationCreate</c> 與 Owner 路由沒有這個欄位，目標只在信封的 <c>dev</c>）。
    /// 回傳複本
    /// </summary>
    public static JObject WithTarget(JObject cmd, RemoteCommandSpec spec, string deviceId)
    {
        var copy = (JObject)cmd.DeepClone();
        if (HasField(spec, DeviceIdField))
            copy[DeviceIdField] = deviceId;
        return copy;
    }

    /// <summary>
    /// 帳戶 id 換算（D6）：種類 AccountId、值是 <c>la_…</c> 的欄位換成那個邏輯帳戶在目標電腦上的本機 id。
    /// 那台沒有連結時立即拒絕 invalid（<see cref="NoLinkMessage"/>）；本機 id、null、LogicalAccountId 欄位原樣。回傳複本
    /// </summary>
    public static (JObject? Cmd, RouteRejection? Rejection) MapAccounts(JObject cmd, RemoteCommandSpec spec, string deviceId, IReadOnlyList<AccountLinkFact> links)
    {
        var copy = (JObject)cmd.DeepClone();
        foreach (var field in spec.Fields)
        {
            if (field.Kind != RemoteFieldKind.AccountId || !RemoteCommandShapeRules.IsString(copy[field.Name], out var id) || !SyncIds.IsLogicalAccountId(id))
                continue;
            var link = links.FirstOrDefault(l => string.Equals(l.LogicalAccountId, id, StringComparison.Ordinal)
                                                 && string.Equals(l.DeviceId, deviceId, StringComparison.Ordinal));
            if (link is null)
                return (null, new RouteRejection(RemoteResultCodes.Invalid, CmdError: false, NoLinkMessage));
            copy[field.Name] = link.LocalAccountId;
        }

        return (copy, null);
    }

    /// <summary>
    /// 輸出（<c>accountLogin</c>）的 accountId 要換回的、瀏覽器看到的 id（D6）：型錄有 AccountId 欄位的是指令原本的 accountId
    /// （可能是 null）；沒有而有 LogicalAccountId 欄位的是它的值（<c>accountAdd</c> 沒帶時 null＝原樣轉送）；都沒有回 null
    /// </summary>
    /// <param name="browserCmd">瀏覽器送來的指令（換算之前）</param>
    public static string? BrowserAccountId(RemoteCommandSpec spec, JObject browserCmd)
    {
        var field = spec.Fields.FirstOrDefault(f => f.Kind == RemoteFieldKind.AccountId)
                    ?? spec.Fields.FirstOrDefault(f => f.Kind == RemoteFieldKind.LogicalAccountId);
        return field is not null && RemoteCommandShapeRules.IsString(browserCmd[field.Name], out var id) ? id : null;
    }

    /// <summary>
    /// 選連線（D7）：只看已證明的連線（不用 <c>IsOnline</c>：它把還沒證明的也算進去）。一條都沒有是 offline；都沒協商 cmd1 是 unsupported；
    /// 協商出 cmd1 而不能簽的全部列入 <see cref="ConnectionChoice.Abort"/>；剩下的取 <see cref="ConnectionFact.Order"/> 最大的，沒有剩下是 offline
    /// （重連時會依輪換聲明改釘）
    /// </summary>
    public static ConnectionChoice ChooseConnection(IReadOnlyList<ConnectionFact> connections)
    {
        var proven = connections.Where(c => c.Proven).ToList();
        if (proven.Count == 0)
            return new ConnectionChoice(null, RemoteResultCodes.Offline, []);
        var negotiated = proven.Where(c => c.Cmd1).ToList();
        if (negotiated.Count == 0)
            return new ConnectionChoice(null, RemoteResultCodes.Unsupported, []);
        var abort = negotiated.Where(c => !c.CanSign).Select(c => c.ConnectionId).ToList();
        var best = negotiated.Where(c => c.CanSign).MaxBy(c => c.Order);
        return best is null
            ? new ConnectionChoice(null, RemoteResultCodes.Offline, abort)
            : new ConnectionChoice(best.ConnectionId, null, abort);
    }

    private static bool HasField(RemoteCommandSpec spec, string name)
        => spec.Fields.Any(f => string.Equals(f.Name, name, StringComparison.Ordinal));

    private static DeviceTargets Reject(string code, bool cmdError, string? message = null)
        => new(DeviceTargetKind.Single, [], new RouteRejection(code, cmdError, message));
}
