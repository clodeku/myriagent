using Myriagent.Sync;
using MyriagentHub.Auth;
using MyriagentHub.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 閘道的多人協作（docs/plans/2026-10-30-collaborators.md 2.3；web-protocol 的 <c>collaborators</c>、<c>init.viewer</c>）：
/// <list type="bullet">
/// <item>協作人的連線：init 只有分享給他的對話與他自己的介面設定，帳戶、電腦、用量、排程一律是空的或 null，之後也不推這些部分；
/// <c>control</c>（擁有者的預設密碼警告）不給他。</item>
/// <item>名冊或分享改了（<see cref="CollaboratorsChanged"/>）：下一輪協作人的清單照新的分享重組（清單的鍵含名冊的版本），
/// 開著不再分享的對話的連線收 <c>conversationClosed</c>；擁有者的連線收新的一份 <c>collaborators</c>。</item>
/// </list>
/// <paramref name="collaborators"/> 是 null（單元測試的 GatewayHarness）時沒有協作人：一切照舊
/// </summary>
internal sealed partial class BrowserGateway
{
    private int collaboratorsDirty;
    private long collaboratorsVersion;
    private ClientOutboxMessage? collaboratorsMessage;

    /// <summary>名冊（null＝沒有多人協作）</summary>
    private CollaboratorDirectory? Collaborators => collaborators;

    /// <summary>
    /// 名冊或分享改了、有協作人連上或斷線（「在線」）：記下並喚醒迴圈（任何執行緒、不可阻塞）。狀態在迴圈裡重組
    /// </summary>
    public void CollaboratorsChanged()
    {
        if (collaborators is null)
            return;
        Interlocked.Exchange(ref collaboratorsDirty, 1);
        Wake();
    }

    /// <summary>這條連線看不看得到這個對話（擁有者一律看得到）</summary>
    internal bool CanSeeConversation(BrowserConnection connection, string conversationId) =>
        !connection.IsCollaborator || collaborators?.CanSee(connection.CollaboratorId, conversationId) == true;

    /// <summary>
    /// 一輪的協作人工作（在 <see cref="RunOnce"/> 的對話之前）：名冊改了就重組擁有者的 <c>collaborators</c>、關掉協作人開著但不再分享的對話。
    /// 溢出要重送 init 時回 true
    /// </summary>
    private bool RunCollaborators(IReadOnlyList<BrowserConnection> all, long nowMs)
    {
        if (collaborators is null)
            return false;
        var resync = false;
        if (Interlocked.Exchange(ref collaboratorsDirty, 0) == 1 || collaboratorsMessage is null)
        {
            var state = collaborators.State(connections.OnlineCollaborators());
            var json = CollaboratorsMessageJson(state);
            if (collaboratorsMessage is null || !string.Equals(collaboratorsMessage.Json, json, StringComparison.Ordinal))
            {
                collaboratorsVersion++;
                collaboratorsMessage = new ClientOutboxMessage(CollaboratorsType, json, CollaboratorsType);
            }
            foreach (var connection in all)
            {
                if (!connection.IsCollaborator || !connection.InitSent)
                    continue;
                foreach (var conversationId in connection.OpenConversationIds())
                {
                    if (CanSeeConversation(connection, conversationId))
                        continue;
                    if (connection.TryEnqueueConversationUpdate(ConversationGatewayRules.ClosedMessage(conversationId), null) is { } closed)
                        resync |= HandleResult(connection, closed, nowMs).Resync;
                    connection.CloseConversation(conversationId);
                }
            }
        }
        foreach (var connection in all)
        {
            if (connection.IsCollaborator || !connection.InitSent || collaboratorsMessage is null || connection.CollaboratorsSeen >= collaboratorsVersion)
                continue;
            var (queued, overflow) = Enqueue(connection, collaboratorsMessage, nowMs);
            resync |= overflow;
            if (queued)
                connection.MarkCollaboratorsSeen(collaboratorsVersion);
        }
        return resync;
    }

    /// <summary>協作人的介面設定改了：他的每條連線送一則 <c>settings</c>（迴圈之外；在連線鎖裡看已送過 init 再入列）</summary>
    public void PushCollaboratorSettings(string collaboratorId)
    {
        if (collaborators is null)
            return;
        var message = new ClientOutboxMessage("settings",
            new JObject { ["type"] = "settings", ["settings"] = collaborators.SettingsOf(collaboratorId) }.ToString(Formatting.None), "settings");
        foreach (var connection in connections.Snapshot())
        {
            if (connection.CollaboratorId == collaboratorId)
                SendAfterInit(connection, message);
        }
    }

