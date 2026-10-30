namespace Myriagent.Sync;

/// <summary>
/// 桌面閒置時釋放已編譯的 DLP 樣式（WP4.9 步 5、計畫 D3、D5；S6）：<see cref="CredentialLeakRules"/> 的 NonBacktracking 樣式在 300 個對話的啟動重整後
/// 約 15 MB（受管理堆積），加上 GC 為那一陣配置留著的已提交空間，已配對閒置時比未配對多 60 MB 以上（WP4.9 計畫附錄 B 步 3）。
/// 同步引擎在一陣會遮蔽的工作（對話的變化、重整）之後靜置 <see cref="QuietPeriod"/>、沒有執行中的回合、沒有人在網頁上看、重整做完時才釋放；
/// 有回合或有人看而還沒編譯時先在引擎執行緒預熱（第一則即時更新不必等建置）。
/// 步 6b 另外兩件事，同樣的閒置條件：樣式從沒建過（已配對但連不上：不進 Live、不重整、不遮蔽）時，啟動之後靜置滿也做一次積極回收
/// （<see cref="ShouldCompact"/>；否則啟動讀 sync.db、outbox 與副本留下的 GC 已提交空間一直不還）；之後閒置期間 GC 已提交的空間長回來
/// <see cref="RecompactGrowthBytes"/> 以上、距上一次積極回收滿 <see cref="RecompactInterval"/> 才再做一次（穩態有上限，不必量十小時）。純函式
/// </summary>
internal static class DlpCacheTrimRules
{
    /// <summary>最後一次會遮蔽的工作之後要靜置多久才釋放（探針的 <c>ProbeRules.IdleSettleDelay</c>＝這個＋5 秒）</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(20);

    /// <summary>計時器到期時以單調時鐘量 idleFor，系統計時器比它早到（實測約 4.5% 早 0.1～0.8 ms）時再補一個 Windows 計時器刻度的餘裕</summary>
    public static readonly TimeSpan RecheckSlack = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// 閒置期間再做一次積極回收的最短間隔（距上一次積極回收，不論是釋放樣式那一次、啟動那一次或上一次再壓縮；步 6b）。
    /// 一小時最多一次：阻塞的積極回收會停住所有受管理的執行緒（含 UI），小堆積約幾毫秒到二十毫秒
    /// </summary>
    public static readonly TimeSpan RecompactInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// 閒置期間 GC 已提交的空間比上一次積極回收之後多了這麼多才再做（16 MB；步 6b）。依步 6 與合併前審查 F1 的數字：積極回收之後已配對比未配對
    /// 少 0.7～1.4 MB（300 個對話）、沒有樣式可釋放時多 8.5 MB，S6 的上限是 +30 MB；閒置的配置約 5 MB/h（沒有舊對話）～21 MB/h（300 個對話＋
    /// 1,500 個副本，步 6b 的量測）、這台的 gen0 預算上限 48 MB，不管的話幾個到十幾個小時會長滿 gen0 預算（比積極回收之後多約 48 MB）。
    /// 和一小時最多一次合起來，穩態最多比積極回收之後多「16 MB＋一個檢查週期的配置」與「一小時的配置」的較大者（21 MB/h 時約 21 MB）
    /// </summary>
    public const long RecompactGrowthBytes = 16L << 20;

    /// <summary>距上一次積極回收滿 <see cref="RecompactInterval"/>、已提交的空間還沒長到門檻時，多久再看一次（10 分鐘：計時器幾乎不花 CPU）</summary>
    public static readonly TimeSpan RecompactCheckPeriod = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 要不要釋放：已編譯、靜置滿 <see cref="QuietPeriod"/>、重整沒有進行中也沒有排著的、沒有執行中的回合、沒有被觀看的對話
    /// </summary>
    /// <param name="idleFor">距離最後一次會遮蔽的工作多久</param>
    /// <param name="reconcileIdle">重整沒有進行中、佇列是空的</param>
    /// <param name="runningTurns">執行中的回合數</param>
    /// <param name="watchedConversations">伺服器說有人在看的對話數</param>
    /// <param name="compiled">已編譯的樣式在不在（已經釋放的不再釋放）</param>
    public static bool ShouldTrim(TimeSpan idleFor, bool reconcileIdle, int runningTurns, int watchedConversations, bool compiled)
        => compiled && Idle(idleFor, reconcileIdle, runningTurns, watchedConversations);

    /// <summary>要不要預熱：有執行中的回合或被觀看的對話（接下來會有即時更新要遮蔽），而且還沒編譯</summary>
    public static bool ShouldPrewarm(int runningTurns, int watchedConversations, bool compiled)
        => !compiled && (runningTurns > 0 || watchedConversations > 0);

