using System.Globalization;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Myriagent.Agents;

/// <summary>
/// Claude 的持續目標（/goal）：CLI 把目標做成這個程序的 Stop hook，每一輪結束時評估一次並在逐字稿記一筆 goal_status
/// （<c>{"type":"attachment","attachment":{"type":"goal_status","met":false,"condition":"…"}}</c>）。目標只活在程序的記憶體裡——
/// 實測 2.1.310 停止之後目標還在（下一則訊息又接著追），--resume 開的新程序就沒有了，所以只看這個程序啟動之後寫的那幾筆。
/// docs/protocol-notes.md「Claude：/goal」
/// </summary>
internal static class ClaudeGoalRules
{
    /// <summary>只讀逐字稿最後這麼多位元組（每一輪結束都會寫一筆，進行中的目標一定在尾端）</summary>
    public const int TailBytes = 512 * 1024;

    /// <summary>逐字稿與程序啟動的時間比較時的寬限（程序啟動時間是 app 記的，逐字稿是 CLI 寫的，同一台電腦的時鐘）</summary>
    public static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 最後一筆 goal_status（時間早於 since 的不算）：還沒達成（met 是 false、沒有 failed）回目標的條件，達成、清除或失敗回空字串；
    /// 沒有任何一筆回 null（不知道：沿用之前的判斷）
    /// </summary>
    public static string? LastGoal(IEnumerable<string> lines, DateTimeOffset since)
    {
        string? last = null;
        foreach (var line in lines)
        {
            if (!line.Contains("\"goal_status\"", StringComparison.Ordinal))
                continue;
            JObject o;
            try
            {
                o = JObject.Parse(line);
            }
            catch (Exception)
            {
                continue;
            }
            if (o["attachment"] is not JObject a || a.Value<string>("type") != "goal_status")
                continue;
            if (o["timestamp"] is JValue { Type: JTokenType.String or JTokenType.Date } ts
                && DateTimeOffset.TryParse(ts.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                && at < since)
                continue;
            bool met = a["met"] is JValue { Type: JTokenType.Boolean } m && m.Value<bool>();
            bool failed = a["failed"] is JValue { Type: JTokenType.Boolean } f && f.Value<bool>();
            var condition = a["condition"] is JValue { Type: JTokenType.String } c ? c.ToString().Trim() : string.Empty;
            last = met || failed || condition.Length == 0 ? string.Empty : condition;
        }
        return last;
    }

    /// <summary>讀檔尾的完整行（第一行可能被切掉一半，丟掉）；讀不到回空清單。開檔不擋 CLI 寫入</summary>
    public static IReadOnlyList<string> ReadTail(string path, int maxBytes = TailBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - start];
            int read = 0;
            while (read < buffer.Length)
            {
                int n = fs.Read(buffer, read, buffer.Length - read);
                if (n == 0)
                    break;
                read += n;
            }
            var lines = Encoding.UTF8.GetString(buffer, 0, read).Split('\n');
            return (start > 0 ? lines.Skip(1) : lines).Where(l => l.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>標頭上目標的條件最多這麼多字（完整的在逐字稿；網頁的晶片再截短）</summary>
    public const int MaxConditionLength = 2000;

    public static string Shorten(string condition)
        => condition.Length <= MaxConditionLength ? condition : condition[..MaxConditionLength] + "…";
}
