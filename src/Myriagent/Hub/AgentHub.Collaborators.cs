using Myriagent.Core;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 多人協作（docs/plans/2026-10-30-collaborators.md 第 3 節；web-protocol 的 <c>collaborators</c>、<c>collab*</c>、<c>collabResult</c>）：
/// 擁有者在這台的設定頁管理協作人、在對話選單設定分享。協作人的帳號與分享都在伺服器（伺服器說了算）：清單是伺服器經裝置 hub 送來的
/// <c>CollabUpdate</c>（<see cref="ApplyCollaborators"/>），管理指令經同步橋的 <c>CollabCmd</c> 交給伺服器，結果以 <c>collabResult</c> 回給發起的視窗。
/// 網頁的對話 id：這台的對話與副本都是資料夾名（<see cref="ConversationIds.ToWire"/> 換成線上的 id），分享的鍵也換回這台的 id。
/// 未配對、伺服器太舊時 <c>collaborators</c> 是 null（設定頁不顯示這一區、對話選單沒有「協作人…」）
/// </summary>
internal sealed partial class AgentHub
{
    private CollaboratorsState? collaboratorsState;

    /// <summary>伺服器送來的協作人清單與分享（UI 執行緒）；null＝不知道（未配對、解除配對）。內容變了才送給各視窗</summary>
    internal void ApplyCollaborators(CollaboratorsState? state)
    {
        collaboratorsState = state;
        var message = new JObject { ["type"] = "collaborators", ["collaborators"] = RenderCollaborators() };
        foreach (var v in views.Values.Where(v => v.Id != SyncViewId && v is not RemoteResponseView).ToList())
            v.PostToWeb(message);
    }

    /// <summary><c>{list, shares}</c>（web-protocol 第一節的 collaborators）；不知道時是 null</summary>
    private JToken RenderCollaborators()
    {
        if (collaboratorsState is not { } state)
            return JValue.CreateNull();
        var list = new JArray();
        foreach (var c in state.List ?? [])
        {
            if (c is null)
                continue;
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
        foreach (var (wireId, users) in state.Shares ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            if (users is null || users.Count == 0)
                continue;
            shares[FindByWireId(wireId)?.Meta.Id ?? wireId] = new JArray(users.Where(u => u is not null));
        }
        return new JObject { ["list"] = list, ["shares"] = shares };
    }

    /// <summary>
    /// collabCreate／collabUpdate／collabDelete／collabShare（AgentHub.cs 的分派）：轉成 <see cref="CollabCmdRequest"/> 經同步橋送給伺服器，
    /// 結果以 <c>collabResult</c> 回給這個視窗（帶網頁的 requestId），失敗另送 toast。分享的對話要已經同步到伺服器（線上的 id）
    /// </summary>
    private void HandleCollaboratorCommand(IAgentView view, string cmd, JObject command)
    {
        string? Str(string key) => command[key] is JValue { Type: JTokenType.String } v ? (string?)v.Value : null;
        var requestId = Str("requestId");
        if (sync is null)
        {
            ReplyCollab(view, cmd, requestId, new CollabCmdResult(false, CollaboratorCodes.Unavailable));
            return;
        }
        string? wireId = null;
        if (cmd == CollaboratorCommands.Share)
        {
            var c = Str("conversationId") is { } id && conversations.TryGetValue(id, out var found) ? found : null;
            wireId = c is null || c.IsDraft ? null : ConversationIds.ToWire(c.Meta.Id);
            if (wireId is null)
            {
                ReplyCollab(view, cmd, requestId, new CollabCmdResult(false, CollaboratorCodes.NotFound));
                return;
            }
        }
        List<string>? userIds = null;
        if (command["userIds"] is JArray ids)
            userIds = ids.Where(t => t.Type == JTokenType.String).Select(t => (string)t!).Take(CollaboratorRules.MaxSharesPerConversation + 1).ToList();
        bool? disabled = command["disabled"] is JValue { Type: JTokenType.Boolean } d ? (bool)d.Value! : null;
        var request = new CollabCmdRequest(cmd, Str("userId"), Str("userName"), Str("displayName"), Str("password"), disabled, wireId, userIds);
        _ = SendCollabAsync(view, cmd, requestId, request);
    }

    private async Task SendCollabAsync(IAgentView view, string cmd, string? requestId, CollabCmdRequest request)
    {
        CollabCmdResult result;
        try
        {
            result = await sync!.CollabCmdAsync(request);
        }
        catch (Exception ex)
        {
            Log.Warn($"協作人的管理指令失敗：{ex.Message}");
            result = new CollabCmdResult(false, CollaboratorCodes.Unavailable);
        }
        // CollabCmdAsync 的續體可能在執行緒集區：回 UI 執行緒再碰視窗
        host.BeginInvoke(() => ReplyCollab(view, cmd, requestId, result));
    }

    private void ReplyCollab(IAgentView view, string cmd, string? requestId, CollabCmdResult result)
    {
        Post(view, new JObject
        {
            ["type"] = "collabResult",
            ["requestId"] = requestId,
            ["cmd"] = cmd,
            ["ok"] = result.Ok,
            ["code"] = result.Code,
            ["userId"] = result.UserId,
        });
        if (!result.Ok && result.Code == CollaboratorCodes.Unavailable)
            Toast("伺服器暫時連不上（或還沒配對），協作人的設定沒有送出", isError: true, view);
    }
}
