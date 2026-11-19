using System.Text;
using Myriagent.Agents;

namespace Myriagent.Accounts;

/// <summary>
/// 各家代理共用記憶（設定 shareAgentMemory，2026-11-19 使用者要求：同一個對話換成別的模型也要用同一份記憶）。
/// <para>
/// 記憶是 Claude Code 的那一套：<c>&lt;~/.claude&gt;\projects\&lt;專案&gt;\memory\</c> 裡一則一個檔＋索引 <c>MEMORY.md</c>
/// （資料夾照 <see cref="SharedCustomizationRules.SharedMemoryDirectory"/>，和 Claude 各帳戶共用的是同一個）。Claude 的 CLI 自己會載入索引、自己會寫；
/// Codex、Antigravity、Grok 沒有這個功能，所以由 app 在給代理的說明後面接上「記憶在哪裡、格式、什麼該記」與目前的索引，
/// 並讓它們寫得進那個資料夾（Codex 的沙箱可寫資料夾、Antigravity 的 --add-dir、Grok 的自動接受編輯）。
/// </para>
/// 說明只在 CLI 的對話建立時送一次（同其他說明）：之後別的對話更新了索引，要代理自己重讀 MEMORY.md——文字裡有寫。
/// </summary>
internal static class SharedMemoryRules
{
    public const string IndexFileName = "MEMORY.md";

    /// <summary>索引附進說明的上限（同 Claude Code 載入 MEMORY.md 的做法：只取前面這麼多行）</summary>
    public const int MaxIndexLines = 200;

    /// <summary>索引附進說明的字數上限：一行很長的索引不能把整段說明撐爆</summary>
    public const int MaxIndexChars = 24_000;

    /// <summary>讀索引檔時最多讀這麼多位元組（超過的照樣只取前面，由 <see cref="TrimIndex"/> 再裁）</summary>
    public const int MaxIndexFileBytes = 256 * 1024;

    /// <summary>這一種代理的共用記憶要不要由 app 交給它：Claude 的 CLI 自己處理（autoMemoryDirectory），其他三家由 app 帶</summary>
    public static bool AppliesTo(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => false,
        AgentProvider.Codex or AgentProvider.Antigravity or AgentProvider.Grok => true,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 索引要附進說明的部分：換行統一成 \n、去掉結尾空白，最多 <see cref="MaxIndexLines"/> 行、<see cref="MaxIndexChars"/> 個字（裁在行的邊界）。
    /// 沒有索引（null、空的、只有空白）回空字串；Truncated＝後面還有沒附上的
    /// </summary>
    public static (string Text, bool Truncated) TrimIndex(string? index)
    {
        if (string.IsNullOrWhiteSpace(index))
            return (string.Empty, false);
        var lines = index.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd().Split('\n');
        var kept = new StringBuilder();
        int count = 0;
        foreach (var line in lines)
        {
            if (count == MaxIndexLines || kept.Length + line.Length + 1 > MaxIndexChars)
                return (kept.ToString().TrimEnd('\n'), true);
            kept.Append(line.TrimEnd()).Append('\n');
            count++;
        }
        return (kept.ToString().TrimEnd('\n'), false);
    }

    /// <summary>
    /// 接在給代理的說明後面的那一段（前面自帶兩個換行）。<paramref name="directory"/> 是記憶資料夾的完整路徑，
    /// <paramref name="index"/> 是 MEMORY.md 目前的內容（還沒有是 null）
    /// </summary>
    public static string Instructions(string directory, string? index)
    {
        var (text, truncated) = TrimIndex(index);
        var indexPath = Path.Combine(directory, IndexFileName);
        var sb = new StringBuilder();
        sb.Append("\n\n# 記憶\n\n")
            .Append("這個專案有一份跨對話、各家代理（Claude、Codex、Antigravity、Grok）共用的檔案式記憶，在 `").Append(directory).Append("`。")
            .Append("同一個專案的對話不管用哪一家、哪一個模型，讀寫的都是這一份；資料夾可以直接讀寫，不必先問。\n\n")
            .Append("每一則記憶是一個檔、只記一件事，開頭是：\n\n")
            .Append("```markdown\n---\nname: <短的-kebab-case-名稱>\ndescription: <一行摘要，之後用它判斷相不相關>\nmetadata:\n  type: user | feedback | project | reference\n---\n\n")
            .Append("<內容；feedback、project 兩類接著寫 **Why:** 與 **How to apply:**。相關的記憶以 [[名稱]] 互相連結。>\n```\n\n")
            .Append("`user`：使用者是誰（角色、專長、偏好）。`feedback`：使用者對你怎麼做事的指正與肯定，連同原因。")
            .Append("`project`：從程式碼與 git 歷史看不出來的進行中工作、目標與限制（相對的日期換成絕對日期）。`reference`：外部資源在哪裡（網址、儀表板、單號）。\n\n")
            .Append("`").Append(IndexFileName).Append("` 是索引：一則記憶一行（`- [標題](檔名.md) — 一句提示`），不放內容、沒有開頭的中繼資料。")
            .Append("存一則記憶＝寫好那個檔，再在索引加一行。存之前先看有沒有已經涵蓋的檔，有就更新它、不要另寫一份；發現記憶不對就改掉或刪掉（索引那一行一起）。\n\n")
            .Append("只存「會改變之後的做法、而且以後的對話還用得上」的事：使用者糾正過的做法、明講的長期偏好、不記下來使用者就得再講一次的環境事實與外部資源的位置。")
            .Append("repo 已經記著的（程式結構、修過什麼、git 歷史）、做完的工作的流水帳、只和這次對話有關的不存；拿不準是不是長期的，就不存。\n\n")
            .Append("記憶寫的是當時的狀況：照著做之前，先確認它提到的檔案、函式、旗標還在。和眼前的工作有關的，先讀索引指到的那個檔再動手。")
            .Append("下面的索引是這個對話建立時的內容，之後可能被別的對話更新：要存、或覺得少了什麼時，重讀 `").Append(indexPath).Append("`。\n\n");
        if (text.Length == 0)
        {
            sb.Append("目前還沒有任何記憶（`").Append(IndexFileName).Append("` 還不存在；存第一則時建立它）。");
        }
        else
        {
            sb.Append("目前的索引（`").Append(IndexFileName).Append("`）：\n\n").Append(text);
            if (truncated)
                sb.Append("\n\n（索引很長，只附了前面一部分；完整的在 `").Append(indexPath).Append("`。）");
        }
        return sb.ToString();
    }
}
