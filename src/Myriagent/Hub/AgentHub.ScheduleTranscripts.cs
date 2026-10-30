using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Schedules;

namespace Myriagent.Hub;

// 排程的回溯（排程頁的 S2，設計 docs/plans/2026-10-26-schedule-manager.md 第 3 節）：以前在終端機、IDE 或 Myriagent（這個功能之前）讓代理建立的排程，
// 從各帳戶的 CLI 對話紀錄找出建立它的指令（TranscriptOriginScanner，增量、只讀、背景低優先的執行緒），連到那個工作階段：
// 工作階段 id 對得上 Myriagent 的對話就連到對話，否則記成外部的工作階段，明細可以一鍵匯入成 Myriagent 對話（沿用匯入外部對話）。
// 啟動 1 分鐘後第一次、之後每 30 分鐘一次（只讀新增的部分），「重新掃描」也會順便補一次。掃到的與讀到哪記在 schedules\scan-index.json。
// 沒掛（AttachScheduleTranscripts；測試預設）時不掃：HubHarness 的系統預設帳戶是這台真的 ~/.claude
internal sealed partial class AgentHub
{
    internal static readonly TimeSpan TranscriptFirstDelay = TimeSpan.FromMinutes(1);

    internal static readonly TimeSpan TranscriptInterval = TimeSpan.FromMinutes(30);

    private Func<IReadOnlyList<TranscriptRoot>>? transcriptRoots;
    private TranscriptScanIndex? transcriptIndex;
    private bool transcriptScanning;
    private DateTime transcriptAttachedAt;
    private DateTime transcriptLastScan = DateTime.MinValue;

    /// <summary>掛上要回溯的帳戶資料夾（AppController；在 UI 執行緒取）</summary>
    internal void AttachScheduleTranscripts(Func<IReadOnlyList<TranscriptRoot>> roots)
    {
        transcriptRoots = roots ?? throw new ArgumentNullException(nameof(roots));
        transcriptAttachedAt = ScheduleClock();
    }

    private string TranscriptIndexPath => Path.Combine(scheduleDirectory!, "scan-index.json");

    /// <summary>這台各帳戶的設定資料夾（系統預設的也算；同一個資料夾只掃一次）</summary>
    internal IReadOnlyList<TranscriptRoot> DefaultTranscriptRoots()
        => accounts.Accounts
            .Where(a => a.Provider is AgentProvider.Claude or AgentProvider.Codex or AgentProvider.Antigravity or AgentProvider.Grok)
            .Select(a => new TranscriptRoot(a.Provider, a.Id, Myriagent.Accounts.AccountRules.ResolveConfigDirectory(a)))
            .Where(r => !string.IsNullOrWhiteSpace(r.ConfigDirectory))
            .ToList();

