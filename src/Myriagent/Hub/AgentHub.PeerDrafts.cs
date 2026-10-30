using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 桌面在其他電腦開新對話（sync-protocol 14.7、web-protocol 的 draftSetDevice）：草稿照舊在這台的記憶體，網頁以 draftSetDevice {conversationId, deviceId, accountId, provider}
// 選要開在哪台電腦與那台的哪個邏輯帳戶（deviceId 是 null 或這台＝回到本機草稿）。遠端草稿的第一次 send（網頁帶 commandId）不啟動這台的 CLI，
// 改組 conversationCreate {deviceId, accountId, permissionMode, model?, effort?, folder?, title?, text} 交給同步（PeerCmd）；伺服器先回 peerCreated {conversationId}，
// 那台建好、副本出現在這台時，把開著草稿的每個檢視換成副本（conversationClosed {conversationId: 草稿, replacedBy: 副本}）並丟掉草稿。
// 被拒時以 sendRejected 把文字還給草稿。遠端草稿可以先選那台的工作資料夾（setFolder 手輸或挑最近用過的、resetFolder 回到那台對話自己的工作區；
// 第一次 send 帶 folder，那台建立時檢查，同瀏覽器的伺服器草稿），不能附加檔案（送出第一則訊息後才能附加）。只在 UI 執行緒
internal sealed partial class AgentHub
{
    /// <summary>遠端草稿的狀態（只有 <see cref="ConversationState.IsDraft"/> 而且 <see cref="PeerDeviceId"/> 不是 null 時有意義）</summary>
    private sealed partial class ConversationState
    {
        /// <summary>要在哪台電腦建立（null＝這台的本機草稿）</summary>
        public string? PeerDeviceId;

        /// <summary>那台的邏輯帳戶（la_…）</summary>
        public string? PeerAccountId;

        /// <summary>標頭顯示的帳戶 id：這台有連結同一個邏輯帳戶時是本機的 id，否則是邏輯帳戶 id（網頁的 remoteOnly 帳戶）</summary>
        public string? PeerAccountShownId;

        /// <summary>第一次 send 送出的 commandId（建立中；被拒時清掉）</summary>
        public string? PeerCreatingCommandId;

        /// <summary>伺服器回的新對話 id（peerCreated）；副本出現時換過去</summary>
        public string? PeerCreatedId;

        /// <summary>第一次 send 的文字（被拒時還回去）</summary>
        public string? PeerCreatingText;

        /// <summary>那台電腦上的工作資料夾（完整路徑；null＝那台對話自己的工作區）。換電腦、回到這台時清掉</summary>
        public string? PeerFolder;
    }

    /// <summary>
    /// 遠端草稿上不能做的本機指令（附件與本機自訂代理都在那台電腦上；chooseFolder 是這台的資料夾對話框）。
    /// setFolder、resetFolder 由 <see cref="SetPeerDraftFolder"/> 記在草稿上
    /// </summary>
    private static readonly HashSet<string> PeerDraftLocalOnly = new(StringComparer.Ordinal)
    {
        "chooseFolder", "attach", "addFiles", "pasteImage", "addPathAttachment", "setAccount", "openFolder", "setAgent", "setOutputStyle",
    };

    /// <summary>HandleCommand 開頭（擋副本之前）：draftSetDevice、遠端草稿的 send 與不能做的本機指令在這裡處理，回 true</summary>
    private bool TryHandlePeerDraft(IAgentView view, JObject command)
    {
        var cmd = command.Value<string>("cmd");
        var conversationId = command.Value<string>("conversationId");
        if (cmd == null || conversationId == null || !conversations.TryGetValue(conversationId, out var c) || !c.IsDraft)
            return false;
        if (cmd == "draftSetDevice")
        {
            SetDraftDevice(view, c, command);
            return true;
        }
        if (c.PeerDeviceId == null)
            return false;
        if (cmd == "send")
        {
            SendPeerDraft(view, c, command);
            return true;
        }
        if (cmd is "setFolder" or "resetFolder")
        {
            SetPeerDraftFolder(view, c, cmd == "setFolder" ? command["path"] as JValue : null, reset: cmd == "resetFolder");
            return true;
        }
        if (cmd == "voiceMessage")
        {
            // 錄音要在建立對話的那台辨識（要先有對話才能上傳給它）
            Post(view, PeerFileRules.Toast("這是要在其他電腦開的新對話：送出第一則訊息之後才能用語音", "error"));
            return true;
        }
        if (PeerDraftLocalOnly.Contains(cmd))
        {
            Post(view, PeerFileRules.Toast(cmd == "chooseFolder"
                ? "這是要在其他電腦開的新對話：工作資料夾要輸入那台電腦上的路徑，或從最近用過的挑"
                : "這是要在其他電腦開的新對話：送出第一則訊息之後才能附加檔案", "error"));
            return true;
        }
        return false;
    }

