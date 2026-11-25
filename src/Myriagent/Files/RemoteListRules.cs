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
    /// fileFetch 的路徑不在這個對話的資料夾裡時 fileRef.error 的固定文字（網頁 liverules.FILE_REF_OUTSIDE 照字面比對，兩邊一起改）：
    /// 不當成被拒，網頁在框裡說明並給擁有者「用檔案傳輸下載」（deviceFetch：那台要「可以操作」，把關在那裡）。
    /// 伺服器的投影只看 error 是不是非空字串、原樣轉給瀏覽器；舊版的網頁一律顯示 <see cref="FetchRefused"/> 的文字
    /// </summary>
    public const string FetchOutside = "這個檔案不在對話的資料夾裡";

    /// <summary>
    /// 守門員拒絕 fileFetch 的原因是不是「只是不在這個對話的範圍裡」（2026-11-20 實機：代理照使用者的要求把 PDF 存到「下載」資料夾，
    /// 手機上點了四次都只看到被拒）：原因是 scope、路徑正規化得了、而且不在對話資料夾裡（裡面被擋的是 app 自己的檔，照舊回 policy）
    /// </summary>
    public static bool IsOutsideConversation(string? reason, string? path, string? conversationDirectory)
        => reason == RemoteFileGuardReasons.Scope
           && RemoteFolderRules.TryNormalize(path, out var normalized)
           && !(RemoteFolderRules.TryNormalize(conversationDirectory, out var conversation) && RemoteFolderRules.IsWithin(normalized, conversation));

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
