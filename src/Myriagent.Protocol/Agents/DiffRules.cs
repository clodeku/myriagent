using System.Globalization;
using System.Text.RegularExpressions;

namespace Myriagent.Agents;

/// <summary>
/// 編輯類工具的行差異（畫面上像 VS Code 那樣以紅綠逐行顯示）：
/// Claude 的 Edit／MultiEdit 給的是 old_string／new_string，以最長共同子序列算行差異；Write 是整個新檔（全部新增）；
/// Codex 的 fileChange：修改檔案帶 unified diff 文字，逐行分類；新增／刪除檔案只帶檔案原文，整段當新增／刪除。
/// 結果只為顯示：太長截斷（截斷提示行記下截掉的新增／刪除行數，統計照樣算進去）、長段未變更的內容收合成一行。
/// </summary>
internal static partial class DiffRules
{
    /// <summary>單一差異最多保留幾行（超過截斷並註明）</summary>
    public const int MaxLines = 800;

    /// <summary>變更前後保留的未變更行數</summary>
    public const int ContextLines = 3;

    /// <summary>LCS 的行數上限（去掉共同頭尾後，中間不同部分的 n×m 超過上限平方的一半就改成「全部刪除＋全部新增」，避免大檔吃記憶體）</summary>
    private const int LcsLimit = 1500;

    public static List<AgentDiffLine> FromEdit(string? oldText, string? newText)
        => Truncate(CollapseContext(LineDiff(SplitLines(oldText), SplitLines(newText))));

    /// <summary>新檔案：全部是新增行</summary>
    public static List<AgentDiffLine> FromNewFile(string? content)
        => Truncate(SplitLines(content).Select(l => new AgentDiffLine("add", l)).ToList());

    /// <summary>MultiEdit：每段編輯之間加一行標題</summary>
    public static List<AgentDiffLine> FromEdits(IReadOnlyList<(string? Old, string? New)> edits)
    {
        // 每段都算完再截斷（不在超過上限時提前停）：截斷提示與統計才包含後面幾段
        var result = new List<AgentDiffLine>();
        for (int i = 0; i < edits.Count; i++)
        {
            if (edits.Count > 1)
                result.Add(new AgentDiffLine("hunk", $"編輯 {i + 1}／{edits.Count}"));
            result.AddRange(CollapseContext(LineDiff(SplitLines(edits[i].Old), SplitLines(edits[i].New))));
        }
        return Truncate(result);
    }

