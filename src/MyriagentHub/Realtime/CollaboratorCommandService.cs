using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 擁有者管理協作人的指令（多人協作；web-protocol 的 <c>collab*</c>、sync-protocol 14.14 的 <c>CollabCmd</c>）：瀏覽器版由伺服器處理（分類照樣是
/// not_available，<see cref="BrowserCommandRules.CollaboratorServerCommands"/>，由第 4 階段的處理者接手），桌面經裝置 hub 的 <c>CollabCmd</c> 送來同一份。
/// 只收擁有者：協作人的連線在 <see cref="BrowserCommandHandler"/> 的白名單就被擋掉（這裡再擋一次）。分享的對話要在伺服器的對話目錄裡（不存在、已刪除回 not_found）。
/// 結果只給發起的那一邊（瀏覽器的 <c>collabResult</c>、裝置的回傳值），名冊的變動由閘道與 <see cref="CollaboratorFanout"/> 推給所有擁有者
/// </summary>
internal sealed class CollaboratorCommandService(CollaboratorDirectory directory, IConversationDirectory conversations, BrowserGateway gateway)
{
    public bool Handles(string name) => BrowserCommandRules.CollaboratorServerCommands.Contains(name);

    /// <summary>瀏覽器（擁有者）：回 null＝接受（結果以 <c>collabResult</c> 另外送給這條連線）；欄位型別不對回 invalid</summary>
    public async Task<string?> HandleBrowserAsync(BrowserConnection connection, string name, JObject body, CancellationToken cancellationToken)
    {
        if (connection.IsCollaborator)
            return BrowserErrorCodes.Invalid;
        if (Parse(name, body) is not { } request)
            return BrowserErrorCodes.Invalid;
        var actor = new CollaboratorActor(AuditActors.Owner, MyriagentHub.Auth.SessionRules.ShortId(connection.SessionId), connection.Ip);
        var result = await HandleAsync(request, actor, cancellationToken);
        var reply = new JObject
        {
            ["type"] = "collabResult",
            ["requestId"] = body["requestId"] is JValue { Type: JTokenType.String } id ? (string?)id.Value : null,
            ["cmd"] = name,
            ["ok"] = result.Ok,
            ["code"] = result.Code,
            ["userId"] = result.UserId,
        };
        gateway.SendAfterInit(connection, new ClientOutboxMessage("collabResult", reply.ToString(Formatting.None)));
        return null;
    }

    /// <summary>裝置（collab1，桌面替擁有者送的）：照指令執行，回結果</summary>
    public Task<CollabCmdResult> HandleDeviceAsync(string deviceId, CollabCmdRequest request, CancellationToken cancellationToken) =>
        HandleAsync(request, new CollaboratorActor(AuditActors.Device, deviceId, null), cancellationToken);

    private async Task<CollabCmdResult> HandleAsync(CollabCmdRequest request, CollaboratorActor actor, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Cmd)
            {
                case Myriagent.Sync.CollaboratorCommands.Create:
                    return await directory.CreateAsync(request.UserName, request.DisplayName, request.Password, actor, cancellationToken);
                case Myriagent.Sync.CollaboratorCommands.Update:
                    return await directory.UpdateAsync(request.UserId, request.DisplayName, request.Disabled, request.Password, actor, cancellationToken);
                case Myriagent.Sync.CollaboratorCommands.Delete:
                    return await directory.DeleteAsync(request.UserId, actor, cancellationToken);
                case Myriagent.Sync.CollaboratorCommands.Share:
                    if (request.ConversationId is not { } conversationId || !ConversationIds.IsWireId(conversationId))
                        return new CollabCmdResult(false, CollaboratorCodes.Invalid);
                    if (conversations.Find(conversationId) is null)
                        return new CollabCmdResult(false, CollaboratorCodes.NotFound);
                    return await directory.ShareAsync(conversationId, request.UserIds, actor, cancellationToken);
                default:
                    return new CollabCmdResult(false, CollaboratorCodes.Invalid);
            }
        }
        catch (OperationCanceledException)
        {
            return new CollabCmdResult(false, CollaboratorCodes.Unavailable);
        }
    }

    /// <summary>網頁的 <c>collab*</c> 指令 → 共用的請求（字串欄位一律 ≤4096 字，MessageGuard 已擋；型別不對回 null）</summary>
    internal static CollabCmdRequest? Parse(string name, JObject body)
    {
        if (!Myriagent.Sync.CollaboratorCommands.IsCollaboratorCommand(name))
            return null;
        if (!TryString(body, "userId", out var userId) || !TryString(body, "userName", out var userName) || !TryString(body, "displayName", out var displayName)
            || !TryString(body, "password", out var password) || !TryString(body, "conversationId", out var conversationId))
            return null;
        bool? disabled = null;
        if (body["disabled"] is { } disabledToken && disabledToken.Type != JTokenType.Null)
        {
            if (disabledToken.Type != JTokenType.Boolean)
                return null;
            disabled = (bool)disabledToken;
        }
        List<string>? userIds = null;
        if (body["userIds"] is { } idsToken && idsToken.Type != JTokenType.Null)
        {
            if (idsToken is not JArray array || array.Count > CollaboratorRules.MaxSharesPerConversation || array.Any(t => t.Type != JTokenType.String))
                return null;
            userIds = array.Select(t => (string)t!).ToList();
        }
        return new CollabCmdRequest(name, userId, userName, displayName, password, disabled, conversationId, userIds);
    }

    private static bool TryString(JObject body, string key, out string? value)
    {
        value = null;
        if (body[key] is not { } token || token.Type == JTokenType.Null)
            return true;
        if (token.Type != JTokenType.String)
            return false;
        value = (string?)token;
        return true;
    }
}
