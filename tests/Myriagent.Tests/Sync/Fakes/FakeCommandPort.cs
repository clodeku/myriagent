using System.Collections.Concurrent;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Fakes;

/// <summary>呼叫 <see cref="FakeCommandPort.ExecuteRemoteAsync"/> 的一次（呼叫端的執行緒）</summary>
internal sealed record PortCall(CommandEnvelopeBody Body, int Thread);

/// <summary>腳本實際執行的一次（中樞閘門的位置：在不在測試的 UI 執行緒）</summary>
internal sealed record PortRun(string CommandId, bool OnUi, int Thread);

/// <summary>
/// 測試用的宿主（WP4.4 計畫 D35）：包一個 <see cref="FakeHostPort"/>，同時實作 <see cref="ISyncHostPort"/> 與 <see cref="IRemoteCommandPort"/>。
/// <see cref="ExecuteRemoteAsync"/> 照協調者的語意（D3）：排到測試的 UI 執行緒，在那裡以 <see cref="Script"/> 決定閘門的結果；
/// 還沒開始就取消回 rejected／busy、Started=false。<see cref="FakeHostPort"/> 本身不實作 port，既有測試的 Hello 仍然沒有 cmd1
/// </summary>
internal sealed class FakeCommandPort(FakeHostPort inner) : ISyncHostPort, IRemoteCommandPort, IPeerCommandPort, IRemoteExecPort
{
    /// <summary>遠端執行指令（14.11）：接得住串流（預設 false：不宣告 exec1，既有測試的 Hello 不變）</summary>
    public bool ExecSupported { get; set; }

    /// <summary>遠端執行指令：引擎交回的 ExecReply（依序）</summary>
    public ConcurrentQueue<ExecChunk> ExecReplies { get; } = new();

    /// <summary>遠端執行指令：引擎要求停止的（cid, orphaned）</summary>
    public ConcurrentQueue<(string CommandId, bool Orphaned)> ExecAborts { get; } = new();

    /// <summary>遠端執行指令：引擎回報的「能不能搬」</summary>
    public ConcurrentQueue<bool> ExecLive { get; } = new();

    public void DeliverExecReply(ExecChunk chunk) => ExecReplies.Enqueue(chunk);

    public void AbortExec(string commandId, bool orphaned) => ExecAborts.Enqueue((commandId, orphaned));

    public void ReportExecLive(bool live) => ExecLive.Enqueue(live);

    public FakeHostPort Inner { get; } = inner;

    /// <summary>桌面遠端操作（14.7）：引擎交回的結果與輸出（依序）</summary>
    public ConcurrentQueue<PeerReply> PeerReplies { get; } = new();

    /// <summary>桌面遠端操作：引擎回報的「能不能送」（依序，值變了才回報）</summary>
    public ConcurrentQueue<bool> PeerLive { get; } = new();

    public void DeliverPeerReply(PeerReply reply) => PeerReplies.Enqueue(reply);

    public void ReportPeerLive(bool live) => PeerLive.Enqueue(live);

    /// <summary>閘門的腳本（在 UI 執行緒上呼叫）：可以回還沒完成的工作讓指令卡住；null＝一律 done</summary>
    public Func<CommandEnvelopeBody, Action<JObject>, Task<RemoteOutcome>>? Script { get; set; }

    /// <summary>回 true 的指令不進閘門，直接回 Started=false 的 busy（模擬協調者換了世代、還沒開始就取消）</summary>
    public Func<CommandEnvelopeBody, bool>? NotStarted { get; set; }

    /// <summary>進閘門時讀到的政策（只進紀錄）</summary>
    public string Policy { get; set; } = "Control";

    public bool Paused { get; set; }

    public ConcurrentQueue<PortCall> Calls { get; } = new();

    public ConcurrentQueue<PortRun> Runs { get; } = new();

    /// <summary>每次 Hello 回報的「這台能不能執行遠端指令」</summary>
    public ConcurrentQueue<bool> Reports { get; } = new();

    /// <summary>實際進了閘門的 cid（依序）</summary>
    public IReadOnlyList<string> Executed => Runs.Select(r => r.CommandId).ToList();

    public long UiAliveAt => Inner.UiAliveAt;

    public static RemoteOutcome Done(CommandEnvelopeBody body) => new(RemoteResultStates.Done, null, null, body.Tier, body.FullAccess, null);

    public static RemoteOutcome Rejected(CommandEnvelopeBody body, string code, string? message = null, string step = RemoteGateSteps.Handler) =>
        new(RemoteResultStates.Rejected, code, message, body.Tier, body.FullAccess, step);

    public static RemoteOutcome Unknown(CommandEnvelopeBody body) => new(RemoteResultStates.Unknown, null, null, body.Tier, body.FullAccess, RemoteGateSteps.Handler);

    public Task<RemoteCommandRun> ExecuteRemoteAsync(CommandEnvelopeBody body, Action<JObject> output, CancellationToken ct)
    {
        Calls.Enqueue(new PortCall(body, Environment.CurrentManagedThreadId));
        var done = new TaskCompletionSource<RemoteCommandRun>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (NotStarted?.Invoke(body) == true)
        {
            done.SetResult(NotStartedRun(body));
            return done.Task;
        }

        // 0＝還沒開始、1＝開始了、-1＝取消（先到的為準）
        var state = 0;
        var registration = ct.Register(() =>
        {
            if (Interlocked.CompareExchange(ref state, -1, 0) == 0)
                done.TrySetResult(NotStartedRun(body));
        });
        Inner.Ui.Post(() =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                return;
            registration.Dispose();
            _ = RunAsync(body, output, done);
        });
        return done.Task;
    }

    public void ReportCommandsSupported(bool supported) => Reports.Enqueue(supported);

    public Task RunOnUiAsync(Action action, CancellationToken ct) => Inner.RunOnUiAsync(action, ct);

    public void PublishStatus(SyncStatusSnapshot status) => Inner.PublishStatus(status);

    public void PublishPollSnapshot(UsagePollSnapshot snapshot) => Inner.PublishPollSnapshot(snapshot);

    public void PingUi() => Inner.PingUi();

    private RemoteCommandRun NotStartedRun(CommandEnvelopeBody body) =>
        new(Rejected(body, RemoteResultCodes.Busy, "電腦正在關閉同步，指令沒有執行"), Policy, Paused, false);

    private async Task RunAsync(CommandEnvelopeBody body, Action<JObject> output, TaskCompletionSource<RemoteCommandRun> done)
    {
        Runs.Enqueue(new PortRun(body.Cid, Inner.Ui.IsCurrent, Environment.CurrentManagedThreadId));
        try
        {
            var outcome = await (Script?.Invoke(body, output) ?? Task.FromResult(Done(body)));
            done.TrySetResult(new RemoteCommandRun(outcome, Policy, Paused, true));
        }
        catch (Exception ex)
        {
            done.TrySetException(ex);
        }
    }
}
