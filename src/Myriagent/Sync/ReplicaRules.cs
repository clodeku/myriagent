using Myriagent.Agents;
using Myriagent.Hub;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 複本（其他電腦的對話，WP4.3 計畫步 14、15，D26、D28）的純函式：存在 replica_meta／replica_items 的線上 payload（已經驗證過的）→ 中樞要的樣子。
/// 解不開的一律當成沒有（不丟例外：sync.db 裡的內容也不完全可信）
/// </summary>
internal static class ReplicaRules
{
    /// <summary>複本的政策（meta 的 syncPolicy）：解不開或不認得的當成 localOnly（沒有項目）</summary>
    public static string PolicyOf(string metaJson)
    {
        var policy = Meta(metaJson)?.SyncPolicy;
        return policy is ConversationSyncPolicies.Full or ConversationSyncPolicies.Metadata or ConversationSyncPolicies.Paused ? policy : ConversationSyncPolicies.LocalOnly;
    }

    /// <summary>政策是 full：有同步項目（metadata、paused 只有標題與狀態，WP4.2 的 P10）</summary>
    public static bool HasItems(string metaJson) => PolicyOf(metaJson) == ConversationSyncPolicies.Full;

    /// <summary>
    /// replica_meta 的一列 → 中樞的複本中繼資料：對話 id 是線上的 id，帳戶是空的（擁有者電腦的本機帳戶在這台沒有意義）、
    /// 沒有工作資料夾（擁有者電腦上的路徑）與 CLI 的 session，擁有者與 epoch 照列上的。擁有者的自訂資料夾另外放在
    /// <see cref="ReplicaMeta.CustomFolder"/>／<see cref="ReplicaMeta.Folder"/>，只供標頭與清單顯示（同瀏覽器版）；擁有者的本機帳戶 id 另外放在
    /// <see cref="ReplicaMeta.OwnerAccountId"/>（標頭的帳戶晶片換成邏輯帳戶用）。payload 解不開、供應商不認得回 null
    /// </summary>
    public static ReplicaMeta? ToMeta(ReplicaMetaRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.Deleted || Meta(row.MetaJson) is not { } payload || !WireItemReader.TryName<AgentProvider>(payload.Provider, out var provider))
            return null;
        var meta = new AgentConversation
        {
            Id = row.ConversationId,
            Title = payload.Title ?? string.Empty,
            AccountId = string.Empty,
            Provider = provider,
            Model = payload.Model,
            Effort = payload.Effort,
            PermissionMode = WireItemReader.TryName<AgentPermissionMode>(payload.PermissionMode, out var mode) ? mode : AgentPermissionMode.Ask,
            WorkingDirectory = null,
            CreatedAt = WireItemReader.FromUnixMs(payload.CreatedAt),
            UpdatedAt = WireItemReader.FromUnixMs(payload.UpdatedAt),
            SyncPolicy = PolicyOf(row.MetaJson),
            OwnerDeviceId = row.OwnerDeviceId,
            LeaseEpoch = row.Epoch,
        };
        var folder = payload.CustomFolder && !string.IsNullOrWhiteSpace(payload.WorkingDirectory) ? payload.WorkingDirectory : null;
        return new ReplicaMeta(row.ConversationId, row.OwnerDeviceId, meta, payload.Running, payload.Waiting, HasItems(row.MetaJson),
            payload.CustomFolder, folder, Models(payload.Models), string.IsNullOrEmpty(payload.LocalAccountId) ? null : payload.LocalAccountId);
    }

    /// <summary>
    /// 擁有者發布的模型清單（meta 的 <c>models</c>）→ 標頭的模型選單：value 空的與重複的略過、label 空的用 value、推理強度略過空的；
    /// 沒送或一項都沒有回 null（標頭照舊用內建的預設選單）。「預設」由渲染標頭時補（同本機的對話）
    /// </summary>
    public static IReadOnlyList<AgentModelOption>? Models(IReadOnlyList<ConversationModelOption>? models)
    {
        if (models is null)
            return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<AgentModelOption>();
        foreach (var m in models)
        {
            if (m?.Value is not { Length: > 0 } value || !seen.Add(value))
                continue;
            var efforts = (m.Efforts ?? Array.Empty<string>()).Where(e => !string.IsNullOrEmpty(e)).ToList();
            result.Add(new AgentModelOption(value, string.IsNullOrEmpty(m.Label) ? value : m.Label, string.IsNullOrEmpty(m.Description) ? null : m.Description, efforts));
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// replica_items 的 payload（conv.item）→ 桌面的項目（<see cref="WireItemReader"/>）。WP4.7 步 12 起 blob 預取到的欄位在存進 replica_items 之前
    /// 就換回內容（<see cref="ReplicaBlobRules.Inline"/>），這裡不取 blob：還有 blob 參照（取不到的）、格式不對、種類或狀態不認得都回 null。
    /// 代理產出媒體的參照放進 <see cref="AgentChatItem.MediaRefs"/>（<see cref="ReplicaMediaRules.AddMediaRefs"/>，渲染時才換成網址）
    /// </summary>
    public static AgentChatItem? ToItem(string payload)
    {
        if (string.IsNullOrEmpty(payload) || !ConversationEventRules.TryParseStrict(payload, out var token) || token is not JObject o)
            return null;
        if (!ConversationWire.TryReadItem(o, _ => null, out var wire) || WireItemReader.ToChatItem(wire) is not { } item)
            return null;
        ReplicaMediaRules.AddMediaRefs(item, wire);
        return item;
    }

    private static ConversationMetaPayload? Meta(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return SyncJson.Deserialize<ConversationMetaPayload>(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }
}
