using Microsoft.Data.Sqlite;

namespace MyriagentHub.Sync;

/// <summary>
/// 物化一筆對話事件時的例外分類（WP4.2 計畫 D37）：毒事件只拒收那一筆（<c>ROLLBACK TO ev</c>、回 invalid、同批其他事件照收），
/// 不讓連線一再中斷重連（桌面重連後送同一批，又會失敗）；其他例外照舊往外丟，由批次幫浦中斷連線。
/// </summary>
internal static class ConversationFaultRules
{
    /// <summary>SQLite 的主要結果碼：constraint、mismatch、range（資料本身的問題）</summary>
    private static readonly HashSet<int> PoisonSqliteCodes = [19, 20, 25];

    /// <summary>
    /// 是不是這一筆資料本身造成的：JSON（Newtonsoft 與 System.Text.Json）、轉型、參數（含子類）、格式、溢位、null 參考、找不到鍵、索引超出範圍，
    /// 以及 SQLite 的 constraint（19）、mismatch（20）、range（25；擴充結果碼取低 8 位元）。
    /// busy、IO、磁碟滿、毀損、取消、<see cref="ObjectDisposedException"/>、<see cref="InvalidOperationException"/> 這類環境與程式狀態的問題不是
    /// </summary>
    public static bool IsPoison(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            SqliteException sqlite => PoisonSqliteCodes.Contains(sqlite.SqliteErrorCode & 0xFF),
            Newtonsoft.Json.JsonException or System.Text.Json.JsonException => true,
            InvalidCastException or ArgumentException or FormatException or OverflowException => true,
            NullReferenceException or KeyNotFoundException or IndexOutOfRangeException => true,
            _ => false,
        };
    }
}
