using System.Text.RegularExpressions;
using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// 延伸成新對話（web-protocol 的 forkConversation，2026-10-25 使用者要求：「從既有的對話延伸開一個新的對話，複製原有的內容但是全新的代理，
/// 讓我把正在做的事分成兩個代理各自延伸」）。新對話複製原對話的項目與附件、沿用帳戶、模型、權限模式與工作資料夾，之後和原對話各自進行：
/// <list type="bullet">
/// <item>Claude：原對話的逐字稿當下複製一份（<c>conversations\{新}\fork-source\</c>），新對話第一次啟動以
/// <c>--resume &lt;副本&gt; --fork-session --session-id &lt;新的 sid&gt;</c> 分支（實測 E-4，protocol-notes），新的代理帶著完整的前文（含工具結果）；
/// 副本凍結在延伸的那一刻，原對話之後的內容不會跑進來</item>
/// <item>其他代理（與找不到逐字稿的 Claude 對話）：新對話的 CLI 對話還沒建立（SessionStarted＝false），第一則訊息附上前文節錄（同換帳戶）</item>
/// </list>
/// </summary>
internal static partial class ConversationForkRules
{
    /// <summary>Claude 的逐字稿副本放在新對話資料夾的這個子資料夾（新的 session 開始後刪掉）</summary>
    public const string SourceFolderName = "fork-source";

    /// <summary>前文節錄的上限（字）：延伸的目的就是帶著前情做下去，比換帳戶的（AgentHubRules.MaxHistoryRecapChars）寬</summary>
    public const int MaxRecapChars = 30_000;

    /// <summary>給新代理的前文節錄開頭（接在第一則訊息之前，畫面不顯示）</summary>
    public const string RecapIntro =
        "（系統說明：這是從另一個對話延伸出來的新對話，你是接手的新代理，看不到原對話的工具結果；以下是先前對話的節錄，供你了解前情；請直接回應最後這則新訊息。）\n";

    /// <summary>延伸時原對話還在執行的工具卡，在新對話的輸出後面加這句</summary>
    public const string InterruptedToolNote = "（延伸時這個工具還在原對話執行：在這個延伸的對話裡算已中斷，結果要看原對話）";

    /// <summary>延伸時還在等回應的卡片（核准、問卷、計畫確認）：新對話的代理沒有提出它，不能在這裡回應</summary>
    public const string StaleCardOutput = "延伸時這張卡還在原對話等回應：在這個延伸的對話裡不能回應（原對話照常可以）";

    /// <summary>延伸出來、CLI 對話還沒建立的新對話第一則訊息前面附的前文節錄（同換帳戶，開頭說明與上限不同）；沒有可用內容回 null</summary>
    public static string? BuildRecap(IReadOnlyList<AgentChatItem> items)
        => AgentHubRules.BuildHistoryRecap(items, MaxRecapChars, RecapIntro);

    /// <summary>不能延伸的原因；可以回 null</summary>
    public static string? Block(bool isReplica, bool isDraft, bool itemsLoaded, bool forking)
    {
        if (isReplica)
            return "只有這台電腦的對話可以延伸";
        if (isDraft)
            return "還沒送出過訊息的對話沒有內容可以延伸";
        if (!itemsLoaded)
            return "這個對話的內容讀不到，不能延伸";
        if (forking)
            return "正在延伸這個對話";
        return null;
    }

    /// <summary>新對話的標題：原標題加「（延伸）」；已經是延伸出來的（「（延伸）」「（延伸 N）」結尾）改成下一個編號</summary>
    public static string Title(string? source)
    {
        var title = string.IsNullOrWhiteSpace(source) ? "新對話" : source.Trim();
        var m = SuffixPattern().Match(title);
        if (!m.Success)
            return title + "（延伸）";
        int n = m.Groups[1].Success && int.TryParse(m.Groups[1].Value, out var parsed) ? parsed : 1;
        return title[..m.Index] + $"（延伸 {n + 1}）";
    }

    /// <summary>
    /// 新對話的提示（加在複製過來的項目之後）：從哪裡延伸、新的代理看得到多少前情、原對話當時是不是還在執行
    /// </summary>
    /// <param name="nativeContext">Claude 以逐字稿副本分支（新的代理帶著完整的前文）；false＝第一則訊息附前文節錄</param>
    public static string Notice(string? sourceTitle, bool nativeContext, bool sourceRunning)
    {
        var title = string.IsNullOrWhiteSpace(sourceTitle) ? "原對話" : sourceTitle.Trim();
        if (title.Length > 40)
            title = title[..40] + "…";
        return $"從「{title}」延伸出來的新對話：上面是複製過來的內容，接下來由新的代理接手，和原對話各自進行。"
               + (nativeContext
                   ? "新的代理帶著原對話的完整紀錄（含工具結果）。"
                   : "新的代理看不到先前的工具結果：送出第一則訊息時會附上前文節錄。")
               + (sourceRunning ? "延伸時原對話還在執行，進行中的部分在這裡算已中斷。" : string.Empty);
    }

