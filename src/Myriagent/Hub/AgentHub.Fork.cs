using Myriagent.Agents;
using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 延伸成新對話（web-protocol 的 forkConversation，2026-10-25 使用者要求）：複製這個對話的項目與附件，開一個由新的代理接手的對話，
/// 之後兩邊各自進行。Claude 以原對話逐字稿當下的副本 --fork-session 分支（新的代理帶著完整的前文）。
/// Grok 已建立的對話呼叫 _x.ai/session/fork（完整紀錄含工具結果）；失敗或還沒建立過就同其他代理，第一則訊息附前文節錄
/// （<see cref="ConversationForkRules"/>）。只有桌面這台的正式對話；原對話執行中也可以延伸（進行中的部分在新對話算已中斷）
/// </summary>
internal sealed partial class AgentHub
{
    private sealed partial class ConversationState
    {
        /// <summary>正在延伸成新對話（背景複製附件與逐字稿的期間；同時只做一次）</summary>
        public bool Forking;
    }

    private void ForkConversation(IAgentView view, ConversationState? source, string? paneId)
    {
        if (source == null || view is RemoteResponseView)
            return;
        _ = ForkConversationAsync(view, source, paneId);
    }

    private async Task ForkConversationAsync(IAgentView view, ConversationState source, string? paneId)
    {
        if (!source.IsDraft && !source.IsReplica)
            EnsureItems(source, notifyUnreadable: false);
        if (ConversationForkRules.Block(source.IsReplica, source.IsDraft, source.ItemsLoaded, source.Forking) is { } block)
        {
            Toast(block, view: view);
            return;
        }

        var meta = source.Meta;
        var forkId = Guid.NewGuid().ToString("N");
        var fromAttachments = store.GetAttachmentDirectory(meta.Id);
        var toAttachments = store.GetAttachmentDirectory(forkId);
        // 在 UI 執行緒取快照：背景複製檔案期間原對話照常進行，之後的內容不會跑進來
        var items = ConversationForkRules.CopyItems(source.Items, fromAttachments, toAttachments);
        var sourceRunning = source.Running;
        var workingDirectory = GetWorkingDirectory(source);
        var transcript = ForkTranscriptOf(source);
        // 在其他對話資料夾裡的媒體（原對話的代理存在自己資料夾的截圖，或原對話自己也是延伸來的）：新對話自己留一份，
        // 遠端只放行對話自己的資料夾，刪掉原對話之後這台也看得到（AgentHub.MediaKeep.cs 的 ForkKeepJob）
        var forkDirectory = store.GetConversationDirectory(forkId);
        var keepJobs = items.Where(i => MediaKeepRules.NeedsForkKeep(i, store.RootDirectory, forkDirectory, workingDirectory))
            .Select(i => ForkKeepJob(forkId, i, forkDirectory, workingDirectory))
            .ToList();
        var snapshotPath = transcript == null
            ? null
            : Path.Combine(store.GetConversationDirectory(forkId), ConversationForkRules.SourceFolderName, Path.GetFileName(transcript));

        source.Forking = true;
        string? snapshot;
        Dictionary<string, Dictionary<string, string>> kept;
        string? grokForkId = null;
        try
        {
            (snapshot, kept) = await Task.Run(() =>
            {
                CopyForkAttachments(fromAttachments, toAttachments);
                var keptMedia = KeepForkMedia(keepJobs, forkDirectory);
                return (transcript != null ? SnapshotTranscript(transcript, snapshotPath!) : null, keptMedia);
            });
            grokForkId = await TryForkNativeSessionAsync(source);
        }
        catch (Exception ex)
        {
            Log.Warn($"延伸對話 {meta.Id} 失敗：{ex.Message}");
            Toast("延伸失敗：" + ex.Message, isError: true, view: view);
            return;
        }
        finally
        {
            source.Forking = false;
        }
        if (disposed)
            return;
        foreach (var item in items)
        {
            if (!kept.TryGetValue(item.Id, out var map))
                continue;
            item.KeptMedia ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (original, relative) in map)
                item.KeptMedia[original] = relative;
        }