    /// <summary>
    /// 沒有樣式要釋放（<see cref="ShouldTrim"/> 不成立）時，要不要只做一次積極回收（步 6b）：閒置條件同 <see cref="ShouldTrim"/>，而且
    /// 這次啟動之後還沒積極回收過（<paramref name="sinceCompaction"/> 是 null：已配對但連不上，樣式從沒建過），或距上一次滿
    /// <see cref="RecompactInterval"/>、GC 已提交的空間比那一次之後多了 <see cref="RecompactGrowthBytes"/> 以上
    /// </summary>
    /// <param name="sinceCompaction">距上一次積極回收多久（這次啟動之後還沒做過是 null）</param>
    /// <param name="committedGrowthBytes">GC 已提交的空間（<see cref="CommittedEstimate"/>）比上一次積極回收之後多了多少</param>
    public static bool ShouldCompact(TimeSpan idleFor, bool reconcileIdle, int runningTurns, int watchedConversations, TimeSpan? sinceCompaction, long committedGrowthBytes)
        => Idle(idleFor, reconcileIdle, runningTurns, watchedConversations)
           && (sinceCompaction is not { } since || (since >= RecompactInterval && committedGrowthBytes >= RecompactGrowthBytes));

    /// <summary>
    /// 到期了但還不能釋放時、下一次多久之後再看（WP4.9 合併前審查 THR-3）：只差靜置時間（重整閒著、沒有回合、沒人看，<paramref name="idleFor"/> 還不到
    /// <see cref="QuietPeriod"/>：計時器比單調時鐘早到）時只等剩下的部分加 <see cref="RecheckSlack"/>，不再整整等一個靜置期（否則釋放晚 20 秒、
    /// 探針在靜置期＋5 秒取的閒置記憶體偶爾量到還沒釋放的值）；其他情況（有回合、有人看、重整中）等一個靜置期
    /// </summary>
    public static TimeSpan RecheckDelay(TimeSpan idleFor, bool reconcileIdle, int runningTurns, int watchedConversations)
        => reconcileIdle && runningTurns == 0 && watchedConversations == 0 && idleFor < QuietPeriod
            ? QuietPeriod - idleFor + RecheckSlack
            : QuietPeriod;

    /// <summary>
    /// 到期時什麼都沒做（不釋放、不積極回收）時，下一次多久之後再看（步 6b：計時器一直排著，閒置期間才看得到已提交的空間長回來）：
    /// 有回合、有人看、重整中，或只差靜置時間時照 <see cref="RecheckDelay"/>；閒置而距上一次積極回收還不到 <see cref="RecompactInterval"/>
    /// 就等到滿（同樣補 <see cref="RecheckSlack"/>，計時器早到時不會白看一次再等十分鐘）；已經滿了只是還沒長到門檻，每 <see cref="RecompactCheckPeriod"/> 看一次
    /// </summary>
    public static TimeSpan NextCheck(TimeSpan idleFor, bool reconcileIdle, int runningTurns, int watchedConversations, TimeSpan? sinceCompaction)
    {
        if (!Idle(idleFor, reconcileIdle, runningTurns, watchedConversations))
            return RecheckDelay(idleFor, reconcileIdle, runningTurns, watchedConversations);
        return sinceCompaction switch
        {
            // 閒置而且還沒積極回收過時 ShouldCompact 已經成立（不會走到這裡）；保守地再等一個靜置期
            null => QuietPeriod,
            { } since when since < RecompactInterval => RecompactInterval - since + RecheckSlack,
            _ => RecompactCheckPeriod,
        };
    }

    /// <summary>
    /// 目前 GC 已提交空間的估計（位元組；步 6b）：<c>GCMemoryInfo.TotalCommittedBytes</c> 是上一次 GC 結束時的值，兩次 GC 之間 gen0 的配置會再提交
    /// 新的空間卻看不到；<c>GC.GetTotalMemory(false)</c> 含上一次 GC 之後的配置。積極回收之後兩者幾乎相等、配置讓後者長；一般的 GC 之後留著的
    /// 空間（前者）又被後來的配置重用。兩者取大（<c>tools/probes/wp4.9/gc-committed</c>，.NET 10.0.13：積極回收後 10.6 MB、配置 45 MB 之後私有記憶體 +45、前者不動、後者 55.8）
    /// </summary>
    public static long CommittedEstimate(long lastGcCommittedBytes, long totalMemoryBytes) => Math.Max(lastGcCommittedBytes, totalMemoryBytes);

    private static bool Idle(TimeSpan idleFor, bool reconcileIdle, int runningTurns, int watchedConversations)
        => reconcileIdle && runningTurns == 0 && watchedConversations == 0 && idleFor >= QuietPeriod;
}
