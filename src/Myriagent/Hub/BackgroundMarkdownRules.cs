using Myriagent.Agents;
using Myriagent.Rendering;

namespace Myriagent.Hub;

/// <summary>
/// 很長的執行中回覆改在背景執行緒轉 Markdown（BackgroundMarkdown，2026-10-24 擁有者決定）。StreamFlushRules 已經把長的降到每 100／200 ms 送一次，
/// 但每次送仍要在 UI 執行緒整則重轉（2026-10-23 實測 20 KB 1.4 ms、50 KB 4.3 ms、100 KB 7.4 ms）：好幾個對話同時寫長回覆時，
/// UI 執行緒每秒被佔掉好幾十毫秒，送出、切換分頁跟著排隊。背景轉好、回到 UI 執行緒只組項目與登記白名單
/// </summary>
internal static class BackgroundMarkdownRules
{
    /// <summary>
    /// 文字超過這麼長才在背景轉（同 <see cref="StreamFlushRules.ShortChars"/>）：更短的轉一次不到 0.5 ms，多繞一趟背景執行緒反而晚上畫面
    /// </summary>
    public const int MinChars = StreamFlushRules.ShortChars;

    /// <summary>
    /// 這一筆送畫面時要不要在背景轉：執行中、本機的對話（複本用另一個渲染器、經伺服器取媒體）、文字走 Markdown、夠長。
    /// 結束的一律在 UI 執行緒轉（最後的內容馬上送、和整份重送的結果相同）
    /// </summary>
    public static bool Eligible(AgentItemKind kind, bool running, bool replica, int textChars)
        => running && !replica && textChars > MinChars && ItemRenderer.TextIsMarkdown(kind);

    /// <summary>送畫面的時機到了（StreamFlushRules 沒有延後）、這一筆在背景轉時要做什麼</summary>
    public enum Step
    {
        /// <summary>轉好的就是目前的文字：送出</summary>
        Post,

        /// <summary>轉好的是稍早的內容（目前的文字以它開頭）：照樣送出（串流一直在長，等最新的永遠追不上），這一筆留著下一輪再排</summary>
        PostAndKeepDirty,

        /// <summary>沒有轉好的、也沒有在轉（或轉好的不是這段文字的前段：CLI 重開後從頭重寫）：排一份目前的文字，轉好時再送</summary>
        Request,

        /// <summary>正在轉：等轉好時的回呼</summary>
        Wait,

        /// <summary>背景轉換失敗（例如巢狀過深）：改在 UI 執行緒照常轉（ItemRenderer 會改用純文字顯示並記錄）</summary>
        RenderNow,
    }

    /// <param name="result">轉好的那一份的文字；null＝沒有轉好的</param>
    /// <param name="failed">轉好的那一份是失敗的</param>
    /// <param name="current">項目目前的文字</param>
    /// <param name="pending">這一筆還在排隊或正在轉</param>
    public static Step Next(string? result, bool failed, string current, bool pending)
    {
        if (result is not null)
        {
            if (failed)
                return Step.RenderNow;
            if (string.Equals(result, current, StringComparison.Ordinal))
                return Step.Post;
            if (current.StartsWith(result, StringComparison.Ordinal))
                return Step.PostAndKeepDirty;
        }
        return pending ? Step.Wait : Step.Request;
    }
}
