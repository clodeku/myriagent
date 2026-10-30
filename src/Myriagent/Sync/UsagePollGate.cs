using Myriagent.Accounts;

namespace Myriagent.Sync;

/// <summary>
/// 用量查詢閘門（AccountManager.UsagePollPolicy）：引擎以 <see cref="Publish"/>（任何執行緒）換上新的快照，
/// UI 執行緒以 Volatile 讀、照 <see cref="UsageSyncRules.ShouldPoll"/> 判斷。還沒有快照（未配對、引擎還沒發布）時一律查（離線優先）
/// </summary>
internal sealed class UsagePollGate : IUsagePollPolicy
{
    private readonly TimeProvider time;
    private UsagePollSnapshot? snapshot;

    public UsagePollGate(TimeProvider time) => this.time = time;

    public UsagePollSnapshot? Snapshot => Volatile.Read(ref snapshot);

    /// <summary>換上新的快照（null＝回到未配對：一律查）</summary>
    public void Publish(UsagePollSnapshot? next) => Volatile.Write(ref snapshot, next);

    public bool ShouldPoll(AgentAccount account) => UsageSyncRules.ShouldPoll(Snapshot, account.Id, time.GetUtcNow());
}