    private void SetDraftDevice(IAgentView view, ConversationState c, JObject command)
    {
        var deviceId = command.Value<string>("deviceId");
        if (c.PeerCreatingCommandId != null)
        {
            Post(view, PeerFileRules.Toast("新對話建立中，請稍候", "error"));
            return;
        }
        if (string.IsNullOrEmpty(deviceId) || deviceId == peerSink?.DeviceId)
        {
            (c.PeerDeviceId, c.PeerAccountId, c.PeerAccountShownId, c.PeerFolder) = (null, null, null, null);
            // 回到本機：帳戶要是這台的（遠端草稿可能換過代理種類）
            if (accounts.Find(c.Meta.AccountId) is { } local && local.Provider != c.Meta.Provider)
                ApplyDraftProvider(c, local.Provider);
            MarkHeaderDirty(c);
            return;
        }

        var logical = command.Value<string>("accountId");
        if (!peerLive || !SyncIds.IsDeviceId(deviceId) || !SyncIds.IsLogicalAccountId(logical)
            || !Enum.TryParse<AgentProvider>(command.Value<string>("provider"), ignoreCase: false, out var provider) || !Enum.IsDefined(provider))
        {
            Post(view, PeerFileRules.Toast("現在不能換到這台電腦（沒有連上伺服器或帳戶不對）", "error"));
            return;
        }

        // 資料夾是那台電腦上的路徑：換到另一台就回到那台對話自己的工作區（只換帳戶留著）
        if (c.PeerDeviceId != deviceId)
            c.PeerFolder = null;
        c.PeerDeviceId = deviceId;
        c.PeerAccountId = logical;
        c.PeerAccountShownId = accounts.Accounts.FirstOrDefault(a => a.LogicalAccountId == logical)?.Id ?? logical;
        c.Meta.Agent = null;
        c.Meta.CodexSkills = null;
        c.Meta.CodexServiceTier = null;
        c.CodexGoal = null;
        if (provider != c.Meta.Provider)
            ApplyDraftProvider(c, provider);
        MarkHeaderDirty(c);
    }

    /// <summary>
    /// 遠端草稿的 setFolder／resetFolder：記在草稿上，第一次 send 才帶給那台（那台建立時檢查存不存在、能不能從遠端用；不行就整則被拒、文字還給草稿）。
    /// 這台只看形狀（<see cref="PeerDraftRules.Folder"/>）：路徑在那台電腦上，這台檢查不了
    /// </summary>
    private void SetPeerDraftFolder(IAgentView view, ConversationState c, JValue? path, bool reset)
    {
        if (c.PeerCreatingCommandId != null)
        {
            Post(view, PeerFileRules.Toast("新對話建立中，請稍候", "error"));
            return;
        }
        var folder = reset ? null : PeerDraftRules.Folder(path is { Type: JTokenType.String, Value: string text } ? text : null);
        if (!reset && folder == null)
        {
            Post(view, PeerFileRules.Toast(@"要輸入那台電腦上的完整路徑（例如 C:\repo\demo），不能是網路上的資料夾", "error"));
            return;
        }
        c.PeerFolder = folder;
        MarkHeaderDirty(c);
    }

    /// <summary>換了代理種類：權限模式改成那一家的預設、模型與推理強度與自訂代理改回預設</summary>
    private void ApplyDraftProvider(ConversationState c, AgentProvider provider)
    {
        c.Meta.Provider = provider;
        c.Meta.Model = null;
        c.Meta.Effort = null;
        c.Meta.Agent = null;
        c.Meta.PermissionMode = ConversationDefaultsRules.PermissionFor(provider, ProviderDefaults(provider));
    }

