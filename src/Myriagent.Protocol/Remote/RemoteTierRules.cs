using Myriagent.Agents;
using Newtonsoft.Json.Linq;

namespace Myriagent.Remote;

/// <summary>誰在算層級：伺服器只是提示（事實不明時取可能的最低），電腦取最高（D5）</summary>
internal enum RemoteSide
{
    Server,
    Device,
}

/// <summary>
/// 算層級用的事實（D5、D9、D14）：null＝不知道（伺服器端當成最低、電腦端當成最高）。電腦的閘門 5d 以自己的狀態填（WP4.4），
/// 伺服器只知道它看得到的（例如卡片的 risk）
/// </summary>
/// <param name="Mode">對話目前的權限模式（代理輸入用）</param>
/// <param name="PreApproved">對話（新對話是那個帳戶）有預先放行規則（DetectPreApproved，WP4.4；偵測不到算有，Q3-A）</param>
/// <param name="Risk">核准卡的遠端風險（ClassifyRemoteRisk，WP4.4；null＝沒分類，電腦端當成非一般，D9）</param>
/// <param name="FolderKnown">setFolder／conversationCreate 的資料夾是本機使用者選過的（RemoteFolderRules.IsKnown）</param>
/// <param name="FolderForbidden">那個資料夾在禁止清單（RemoteFolderRules.IsForbidden）</param>
/// <param name="PathInScope">listFiles 的 path 在對話的工作資料夾或對話資料夾內（RemoteFolderRules.IsWithin）</param>
/// <param name="TargetMode">setOption permission／conversationCreate 實際會設成的模式（<c>default</c> 的解析與代理種類的正規化之後）</param>
internal sealed record RemoteTierFacts(
    AgentPermissionMode? Mode = null,
    bool? PreApproved = null,
    AgentRemoteRisk? Risk = null,
    bool? FolderKnown = null,
    bool? FolderForbidden = null,
    bool? PathInScope = null,
    AgentPermissionMode? TargetMode = null)
{
    /// <summary>什麼都不知道</summary>
    public static RemoteTierFacts Unknown { get; } = new();
}

/// <summary>層級（0～2）與要不要「允許遠端完全自動」</summary>
internal sealed record RemoteTierResult(int Tier, bool FullAccess)
{
    /// <summary>兩者取較高：層級取大、FullAccess 取 OR（只升不降）</summary>
    public RemoteTierResult Max(RemoteTierResult other) => new(Math.Max(Tier, other.Tier), FullAccess || other.FullAccess);
}

/// <summary>
/// 遠端指令的層級（設計 8.10、14.11 D5、D9；docs/sync-protocol.md 14.4）：伺服器先算一次（提示，事實不明取最低），
/// 電腦依自己的事實重算（事實不明取最高），最終取兩者較高、FullAccess 取 OR，所以伺服器的程式錯誤不會讓層級降低。
/// 層級的要求在 <see cref="RemoteAuthRules"/>、本機政策在 <see cref="RemotePolicyRules"/>。指令要先通過 <see cref="RemoteCommandShapeRules.Check"/>
/// <list type="bullet">
/// <item>0：stop。</item>
/// <item>1：cancelQueued、skipQuestion、rename、resetFolder、removeAttachment、addUploaded、工作資料夾內的 listFiles、fileSearch、
/// 不附言的 approve deny、風險一般的 approve allow、approvePlan manual／keepPlanning、setOption model／effort、權限改每步詢問或規劃、
/// 已知而且不在禁止清單的 setFolder、accountRefresh、cliCheck、accountLoginCancel。</item>
/// <item>代理輸入（send〔含從佇列取出的空 send〕、語音訊息 voiceMessage、answer、附言的 approve／approvePlan、帶文字的 conversationCreate）是 1；
/// 對話是完全自動、或有預先放行規則時是 2＋FullAccess（Q3-A）。</item>
/// <item>2：其他。風險不是一般的 allow（含 null）、allowSession、acceptEdits、權限改自動接受編輯／自動、改完全自動（＋FullAccess）、
/// 其他 setFolder、setAccount、delete、listFiles dirsOnly 或範圍外、fileFetch、帳戶的新增／登入／登出／移除、cliInstall、cliUpdate、remoteExec；
/// conversationCreate 的模式照 setOption permission、資料夾照 setFolder。</item>
/// </list>
/// </summary>
internal static class RemoteTierRules
{
    private static readonly RemoteTierResult Zero = new(0, false);

