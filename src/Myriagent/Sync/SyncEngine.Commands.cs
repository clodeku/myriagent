using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Security;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

// 遠端指令的引擎部分（WP4.4；計畫 D2～D11、D36，sync-protocol 14.4、14.6）：實作 SyncEngine.Contracts.cs 宣告的 WP4.4 登記點。
// 收到 CommandDelivery → 以這條連線 Hello 記下的伺服器時鐘驗信封 → commands_seen 去重、新指令在同一個交易記 executing 與遠端操作紀錄
// （提交之後才執行，同步不變式「遠端指令至多執行一次」）→ 經 IRemoteCommandPort 在 UI 執行緒進中樞閘門 → 結果與輸出依序送回。
// 狀態只在引擎執行緒讀寫（不加鎖）；中樞只經 port。沒有 port（測試的 FakeHostPort、未接上的宿主）時不宣告 cmd1、不碰 sync.db，
// Hello 與第 3 階段、WP4.1 相同。斷線時還沒送出的結果留在記憶體（最多 64 則），下一條協商出 cmd1 的連線交握完成時補送；輸出一律丟掉。
// 伺服器時鐘跟著連線：協商出 cmd1 的連線滿 6 小時（而且沒有執行中的指令）、時間判不過、睡眠恢復都重新 Hello（ENV-4）
internal sealed partial class SyncEngine : ISyncEngineCommands
{
    // ── 以下只在引擎執行緒讀寫 ──

    /// <summary>目前這條連線的指令狀態（Hello 時建立；宣告了 cmd1 才收指令；連線丟掉時清掉）</summary>
    private CommandLink? commandLink;

    /// <summary>斷線時還沒送出的結果（依原本順序，最多 <see cref="SyncCommandRules.MaxPendingResults"/> 則；引擎停止就丟，D10）</summary>
    private readonly List<CommandResult> pendingResults = [];

    /// <summary>這個引擎實例上一次修剪的時間（Unix 毫秒；null＝還沒修剪過，D11）</summary>
    private long? lastPruneAtMs;

    /// <summary>進行中表：已記 executing、交給中樞還沒有結果的指令（cid → 狀態）</summary>
    private readonly Dictionary<string, RunningCommand> runningCommands = new(StringComparer.Ordinal);

    /// <summary>已完成結果的快取（完成時寫不進 sync.db，重送也回得出上一次的結果；最多 <see cref="SyncCommandRules.MaxCompletedCache"/> 筆）</summary>
    private readonly Dictionary<string, CommandResult> completedResults = new(StringComparer.Ordinal);

    private readonly Queue<string> completedOrder = new();

    /// <summary>這個引擎實例做過啟動回收（殘留的 executing 改成 unknown，D11）</summary>
    private bool commandStartupDone;

    private bool audienceWarned;

    /// <summary>
    /// 上一次因為時間判不過而重新 Hello 的單調時戳（<see cref="CommandTimestamp"/>；每分鐘最多一次，D4）。不用牆上時鐘：
    /// 牆上時鐘往回調時限制會一直判成「一分鐘內」，時鐘不可用的連線就一直回 expired（合併前審查 T3）
    /// </summary>
    private long? lastClockReHelloTimestamp;

    /// <summary>
    /// 測試接縫：取代 <c>time.GetTimestamp()</c>，只用在伺服器時鐘的年齡與界線（D35；撥快 ManualTimeProvider 會觸發心跳與重新 Hello 的計時器）。
    /// 啟動前設定（之後只在引擎執行緒讀）
    /// </summary>
    internal Func<long>? CommandTimestampForTest { get; set; }

    private IRemoteCommandPort? CommandPort => args.Host as IRemoteCommandPort;

    /// <summary>一條連線的指令狀態：宣告了 cmd1 沒有、Hello 記下的伺服器時鐘、依序送出的佇列（輸出與結果）</summary>
    private sealed class CommandLink(SyncConnection conn, bool declared)
    {
        public SyncConnection Conn { get; } = conn;

        /// <summary>這條連線的 Hello 由這裡宣告了 cmd1（測試以登記點硬加的不算）</summary>
        public bool Declared { get; } = declared;

        /// <summary>Hello 的伺服器時鐘（往返超過 30 秒時是 null：除了 stop 一律 expired）</summary>
        public ServerClock? Clock { get; set; }

