using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 對話上方「最近的指令」那一行的歷史指令（web-protocol 的 promptHistory；2026-10-25 使用者要求：行尾的按鈕展開最近 10 筆，點一筆帶進輸入框，
/// app 重開後也要在）。來源是對話的 items（<see cref="AgentHubRules.RecentPrompts"/>）：網頁只載入最後一頁、標頭的 lastPrompt 會截斷，
/// 所以按了才由這裡給完整的文字，只回給問的那個視窗。只有桌面（瀏覽器版是 desktop_only，網頁從已載入的項目找）
/// </summary>
internal sealed partial class AgentHub
{
    private void PostPromptHistory(IAgentView view, ConversationState? c, string? requestId)
    {
        if (c == null || view is RemoteResponseView)
            return;
        var prompts = c.ItemsLoaded ? AgentHubRules.RecentPrompts(c.Items) : [];
        Post(view, new JObject
        {
            ["type"] = "promptHistory",
            ["conversationId"] = c.Meta.Id,
            ["requestId"] = requestId,
            ["prompts"] = new JArray(prompts.Select(item => new JObject
            {
                ["id"] = item.Id,
                ["seq"] = item.Seq,
                ["time"] = ToUnixMs(item.CreatedAt),
                ["text"] = item.Text,
                ["attachments"] = item.Attachments.Count,
            })),
        });
    }
}
