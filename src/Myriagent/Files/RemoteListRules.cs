using System.Text;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Files;

/// <summary>
/// 遠端檔案指令（listFiles、fileSearch、fileFetch；WP4.7 計畫 D33～D36）輸出的純函式規則：大小上限、上一層、守門員拒絕原因的分類、檔案版本
/// </summary>
internal static class RemoteListRules
{
    /// <summary>entries 序列化後的上限（CommandOutput ≤1 MB−4 KB，伺服器換成網址之後還要放得下，D33）</summary>
    public const int MaxOutputBytes = 700 * 1024;

    /// <summary>fileFetch 失敗時 fileRef.error 的固定文字（WPR 第一節、契約:593；網頁照字面比對）</summary>
    public const string FetchRefused = "這個檔案不能從網頁讀取";

    /// <summary>
    /// 邊加邊量（UTF-8、不縮排，加上分隔的逗號），加進去會超過 <paramref name="maxBytes"/> 就停並回 truncated（之後的一筆都不加，順序不變）。
    /// 沒有父節點的物件直接放進陣列（不複製）
    /// </summary>
    public static (JArray Entries, bool Truncated) Fit(IEnumerable<JObject> entries, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        var result = new JArray();
        long used = 2;   // 中括號
        foreach (var entry in entries)
        {
            var size = Encoding.UTF8.GetByteCount(entry.ToString(Formatting.None)) + (result.Count == 0 ? 0 : 1);
            if (used + size > maxBytes)
                return (result, true);
            used += size;
            result.Add(entry);
        }

        return (result, false);
    }

    /// <summary>
    /// 上一層（<paramref name="path"/> 是正規化的「X:\a\b」）：在範圍的根（<paramref name="roots"/> 任何一個，正規化後不分大小寫相同）或磁碟根目錄時 null，
    /// 否則是去掉最後一段的路徑（「X:\a」的上一層是「X:\」）
    /// </summary>
    public static string? Parent(string path, IEnumerable<string?> roots)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(roots);
        if (!RemoteFolderRules.TryNormalize(path, out var normalized))
            return null;
        if (roots.Any(root => RemoteFolderRules.TryNormalize(root, out var r) && string.Equals(r, normalized, StringComparison.OrdinalIgnoreCase)))
            return null;
        var cut = normalized.LastIndexOf('\\');
        if (cut < 2 || normalized.Length <= 3)
            return null;
        return cut == 2 ? normalized[..3] : normalized[..cut];
    }

    /// <summary>
    /// 守門員的拒絕原因是「這個路徑不能給遠端」（範圍外、禁止清單、UNC、裝置、替代資料流、不合格的寫法、網路磁碟、8.3 短名、真實路徑不符、重新解析點）：
    /// 指令以 policy 拒絕（D33：範圍外一律 policy）。其他原因（不存在、不是資料夾或檔案、讀不到、逾時、拒絕的檔名、太大、硬連結、格式）是那個檔本身的問題，
    /// listFiles 回帶 error 的 files、fileFetch 回帶 error 的 fileRef
    /// </summary>
    public static bool RefusesPath(string reason) => reason switch
    {
        RemoteFileGuardReasons.Scope or RemoteFileGuardReasons.Forbidden or RemoteFileGuardReasons.Unc or RemoteFileGuardReasons.Device
            or RemoteFileGuardReasons.Ads or RemoteFileGuardReasons.Invalid or RemoteFileGuardReasons.Drive or RemoteFileGuardReasons.ShortName
            or RemoteFileGuardReasons.FinalPath or RemoteFileGuardReasons.Reparse => true,
        _ => false,
    };

    /// <summary>listFiles 失敗（不是路徑被拒）時 files.error 的文字</summary>
    public static string ListError(string reason) => reason switch
    {
        RemoteFileGuardReasons.NotFound => "找不到資料夾",
        RemoteFileGuardReasons.NotDirectory => "這不是資料夾",
        RemoteFileGuardReasons.Timeout => "讀取資料夾逾時",
        _ => "無法讀取這個資料夾",
    };

    /// <summary>抽屜縮圖網址的 <c>v</c>（伺服器只收 1～19 位數字）：修改時間的 UTC ticks，檔案改寫後就不同；負的或 0 回 null</summary>
    public static long? Version(DateTime lastWriteTimeUtc)
    {
        var ticks = lastWriteTimeUtc.Kind == DateTimeKind.Local ? lastWriteTimeUtc.ToUniversalTime().Ticks : lastWriteTimeUtc.Ticks;
        return ticks > 0 ? ticks : null;
    }
}