        /// <summary>還沒送出的輸出（<see cref="CommandOutput"/>）與結果（<see cref="CommandResult"/>），依序一次送一則</summary>
        public List<object> Outgoing { get; } = [];

        /// <summary>正在送的那一則（送完才送下一則）</summary>
        public object? Sending { get; set; }

        /// <summary>時間判不過：送完佇列之後重新 Hello</summary>
        public bool ReHelloRequested { get; set; }

        /// <summary>連線丟掉了：還沒送出的結果已經移到 pendingResults，之後這條連線的送出回呼一律不理</summary>
        public bool Closed { get; set; }

        /// <summary>時鐘滿 6 小時重新 Hello 的計時器（只在協商出 cmd1 的連線；連線丟掉就 Dispose）</summary>
        public ITimer? RefreshTimer { get; set; }
    }

    /// <summary>交給中樞、還沒有結果的指令</summary>
    private sealed class RunningCommand(string commandId, long auditSeq, string name, CommandLink origin)
    {
        public string CommandId { get; } = commandId;

        /// <summary>BeginCommand 寫的那一列紀錄（完成時更新它）</summary>
        public long AuditSeq { get; } = auditSeq;

        /// <summary>指令名稱（輸出的型錄白名單照它）</summary>
        public string Name { get; } = name;

        /// <summary>收到它的連線：輸出只送回這條</summary>
        public CommandLink Origin { get; } = origin;

        /// <summary>收到幾次（執行中重送的也算；每一次都回一則結果）</summary>
        public int Deliveries { get; set; } = 1;

        /// <summary>結果已經排進送出佇列：之後到的輸出丟掉</summary>
        public bool Finished { get; set; }
    }

    // ── 登記點（SyncEngine.Contracts.cs） ──

    /// <summary>
    /// Hello 的功能（D6、D11）：有 port、配對網址的 aud 合格、sync.db 有兩張指令的表、這個引擎實例的啟動回收做得了，才宣告 <c>cmd1</c>；
    /// 結果經 port 告訴協調者（不看政策：政策由閘門 5e 判斷）
    /// </summary>
    partial void ContributeCommandFeatures(List<string> into)
    {
        var port = CommandPort;
        if (port is null)
            return;
        var ready = PrepareCommands();
        if (current is { } conn)
            commandLink = new CommandLink(conn, ready);
        if (ready)
        {
            into.Add(DeviceFeatureRules.Cmd1);
            // 桌面遠端操作（sync-protocol 14.7）：接受另一台桌面發起的信封、也能對其他電腦下指令（SyncEngine.Peer.cs）
            into.Add(DeviceFeatureRules.Peer1);
            // 遠端執行指令（14.11）：宿主接得住串流才宣告（SyncEngine.Exec.cs）
            if (ExecPort is not null)
                into.Add(DeviceFeatureRules.Exec1);
            // 多人協作（14.14）：接受協作人發起的信封（閘門只收白名單）、管理協作人與分享（SyncEngine.Collaborators.cs）
            into.Add(DeviceFeatureRules.Collab1);
            // 電腦狀態面板的檔案傳輸（2026-11-09）：deviceBrowse、deviceFetch、devicePut（AgentHub.DeviceFiles.cs）
            into.Add(DeviceFeatureRules.Xfer1);
            into.Add(DeviceFeatureRules.Xfer2);
            // 遠端桌面（14.16）：宿主接得住才宣告（SyncEngine.Desk.cs）
            if (DeskPort is not null)
            {
                into.Add(DeviceFeatureRules.Desk1);
                // 遠端桌面輸入的端對端加密（DeskSealRules）：收帶 viewerKey 的 deskOpen
                into.Add(DeviceFeatureRules.DeskE2e1);
            }
        }
        try
        {
            port.ReportCommandsSupported(ready);
        }
        catch (Exception ex)
        {
            Log.Error("回報這台能不能執行遠端指令失敗", ex);
        }
    }

