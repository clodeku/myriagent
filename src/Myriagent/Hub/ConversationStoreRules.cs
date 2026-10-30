using Myriagent.Agents;
using Myriagent.Sync;

namespace Myriagent.Hub;

/// <summary>對話存檔的純函式規則：meta.json 損毀時從資料夾與紀錄重建一份最小的對話資料</summary>
internal static class ConversationStoreRules
{
    public const string RecoveredTitle = "還原的對話";

    /// <summary>app 建立的對話資料夾名稱（對話 Id＝32 個十六進位字元）；其他資料夾不當成對話還原</summary>
    public static bool IsConversationFolderName(string? name)
        => name is { Length: 32 } && name.All(Uri.IsHexDigit);

    /// <summary>
    /// meta.json 損毀時重建：Id 是資料夾名稱、標題取第一則使用者訊息、時間取紀錄檔的修改時間；
    /// 帳戶不明（AccountId 留空＝畫面標示帳戶已移除，使用者換帳戶後以前文節錄續接），工作資料夾回到對話自己的 workspace。
    /// 同步政策也不明：有旁檔（sync-policy.json）就用它的值，否則取最嚴格的 localOnly（原本可能是 localOnly，不能因為 meta.json 壞掉就被當成
    /// full 發布出去；要同步由擁有者在本機改回）
    /// </summary>
    /// <param name="sidecarPolicy">旁檔的政策（沒有旁檔是 null；讀不到或損毀的呼叫端已當成 localOnly）</param>
    public static AgentConversation RebuildMeta(string id, string? firstUserMessage, DateTime lastWrite, string? sidecarPolicy = null)
    {
        var title = string.IsNullOrWhiteSpace(firstUserMessage) ? RecoveredTitle : AgentHubRules.MakeTitle(firstUserMessage);
        return new AgentConversation
        {
            Id = id,
            Title = title,
            AccountId = string.Empty,
            SessionStarted = false,
            CreatedAt = lastWrite,
            UpdatedAt = lastWrite,
            SyncPolicy = sidecarPolicy ?? ConversationSyncPolicies.LocalOnly,
        };
    }

    /// <summary>
    /// 載入時合併 meta.json 的政策與旁檔的政策：旁檔比較嚴格（<see cref="ConversationSyncPolicies.Strictness"/>）就用旁檔的原字串，
    /// 否則照 meta.json（沒有旁檔是 null，不改）
    /// </summary>
    public static string? MergeSyncPolicy(string? stored, string? sidecarPolicy)
        => sidecarPolicy != null && ConversationSyncPolicies.Strictness(sidecarPolicy) > ConversationSyncPolicies.Strictness(stored) ? sidecarPolicy : stored;

    /// <summary>這個政策要不要旁檔：不是 null 也不是 full（含不認得的值：解讀成 localOnly，原字串照存）</summary>
    public static bool NeedsSyncPolicySidecar(string? policy) => policy != null && policy != ConversationSyncPolicies.Full;
}