        // 有逐字稿的副本或 Grok 分叉成功：新對話的 CLI 對話已經在；沒有就同換帳戶，第一則訊息附前文節錄
        bool grokNative = grokForkId != null;
        bool native = snapshot != null || grokNative;
        var now = DateTime.Now;
        var fork = new AgentConversation
        {
            Id = forkId,
            Title = ConversationForkRules.Title(meta.Title),
            AccountId = meta.AccountId,
            Provider = meta.Provider,
            SessionId = grokNative ? grokForkId! : native ? Guid.NewGuid().ToString() : InitialSessionId(meta.Provider),
            SessionStarted = native,
            SessionAccountId = native ? meta.AccountId : null,
            // 原對話回溯過、還沒送出：Claude 分支點照樣帶過去。Grok 分叉是整份，不帶 ResumeAt、也不留逐字稿副本
            ResumeAt = grokNative ? null : native ? meta.ResumeAt : null,
            ForkSourceTranscript = grokNative ? null : snapshot,
            Model = meta.Model,
            Effort = meta.Effort,
            Agent = meta.Agent,
            OutputStyle = meta.OutputStyle,
            CodexServiceTier = meta.CodexServiceTier,
            CodexMode = meta.CodexMode,
            CodexSkills = meta.CodexSkills?.ToList(),
            PermissionMode = meta.PermissionMode,
            // 同一個工作資料夾（原對話用自己的工作區時也指到那裡）：延伸的是同一件事，代理先前寫的檔案、逐字稿裡的路徑都在那裡
            WorkingDirectory = workingDirectory,
            // 內容是複製的：只留在這台、偵測到權杖暫停同步的，延伸出來的也一樣
            SyncPolicy = meta.SyncPolicy,
            ForkedFrom = meta.Id,
            ForkedFromTitle = meta.Title,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var state = new ConversationState(fork) { IsDraft = false, ItemsLoaded = true, LastActivity = now };
        foreach (var item in items)
        {
            item.Seq = ++state.LastSeq;
            state.Items.Add(item);
        }
        state.Items.Add(new AgentChatItem
        {
            Kind = AgentItemKind.Notice,
            NoticeKind = AgentNoticeKind.Fork,
            Text = ConversationForkRules.Notice(meta.Title, native, sourceRunning),
            Seq = ++state.LastSeq,
        });

        bool viewOpen = views.ContainsKey(view.Id);
        if (viewOpen)
            state.ViewIds.Add(view.Id);
        conversations[fork.Id] = state;
        SaveConversation(state, saveItems: true);
        if (conversationSink != null)
        {
            if (PrepareOrdsForPublish(fork.Id, hint: null))
                store.SaveItems(fork.Id, state.Items);
            foreach (var item in state.Items)
                HookItemChanged(state, item);
        }
        RequestAntigravityWorkspaceScan(state);
        MarkListDirty();
        MarkAccountsDirty();
        FlushNow();

        if (viewOpen)
        {
            PostConversation(view, state, paneId);
            OnConversationOpened(state);
            Post(view, new JObject { ["type"] = "focusConversation", ["conversationId"] = fork.Id });
            Toast("已延伸成新的對話：在這裡交代新的代理接下來要做的事", view: view, level: "success");
        }
    }

