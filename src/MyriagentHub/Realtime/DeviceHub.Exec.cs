using MyriagentHub.Sync;

namespace MyriagentHub.Realtime;

/// <summary>
/// 裝置 hub 的遠端執行指令串流（docs/sync-protocol.md 14.11，<c>exec1</c>；契約 D50：服務以方法參數注入，這個 partial 檔只用 <c>Context</c> 與注入的服務）。
/// 型別寫完整名稱：方法和型別同名。只收已證明、協商出 <c>exec1</c> 的連線（<see cref="ExecRelays"/> 檢查）
/// </summary>
internal sealed partial class DeviceHub
{
    /// <summary>裝置 → 伺服器（Invoke，<c>exec1</c>）：被操作的電腦送輸出、狀態與結束碼；伺服器轉給發起的電腦。<c>keep</c> 是 false 時那台要停止這個指令</summary>
    public Task<Myriagent.Sync.ExecOutputResult> ExecOutput(Myriagent.Sync.ExecChunk? chunk, ConnectionRegistry registry, ExecRelays relays) =>
        relays.ReceiveOutputAsync(registry.Get(Context.ConnectionId), chunk, Context.ConnectionAborted);

    /// <summary>裝置 → 伺服器（Invoke，<c>exec1</c>）：發起的電腦要停止它下的遠端執行指令；伺服器轉成 <c>ExecAbort</c> 給被操作的電腦</summary>
    public Task<Myriagent.Sync.ExecCancelResult> ExecCancel(Myriagent.Sync.ExecCancel? request, ConnectionRegistry registry, ExecRelays relays) =>
        relays.CancelAsync(registry.Get(Context.ConnectionId), request, Context.ConnectionAborted);
}