    /// <summary>
    /// 協作人的 init：同擁有者的形狀，帳戶、電腦、用量、CLI 都是空的，排程是 null，另帶 <c>viewer</c>；清單只有分享給他的對話。
    /// 不快取（協作人少、init 也少）
    /// </summary>
    private (ClientOutboxMessage Message, ConversationListMark? List) CollaboratorInitMessage(string mode, BrowserConnection connection, long nowMs)
    {
        var entry = collaborators?.Find(connection.CollaboratorId);
        ConversationListView? list = null;
        ConversationListMark? mark = null;
        if (views is not null)
            (list, mark) = InitConversationList(connection, nowMs);
        var init = new JObject
        {
            ["type"] = ClientOutboxRules.InitType,
            ["mode"] = mode,
            ["popoutConversationId"] = null,
            ["app"] = new JObject { ["version"] = BuildInfo.Current.Version, ["build"] = BuildInfo.Current.Build, ["commitAt"] = BuildInfo.Current.CommitAtMs },
            ["settings"] = connection.CollaboratorId is { } id && collaborators is not null ? collaborators.SettingsOf(id) : new JObject(),
            ["accounts"] = new JArray(),
            ["conversations"] = list is null ? new JArray() : new JRaw(list.ItemsJson),
            ["cli"] = new JObject(),
            ["providers"] = Sync.AccountViewRules.Providers(),
            ["devicesCli"] = new JObject(),
            ["tokens"] = null,
            ["schedules"] = null,
            ["layout"] = null,
            ["remote"] = new JObject
            {
                ["mode"] = "browser",
                ["deviceId"] = null,
                ["devices"] = new JArray(),
                ["sync"] = null,
            },
            ["viewer"] = new JObject
            {
                ["role"] = "collaborator",
                ["name"] = entry?.DisplayName ?? string.Empty,
                ["userName"] = entry?.UserName ?? string.Empty,
            },
        };
        if (reconnectGraceMs is { } grace)
            init["remote"]!["reconnectGraceMs"] = grace;
        return (new ClientOutboxMessage(ClientOutboxRules.InitType, init.ToString(Formatting.None)), mark);
    }

    /// <summary>擁有者的 init 帶的協作人狀態（null＝沒有多人協作）與它的版本</summary>
    private (JToken Value, long Version) OwnerCollaboratorsForInit()
    {
        if (collaborators is null)
            return (JValue.CreateNull(), 0);
        if (collaboratorsMessage is null)
        {
            collaboratorsVersion++;
            collaboratorsMessage = new ClientOutboxMessage(CollaboratorsType, CollaboratorsMessageJson(collaborators.State(connections.OnlineCollaborators())), CollaboratorsType);
        }
        return (JObject.Parse(collaboratorsMessage.Json)["collaborators"]!, collaboratorsVersion);
    }

    internal const string CollaboratorsType = "collaborators";

    /// <summary><c>{type:"collaborators", collaborators:{list:[…], shares:{對話 id:[協作人 id…]}}}</c>（web-protocol 第一節；桌面送的形狀相同）</summary>
    internal static string CollaboratorsMessageJson(CollaboratorsState state) =>
        new JObject { ["type"] = CollaboratorsType, ["collaborators"] = CollaboratorsJson(state) }.ToString(Formatting.None);

    /// <summary><c>{list:[…], shares:{…}}</c>（init 的 collaborators、collaborators 訊息的內容）</summary>
    internal static JObject CollaboratorsJson(CollaboratorsState state)
    {
        var list = new JArray();
        foreach (var c in state.List)
        {
            list.Add(new JObject
            {
                ["id"] = c.Id,
                ["userName"] = c.UserName,
                ["displayName"] = c.DisplayName,
                ["disabled"] = c.Disabled,
                ["createdAt"] = c.CreatedAt,
                ["lastLoginAt"] = c.LastLoginAt,
                ["online"] = c.Online,
            });
        }
        var shares = new JObject();
        foreach (var (conversationId, users) in state.Shares)
            shares[conversationId] = new JArray(users);
        return new JObject { ["list"] = list, ["shares"] = shares };
    }
}