    private void SendPeerDraft(IAgentView view, ConversationState c, JObject command)
    {
        var text = command.Value<string>("text") ?? string.Empty;
        var commandId = command.Value<string>("commandId");
        void Reject(string message)
        {
            Post(view, PeerFileRules.Toast(message, "error"));
            Post(view, new JObject { ["type"] = "sendRejected", ["conversationId"] = c.Meta.Id, ["text"] = text });
            if (commandId != null)
                Post(view, PeerCommandRules.Synthesized(commandId, RemoteResultCodes.Invalid).Message);
        }

        if (!SyncIds.IsUlid(commandId) || string.IsNullOrWhiteSpace(text))
        {
            Reject("要在其他電腦開的新對話，第一則一定要有文字");
            return;
        }
        if (c.PeerCreatingCommandId != null)
        {
            Reject("新對話建立中，請稍候");
            return;
        }
        if (peerSink is not { } sink)
        {
            Post(view, PeerCommandRules.Synthesized(commandId!, PeerCommandRules.NotSent).Message);
            Post(view, new JObject { ["type"] = "sendRejected", ["conversationId"] = c.Meta.Id, ["text"] = text });
            return;
        }

        var create = PeerDraftRules.ConversationCreate(c.PeerDeviceId!, c.PeerAccountId!, c.Meta, text, c.PeerFolder);
        c.PeerCreatingCommandId = commandId;
        c.PeerCreatingText = text;
        MarkHeaderDirty(c);
        PrunePeerPending();
        peerPending[commandId!] = new PeerPending(view.Id, RemoteCommandCatalog.ConversationCreate, Environment.TickCount64) { DraftId = c.Meta.Id };
        try
        {
            sink.SubmitPeerCommand(commandId!, create);
        }
        catch (Exception ex)
        {
            Log.Error("在其他電腦開新對話的指令交給同步失敗", ex);
            peerPending.Remove(commandId!);
            (c.PeerCreatingCommandId, c.PeerCreatingText) = (null, null);
            Post(view, PeerCommandRules.Synthesized(commandId!, PeerCommandRules.NotSent).Message);
            Post(view, new JObject { ["type"] = "sendRejected", ["conversationId"] = c.Meta.Id, ["text"] = text });
        }
    }

    /// <summary>
    /// 遠端草稿的回覆（<see cref="OnPeerReply"/> 轉來；回 true＝已處理、不再轉給檢視）：peerCreated 記下新對話 id（副本已在就換過去）；
    /// commandResult rejected 把文字還給草稿；sendRejected 的對話 id 換成草稿的
    /// </summary>
    private bool HandlePeerDraftReply(PeerPending pending, JObject message)
    {
        if (pending.DraftId is not { } draftId || !conversations.TryGetValue(draftId, out var c) || !c.IsDraft)
            return false;
        switch (message.Value<string>("type"))
        {
            case CommandPeerCreatedType:
                if (message.Value<string>("conversationId") is { } created && ConversationIds.IsWireId(created))
                {
                    c.PeerCreatedId = created;
                    if (conversations.TryGetValue(created, out var replica) && replica.IsReplica)
                        SwapPeerDraft(c, created);
                }
                return true;
            case "commandResult" when message.Value<string>("state") == RemoteResultStates.Rejected:
                if (c.PeerCreatingText is { } text && views.TryGetValue(pending.ViewId, out var view))
                    Post(view, new JObject { ["type"] = "sendRejected", ["conversationId"] = draftId, ["text"] = text });
                (c.PeerCreatingCommandId, c.PeerCreatingText, c.PeerCreatedId) = (null, null, null);
                MarkHeaderDirty(c);
                return false;
            case "sendRejected":
                message["conversationId"] = draftId;
                return false;
            default:
                return false;
        }
    }

    internal const string CommandPeerCreatedType = "peerCreated";

    /// <summary>副本出現了（UpsertReplica 新建時呼叫）：等著它的遠端草稿換過去</summary>
    private void OnPeerReplicaCreated(string conversationId)
    {
        foreach (var draft in conversations.Values.Where(d => d.IsDraft && d.PeerCreatedId == conversationId).ToList())
            SwapPeerDraft(draft, conversationId);
    }

    /// <summary>開著草稿的每個檢視換成副本（網頁在同一個位置換分頁、送 open），之後丟掉草稿</summary>
    private void SwapPeerDraft(ConversationState draft, string replicaId)
    {
        foreach (var viewId in draft.ViewIds.ToList())
        {
            if (views.TryGetValue(viewId, out var view))
                Post(view, new JObject { ["type"] = "conversationClosed", ["conversationId"] = draft.Meta.Id, ["replacedBy"] = replicaId });
        }
        draft.ViewIds.Clear();
        draft.VisibleViewIds.Clear();
        DiscardDraft(draft);
        MarkAccountsDirty();
    }

