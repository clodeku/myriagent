namespace Myriagent.Hub;

/// <summary>對話回收區的純規則（WP4.4 計畫步 7、D24）：遠端刪除的對話留 30 天，只處理 app 建立的對話資料夾名稱</summary>
internal static class ConversationTrashRules
{
    /// <summary>保留期（毫秒）</summary>
    public const long RetentionMs = ConversationTrash.RetentionDays * 86_400_000L;

    /// <summary>超過保留期（剛好 30 天還留著，多 1 毫秒才清；時鐘倒退時不清）</summary>
    public static bool IsExpired(long trashedAt, long now) => now - trashedAt > RetentionMs;

    /// <summary>可以搬進回收區、從回收區清掉的對話 id（app 建立的資料夾名稱：32 個十六進位字元；其他資料夾一律不動）</summary>
    public static bool IsTrashableId(string? id) => ConversationStoreRules.IsConversationFolderName(id);
}
