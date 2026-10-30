using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// 背景工作的「詳細」（web-protocol 的 taskDetail；2026-10-25 使用者要求「詳細裡除了顯示輸出外，也要簡易說明這個背景工作是在做什麼事情」）：
/// 標頭每一列的 key、從工具卡的說明拆出代理寫的說明與指令全文、依指令的樣子寫一句白話的說明、看不到輸出時的原因
/// </summary>
internal static partial class BackgroundTaskDetailRules
{
    /// <summary>「詳細」對話框的內容（宿主依工具卡的說明產生）</summary>
    /// <param name="Explain">白話說明這個背景工作在做什麼、什麼時候結束</param>
    /// <param name="AgentNote">代理開始它時自己寫的說明（Claude 指令的 description）；沒有是 null</param>
    /// <param name="Content">指令全文、交代給子代理的內容；沒有是 null</param>
    /// <param name="ContentLabel">Content 的標題（指令、交代給子代理的工作…）</param>
    internal sealed record Detail(string Explain, string? AgentNote, string? Content, string ContentLabel);

    /// <summary>
    /// 標頭 background.tasks[].key（taskDetail 以它找回那一個）：同一個工具名稱與一行摘要在標頭只列一次（BackgroundWorkRules.View），
    /// 所以以這兩個算——SHA-256 的前 12 碼，前面加 t
    /// </summary>
    public static string Key(AgentBackgroundTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(tool.ToolName + "\n" + TextRules.FirstLine(tool.Title)));
        return "t" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>依工具的種類與說明產生「詳細」的內容</summary>
    public static Detail Describe(AgentBackgroundTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var title = TextRules.FirstLine(tool.Title);
        switch (tool.ToolKind)
        {
            case "agent":
                return new Detail(
                    (title.Length > 0 ? $"子代理（另一個代理）在背景處理「{title}」" : "子代理（另一個代理）在背景處理一件工作") + "，做完會把結果交給這個對話的代理。",
                    null, Blank(tool.Detail), "交代給子代理的工作");
            case "bash":
            {
                var (note, command) = SplitCommand(tool.Detail, tool.Title);
                return new Detail(ExplainCommand(command), note, command, "指令");
            }
            default:
            {
                var what = title.Length > 0 ? $"「{title}」" : "一件工作";
                var explain = tool.ToolName switch
                {
                    "Monitor" => $"持續監看{what}的輸出，有新的事件就通知代理；通常要停止才會結束。",
                    _ when tool.ToolName.Contains("排程", StringComparison.Ordinal) || tool.ToolName.Contains("計時", StringComparison.Ordinal)
                        => $"排定的計時或排程：時間到了會叫代理處理{what}。",
                    _ => $"代理以「{tool.ToolName}」在背景執行{what}，完成後代理會接著處理。",
                };
                return new Detail(explain, null, Blank(tool.Detail), "內容");
            }
        }
    }