    /// <summary>遠端指令（D7、D8）：驗信封 → 去重並先記 executing → 經 port 進中樞閘門；結果與輸出依序送回</summary>
    partial void OnCommand(SyncConnection conn, CommandDelivery delivery)
    {
        if (commandLink is not { Declared: true } link || !ReferenceEquals(link.Conn, conn) || CommandPort is not { } port
            || store is null || device is null || trust is null)
        {
            // 只有測試以登記點硬加 cmd1 時會到這裡：這條連線不是這裡宣告的，不處理
            Log.Warn("收到遠端指令，但這條連線沒有宣告 cmd1：略過");
            return;
        }

        // 稽核的事實（AuditFacts）會遮蔽指令名稱、可能重建已釋放的 DLP 樣式：記一次活動，靜置期滿再釋放（WP4.9 合併前審查 THR-1）
        NoteDlpActivity();

        var d = device.Current;
        var aud = SyncCommandRules.Audience(d.ServerUrl) ?? string.Empty;
        var target = new CommandEnvelopeTarget(trust.PinnedHubSpki, aud, d.DeviceId, conn.EnvelopeConn ?? string.Empty,
            AcceptsDeviceIssuer: DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Peer1),
            AcceptsCollaboratorIssuer: DeviceFeatureRules.Has(conn.Features, DeviceFeatureRules.Collab1));
        var verdict = CommandEnvelope.Verify(delivery.Envelope, delivery.CommandId, target, SyncCommandRules.UsableBounds(link.Clock, CommandTimestamp()));
        if (!verdict.Ok)
        {
            RejectEnvelope(link, delivery, verdict, aud);
            return;
        }

        var body = verdict.Body!;
        var cid = body.Cid;
        var digest = verdict.ContentSha256!;
        CommandBegin begin;
        try
        {
            begin = store.BeginCommand(cid, digest, AuditFacts(delivery, verdict, null) with { CommandId = cid }, NowMs());
        }
        catch (SyncStoreException ex)
        {
            // 什麼都沒記也沒執行：回 busy（durable 由伺服器重簽再試）
            Log.Warn($"遠端指令 {cid} 寫不進遠端操作紀錄（{ex.Failure}），不執行：{ex.Message}");
            DeliverResult(new CommandResult(cid, RemoteResultStates.Rejected, RemoteResultCodes.Busy, "這台電腦暫時無法寫入遠端操作紀錄，指令沒有執行，請稍後再試",
                body.Tier, body.FullAccess));
            return;
        }