    /// <summary>維護計時器（TickSchedules 之後）：時候到了就在背景掃一次</summary>
    private void TickScheduleTranscripts(bool force = false)
    {
        if (transcriptRoots == null || scheduleDirectory == null || transcriptScanning || disposed)
            return;
        var now = ScheduleClock();
        bool due = transcriptLastScan == DateTime.MinValue ? now - transcriptAttachedAt >= TranscriptFirstDelay : now - transcriptLastScan >= TranscriptInterval;
        if (!due && !force)
            return;
        transcriptScanning = true;
        var roots = transcriptRoots();
        var previous = transcriptIndex;
        var path = TranscriptIndexPath;
        var ct = scheduleCancel.Token;
        var thread = new Thread(() =>
        {
            TranscriptScanIndex? result = null;
            try
            {
                var index = previous ?? JsonFile.Read<TranscriptScanIndex>(path) ?? new TranscriptScanIndex();
                index.Files = new Dictionary<string, TranscriptFileState>(index.Files ?? new(), StringComparer.OrdinalIgnoreCase);
                index.Hits ??= new List<TranscriptHit>();
                result = TranscriptOriginScanner.Scan(roots, index, ct);
                JsonFile.Write(path, result);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Error("排程回溯：掃描對話紀錄失敗", ex);
            }
            host.BeginInvoke(() => OnTranscriptsScanned(result));
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "排程回溯",
        };
        thread.Start();
    }

    private void OnTranscriptsScanned(TranscriptScanIndex? index)
    {
        transcriptScanning = false;
        transcriptLastScan = ScheduleClock();
        if (disposed || index == null)
            return;
        transcriptIndex = index;
        if (ApplyTranscriptOrigins())
            PostSchedules(force: false);
    }

    /// <summary>還沒有出處的排程：從掃到的紀錄找建立它的那一筆（工作階段對得上 Myriagent 對話的連到對話）。有新增回 true</summary>
    private bool ApplyTranscriptOrigins()
    {
        if (transcriptIndex is not { } index || scheduleStore is not { } store || scheduleScan is not { } scan || index.Hits.Count == 0)
            return false;
        bool changed = false;
        foreach (var t in scan.Tasks)
        {
            if (store.State.Origins.ContainsKey(t.Path) || store.State.Ignored.Contains(t.Path, StringComparer.OrdinalIgnoreCase))
                continue;
            if (TranscriptOriginRules.OriginFor(t.Path, index.Hits) is not { } hit)
                continue;
            var conversation = conversations.Values.FirstOrDefault(c => !c.IsReplica && !c.IsDraft
                && string.Equals(c.Meta.SessionId, hit.SessionId, StringComparison.OrdinalIgnoreCase));
            store.State.Origins[t.Path] = new ScheduleOriginEntry
            {
                Source = conversation != null ? "myriagent" : "transcript",
                Verb = hit.Verb == "changed" ? "changed" : "created",
                At = hit.At,
                ConversationId = conversation?.Meta.Id,
                Title = conversation?.Meta.Title ?? ExternalSessionTitle(hit),
                Provider = hit.Provider,
                TranscriptPath = conversation == null ? hit.FilePath : null,
                AccountId = hit.AccountId,
                SessionId = hit.SessionId,
                Cwd = hit.Cwd,
            };
            changed = true;
        }
        if (changed)
            store.Save();
        return changed;
    }

    /// <summary>外部工作階段的標題：工作資料夾的名稱（沒有就只寫是哪家的）</summary>
    internal static string ExternalSessionTitle(TranscriptHit hit)
    {
        var folder = string.IsNullOrWhiteSpace(hit.Cwd) ? null : Path.GetFileName(hit.Cwd.TrimEnd('\\', '/'));
        return folder is { Length: > 0 } ? $"{hit.Provider} 工作階段・{folder}" : $"{hit.Provider} 工作階段";
    }

    /// <summary>scheduleAction 的 importOrigin：把出處的外部工作階段匯入成 Myriagent 對話（沿用匯入外部對話），之後連到它</summary>
    private void ImportScheduleOrigin(IAgentView view, string path, string? paneId)
    {
        if (scheduleStore?.State.Origins.TryGetValue(path, out var origin) != true || origin!.TranscriptPath == null || origin.SessionId == null)
        {
            Toast("這個排程沒有可以匯入的外部工作階段", isError: true, view);
            return;
        }
        if (!Enum.TryParse<AgentProvider>(origin.Provider, out var provider))
            return;
        var account = accounts.Find(origin.AccountId);
        var key = ExternalConversationRules.ComputeSourceKey(provider, origin.AccountId ?? "", origin.TranscriptPath);
        externalImportCache[key] = new ExternalConversationCandidate(key, provider, origin.AccountId ?? "", account?.Label ?? provider.ToString(), origin.SessionId,
            origin.Title ?? "匯入的工作階段", origin.Cwd, origin.At, 0, origin.TranscriptPath);
        ImportExternalConversation(view, key, paneId);
    }

    /// <summary>匯入完成（或之後任何時候）：外部工作階段的出處對得上新的 Myriagent 對話就改連過去（維護計時器順便看）</summary>
    private void RelinkTranscriptOrigins()
    {
        if (scheduleStore is not { } store)
            return;
        bool changed = false;
        foreach (var origin in store.State.Origins.Values)
        {
            if (origin.Source != "transcript" || origin.SessionId == null)
                continue;
            var c = conversations.Values.FirstOrDefault(x => !x.IsReplica && !x.IsDraft && string.Equals(x.Meta.SessionId, origin.SessionId, StringComparison.OrdinalIgnoreCase));
            if (c == null)
                continue;
            origin.Source = "myriagent";
            origin.ConversationId = c.Meta.Id;
            origin.Title = c.Meta.Title;
            origin.TranscriptPath = null;
            changed = true;
        }
        if (changed)
        {
            store.Save();
            PostSchedules(force: true);
        }
    }
}
