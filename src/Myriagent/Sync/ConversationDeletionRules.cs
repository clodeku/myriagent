namespace Myriagent.Sync;

/// <summary>在執行緒集區檢查一個對話資料夾的結果（WP4.3 計畫 D12）</summary>
/// <param name="MetaExists"><c>meta.json</c> 存在</param>
/// <param name="ItemsExists"><c>items.json</c> 存在</param>
/// <param name="CorruptBackupExists"><c>meta.json.corrupt-*</c>（JsonFile 改名的損毀備份）存在</param>
/// <param name="ReadError">檢查時發生讀取錯誤（被占用、沒有權限、路徑問題）</param>
internal readonly record struct FolderProbe(bool MetaExists, bool ItemsExists, bool CorruptBackupExists, bool ReadError);

/// <summary>
/// 啟動時「已知、但中樞清單裡沒有」的對話是不是真的刪掉了（D12）：中樞載入時 Id 不符、損毀、被占用的對話都會被略過，
/// 只憑清單裡沒有就送 <c>conv.deleted</c> 會把它們在伺服器上永久刪除，所以要資料夾裡什麼都沒有、也沒有讀取錯誤才算。純函式
/// </summary>
internal static class ConversationDeletionRules
{
    /// <summary>四個都是 false 才確定刪除；其他情況只記警告、不送刪除</summary>
    public static bool ConfirmedDeleted(FolderProbe probe)
        => !probe.MetaExists && !probe.ItemsExists && !probe.CorruptBackupExists && !probe.ReadError;
}
