using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 回溯（web-protocol 的 rewindPreview、rewind、rewound；2026-10-25）：Claude 對話的使用者訊息可以回到「這則之前」——
/// 對話：這則與之後的項目移除、文字與附件放回輸入框，下一次啟動以 --resume-session-at 前一輪最後的 assistant --fork-session 分支出新的 session
/// （這則是第一則時開新的 session）；檔案：rewind_files 把代理以編輯工具改的檔案還原（Bash 改的不還原）。兩者可以分開選。
/// 只在閒置、沒有背景工作時；項目要有 CliUuid（這個版本起送的 Claude 訊息）
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>正在回溯（同時只做一次）</summary>
        public bool Rewinding;
    }

    /// <summary>不能回溯的原因；可以回 null</summary>
    private string? RewindBlock(ConversationState c, AgentChatItem? item)
    {
        if (c.IsReplica || c.IsDraft || c.Meta.Provider != AgentProvider.Claude)
            return "只有這台電腦的 Claude 對話可以回溯";
        if (item == null || item.Kind != AgentItemKind.User)
            return "找不到這則訊息";
        if (string.IsNullOrEmpty(item.CliUuid))
            return "這則訊息不能回溯（app 開始記錄之前送的、或不是在這個 app 送的）";
        if (c.Running)
            return "這一輪還在執行：先按停止再回溯";
        if (c.Rewinding)
            return "正在回溯";
        if (BackgroundOf(c) != null)
            return "還有背景工作在跑：等它結束（或停止）再回溯";
        return null;
    }

    private AgentChatItem? RewindItem(ConversationState c, string? itemId)
    {
        EnsureItems(c, notifyUnreadable: false);
        return c.ItemsLoaded ? c.Items.FirstOrDefault(i => i.Id == itemId) : null;
    }

    /// <summary>回溯前的預覽（rewindPreview）：對話回不回得去、檔案會改哪些（dry run）。結果只回給問的視窗</summary>
    private async Task RewindPreviewAsync(IAgentView view, ConversationState? c, string? itemId, string? requestId)
    {
        if (c == null)
            return;
        var reply = new JObject { ["type"] = "rewindPreview", ["conversationId"] = c.Meta.Id, ["requestId"] = requestId, ["itemId"] = itemId };
        var item = RewindItem(c, itemId);
        if (RewindBlock(c, item) is { } block)
        {
            reply["error"] = block;
            Post(view, reply);
            return;
        }
        int index = c.Items.IndexOf(item!);
        reply["laterItems"] = c.Items.Count - index;
        reply["laterMessages"] = c.Items.Skip(index).Count(i => i.Kind == AgentItemKind.User);
        c.Rewinding = true;
        try
        {
            var settings = BuildSessionSettings(c, historyRecap: null);
            var session = EnsureSession(c);
            var transcript = session.TranscriptFor(settings);
            var point = await Task.Run(() => ClaudeRewindRules.Find(ClaudeRewindRules.ReadLines(transcript), item!.CliUuid!));
            reply["conversation"] = point.Kind != ClaudeRewindPointKind.NotFound;
            reply["conversationError"] = point.Kind == ClaudeRewindPointKind.NotFound ? "Claude 的對話紀錄裡找不到這則之前的分支點（例如剛好在自動壓縮之後）" : null;
            AgentRewindResult files;
            try
            {
                files = await session.RewindFilesAsync(item!.CliUuid!, dryRun: true, settings);
            }
            catch (Exception ex)
            {
                files = new AgentRewindResult(false, [], 0, 0, ex.Message);
            }
            var cwd = GetWorkingDirectory(c);
            reply["files"] = new JArray(files.Files.Take(100).Select(f => RewindPathLabel(f, cwd)));
            reply["fileCount"] = files.Files.Count;
            reply["insertions"] = files.Insertions;
            reply["deletions"] = files.Deletions;
            reply["filesError"] = files.CanRewind ? null : files.Error;
        }
        finally
        {
            c.Rewinding = false;
        }
        if (!disposed)
            Post(view, reply);
    }

    /// <summary>工作資料夾裡的檔案顯示相對路徑，其他照原樣</summary>
    private static string RewindPathLabel(string path, string cwd)
    {
        try
        {
            var relative = Path.GetRelativePath(cwd, path);
            return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : relative;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>回溯（rewind）：files＝還原檔案、conversation＝回溯對話；先做檔案（失敗就不動對話）</summary>
    private async Task RewindAsync(IAgentView view, ConversationState? c, string? itemId, bool files, bool conversation)
    {
        if (c == null || (!files && !conversation))
            return;
        var item = RewindItem(c, itemId);
        if (RewindBlock(c, item) is { } block)
        {
            Toast(block, view: view);
            return;
        }
        var uuid = item!.CliUuid!;
        c.Rewinding = true;
        ClaudeRewindPoint? point = null;
        try
        {
            var settings = BuildSessionSettings(c, historyRecap: null);
            var session = EnsureSession(c);
            if (files)
            {
                AgentRewindResult result;
                try
                {
                    result = await session.RewindFilesAsync(uuid, dryRun: false, settings);
                }
                catch (Exception ex)
                {
                    result = new AgentRewindResult(false, [], 0, 0, ex.Message);
                }
                if (!result.CanRewind)
                {
                    Toast("檔案沒有還原：" + (result.Error ?? "不明的原因") + (conversation ? "（對話也沒有回溯）" : string.Empty), isError: true, view: view);
                    return;
                }
            }
            if (conversation)
            {
                var transcript = session.TranscriptFor(settings);
                point = await Task.Run(() => ClaudeRewindRules.Find(ClaudeRewindRules.ReadLines(transcript), uuid));
            }
        }
        finally
        {
            c.Rewinding = false;
        }
        if (disposed || !conversations.TryGetValue(c.Meta.Id, out var live) || !ReferenceEquals(live, c))
            return;

        var preview = TextRules.FirstLine(item.Text);
        if (preview.Length > 40)
            preview = preview[..40] + "…";
        if (conversation)
        {
            // 等待期間開始了別的一輪、這則被移除：對話不動（檔案已經還原了就照實說）
            if (point is not { Kind: not ClaudeRewindPointKind.NotFound } || c.Running || !c.Items.Contains(item))
            {
                Toast((files ? "檔案已還原，但" : string.Empty) + "對話沒有回溯："
                      + (point?.Kind == ClaudeRewindPointKind.NotFound ? "Claude 的對話紀錄裡找不到這則之前的分支點" : "對話已經變了，請再試一次"), isError: true, view: view);
                SaveConversation(c, saveItems: false);
                return;
            }
            ApplyConversationRewind(view, c, item, point);
        }
        AddItem(c, new AgentChatItem
        {
            Kind = AgentItemKind.Notice,
            Text = conversation
                ? $"已回溯到「{preview}」之前：之後的對話代理看不到了，那則訊息放回了輸入框" + (files ? "；檔案也還原了（以指令改的檔案不會還原）" : string.Empty)
                : $"已把檔案還原到「{preview}」之前（以指令改的檔案不會還原；對話照舊）",
        });
        FlushNow();
        SaveConversation(c, saveItems: true);
    }

    /// <summary>移除這則與之後的項目、記下分支點（第一則就開新的 session）、關掉程序，文字與附件放回輸入框</summary>
    private void ApplyConversationRewind(IAgentView view, ConversationState c, AgentChatItem item, ClaudeRewindPoint point)
    {
        int index = c.Items.IndexOf(item);
        foreach (var later in c.Items.Skip(index).ToList())
            RemoveItem(c, later);
        if (point.Kind == ClaudeRewindPointKind.First)
        {
            c.Meta.SessionId = Guid.NewGuid().ToString();
            c.Meta.SessionStarted = false;
            c.Meta.ResumeAt = null;
        }
        else
        {
            c.Meta.ResumeAt = point.AssistantUuid;
        }
        c.ContextUsage = null;
        // 開著的程序還記得之後的內容（閒置時 CLI 也可能自己開一輪）：關掉，下一則訊息從分支點開
        if (c.Session is { } session)
        {
            JournalProcessClose(c, "rewind");
            _ = session.CloseAsync();
        }
        foreach (var attachment in item.Attachments)
        {
            if (!c.PendingAttachments.Any(a => a.Id == attachment.Id))
                c.PendingAttachments.Add(attachment);
        }
        c.Meta.UpdatedAt = DateTime.Now;
        MarkHeaderDirty(c);
        MarkListDirty();
        PostComposer(c);
        Post(view, new JObject { ["type"] = "rewound", ["conversationId"] = c.Meta.Id, ["text"] = item.Text });
    }
}