    /// <summary>遠端草稿的標頭：所在電腦、那台的帳戶、選的那台的資料夾與那台最近用過的資料夾（那台的副本的自訂資料夾），沒有這台的資料夾與指令</summary>
    private void DecoratePeerDraftHeader(ConversationState c, JObject header)
    {
        if (!c.IsDraft || c.PeerDeviceId is not { } device)
            return;
        header["deviceId"] = device;
        header["accountId"] = c.PeerAccountShownId ?? c.PeerAccountId;
        header["accountMissing"] = false;
        header["workingDirectory"] = TextOrNull(c.PeerFolder);
        header["customFolder"] = c.PeerFolder != null;
        header["recentFolders"] = new JArray(PeerDraftRules.RecentFolders(conversations.Values
            .Where(x => x.IsReplica && x.ReplicaOwnerDeviceId == device && x.ReplicaCustomFolder)
            .Select(x => (x.ReplicaFolder, x.Meta.UpdatedAt))));
        header["commands"] = new JArray();
        header["agent"] = null;
        header["agents"] = new JArray();
        header["sessionStarted"] = false;
        header["models"] = new JArray(EnsureDefaultModel(DefaultModels(c.Meta.Provider)).Select(m => new JObject
        {
            ["value"] = m.Value,
            ["label"] = m.Label,
            ["description"] = m.Description,
            ["efforts"] = new JArray(m.Efforts),
        }));
        header["peerCreating"] = c.PeerCreatingCommandId != null;
    }
}

/// <summary>桌面在其他電腦開新對話的規則（sync-protocol 14.7）</summary>
internal static class PeerDraftRules
{
    /// <summary>標頭列出那台最近用過的資料夾最多幾個（網頁的選單另外拿掉目前的、只列 6 個）</summary>
    public const int MaxRecentFolders = 10;

    /// <summary>
    /// 第一次 send 組成的 <c>conversationCreate</c>（PeerCmd 的 cmd；伺服器去掉 <c>deviceId</c>、把邏輯帳戶換成那台的本機 id）：
    /// 權限模式是正規化之後的名稱、模型與推理強度是 null 或 <c>default</c> 時不帶、標題不是預設的「新對話」才帶；
    /// 資料夾（<paramref name="folder"/>）是 null 時不帶（那台對話自己的工作區）。鍵照型錄的順序
    /// </summary>
    public static JObject ConversationCreate(string deviceId, string logicalAccountId, AgentConversation meta, string text, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(meta);
        var create = new JObject
        {
            ["cmd"] = RemoteCommandCatalog.ConversationCreate,
            ["deviceId"] = deviceId,
            ["accountId"] = logicalAccountId,
            ["permissionMode"] = AgentPermissionModes.Normalize(meta.Provider, meta.PermissionMode).ToString(),
        };
        if (!string.IsNullOrEmpty(meta.Model) && meta.Model != "default")
            create["model"] = meta.Model;
        if (!string.IsNullOrEmpty(meta.Effort) && meta.Effort != "default")
            create["effort"] = meta.Effort;
        if (folder != null)
            create["folder"] = folder;
        if (!string.IsNullOrWhiteSpace(meta.Title) && meta.Title != "新對話")
            create["title"] = meta.Title;
        create["text"] = text;
        return create;
    }

    /// <summary>
    /// 遠端草稿選的資料夾（setFolder 的 path）：去掉前後空白與包住整個路徑的引號（從檔案總管「複製路徑」貼上的），要是本機磁碟的完整路徑
    /// （<c>C:\…</c>；網路路徑與相對路徑不收，那台的檢查也不收）、不超過型錄的長度上限；不合格回 null
    /// </summary>
    public static string? Folder(string? path)
    {
        var trimmed = path?.Trim();
        if (trimmed is { Length: >= 2 } && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1].Trim();
        return AgentAttachmentRules.IsLocalDrivePath(trimmed) && trimmed!.Length <= RemoteCommandShapeRules.MaxTextLength
               && !trimmed.Any(char.IsControl)
            ? trimmed
            : null;
    }

    /// <summary>
    /// 那台最近用過的資料夾（遠端草稿標頭的 <c>recentFolders</c>）：那台的對話（這台的副本）的自訂資料夾，依對話最後更新的時間由新到舊、
    /// 不分大小寫去重、只留本機磁碟的完整路徑，最多 <see cref="MaxRecentFolders"/> 個
    /// </summary>
    public static List<string> RecentFolders(IEnumerable<(string? Folder, DateTime UpdatedAt)> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return folders
            .Where(f => AgentAttachmentRules.IsLocalDrivePath(f.Folder))
            .OrderByDescending(f => f.UpdatedAt)
            .ThenBy(f => f.Folder, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.Folder!)
            .Where(seen.Add)
            .Take(MaxRecentFolders)
            .ToList();
    }
}
