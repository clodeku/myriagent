using System.Text.RegularExpressions;
using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// 臨時對話（右上角的對話鈕開的浮動面板，2026-11-11 使用者要求；Hub/AgentHub.QuickChat.cs）的純規則：
/// 標題、念出來的文字、要刪的 CLI 紀錄在哪裡
/// </summary>
internal static class QuickChatRules
{
    public const string Title = "臨時對話";

    /// <summary>朗讀一則回覆最多念幾個字（超過截斷）</summary>
    public const int MaxSpeechChars = 20_000;

    /// <summary>程式碼區塊不念，換成這句</summary>
    public const string CodeBlockSpoken = "（這裡有一段程式碼，略過）";

    private static readonly Regex FencedCode = new(@"(^|\n)[ \t]*(```|~~~)[^\n]*\n[\s\S]*?(\n[ \t]*\2[^\n]*(?=\n|$)|$)", RegexOptions.CultureInvariant);

    public static bool IsEphemeral(AgentConversation? meta) => meta?.Ephemeral == true;

    /// <summary>朗讀的文字：程式碼區塊換成一句說明、拿掉 Markdown 符號與網址（<see cref="VoiceNotifyRules.Clean"/>）；沒有內容回空字串</summary>
    public static string SpeechText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return string.Empty;
        var text = FencedCode.Replace(markdown.Replace("\r\n", "\n"), m => m.Groups[1].Value + CodeBlockSpoken + "\n");
        return VoiceNotifyRules.Clean(text, MaxSpeechChars);
    }

    /// <summary>
    /// Claude 放這個臨時對話逐字稿的專案資料夾（<c>&lt;設定資料夾&gt;\projects\&lt;工作資料夾換算的名稱&gt;</c>）：臨時對話用自己的工作區，
    /// 這個資料夾只屬於它，整個刪掉。工作資料夾換算的名稱太長被 CLI 截斷時找不到，回 null（不刪別的）
    /// </summary>
    public static string? ClaudeProjectFolder(string? configDirectory, string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(configDirectory))
            return null;
        var name = ClaudeStreamRules.ProjectFolderName(workingDirectory);
        return name.Length > 200 ? null : Path.Combine(configDirectory, "projects", name);
    }
}
