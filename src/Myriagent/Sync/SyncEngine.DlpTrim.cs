using System.Globalization;
using Myriagent.Core;
using Myriagent.Hub;

namespace Myriagent.Sync;

// WP4.9 計畫步 5（D3、D5；S6）：閒置時釋放已編譯的 DLP 樣式、有活動時先預熱，都在引擎執行緒。
// - 活動：每一批對話的變化（UI 的掛鉤、重整要的 meta 與項目頁都經盒子來）處理完時記下時間、排靜置計時器（TimeProvider，測試撥快）；
//   伺服器的觀看清單改變時、遠端指令進來與結果回來時也記一次活動（合併前審查 DLP-2、THR-1：只看不改的觀看會預熱、遠端指令的稽核會遮蔽，
//   兩者都可能重建已釋放的樣式，不排計時器的話重建的那一份一直留到下一批對話的變化）。送出時的投影在那一批之後 100 ms 內（送出延遲），算在同一陣裡。
//   啟動做完（Startup 開好 sync.db、準備連線之前）也算一次活動（步 6b）。
// - 計時器到期時照 DlpCacheTrimRules.ShouldTrim 判斷（靜置期從最後一批活動算）：釋放、做一次積極回收（阻塞、壓縮，把為啟動重整那一陣配置
//   留著的已提交空間還給系統；計畫 D5、附錄 B 步 3）。還不能釋放（有回合、有人看、重整中）就再等一個靜置期；
//   只因系統計時器比單調時鐘早到而差一點點的，只補剩下的時間加一個計時器刻度（DlpCacheTrimRules.RecheckDelay）。
// - 沒有樣式要釋放時（步 6b，DlpCacheTrimRules.ShouldCompact，同樣的閒置條件）：這次啟動之後還沒積極回收過就做一次（已配對但連不上的桌面不進 Live、
//   不重整、不遮蔽，樣式從沒建過，否則啟動讀 sync.db、outbox 與副本留下的已提交空間一直不還：步 6 量到比未配對多約 18 MB、上千個副本時約 44 MB）；
//   之後閒置期間 GC 已提交的空間比上一次積極回收之後多了 16 MB 以上、而且距上一次滿一小時才再做一次（心跳與 keepalive 的配置讓 gen0 的已提交空間
//   慢慢長回來，合併前審查 F1）。所以計時器一直排著：做完之後一小時、之後每 10 分鐘看一次（DlpCacheTrimRules.NextCheck），有活動時改排靜置期。
// - 別處（遠端指令的中樞處理、WP4.7 的遠端檔案與媒體伺服器）在釋放之後要用時自己重建：每一陣活動最多釋放一次，不會來回重建
//   （UI 執行緒上的呼叫端由 WP4.9 計畫步 6 第 2 點量過）；遠端指令進來與結果回來都算活動，所以指令期間重建的那一份在靜置期之後照樣釋放。
// - 只有 TrimDlpCache（預設開著）時才做：測試的 EngineHarness 預設關掉（全程序共用的樣式，量記憶體與平行的測試不互相干擾），要測的測試自己打開。
// - 停止時 SyncEngine.cs 的 CancelTimers 一併取消（CancelDlpTrimTimer：最長一小時才觸發，回呼抓著整個舊引擎，同合併前審查 T4）；回呼看到 stopping 也直接丟掉
internal sealed partial class SyncEngine
{
    private ITimer? dlpTrimTimer;
    private int dlpTrimToken;
    private long dlpActivityAt;
    private int dlpTrims;
    private int dlpCompactions;

    /// <summary>上一次積極回收的時間（<see cref="TimeProvider.GetTimestamp"/>；這次啟動之後還沒做過是 null）</summary>
    private long? dlpCompactedAt;

    /// <summary>上一次積極回收之後的 GC 已提交空間（<see cref="DlpCacheTrimRules.CommittedEstimate"/>）</summary>
    private long dlpCommittedAfterCompaction;