    private static readonly RemoteTierResult One = new(1, false);

    private static readonly RemoteTierResult Two = new(2, false);

    private static readonly RemoteTierResult TwoFull = new(2, true);

    /// <summary>
    /// 一個指令的層級。<paramref name="command"/> 要已經通過 <see cref="RemoteCommandShapeRules.Check"/>；不在型錄的指令丟例外（閘門 5a 先擋，
    /// 型錄有、這裡沒有的是程式錯誤）
    /// </summary>
    public static RemoteTierResult Evaluate(string cmd, JObject command, RemoteTierFacts facts, RemoteSide side)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(facts);
        var low = side == RemoteSide.Server;
        return cmd switch
        {
            "stop" => Zero,
            "cancelQueued" or "skipQuestion" or "rename" or "resetFolder" or "removeAttachment" or "addUploaded" or "fileSearch"
                or "accountRefresh" or "cliCheck" or "accountLoginCancel" => One,
            // 語音訊息（voiceMessage）辨識後就是一則 send：層級同 send
            "send" or "answer" or "voiceMessage" => AgentInput(facts.Mode, facts.PreApproved, low),
            "approve" => (Str(command, "choice") switch
            {
                "allow" => (facts.Risk ?? (low ? AgentRemoteRisk.Normal : null)) == AgentRemoteRisk.Normal ? One : Two,
                "allowSession" => Two,
                "deny" => One,
                var other => throw new ArgumentOutOfRangeException(nameof(command), other, "approve 的 choice 不在型錄"),
            }).Max(Message(command, facts, low)),
            "approvePlan" => (Str(command, "choice") switch
            {
                "acceptEdits" => Two,
                "manual" or "keepPlanning" => One,
                var other => throw new ArgumentOutOfRangeException(nameof(command), other, "approvePlan 的 choice 不在型錄"),
            }).Max(Message(command, facts, low)),
            "setOption" => Str(command, "key") switch
            {
                "model" or "effort" => One,
                "permission" => ModeTier(Target(Str(command, "value"), facts.TargetMode, low)),
                var other => throw new ArgumentOutOfRangeException(nameof(command), other, "setOption 的 key 不在型錄"),
            },
            "setFolder" => FolderSafe(facts, low) ? One : Two,
            // 新對話的資料夾挑選：同 listFiles 的 dirsOnly
            RemoteCommandCatalog.ListFolders => Two,
            // 電腦狀態面板的檔案傳輸：整台的本機磁碟（同遠端選資料夾、fileFetch）
            RemoteCommandCatalog.DeviceBrowse or RemoteCommandCatalog.DeviceFetch or RemoteCommandCatalog.DevicePut
                or RemoteCommandCatalog.DeviceTree or RemoteCommandCatalog.DeviceFetchMany => Two,
            // 遠端桌面：看與操作整個畫面（同遠端執行指令）
            RemoteCommandCatalog.DeskOpen => Two,
            "listFiles" => command.Value<bool?>("dirsOnly") == true ? Two
                : command["path"] is null or { Type: JTokenType.Null } || (facts.PathInScope ?? low) ? One : Two,
            "delete" or "setAccount" or "fileFetch" or "accountAdd" or "accountLogin" or "accountLoginDevice" or RemoteCommandCatalog.AccountLoginCode or "accountLogout"
                or "accountRemove" or "cliInstall" or "cliUpdate" or RemoteCommandCatalog.RemoteExec or RemoteCommandCatalog.ScheduleAction => Two,
            // 交給代理：在那台的管理對話送一則訊息（同開新對話＋送出）
            RemoteCommandCatalog.ScheduleAgent => One,
            RemoteCommandCatalog.ConversationCreate => CreateTier(command, facts, low),
            _ => throw new ArgumentOutOfRangeException(nameof(cmd), cmd, "不在型錄的指令沒有層級"),
        };
    }

    /// <summary>最終的層級：信封宣告的與電腦重算的取較高（D5）；宣告的層級超出 0～2 時丟例外（信封驗證先擋）</summary>
    public static RemoteTierResult Final(int declaredTier, bool declaredFullAccess, RemoteTierResult device)
    {
        if (declaredTier is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(declaredTier), declaredTier, "層級是 0～2");
        return new RemoteTierResult(declaredTier, declaredFullAccess).Max(device);
    }

    /// <summary>
    /// 代理輸入：對話是完全自動、或有預先放行規則時是 2＋FullAccess（Q3-A）。不明時伺服器當成都沒有、電腦當成都有
    /// </summary>
    public static RemoteTierResult AgentInput(AgentPermissionMode? mode, bool? preApproved, bool low)
    {
        var fullAccess = (mode is { } m ? m == AgentPermissionMode.FullAccess : !low) || (preApproved ?? !low);
        return fullAccess ? TwoFull : One;
    }

    /// <summary>改成這個權限模式的層級：每步詢問、規劃是 1，自動接受編輯、自動是 2，完全自動是 2＋FullAccess；不明是 2＋FullAccess</summary>
    public static RemoteTierResult ModeTier(AgentPermissionMode? mode) => mode switch
    {
        AgentPermissionMode.Ask or AgentPermissionMode.Plan => One,
        AgentPermissionMode.AcceptEdits or AgentPermissionMode.Auto => Two,
        _ => TwoFull,
    };

    /// <summary>附言（approve／approvePlan 的 message 不是空白）送進代理：照代理輸入</summary>
    private static RemoteTierResult Message(JObject command, RemoteTierFacts facts, bool low)
        => string.IsNullOrWhiteSpace(Str(command, "message")) ? Zero : AgentInput(facts.Mode, facts.PreApproved, low);

    /// <summary>
    /// 實際會設成的模式：電腦一律用事實（不明＝最高，所以電腦一定要填）；伺服器用事實，沒有時用指令寫的名稱，
    /// <c>default</c>／空字串（設定頁的預設，伺服器不知道）當成最低的每步詢問
    /// </summary>
    private static AgentPermissionMode? Target(string? requested, AgentPermissionMode? target, bool low)
    {
        if (target != null || !low)
            return target;
        return Enum.TryParse<AgentPermissionMode>(requested, ignoreCase: false, out var mode) && Enum.GetNames<AgentPermissionMode>().Contains(requested)
            ? mode
            : AgentPermissionMode.Ask;
    }

    /// <summary>資料夾是本機選過的而且不在禁止清單（不明時伺服器當成是、電腦當成不是）</summary>
    private static bool FolderSafe(RemoteTierFacts facts, bool low) => (facts.FolderKnown ?? low) && !(facts.FolderForbidden ?? !low);

    /// <summary>新對話：模式照 setOption permission、資料夾照 setFolder（沒給＝對話自己的 workspace）、帶文字照代理輸入（模式是新對話的模式）</summary>
    private static RemoteTierResult CreateTier(JObject command, RemoteTierFacts facts, bool low)
    {
        var mode = Target(Str(command, "permissionMode"), facts.TargetMode, low);
        var tier = ModeTier(mode);
        if (command["folder"] is not (null or { Type: JTokenType.Null }) && !FolderSafe(facts, low))
            tier = tier.Max(Two);
        if (!string.IsNullOrWhiteSpace(Str(command, "text")))
            tier = tier.Max(AgentInput(mode, facts.PreApproved, low));
        return tier;
    }

    private static string? Str(JObject command, string key) => command[key] is JValue { Type: JTokenType.String, Value: string text } ? text : null;
}
