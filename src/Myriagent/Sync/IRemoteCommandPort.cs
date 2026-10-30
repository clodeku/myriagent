using Myriagent.Hub;
using Myriagent.Security;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 引擎 → 中樞閘門的入口（WP4.4 計畫 D2、D3）：由協調者的 EnginePort 實作，引擎以 <c>args.Host as IRemoteCommandPort</c> 取得，
/// 沒有就不宣告 <c>cmd1</c>（測試的 FakeHostPort 不實作它，WP4.1、WP4.3 的 Hello 斷言不變）。
/// <see cref="ISyncHostPort.RunOnUiAsync"/> 拿不到結果，所以另開這個介面，不改 ISyncHostPort（WP4.3 與多個替身都實作它）
/// </summary>
internal interface IRemoteCommandPort
{
    /// <summary>
    /// 經 UI 執行緒進中樞閘門（D3；任何執行緒呼叫，回傳的工作不在 UI 執行緒上接續）。<paramref name="output"/> 在 UI 執行緒上呼叫
    /// （中樞回給回應檢視的網頁訊息）。<paramref name="ct"/> 取消時還沒開始就回 rejected／busy、<see cref="RemoteCommandRun.Started"/> 是 false
    /// （確定沒有執行）；已經開始就照樣等閘門的結果，不理會取消
    /// </summary>
    Task<RemoteCommandRun> ExecuteRemoteAsync(CommandEnvelopeBody body, Action<JObject> output, CancellationToken ct);

    /// <summary>每次 Hello 後告訴協調者這台能不能執行遠端指令（D6、D31；任何執行緒）</summary>
    void ReportCommandsSupported(bool supported);
}

/// <summary>
/// 一次閘門的結果（D2）：<paramref name="Policy"/>、<paramref name="Paused"/> 是進閘門的同一個 UI 工作裡讀的這台政策（<c>HelloPolicy</c> 的
/// <c>RemoteControlLevel</c> 名稱與遠端暫停），只進遠端操作紀錄。<paramref name="Started"/> 是 false 表示確定沒有進閘門（還沒開始就取消、
/// 協調者已換世代或已釋放），這時結果一定是 rejected／busy，政策與暫停沒有讀（null：取消可能發生在任何執行緒，設定只能在 UI 執行緒讀）
/// </summary>
internal sealed record RemoteCommandRun(RemoteOutcome Outcome, string? Policy, bool? Paused, bool Started);

/// <summary>
/// 引擎給協調者與 WP4.6 的遠端指令功能（<c>SyncEngine</c> 實作；協調者以 <c>engine as ISyncEngineCommands</c> 取得，
/// 不改 ISyncEngine：WP4.3 與 RecordingSyncEngine 都實作它）。全部只把工作排進引擎執行緒
/// </summary>
internal interface ISyncEngineCommands
{
    /// <summary>從睡眠或休眠恢復（任何執行緒，D32）：清掉所有連線的伺服器時鐘；退避中照 RetryNow（保留 notBefore），連著就斷線重連（重新 Hello）</summary>
    void OnSystemResumed();

    /// <summary>
    /// 遠端操作紀錄的一頁（<c>at</c> 由新到舊、同一個 <c>at</c> 依 seq 由大到小；<paramref name="beforeAt"/> 有值時只取更早的，再帶 <paramref name="beforeSeq"/>
    /// 〔上一頁最後一列的 seq〕時以 <c>(at &lt; before OR (at = before AND seq &lt; beforeSeq))</c> 翻頁；sync.db 沒開時是空的）
    /// </summary>
    Task<IReadOnlyList<RemoteAuditEntry>> LoadRemoteAuditAsync(long? beforeAt, int limit, CancellationToken ct, long? beforeSeq = null);

    /// <summary>把遠端操作紀錄匯出成 JSONL（解除配對前，D33）；回寫了幾列，sync.db 沒開時是 0</summary>
    Task<int> ExportRemoteAuditAsync(string path, CancellationToken ct);
}

/// <summary>
/// 遠端操作紀錄的一列（sync.db 的 <c>remote_audit</c>，保留 180 天；計畫 D12、web-protocol <c>remoteAudit</c>）。給 WP4.6 的紀錄畫面讀：
/// <see cref="State"/> 是 <c>result</c> 欄的原值（<c>executing</c>／<c>done</c>／<c>rejected</c>／<c>unknown</c>），<see cref="IssuerLabel"/>
/// 已從 <c>body_masked</c> 取出（v2 沒有 issuer 欄），<see cref="Count"/> 是同一個指令被重送、回上次結果的次數加一。
/// 不參考 SQLite（中樞與協調者看得到，S6）
/// </summary>
/// <param name="DeclaredTier">信封宣告的層級（伺服器算的）</param>
/// <param name="Tier">閘門 5d 之後的最終層級（還沒完成、或在閘門之前就拒絕時是 null）</param>
/// <param name="Policy">當時這台的遠端控制政策（<c>HelloPolicy</c> 的 <c>RemoteControlLevel</c>，例如 <c>Control</c>）</param>
/// <param name="Step">停在哪一步：信封的 <c>CommandEnvelopeSteps</c>、閘門的 <c>RemoteGateSteps</c>，或去重衝突的 <c>seen</c></param>
internal sealed record RemoteAuditEntry(
    long Seq,
    long At,
    string? CommandId,
    string? Cmd,
    string? ConversationId,
    string? IssuerLabel,
    int? DeclaredTier,
    int? Tier,
    bool? FullAccess,
    string? Policy,
    bool? Paused,
    string State,
    string? Code,
    string? Step,
    bool? DefaultPassword,
    int Count);