        completedResults.TryGetValue(cid, out var cached);
        var decision = SyncCommandRules.Decide(digest, begin.Row, runningCommands.ContainsKey(cid), cached);
        switch (decision.Kind)
        {
            case CommandSeenKind.New:
                StartCommand(link, port, body, begin.AuditSeq ?? throw new InvalidOperationException("新指令沒有紀錄的 seq"));
                break;
            case CommandSeenKind.Duplicate:
                Log.Info($"遠端指令 {cid} 重送：回上一次的結果、不重做");
                DeliverResult(decision.Previous! with { CommandId = cid });
                break;
            case CommandSeenKind.InFlight:
                Log.Info($"遠端指令 {cid} 執行中又收到一次：等原本那次的結果");
                runningCommands[cid].Deliveries++;
                break;
            case CommandSeenKind.Unknown:
                Log.Warn($"遠端指令 {cid} 重送，但上一次不知道有沒有執行完（當機或完成時寫不進去）：回 unknown、不重做");
                DeliverResult(SyncCommandRules.UnknownResult(cid));
                break;
            case CommandSeenKind.Conflict:
                Log.Warn($"遠端指令 {cid} 與先前同一個 id 的內容不同：不執行");
                DeliverResult(new CommandResult(cid, RemoteResultStates.Rejected, RemoteResultCodes.Conflict, "這個指令與先前以同一個 id 送出的內容不同，沒有執行",
                    body.Tier, body.FullAccess));
                break;
            default:
                throw new InvalidOperationException("不認得的去重判斷");
        }
    }

    /// <summary>交握完成（D10）：這條連線協商出 cmd1 才開時鐘的計時器、依原本順序補送上一條連線沒送出的結果（在暫存的第 4 階段訊息之前）</summary>
    partial void OnProvenForCommands(SyncConnection conn)
    {
        // 桌面遠端操作（SyncEngine.Peer.cs）：這條連線能不能對其他電腦下指令
        ReportPeerLive(conn);
        // 遠端執行指令（SyncEngine.Exec.cs）：補送上一條連線沒送出的結束碼
        OnExecProven(conn);
        // 遠端桌面（SyncEngine.Desk.cs）：這條連線能不能搬封包
        OnDeskProven(conn);
        if (commandLink is not { } link || !ReferenceEquals(link.Conn, conn) || !IsLive(link))
            return;
        ScheduleClockRefresh(link);
        if (pendingResults.Count == 0)
            return;
        Log.Info($"補送上一條連線沒送出的遠端指令結果 {pendingResults.Count} 則");
        link.Outgoing.AddRange(pendingResults);
        pendingResults.Clear();
        PumpCommands(link);
    }

    /// <summary>
    /// 連線丟掉了（D10）：時鐘與計時器不再用；還沒送出的結果（含正在送的那則）依序留到下一條協商出 cmd1 的連線，輸出丟掉。
    /// 已經進中樞的指令不取消，結果之後也進 pendingResults
    /// </summary>
    partial void OnConnectionClosedForCommands(SyncConnection conn)
    {
        ReportPeerLive(null);
        OnExecClosed(conn);
        OnDeskClosed(conn);
        if (commandLink is not { } link || !ReferenceEquals(link.Conn, conn))
            return;
        commandLink = null;
        link.Closed = true;
        link.Clock = null;
        link.ReHelloRequested = false;
        CancelClockRefresh(link);
        var kept = new List<CommandResult>();
        if (link.Sending is CommandResult sending)
            kept.Add(sending);
        kept.AddRange(link.Outgoing.OfType<CommandResult>());
        link.Sending = null;
        link.Outgoing.Clear();
        foreach (var result in kept)
            KeepPending(result);
    }

    // ── 對外（ISyncEngineCommands；任何執行緒，只把工作排進引擎執行緒） ──

    public void OnSystemResumed() => thread.Post(OnSystemResumedCore);

    public Task<IReadOnlyList<RemoteAuditEntry>> LoadRemoteAuditAsync(long? beforeAt, int limit, CancellationToken ct, long? beforeSeq = null) =>
        RunOnEngineAsync<IReadOnlyList<RemoteAuditEntry>>(() =>
        {
            Probe(nameof(LoadRemoteAuditAsync), limit);
            return store?.LoadRemoteAudit(beforeAt, limit, beforeSeq) ?? [];
        }, [], ct);

    public Task<int> ExportRemoteAuditAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return RunOnEngineAsync(() =>
        {
            Probe(nameof(ExportRemoteAuditAsync), path);
            return store?.ExportRemoteAudit(path) ?? 0;
        }, 0, ct);
    }

    /// <summary>測試用：目前這條連線的伺服器時鐘（引擎執行緒上讀；沒有連線或沒有時鐘是 null）</summary>
    internal Task<ServerClock?> CommandClockForTestAsync() => RunOnEngineAsync<ServerClock?>(() => commandLink?.Clock, null, CancellationToken.None);

    /// <summary>測試用：斷線時保留、還沒補送的結果數</summary>
    internal Task<int> PendingCommandResultsForTestAsync() => RunOnEngineAsync(() => pendingResults.Count, -1, CancellationToken.None);

    // ── Hello 的伺服器時鐘（SyncEngine.Session.cs 呼叫） ──

    /// <summary>記下這條連線 Hello 的伺服器時鐘（D4；伺服器身分驗過之後、跟著連線，連線斷了就不用）</summary>
    private void RecordHelloClock(SyncConnection conn, long serverTimeMs, long sent, long received)
    {
        if (commandLink is not { } link || !ReferenceEquals(link.Conn, conn))
            return;
        link.Clock = ServerClockRules.HelloClock(serverTimeMs, sent, received, time.TimestampFrequency);
        if (link.Clock is null && link.Declared)
            Log.Warn("Hello 的往返超過 30 秒：這條連線推算不出伺服器時間，除了 stop 的遠端指令都會回 expired");
    }

    // ── 內部 ──

    private long CommandTimestamp() => CommandTimestampForTest?.Invoke() ?? time.GetTimestamp();

    /// <summary>宣告 cmd1 之前的檢查與啟動回收（D6、D11）；sync.db 的失敗接住、記錄，這次不宣告</summary>
    private bool PrepareCommands()
    {
        if (store is null || device is null)
            return false;
        if (SyncCommandRules.Audience(device.Current.ServerUrl) is null)
        {
            if (!audienceWarned)
                Log.Warn("配對網址不是合格的伺服器來源（https／http、沒有路徑）：這台不執行遠端指令");
            audienceWarned = true;
            return false;
        }

        if (!store.CommandTablesReady)
            return false;
        try
        {
            if (!commandStartupDone)
            {
                var interrupted = store.MarkInterruptedUnknown();
                commandStartupDone = true;
                if (interrupted > 0)
                    Log.Warn($"上次執行中被中斷的遠端指令 {interrupted} 個改成 unknown（重送不重做）");
            }

            // 修剪：這個引擎實例第一次 Hello 一定做，之後距上次滿 24 小時才做（app 可以連開好幾週）
            var nowMs = NowMs();
            if (SyncCommandRules.PruneDue(lastPruneAtMs, nowMs))
            {
                var (seen, audit) = store.Prune(nowMs);
                lastPruneAtMs = nowMs;
                Probe("Prune", (seen, audit));
                if (seen > 0 || audit > 0)
                    Log.Info($"修剪遠端指令的紀錄：commands_seen {seen} 列、遠端操作紀錄 {audit} 列");
            }
        }
        catch (SyncStoreException ex)
        {
            Log.Warn($"遠端指令的啟動工作失敗（{ex.Failure}），這次不宣告 cmd1：{ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 驗信封失敗（D7）：只寫一列紀錄（不寫 commands_seen：同一個 cid 之後合格的信封照樣執行）；外層 commandId 是 ULID 才回結果；
    /// 時間判不過而且是 expired 的，另外安排重新 Hello
    /// </summary>
    private void RejectEnvelope(CommandLink link, CommandDelivery delivery, CommandEnvelopeVerdict verdict, string aud)
    {
        var detail = verdict.Detail;
        if (verdict.Step == CommandEnvelopeSteps.Audience && verdict.Body is { } parsed)
            detail += $"（信封 {CatalogSyncRules.CleanText(parsed.Aud, 200)}，這台 {aud}）";
        try
        {
            store!.AuditRejected(AuditFacts(delivery, verdict, detail), verdict.Step!, verdict.Code ?? RemoteResultCodes.Invalid, NowMs());
        }
        catch (SyncStoreException ex)
        {
            Log.Warn($"驗不過的遠端指令寫不進遠端操作紀錄（{ex.Failure}）：{ex.Message}");
        }

        Log.Warn($"遠端指令 {CatalogSyncRules.CleanText(delivery.CommandId, 64)} 的信封驗不過（{verdict.Step}）：{detail}");
        if (SyncCommandRules.VerifyFailureResult(delivery.CommandId, verdict) is { } result)
            DeliverResult(result);
        if (verdict.Step == CommandEnvelopeSteps.Time && verdict.Code == RemoteResultCodes.Expired)
            RequestClockReHello(link);
    }

    /// <summary>紀錄的事實（D12）：驗過與沒驗過的信封都用；command_id 是外層的 commandId（BeginCommand 會換成 cid）</summary>
    private static CommandAuditFacts AuditFacts(CommandDelivery delivery, CommandEnvelopeVerdict verdict, string? detail)
    {
        var body = verdict.Body;
        string? name = body is not null && RemoteCommandShapeRules.IsString(body.Cmd["cmd"], out var cmd)
            ? CatalogSyncRules.CleanText(CredentialLeakRules.Mask(cmd), 64)
            : null;
        return new CommandAuditFacts(
            CatalogSyncRules.CleanText(delivery.CommandId, 64),
            name,
            body?.Conv is { } conv && ConversationIds.IsWireId(conv) ? conv : null,
            body?.Tier,
            body?.Auth.DefaultPassword,
            body?.Iat,
            body?.Created,
            detail,
            SyncCommandRules.MaskBodyForAudit(body),
            SyncCommandRules.SignedSha256(delivery.Envelope),
            delivery.Envelope?.Sig);
    }

    /// <summary>新指令（已記 executing）：放進進行中表，經 port 進中樞閘門（不帶連線：例外不拖垮連線）</summary>
    private void StartCommand(CommandLink link, IRemoteCommandPort port, CommandEnvelopeBody body, long auditSeq)
    {
        var name = RemoteCommandShapeRules.IsString(body.Cmd["cmd"], out var cmd) ? cmd : string.Empty;
        var run = new RunningCommand(body.Cid, auditSeq, name, link);
        runningCommands[body.Cid] = run;
        Action<JObject> output = message => thread.Post(() => OnCommandOutput(run, message));
        Spawn(async () =>
        {
            RemoteCommandRun? outcome = null;
            Exception? error = null;
            try
            {
                outcome = await port.ExecuteRemoteAsync(body, output, lifetime.Token);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            FinishCommand(run, outcome, error);
        });
    }

    /// <summary>
    /// 中樞的結果回來了（引擎執行緒）：寫回 commands_seen 與紀錄（失敗只記錄）、放進已完成結果快取（busy 不放：什麼都沒執行，要能再試）、
    /// 每收到一次就回一則結果。引擎正在停止時不寫也不送（D36：下次啟動改成 unknown）
    /// </summary>
    private void FinishCommand(RunningCommand run, RemoteCommandRun? outcome, Exception? error)
    {
        runningCommands.Remove(run.CommandId);
        run.Finished = true;
        if (stopping || store is null)
        {
            Log.Info($"同步引擎停止中：遠端指令 {run.CommandId} 的結果不寫也不送（下次啟動改成 unknown）");
            return;
        }

        // 執行期間背景的檔案 DLP（fileFetch 的 MediaServerRules.HasLeak）可能重建了樣式：從結果回來的這一刻再算一次靜置期（THR-1）
        NoteDlpActivity();

        CommandResult result;
        string? step;
        if (outcome?.Outcome is not { } gate)
        {
            Log.Error($"遠端指令 {run.CommandId} 在中樞執行失敗：回 unknown", error);
            result = SyncCommandRules.UnknownResult(run.CommandId);
            step = RemoteGateSteps.Handler;
        }
        else
        {
            result = gate.ToResult(run.CommandId);
            step = gate.Step;
            if (RemoteResultRules.Check(result) is { } problem)
            {
                Log.Error($"中樞回的遠端指令結果不合格（{problem}）：回 unknown");
                result = SyncCommandRules.UnknownResult(run.CommandId);
                step = RemoteGateSteps.Handler;
            }
        }

        try
        {
            store.CompleteCommand(run.CommandId, run.AuditSeq, result, step, outcome?.Policy, outcome?.Paused);
            Probe("CompleteCommand", result);
        }
        catch (SyncStoreException ex)
        {
            Log.Warn($"遠端指令 {run.CommandId} 的結果寫不進 sync.db（{ex.Failure}），重送時回快取的結果：{ex.Message}");
        }

        if (SyncCommandRules.KeepsSeenRow(result))
            RememberResult(result);
        for (var i = 0; i < run.Deliveries; i++)
            DeliverResult(result);
    }

    private void RememberResult(CommandResult result)
    {
        if (completedResults.ContainsKey(result.CommandId))
        {
            completedResults[result.CommandId] = result;
            return;
        }

        while (completedResults.Count >= SyncCommandRules.MaxCompletedCache && completedOrder.TryDequeue(out var oldest))
            completedResults.Remove(oldest);
        completedResults[result.CommandId] = result;
        completedOrder.Enqueue(result.CommandId);
    }

    /// <summary>中樞回給回應檢視的網頁訊息（UI 執行緒排過來的）：只送回收到指令的那條連線，結果排進佇列之後的丟掉</summary>
    private void OnCommandOutput(RunningCommand run, JObject message)
    {
        if (run.Finished || !IsLive(run.Origin))
            return;
        var output = new CommandOutput(run.CommandId, message);
        if (RemoteResultRules.CheckOutput(output, run.Name) is { } problem)
        {
            Log.Warn($"遠端指令 {run.CommandId} 的輸出不合格（{problem}）：丟掉");
            return;
        }

        run.Origin.Outgoing.Add(output);
        PumpCommands(run.Origin);
    }

    /// <summary>結果排進目前這條連線的送出佇列（輸出之後）；沒有協商出 cmd1 的連線就留到下一條（D10）</summary>
    private void DeliverResult(CommandResult result)
    {
        if (commandLink is { } link && IsLive(link))
        {
            link.Outgoing.Add(result);
            PumpCommands(link);
            return;
        }

        Log.Info($"遠端指令 {result.CommandId} 的結果沒有協商出 cmd1 的連線可以送：留到下一條連線");
        KeepPending(result);
    }

    /// <summary>留到下一條協商出 cmd1 的連線；超過上限丟最舊的</summary>
    private void KeepPending(CommandResult result)
    {
        if (pendingResults.Count >= SyncCommandRules.MaxPendingResults)
        {
            Log.Warn($"斷線時保留的遠端指令結果超過 {SyncCommandRules.MaxPendingResults} 則：丟掉最舊的（{pendingResults[0].CommandId}）");
            pendingResults.RemoveAt(0);
        }

        pendingResults.Add(result);
    }

    /// <summary>這條連線還是目前的、交握完成、協商出 cmd1（而且是這裡宣告的）</summary>
    private bool IsLive(CommandLink link) =>
        ReferenceEquals(commandLink, link) && link.Declared && IsCurrent(link.Conn) && link.Conn.Proven
        && DeviceFeatureRules.Has(link.Conn.Features, DeviceFeatureRules.Cmd1);

    /// <summary>
    /// 依序送出（D10）：一次一則，送完才送下一則；佇列送完再看要不要重新 Hello。送出的錯誤照 ReplyBusy：ClientBug 只記錄並丟掉那則；
    /// 其他錯誤把結果放回佇列最前面再斷線（斷線時隨佇列留到下一條連線；輸出不放回）
    /// </summary>
    private void PumpCommands(CommandLink link)
    {
        if (link.Sending is not null || link.Outgoing.Count == 0 || !IsLive(link))
            return;
        var next = link.Outgoing[0];
        link.Outgoing.RemoveAt(0);
        link.Sending = next;
        var conn = link.Conn;
        Spawn(async () =>
        {
            try
            {
                switch (next)
                {
                    case CommandResult result:
                        await conn.Session!.CommandResultAsync(result, conn.Token);
                        break;
                    case CommandOutput output:
                        await conn.Session!.CommandOutputAsync(output, conn.Token);
                        break;
                    default:
                        throw new InvalidOperationException("不認得的指令送出項目");
                }
            }
            catch (SyncTransportException ex) when (ex.Failure.Kind == SyncFailureKind.ClientBug)
            {
                Log.Warn($"伺服器拒收遠端指令的{(next is CommandResult ? "結果" : "輸出")}（{ex.Failure.Code}）：只記錄、丟掉這則、不重連");
            }
            catch (SyncTransportException ex)
            {
                if (!link.Closed)
                {
                    link.Sending = null;
                    if (next is CommandResult)
                        link.Outgoing.Insert(0, next);
                    Fail(conn, ex.Failure);
                }

                return;
            }
            catch (OperationCanceledException)
            {
                // 連線被取消（斷線、停止）：斷線時已經把這則留下；停止時記憶體裡的結果本來就丟（下次啟動是 unknown）
                if (!link.Closed)
                {
                    link.Sending = null;
                    if (next is CommandResult)
                        link.Outgoing.Insert(0, next);
                }

                return;
            }

            if (link.Closed)
                return;
            link.Sending = null;
            PumpCommands(link);
            if (link.ReHelloRequested && link.Sending is null && link.Outgoing.Count == 0)
                thread.Post(() => ReHelloForClock(link));
        });
    }

    /// <summary>時間判不過（D4）：安排重新 Hello 換新的伺服器時鐘，每分鐘最多一次；這條連線的佇列送完才斷</summary>
    private void RequestClockReHello(CommandLink link)
    {
        var now = CommandTimestamp();
        if (!SyncCommandRules.ReHelloAllowed(lastClockReHelloTimestamp, now, time.TimestampFrequency))
        {
            Log.Info("遠端指令的時間判不過，但一分鐘內已經重新 Hello 過：這次不重連");
            return;
        }

        lastClockReHelloTimestamp = now;
        link.ReHelloRequested = true;
        if (link.Sending is null && link.Outgoing.Count == 0)
            thread.Post(() => ReHelloForClock(link));
    }

    /// <summary>斷線重連（照 OnUpdatePolicy 的 Drop＋Connect）：新的 Hello 記新的伺服器時鐘</summary>
    private void ReHelloForClock(CommandLink link)
    {
        if (!link.ReHelloRequested || !ReferenceEquals(commandLink, link) || !IsCurrent(link.Conn) || store is null)
            return;
        link.ReHelloRequested = false;
        Log.Info("重新 Hello，換新的伺服器時鐘（遠端指令的時間判不過）");
        Drop(link.Conn);
        Connect();
    }

    /// <summary>
    /// 時鐘滿 6 小時重新 Hello 的計時器（D4；只在協商出 cmd1 的連線）。<paramref name="delay"/> 是 null 時排在時鐘滿 6 小時的時候；
    /// 至少 1 分鐘（時鐘一開始就太舊時不會一直重連）
    /// </summary>
    private void ScheduleClockRefresh(CommandLink link, TimeSpan? delay = null)
    {
        CancelClockRefresh(link);
        if (link.Clock is null)
            return;
        var remaining = SyncCommandRules.ClockRefreshMs - (SyncCommandRules.ClockAgeMs(link.Clock, CommandTimestamp()) ?? SyncCommandRules.ClockRefreshMs);
        var due = delay ?? TimeSpan.FromMilliseconds(Math.Max(SyncCommandRules.ReHelloMinIntervalMs, remaining));
        link.RefreshTimer = CreateTimer(due, () => OnClockRefreshDue(link));
    }

    private static void CancelClockRefresh(CommandLink link)
    {
        link.RefreshTimer?.Dispose();
        link.RefreshTimer = null;
    }

    /// <summary>
    /// 引擎停止（SyncEngine.cs 的 CancelTimers：StopAsync／Cancel，stopping 已是 true）：停掉時鐘的計時器、放掉這條連線的指令狀態。
    /// 停止不經 OnConnectionClosedForCommands（引擎結束時不呼叫），計時器的回呼抓著整個舊引擎，6 小時才觸發（合併前審查 T1）。
    /// Closed＝true：送出途中的接續不會把結果插回佇列（停止時記憶體裡的結果本來就丟，D36）
    /// </summary>
    private void CancelCommandTimers()
    {
        if (commandLink is { } link)
        {
            link.Closed = true;
            link.ReHelloRequested = false;
            CancelClockRefresh(link);
        }

        commandLink = null;
    }

    /// <summary>計時器到了：時鐘滿 6 小時而且沒有執行中的指令就重新 Hello；有執行中的指令每分鐘再看一次</summary>
    private void OnClockRefreshDue(CommandLink link)
    {
        if (link.Closed || !ReferenceEquals(commandLink, link) || !IsLive(link) || store is null)
            return;
        CancelClockRefresh(link);
        if (SyncCommandRules.ShouldRefreshClock(link.Clock, CommandTimestamp(), runningCommands.Count))
        {
            Log.Info("伺服器時鐘滿 6 小時：重新 Hello");
            Drop(link.Conn);
            Connect();
            return;
        }

        ScheduleClockRefresh(link, runningCommands.Count > 0 ? TimeSpan.FromMilliseconds(SyncCommandRules.ReHelloMinIntervalMs) : null);
    }

    /// <summary>
    /// 從睡眠或休眠恢復（D32；引擎執行緒）：單調時鐘在睡眠時可能停止，所有連線的伺服器時鐘作廢；退避中照 RetryNow（保留 notBefore），
    /// 連著（含連線中、交握中）就斷線重連。暫停、停止、待確認時只清時鐘
    /// </summary>
    private void OnSystemResumedCore()
    {
        if (commandLink is { } link)
        {
            link.Clock = null;
            CancelClockRefresh(link);
        }

        if (!opened || store is null || stopping)
            return;
        if (state == SyncEngineState.Backoff)
        {
            Log.Info("系統從睡眠恢復：立刻重試連線");
            OnRetryNow();
            return;
        }

        if (current is { } conn)
        {
            Log.Info("系統從睡眠恢復：重新 Hello（伺服器時鐘作廢）");
            Drop(conn);
            Connect();
        }
    }

    /// <summary>
    /// 在引擎執行緒執行並回結果（ISyncEngineCommands 的讀取與匯出）；引擎執行緒已經結束時回 <paramref name="whenStopped"/>，
    /// <paramref name="ct"/> 取消時立即結束（TaskCanceledException）。等 <c>Exited</c> 用 <see cref="Task.WhenAny(Task, Task)"/>：
    /// 一邊完成時它把自己從另一邊的接續拿掉，引擎連開幾週、呼叫幾千次也不會在 <c>Exited</c> 上留下抓著每次結果的接續（合併前審查 T2）。
    /// 引擎沒有啟動過時排進去的工作不會執行，靠 <c>Exited</c> 回 <paramref name="whenStopped"/>
    /// </summary>
    private async Task<T> RunOnEngineAsync<T>(Func<T> work, T whenStopped, CancellationToken ct)
    {
        if (thread.Exited.IsCompleted)
            return whenStopped;
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = ct.Register(() => done.TrySetCanceled(ct));
        thread.Post(() =>
        {
            try
            {
                done.TrySetResult(work());
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
        });
        await Task.WhenAny(done.Task, thread.Exited).ConfigureAwait(false);
        // 工作做完之後引擎才結束：兩邊的接續都排在執行緒集區，先看工作的結果（同原本的 TrySetResult 先到為準）
        return done.Task.IsCompleted ? await done.Task.ConfigureAwait(false) : whenStopped;
    }
}