    /// <summary>
    /// 複製原對話的項目給新對話：每一項都是新的實例與新的 id；只在原對話有意義的欄位清掉（同步的順序與媒體參照、卡片的摘要與產生它的程序、
    /// 等回應的請求鍵）；延伸時還沒結束的項目收尾（空的回覆與思考拿掉、執行中的工具與回覆算已中斷、等回應的卡片失效——新對話的代理沒有提出它）；
    /// 附件的路徑從原對話的 attachments 改到新對話的（附件資料夾整份複製過去）。CliUuid 保留：Claude 的分支保留每則訊息的 uuid，新對話照樣能回溯。
    /// 清除提示（/clear）照樣複製：畫面照舊把它之前的藏起來，前文節錄也只收它之後的
    /// </summary>
    public static List<AgentChatItem> CopyItems(IReadOnlyList<AgentChatItem> source, string fromAttachmentDirectory, string toAttachmentDirectory)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new List<AgentChatItem>(source.Count);
        foreach (var original in source)
        {
            if (original == null)
                continue;
            if (original.Status == AgentItemStatus.Running && original.Kind is AgentItemKind.Assistant or AgentItemKind.Thinking
                && string.IsNullOrWhiteSpace(original.Text) && (original.MediaPaths == null || original.MediaPaths.Count == 0))
                continue;
            var item = ChatItemCopyRules.Copy(original);
            item.Id = Guid.NewGuid().ToString("N");
            item.Ord = null;
            item.MediaRefs = null;
            item.RequestDigest = null;
            item.ProcessInstance = null;
            item.RequestKey = null;
            item.Seq = 0;
            Settle(item);
            foreach (var attachment in item.Attachments ?? [])
            {
                if (attachment == null)
                    continue;
                attachment.StoredPath = Rebase(attachment.StoredPath, fromAttachmentDirectory, toAttachmentDirectory)!;
                if (attachment.ModelImagePaths != null)
                    attachment.ModelImagePaths = attachment.ModelImagePaths.Select(p => Rebase(p, fromAttachmentDirectory, toAttachmentDirectory)!).ToList();
            }
            if (item.MediaPaths != null)
                item.MediaPaths = item.MediaPaths.Select(p => Rebase(p, fromAttachmentDirectory, toAttachmentDirectory)!).ToList();
            result.Add(item);
        }
        return result;
    }

    /// <summary>延伸時還沒結束的項目收尾（待辦清單照舊：它記的是進度，不是程序的狀態）</summary>
    private static void Settle(AgentChatItem item)
    {
        if (item.Kind == AgentItemKind.Plan)
            return;
        if (item.Status == AgentItemStatus.Running)
        {
            item.Status = item.Kind is AgentItemKind.Tool or AgentItemKind.Assistant ? AgentItemStatus.Cancelled : AgentItemStatus.Done;
            if (item.Kind == AgentItemKind.Tool)
                item.Output = string.IsNullOrEmpty(item.Output) ? InterruptedToolNote : item.Output.TrimEnd() + "\n" + InterruptedToolNote;
        }
        else if (item.Status == AgentItemStatus.Waiting)
        {
            item.Status = AgentItemStatus.Cancelled;
            if (item.Kind is AgentItemKind.Approval or AgentItemKind.Question or AgentItemKind.PlanApproval)
                item.Output = StaleCardOutput;
        }
    }

    /// <summary>在 from 資料夾底下的路徑改到 to 底下（不分大小寫；不在底下、空的照原樣）</summary>
    public static string? Rebase(string? path, string fromDirectory, string toDirectory)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(fromDirectory))
            return path;
        var from = Path.TrimEndingDirectorySeparator(fromDirectory);
        if (path.Length > from.Length && path.StartsWith(from, StringComparison.OrdinalIgnoreCase) && path[from.Length] is '\\' or '/')
            return Path.TrimEndingDirectorySeparator(toDirectory) + path[from.Length..];
        return path;
    }

    /// <summary>
    /// 逐字稿副本要留的長度：到最後一個換行為止（原對話執行中時 CLI 可能正寫到一半，半行留著會讓分支讀到壞行）。
    /// tail 是檔案最後一段（從 tailStart 開始）；這段裡沒有換行回 -1（呼叫端再往前讀）
    /// </summary>
    public static long CompleteLength(ReadOnlySpan<byte> tail, long tailStart)
    {
        int i = tail.LastIndexOf((byte)'\n');
        return i < 0 ? -1 : tailStart + i + 1;
    }

    [GeneratedRegex(@"（延伸(?: (\d+))?）$")]
    private static partial Regex SuffixPattern();
}