    /// <summary>
    /// 指令卡的說明拆成代理寫的說明與指令全文：Claude 的是「description＋空行＋指令」（ClaudeStreamRules.DescribeTool），空行之後的第一行就是一行摘要；
    /// 其他（Codex、Antigravity、Grok）整段都是指令。沒有說明時用一行摘要
    /// </summary>
    public static (string? Note, string? Command) SplitCommand(string? detail, string? title)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return (null, Blank(title));
        var firstLine = TextRules.FirstLine(title);
        int split = detail.IndexOf("\n\n", StringComparison.Ordinal);
        if (split > 0 && firstLine.Length > 0)
        {
            var rest = detail[(split + 2)..];
            if (TextRules.FirstLine(rest) == firstLine)
                return (Blank(detail[..split]), rest.Trim());
        }
        return (null, detail.Trim());
    }

    /// <summary>
    /// 依指令的樣子寫一句白話的說明（依序比對：開發伺服器、監看、測試、建置、安裝、部署、等待、網路、腳本；都不像時是一般的說法）。
    /// 一串指令裡有測試也有等待時算測試：等待通常只是在等別的東西
    /// </summary>
    public static string ExplainCommand(string? command)
    {
        var c = command ?? string.Empty;
        if (DevServerRegex().IsMatch(c))
            return "啟動開發伺服器或其他會一直執行的程式（例如網站的預覽），通常要停止才會結束。";
        if (WatchRegex().IsMatch(c))
            return "持續監看檔案或記錄的變化（有改動就重新執行或印出來），通常要停止才會結束。";
        if (TestRegex().IsMatch(c))
            return "執行測試，跑完會得到通過或失敗的結果，代理會接著看結果。";
        if (BuildRegex().IsMatch(c))
            return "建置專案（編譯、打包），完成後代理會接著處理。";
        if (InstallRegex().IsMatch(c))
            return "安裝或還原套件，完成後代理會接著處理。";
        if (DeployRegex().IsMatch(c))
            return "部署或上傳到別的地方，完成後代理會接著處理；中途停止可能讓對方停在做到一半的狀態。";
        if (WaitRegex().IsMatch(c))
            return "等待一段時間，或反覆檢查某個條件、成立了才結束（輪詢），結束時代理會接著處理。";
        if (NetworkRegex().IsMatch(c))
            return "下載或連到網路服務，完成後代理會接著處理。";
        if (ScriptRegex().Match(c) is { Success: true } script)
            return $"執行腳本 {Path.GetFileName(script.Groups["file"].Value)}，完成後代理會接著處理。";
        return "在背景執行指令，完成後代理會接著處理。";
    }

    /// <summary>看不到輸出時的說明（Claude 的背景指令與 Monitor 以 get_task_output 問、Codex 用工具卡收到的輸出，都不是這裡）</summary>
    public static string NoOutputNote(AgentProvider provider, string? toolKind) => provider switch
    {
        AgentProvider.Claude => toolKind == "agent" ? "子代理沒有即時的輸出，做完時結果會交給代理。" : "這個背景工作沒有即時的輸出。",
        AgentProvider.Codex => "Codex 還沒有送來這個指令的輸出。",
        AgentProvider.Antigravity => "Antigravity 看不到背景工作執行中的輸出。",
        AgentProvider.Grok => "Grok 看不到背景工作執行中的輸出。",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [GeneratedRegex(@"\b(npm|pnpm|yarn|bun)\s+(run\s+)?(dev|start|serve|preview)\b|\bvite(\s|$)|\bnext\s+(dev|start)\b|\bdotnet\s+(watch|run)\b|\bhttp\.server\b|\buvicorn\b|\bflask\s+run\b|\bng\s+serve\b|\bwebpack(-dev-server|\s+serve)\b|\b(hugo|jekyll)\s+serve|\brails\s+s(erver)?\b|\bdocker(-|\s+)compose\s+up\b|\bnodemon\b|\bhttp-server\b|\bnpx\s+serve\b", RegexOptions.IgnoreCase)]
    private static partial Regex DevServerRegex();

    [GeneratedRegex(@"\btail\s+(-\w*f|--follow)\b|\bGet-Content\b.*-Wait\b|\bwatch\s|--watch\b|\binotifywait\b", RegexOptions.IgnoreCase)]
    private static partial Regex WatchRegex();

    [GeneratedRegex(@"\b(dotnet|go|cargo|mvn|gradlew?|deno|bun)\s+test\b|\b(npm|pnpm|yarn)\s+(run\s+)?test|\bpytest\b|\bjest\b|\bvitest\b|\bmocha\b|\bnode\s+--test\b|\bplaywright\s+test\b|\bphpunit\b|\brspec\b|\bctest\b|\bunittest\b", RegexOptions.IgnoreCase)]
    private static partial Regex TestRegex();

    [GeneratedRegex(@"\bdotnet\s+(build|publish|pack|msbuild)\b|\bmsbuild\b|\b(npm|pnpm|yarn|bun)\s+(run\s+)?build\b|\btsc\b|\bcargo\s+build\b|\bgo\s+build\b|\bc?make\b|\bgradlew?\s+(build|assemble)|\bmvn\s+(package|install|compile|verify)\b|\bwebpack\b", RegexOptions.IgnoreCase)]
    private static partial Regex BuildRegex();

    [GeneratedRegex(@"\b(npm|pnpm|yarn|bun)\s+(install|i|add|ci)\b|\bpip3?\s+install\b|\buv\s+(pip|sync|add)\b|\bdotnet\s+(restore|tool\s+install)\b|\b(winget|choco|scoop|apt|apt-get|brew)\s+install\b", RegexOptions.IgnoreCase)]
    private static partial Regex InstallRegex();

    [GeneratedRegex(@"deploy|\bgit\s+push\b|\bscp\b|\brsync\b|\bnpm\s+publish\b|\bkubectl\s+apply\b|\bterraform\s+apply\b|Publish-", RegexOptions.IgnoreCase)]
    private static partial Regex DeployRegex();

    [GeneratedRegex(@"\b(sleep|Start-Sleep)\b|\btimeout\s+/t\b|\buntil\b.*\bdo\b|\bwhile\b.*\b(sleep|Start-Sleep)\b|\bping\s+(-n|-c)\s+\d+", RegexOptions.IgnoreCase)]
    private static partial Regex WaitRegex();

    [GeneratedRegex(@"\b(curl|wget|Invoke-WebRequest|Invoke-RestMethod|iwr|irm)\b|\bgit\s+(clone|fetch|pull)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NetworkRegex();

    [GeneratedRegex(@"\b(?:python3?|py|node|deno|bun|tsx|ts-node|pwsh|powershell(?:\.exe)?|bash|sh|ruby|php)\b[^;&|]*?\s""?(?<file>[^\s""]+\.(?:py|js|mjs|cjs|ts|ps1|sh|rb|php))\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRegex();
}
