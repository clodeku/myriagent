using Myriagent.Agents;

namespace Myriagent.Cli;

/// <summary>更新前排空對話的緊急程度（CliManager 依觸發方式決定）</summary>
internal enum CliUpdateUrgency
{
    /// <summary>
    /// 背景（自動更新）：只關閒置的程序；有執行中或等你回應的對話就什麼都不停，回 Ready=false（CliManager 延後再試，不打擾使用者）
    /// </summary>
    Background,

    /// <summary>
    /// 一般（使用者按「更新」）：閒置的立刻關；執行中的到安全點停下（Codex 先 turn/interrupt），標成「等待 CLI 更新」，
    /// UpdateFinished 後以新版自動接續；等使用者回應的，等回應之後的安全點。超過 DrainGrace 還沒停完，
    /// 由中樞在 CLI 卡問使用者（現在中斷＝cliApplyNow、取消更新＝cliCancelUpdate、或繼續等）
    /// </summary>
    Normal,

    /// <summary>立即（使用者按「現在中斷」）：不等安全點，執行中的立刻停下（被中斷的工具結果未知，接續訊息會說明）</summary>
    Immediate,
}

/// <summary>中樞對「準備更新」的回覆</summary>
/// <param name="Ready">這種 CLI 的對話程序都已結束（閒置的已關、執行中的已停下等更新）：CliManager 可以往下走（等其他程序、清殘留、換檔）</param>
/// <param name="ClosedIdle">這次關掉的閒置程序數</param>
/// <param name="Held">已停下、等 UpdateFinished 之後以新版接續的對話數</param>
/// <param name="Busy">還留著程序、沒有停下的對話數（執行中或等你回應）：Ready=false 的原因</param>
/// <param name="Reason">Ready=false 時給使用者看的原因（例如「已取消更新」）；null＝由 CliManager 依 Busy 組預設說明</param>
internal sealed record CliUpdatePreparation(bool Ready, int ClosedIdle, int Held, int Busy, string? Reason = null);

/// <summary>一次更新的結果</summary>
internal enum CliUpdateOutcome
{
    /// <summary>換到新版</summary>
    Updated,

    /// <summary>指令完成但版本沒變（已是最新、stable 頻道、由套件管理員管理…）</summary>
    Unchanged,

    /// <summary>更新失敗（Error 是原因）；仍可用的舊版在 Identity</summary>
    Failed,

    /// <summary>沒有執行更新（使用者取消、背景更新因有對話在跑而延後）</summary>
    Cancelled,
}

/// <summary>更新結束（不論結果；ICliUpdateParticipant.UpdateFinished）</summary>
/// <param name="Identity">現在可用的身分：成功是新版、失敗或取消是仍可用的舊版；null＝不明、未安裝或安裝損壞——停下的對話不要重開，以提示收尾</param>
/// <param name="Error">Outcome=Failed 的原因（給使用者看）</param>
internal sealed record CliUpdateCompletion(AgentProvider Provider, CliUpdateOutcome Outcome, CliBinaryIdentity? Identity, string? Error = null);

/// <summary>app 觸發的更新進行到哪一步（CliState.UpdatePhase；npm 屏障的進度）</summary>
internal enum CliUpdatePhase
{
    None,

    /// <summary>等中樞排空對話（PrepareForUpdateAsync 進行中）：CLI 卡可以按「現在中斷」</summary>
    Draining,

    /// <summary>對話都停了，等其他程序（帳戶查詢的短命 app-server、剛關的程序）結束、清理 .codex-* 殘留</summary>
    WaitingForProcesses,

    /// <summary>正在換檔（npm install -g、下載並換上受管 Codex、claude update）</summary>
    Installing,

    /// <summary>驗證新版（安裝已穩定、版本來源一致）</summary>
    Verifying,
}

/// <summary>
/// CliManager 與中樞之間的更新屏障（取代原本的 ReleaseIdleSessions 委派）。
/// 兩個方法都由 CliManager 在背景執行緒呼叫：實作端（中樞）只能把工作排回 UI 執行緒（IAgentHost.BeginInvoke），
/// 不能阻塞呼叫端，也不能在呼叫端的執行緒讀寫中樞狀態。
/// 規則：CliManager 每呼叫一次 PrepareForUpdateAsync，之後一定（finally）對同一個 provider 呼叫一次 UpdateFinished。
/// </summary>
internal interface ICliUpdateParticipant
{
    /// <summary>
    /// 準備換掉這種 CLI 的檔案：依緊急程度關閉或停下這種 CLI 的對話程序，回報結果。從呼叫起到 UpdateFinished 為止，
    /// 中樞不再為這種 CLI 啟動新程序（新的送出排進佇列，標頭顯示「等待 … 更新完成…」）。
    /// cancellationToken 取消（app 結束、CliManager 放棄這次更新）時停止排空，Task 以 OperationCanceledException 結束；
    /// 已停下的對話保持停下，等 UpdateFinished
    /// </summary>
    Task<CliUpdatePreparation> PrepareForUpdateAsync(AgentProvider provider, CliUpdateUrgency urgency, CancellationToken cancellationToken);

    /// <summary>
    /// 更新結束（成功、沒換版、失敗、取消都會呼叫）：解除屏障；停下的對話以 completion.Identity 重開並接續
    /// （Identity 為 null 就不重開，那一輪以提示收尾、佇列暫停）；排隊的送出照常處理。
    /// 有換版時在 CliManager.VersionChanged 之後才呼叫
    /// </summary>
    void UpdateFinished(CliUpdateCompletion completion);
}