    /// <summary>
    /// unified diff 文字 → 逐行分類。kind＝add／delete（Codex 新增／刪除檔案）時內容是檔案原文、不是 diff，
    /// 一律整段當新增／刪除：原文裡「- 清單」「---」「+」「@@」開頭的行都是內容，不能當標記解析。
    /// </summary>
    public static List<AgentDiffLine> FromUnified(string? diff, string? kind = null)
    {
        var lines = SplitLines(diff);
        if (kind is "add" or "delete")
            return Truncate(lines.Select(l => new AgentDiffLine(kind == "add" ? "add" : "del", l)).ToList());

        var result = new List<AgentDiffLine>();
        // 檔頭（---／+++／index 等）只出現在每個檔案的第一個 @@ 之前；進了 @@ 區塊後一律依第一個字元分類，
        // 「--- 註解」是刪掉的「-- 註解」、「+++i;」是新增的「++i;」
        bool inHunk = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git", StringComparison.Ordinal))
            {
                inHunk = false;
                continue;
            }
            if (line.StartsWith("\\ No newline", StringComparison.Ordinal) || (!inHunk && IsFileHeader(line)))
                continue;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
                result.Add(new AgentDiffLine("hunk", line));
            }
            else if (line.StartsWith('+'))
                result.Add(new AgentDiffLine("add", line[1..]));
            else if (line.StartsWith('-'))
                result.Add(new AgentDiffLine("del", line[1..]));
            else
                result.Add(new AgentDiffLine("ctx", line.StartsWith(' ') ? line[1..] : line));
        }
        return Truncate(result);
    }

    private static bool IsFileHeader(string line)
        => line.StartsWith("index ", StringComparison.Ordinal)
           || line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)
           || line.StartsWith("new file mode", StringComparison.Ordinal) || line.StartsWith("deleted file mode", StringComparison.Ordinal);

    /// <summary>新增與刪除行數；截斷提示行帶著被截掉的行數，也算進去（統計的是整份差異，不只畫面上顯示的部分）</summary>
    public static (int Add, int Del) Stats(IReadOnlyList<AgentDiffLine>? diff)
    {
        if (diff == null)
            return (0, 0);
        int add = 0, del = 0;
        foreach (var line in diff)
        {
            if (line.T == "add")
            {
                add++;
            }
            else if (line.T == "del")
            {
                del++;
            }
            else if (line.T == "hunk" && TruncatedPattern().Match(line.S) is { Success: true } truncated)
            {
                add += ParseCount(truncated.Groups[1].Value);
                del += ParseCount(truncated.Groups[2].Value);
            }
        }
        return (add, del);
    }

    internal static List<string> SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new List<string>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>
    /// 逐行差異：先去掉共同的頭尾，中間不同的部分以最長共同子序列比對（刪除排在新增前面，同 git 的顯示習慣）。
    /// 上限看的是去掉頭尾之後的大小（大段內容只改一行時仍逐行比對）；中間仍太大才改成「全部刪除＋全部新增」
    /// </summary>
    private static List<AgentDiffLine> LineDiff(List<string> a, List<string> b)
    {
        // 去掉共同的頭尾，縮小 DP 範圍
        int start = 0;
        while (start < a.Count && start < b.Count && a[start] == b[start])
            start++;
        int endA = a.Count, endB = b.Count;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1])
        {
            endA--;
            endB--;
        }

        int n = endA - start, m = endB - start;
        var result = new List<AgentDiffLine>(a.Count + b.Count);
        for (int k = 0; k < start; k++)
            result.Add(new AgentDiffLine("ctx", a[k]));
        if (n * (long)m > (long)LcsLimit * LcsLimit / 2)
        {
            // 中間不同的部分仍太大：不做 DP（n×m 的表格會吃掉大量記憶體），改成全部刪除＋全部新增
            for (int k = start; k < endA; k++)
                result.Add(new AgentDiffLine("del", a[k]));
            for (int k = start; k < endB; k++)
                result.Add(new AgentDiffLine("add", b[k]));
        }
        else
        {
            AppendLcs(a, b, start, n, m, result);
        }
        for (int k = endA; k < a.Count; k++)
            result.Add(new AgentDiffLine("ctx", a[k]));
        return result;
    }

    /// <summary>a、b 從 start 起各 n、m 行的最長共同子序列差異，接在 result 後面</summary>
    private static void AppendLcs(List<string> a, List<string> b, int start, int n, int m, List<AgentDiffLine> result)
    {
        var table = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                table[i, j] = a[start + i] == b[start + j]
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        var pendingAdds = new List<AgentDiffLine>();
        void FlushAdds()
        {
            result.AddRange(pendingAdds);
            pendingAdds.Clear();
        }
        while (x < n && y < m)
        {
            if (a[start + x] == b[start + y])
            {
                FlushAdds();
                result.Add(new AgentDiffLine("ctx", a[start + x]));
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                result.Add(new AgentDiffLine("del", a[start + x]));
                x++;
            }
            else
            {
                pendingAdds.Add(new AgentDiffLine("add", b[start + y]));
                y++;
            }
        }
        while (x < n)
        {
            result.Add(new AgentDiffLine("del", a[start + x]));
            x++;
        }
        while (y < m)
        {
            pendingAdds.Add(new AgentDiffLine("add", b[start + y]));
            y++;
        }
        FlushAdds();
    }

    /// <summary>長段未變更的行收合：變更前後各留 ContextLines 行，中間換成一行「⋯ 未變更 N 行」</summary>
    internal static List<AgentDiffLine> CollapseContext(List<AgentDiffLine> lines)
    {
        if (lines.Count == 0 || lines.All(l => l.T != "ctx"))
            return lines;

        var keep = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].T == "ctx")
                continue;
            for (int k = Math.Max(0, i - ContextLines); k <= Math.Min(lines.Count - 1, i + ContextLines); k++)
                keep[k] = true;
        }

        var result = new List<AgentDiffLine>();
        int hidden = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (keep[i] || lines[i].T != "ctx")
            {
                if (hidden > 0)
                {
                    result.Add(new AgentDiffLine("hunk", $"⋯ 未變更 {hidden} 行"));
                    hidden = 0;
                }
                result.Add(lines[i]);
            }
            else
            {
                hidden++;
            }
        }
        if (hidden > 0)
            result.Add(new AgentDiffLine("hunk", $"⋯ 未變更 {hidden} 行"));
        return result;
    }

    /// <summary>
    /// 超過上限截斷，最後加一行「⋯ 還有 N 行未顯示（+新增 −刪除）」：讓使用者知道截掉的部分有沒有新增內容，
    /// Stats 也從這行把截掉的行數加回去（數字一律不分文化格式，才讀得回來）
    /// </summary>
    private static List<AgentDiffLine> Truncate(List<AgentDiffLine> lines)
    {
        if (lines.Count <= MaxLines)
            return lines;
        var result = lines.Take(MaxLines).ToList();
        var (add, del) = Stats(lines.GetRange(MaxLines, lines.Count - MaxLines));
        var counts = add > 0 || del > 0 ? string.Create(CultureInfo.InvariantCulture, $"（+{add:N0} −{del:N0}）") : string.Empty;
        result.Add(new AgentDiffLine("hunk", string.Create(CultureInfo.InvariantCulture, $"⋯ 還有 {lines.Count - MaxLines:N0} 行未顯示{counts}")));
        return result;
    }

    private static int ParseCount(string value)
        => int.TryParse(value, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var count) ? count : 0;

    /// <summary>Truncate 寫的截斷提示行（格式兩邊要一致）</summary>
    [GeneratedRegex(@"^⋯ 還有 [0-9,]+ 行未顯示（\+([0-9,]+) −([0-9,]+)）$", RegexOptions.CultureInvariant)]
    private static partial Regex TruncatedPattern();
}
