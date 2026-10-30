namespace Myriagent.Rendering;

/// <summary>
/// 已經在別處轉好的一筆項目文字（Markdown → HTML）：桌面把很長的執行中回覆放到背景執行緒轉（BackgroundMarkdown），
/// 回到 UI 執行緒再交給 <see cref="ItemRenderer.Render"/> 組成項目。內容必須和 ItemRenderer 自己轉的逐位元組相同
/// </summary>
/// <param name="Html">AgentMarkdownRenderer.ToHtml 的結果</param>
/// <param name="Images">轉換時找到的本機圖片（依出現順序、含重複；ItemRenderer 照常以 AllowMarkdownImage 通知）</param>
internal sealed record PrerenderedMarkdown(string Html, IReadOnlyList<string> Images);