    /// <summary>
    /// 以 CLI 自己的分叉整份複製已建立的對話（目前只有 Grok 的 _x.ai/session/fork，<see cref="IAgentSession.ForkSessionAsync"/>）。
    /// 成功回新的 session id（來源的 id 與程序都不換）。不支援、失敗、沒有帳戶或還沒建立過回 null（改附前文節錄，不讓整次延伸失敗）
    /// </summary>
    private async Task<string?> TryForkNativeSessionAsync(ConversationState source)
    {
        switch (source.Meta.Provider)
        {
            case AgentProvider.Grok:
                break;
            case AgentProvider.Claude:
                // 以逐字稿副本 --fork-session 分支（ForkTranscriptOf）
            case AgentProvider.Codex:
            case AgentProvider.Antigravity:
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(source), source.Meta.Provider, "未知的代理種類");
        }
        if (!source.Meta.SessionStarted || accounts.Find(source.Meta.AccountId) == null)
            return null;
        try
        {
            return await EnsureSession(source).ForkSessionAsync(Guid.NewGuid().ToString(), BuildSessionSettings(source, historyRecap: null));
        }
        catch (Exception ex)
        {
            Log.Warn($"延伸對話 {source.Meta.Id}：Grok 分叉失敗（{ex.Message}），改附前文節錄");
            return null;
        }
    }

    /// <summary>
    /// 延伸時要分支的 Claude 逐字稿：CLI 端建立過、帳戶還在的 Claude 對話（開著程序時是它寫的那份，原對話自己是還沒分支的延伸對話時是它的副本）；
    /// 其他回 null（改用前文節錄；Grok 走 <see cref="TryForkNativeSessionAsync"/>）
    /// </summary>
    private string? ForkTranscriptOf(ConversationState c)
    {
        if (c.Meta.Provider != AgentProvider.Claude || !c.Meta.SessionStarted || accounts.Find(c.Meta.AccountId) == null)
            return null;
        try
        {
            return EnsureSession(c).TranscriptFor(BuildSessionSettings(c, historyRecap: null));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"延伸對話 {c.Meta.Id}：找不到 Claude 逐字稿（{ex.Message}），改附前文節錄");
            return null;
        }
    }

    /// <summary>
    /// 延伸時補留的媒體（背景；新對話還沒登記，直接複製、不經 MediaKeeper 的排程）：先建好新對話的資料夾（複製不重建不存在的資料夾），
    /// 回傳項目 id → 留下的（原檔 → 副本相對路徑）。個別檔案複製不了就略過（那張照舊指到原檔）
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> KeepForkMedia(IReadOnlyList<MediaKeeper.Job> jobs, string forkDirectory)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (jobs.Count == 0)
            return result;
        Directory.CreateDirectory(forkDirectory);
        foreach (var job in jobs)
        {
            var kept = MediaKeeper.Keep(job, Log.Warn);
            if (kept.Count > 0)
                result[job.ItemId] = kept;
        }
        return result;
    }

    /// <summary>附件資料夾整份複製（含給模型看的圖、回覆媒體的副本）；個別檔案複製不了就略過（那個附件在新對話顯示不出來）</summary>
    private static void CopyForkAttachments(string from, string to)
    {
        if (!Directory.Exists(from))
            return;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(from, "*", options))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"延伸對話：附件 {file} 複製失敗：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 原對話逐字稿的副本：照位元組複製（不擋 CLI 寫入；只搬位元組、不解析不改寫），截到最後一個換行（執行中時 CLI 可能正寫到一半）。
    /// 沒有檔案、複製不了或沒有完整的一行回 null（新對話改附前文節錄）
    /// </summary>
    private static string? SnapshotTranscript(string source, string target)
    {
        try
        {
            if (!File.Exists(source))
                return null;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            long keep;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var output = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                input.CopyTo(output);
                keep = CompleteTranscriptLength(output);
                if (keep > 0)
                {
                    output.SetLength(keep);
                    output.Flush(flushToDisk: true);
                }
            }
            if (keep > 0)
                return target;
            File.Delete(target);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"延伸對話：複製 Claude 逐字稿 {source} 失敗：{ex.Message}，改附前文節錄");
            return null;
        }
    }

    /// <summary>從檔尾往前找最後一個換行（一次讀 64 KB）；整份都沒有換行回 0</summary>
    private static long CompleteTranscriptLength(FileStream stream)
    {
        const int Chunk = 64 * 1024;
        var buffer = new byte[Chunk];
        long end = stream.Length;
        while (end > 0)
        {
            long start = Math.Max(0, end - Chunk);
            int length = (int)(end - start);
            stream.Position = start;
            stream.ReadExactly(buffer, 0, length);
            long keep = ConversationForkRules.CompleteLength(buffer.AsSpan(0, length), start);
            if (keep >= 0)
                return keep;
            end = start;
        }
        return 0;
    }

    /// <summary>延伸出來的 Claude 對話分支好了：清掉 meta 的來源（呼叫端存檔），在背景刪掉這個對話資料夾裡的副本</summary>
    private void ForgetForkSource(ConversationState c)
    {
        var path = c.Meta.ForkSourceTranscript;
        c.Meta.ForkSourceTranscript = null;
        if (string.IsNullOrEmpty(path))
            return;
        var folder = Path.Combine(store.GetConversationDirectory(c.Meta.Id), ConversationForkRules.SourceFolderName);
        // 只刪這個對話自己的 fork-source（meta 被改成別處的路徑也不碰）
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase))
            return;
        _ = Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"延伸對話 {c.Meta.Id}：刪除逐字稿副本失敗：{ex.Message}");
            }
        });
    }
}
