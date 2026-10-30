using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 桌面遠端操作在中樞的規則（sync-protocol 14.7；擁有者 2026-10-22 決定「已配對就能操作」）：網頁對其他電腦的對話（副本）或其他電腦的帳戶、CLI 送的指令，
/// 哪些交給同步、結果哪些可以轉給發起的檢視
/// </summary>
internal static class PeerHubRules
{
    /// <summary>等結果多久（同網頁 cmdtrack 的 150 秒，再加 10 秒）：過了就忘掉這個 commandId（之後到的結果與輸出丟掉）</summary>
    public const long PendingMs = 160_000;

    /// <summary>結果到了之後還收輸出多久（同伺服器的 <c>OutputGraceMs</c>）</summary>
    public const long OutputGraceMs = 30_000;

    /// <summary>同時等結果的指令最多幾個（超過時先忘掉最舊的）</summary>
    public const int MaxPending = 512;

    /// <summary>結果一定轉得出去的訊息種類（伺服器對每個指令都可能送）</summary>
    private static readonly HashSet<string> CommonReplies = new(StringComparer.Ordinal) { "commandResult", "toast", "sendRejected" };

    /// <summary>遠端草稿的新對話 id（伺服器只對 conversationCreate 送，中樞自己處理、不轉給網頁）</summary>
    private const string PeerCreated = "peerCreated";

    /// <summary>
    /// 這個網頁指令要不要交給同步（對其他電腦下）：型錄裡路由 Owner 的指令而且對話是副本；或路由 Device 的指令而且帶的 <c>deviceId</c> 不是這台。
    /// 一定要帶 ULID 的 <c>commandId</c>（網頁才追得到結果）；沒帶的照舊當成本機指令（副本由唯讀的白名單擋下）
    /// </summary>
    /// <param name="command">網頁送來的整個指令物件</param>
    /// <param name="isReplica">指令的 <c>conversationId</c> 是副本</param>
    /// <param name="ownDeviceId">這台的 deviceId（未配對是 null）</param>
    public static RemoteCommandSpec? Forwardable(JObject command, bool isReplica, string? ownDeviceId)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!RemoteCommandShapeRules.IsString(command["cmd"], out var name) || RemoteCommandCatalog.Find(name) is not { } spec)
            return null;
        if (!RemoteCommandShapeRules.IsString(command["commandId"], out var commandId) || !SyncIds.IsUlid(commandId))
            return null;
        return spec.Route switch
        {
            RemoteRoute.Owner => isReplica ? spec : null,
            // conversationCreate 不是網頁指令（遠端草稿由中樞自己組，AgentHub.PeerDrafts.cs）
            RemoteRoute.Device when name == RemoteCommandCatalog.ConversationCreate => null,
            RemoteRoute.Device => RemoteCommandShapeRules.IsString(command["deviceId"], out var deviceId)
                                  && !string.IsNullOrEmpty(ownDeviceId) && !string.Equals(deviceId, ownDeviceId, StringComparison.Ordinal)
                ? spec
                : null,
            _ => null,
        };
    }

    /// <summary>交給同步的 cmd 物件：網頁的指令去掉 <c>commandId</c>（它另外放在 <c>PeerCmd.commandId</c>，同瀏覽器的 <c>Cmd</c>）</summary>
    public static JObject PeerBody(JObject command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var body = (JObject)command.DeepClone();
        body.Remove("commandId");
        return body;
    }

    /// <summary>
    /// 伺服器送回的這則訊息可以轉給發起的檢視嗎：<c>commandResult</c>、<c>toast</c>、<c>sendRejected</c>，或型錄給這個指令的輸出白名單
    /// （伺服器已經照白名單檢查過，這裡再擋一次：被入侵的伺服器不能藉這條路送 <c>init</c> 之類的訊息）
    /// </summary>
    public static bool IsAllowedReply(string? cmd, JObject message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message["type"] is not JValue { Type: JTokenType.String, Value: string type })
            return false;
        if (CommonReplies.Contains(type))
            return true;
        if (type == PeerCreated)
            return cmd == RemoteCommandCatalog.ConversationCreate;
        return cmd is not null && RemoteCommandCatalog.Find(cmd) is { } spec && spec.Outputs.Contains(type, StringComparer.Ordinal);
    }

    /// <summary>
    /// 副本的卡片（核准、問卷、計畫確認）加上 <c>requestDigest</c>：遠端回應一定要帶它（sync-protocol 14.5），本機的卡片不加（輸出不變）
    /// </summary>
    public static void DecorateReplicaCard(JObject rendered, AgentChatItem item)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind is AgentItemKind.Approval or AgentItemKind.Question or AgentItemKind.PlanApproval && item.RequestDigest is { Length: > 0 } digest)
            rendered["requestDigest"] = digest;
    }
}