    /// <summary>閒置時釋放 DLP 樣式（啟動前設定；預設開著，EngineHarness 預設關掉）</summary>
    internal bool TrimDlpCache { get; set; } = true;

    /// <summary>量 GC 已提交的空間、做積極回收的地方（啟動前設定；測試換成假的，撥快的時鐘下模擬已提交的空間長回來、數次數）</summary>
    internal IDlpHeapPort DlpHeap { get; set; } = ProcessDlpHeap.Instance;

    /// <summary>閒置釋放 DLP 樣式的次數（測試與探針用）</summary>
    internal int DlpTrimCount => Volatile.Read(ref dlpTrims);

    /// <summary>積極回收的次數（釋放樣式那幾次＋啟動之後那一次＋閒置期間的再壓縮；測試用）</summary>
    internal int DlpCompactCount => Volatile.Read(ref dlpCompactions);

    /// <summary>
    /// 引擎執行緒：一批會遮蔽（或預熱）的工作做完了（HandleConversationSignal、OnWatchChanged、遠端指令的 OnCommand 與 FinishCommand），
    /// 或啟動做完了（Startup，步 6b）。記下時間、需要時預熱、重排靜置計時器
    /// </summary>
    private void NoteDlpActivity()
    {
        if (!TrimDlpCache || stopping)
            return;
        dlpActivityAt = time.GetTimestamp();
        MaybePrewarmDlp();
        ArmDlpTrim(DlpCacheTrimRules.QuietPeriod);
    }

    /// <summary>引擎執行緒：有執行中的回合或有人在看、而且樣式已釋放時先建好（接下來的即時更新不必等）</summary>
    private void MaybePrewarmDlp()
    {
        if (!TrimDlpCache || stopping || CredentialLeakRules.IsCompiled)
            return;
        if (DlpCacheTrimRules.ShouldPrewarm(RunningTurnsForDlp(), serverWatch.Count, compiled: false))
            CredentialLeakRules.EnsureCompiled();
    }

    private void ArmDlpTrim(TimeSpan due)
    {
        var token = ++dlpTrimToken;
        dlpTrimTimer?.Dispose();
        dlpTrimTimer = CreateTimer(due, () => OnDlpTrimDue(token));
    }

    /// <summary>停止時（CancelTimers）：取消閒置釋放與再壓縮的計時器</summary>
    private void CancelDlpTrimTimer()
    {
        dlpTrimToken++;
        CancelTimer(ref dlpTrimTimer);
    }

    private void OnDlpTrimDue(int token)
    {
        if (token != dlpTrimToken)
            return;
        CancelTimer(ref dlpTrimTimer);
        if (stopping)
            return;

        var compiled = CredentialLeakRules.IsCompiled;
        var reconcileIdle = !refreshing && activeJob is null && reconcileQueue.Count == 0;
        var idle = time.GetElapsedTime(dlpActivityAt);
        var running = RunningTurnsForDlp();
        var watched = serverWatch.Count;
        if (DlpCacheTrimRules.ShouldTrim(idle, reconcileIdle, running, watched, compiled))
        {
            CredentialLeakRules.ReleaseCompiledPatterns();
            Interlocked.Increment(ref dlpTrims);
            CompactDlpHeap("同步閒置：釋放 DLP 樣式的快取、積極回收");
            ArmDlpTrim(DlpCacheTrimRules.RecompactInterval);
            return;
        }

        // 沒有樣式要釋放（步 6b）：這次啟動之後還沒積極回收過，或閒置期間已提交的空間長回來了
        TimeSpan? since = dlpCompactedAt is { } at ? time.GetElapsedTime(at) : null;
        var growth = since is null ? 0 : DlpHeap.CommittedBytes() - dlpCommittedAfterCompaction;
        if (DlpCacheTrimRules.ShouldCompact(idle, reconcileIdle, running, watched, since, growth))
        {
            CompactDlpHeap(since is null
                ? "同步閒置：積極回收（啟動之後第一次，沒有 DLP 樣式要釋放）"
                : $"同步閒置：積極回收（距上一次 {since.Value.TotalMinutes:0} 分鐘、GC 已提交的空間多了 {Mb(growth)} MB）");
            ArmDlpTrim(DlpCacheTrimRules.RecompactInterval);
            return;
        }

        // 還不能做：有回合、有人看、重整中再等一個靜置期；只差計時器比單調時鐘早到的那一點就只補剩下的（THR-3）；
        // 閒置但還不到一小時、或還沒長到門檻，等到滿一小時、之後每 10 分鐘看一次
        ArmDlpTrim(DlpCacheTrimRules.NextCheck(idle, reconcileIdle, running, watched, since));
    }

