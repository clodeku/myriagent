using Myriagent.Sync;

namespace Myriagent.Remote;

/// <summary>
/// 瀏覽器建立的新對話的線上 id（WP4.4 與 WP4.5 共用，兩包加入的這個檔逐位元組相同）：伺服器草稿第一次 send 時，
/// 新對話的 id 由伺服器產生的指令 id（信封的 cid）推得；電腦以它建立對話，伺服器以它把草稿對應到新對話
/// </summary>
internal static class RemoteConversationIdRules
{
    /// <summary>
    /// ULID（26 字大寫 Crockford Base32）的 128 位元依大端轉成 32 個小寫十六進位（合格的線上對話 id）；不是 ULID 回 null
    /// </summary>
    public static string? FromCommandId(string? commandId)
    {
        if (!SyncIds.IsUlid(commandId))
            return null;
        UInt128 value = 0;
        foreach (var c in commandId!)
            value = (value << 5) | (uint)CrockfordBase32.Alphabet.IndexOf(c);
        return value.ToString("x32");
    }
}