    /// <summary>引擎執行緒：積極回收一次，記下時間與之後的已提交空間（下一次再壓縮從這裡算）</summary>
    private void CompactDlpHeap(string what)
    {
        var before = DlpHeap.CommittedBytes();
        var started = time.GetTimestamp();
        DlpHeap.Compact();
        var took = time.GetElapsedTime(started);
        dlpCompactedAt = time.GetTimestamp();
        dlpCommittedAfterCompaction = DlpHeap.CommittedBytes();
        Interlocked.Increment(ref dlpCompactions);
        Log.Info($"{what}（GC 已提交約 {Mb(before)} → {Mb(dlpCommittedAfterCompaction)} MB，{took.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms）");
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>
    /// 執行中的回合（中繼資料的 Running；草稿與副本不算）的對話：不看發布狀態，每一份中繼資料快照都記（<see cref="NoteDlpRunning"/>）。
    /// 不能用 lastMeta：發布過、後來改成 localOnly（或換到 Antigravity）而有墓碑的對話，之後的 MetaChanged 在更新 lastMeta 之前就 return、
    /// ForgetConversation 又把它移出 lastMeta，只有重整那一刻的快照寫得進去——執行中的回合看不到，或重整時記下的 Running 一直留著（合併前審查 PROD-1）
    /// </summary>
    private readonly HashSet<string> dlpRunning = new(StringComparer.Ordinal);

    /// <summary>引擎執行緒：一份中繼資料快照（盒子的 MetaChanged、要到的 MetaBatch）到了，記下這個對話有沒有執行中的回合；刪除時另外移除（不受 ForgetConversation 影響）</summary>
    private void NoteDlpRunning(string wire, PublishMetaSnapshot meta)
    {
        if (meta is { Running: true, IsDraft: false, IsReplica: false })
            dlpRunning.Add(wire);
        else
            dlpRunning.Remove(wire);
    }

    /// <summary>執行中的回合數（<see cref="dlpRunning"/>）。中繼資料還沒到、但已經有執行中的項目在串流時至少算 1</summary>
    private int RunningTurnsForDlp()
    {
        var count = dlpRunning.Count;
        return count == 0 && runningItems.Count > 0 ? 1 : count;
    }
}

/// <summary>量 GC 已提交的空間、做積極回收（WP4.9 步 6b；引擎測試換成假的）</summary>
internal interface IDlpHeapPort
{
    /// <summary>目前 GC 已提交空間的估計（位元組，<see cref="DlpCacheTrimRules.CommittedEstimate"/>）</summary>
    long CommittedBytes();

    /// <summary>阻塞、壓縮的積極回收（計畫 D5）</summary>
    void Compact();
}

/// <summary>這個程序真的 GC</summary>
internal sealed class ProcessDlpHeap : IDlpHeapPort
{
    public static readonly ProcessDlpHeap Instance = new();

    private ProcessDlpHeap()
    {
    }

    public long CommittedBytes() => DlpCacheTrimRules.CommittedEstimate(GC.GetGCMemoryInfo().TotalCommittedBytes, GC.GetTotalMemory(false));

    public void Compact() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
}
