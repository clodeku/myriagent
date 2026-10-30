using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

// 協調者的 token 計數（sync-protocol 14.9，tok1）：中樞的計數變了（ISyncBridge.OnTokensChanged）就通知引擎，
// 引擎經同一代的 EnginePort（ITokenTallyPort）取計數的複本（中樞的 TokenTallyStore 有自己的鎖，任何執行緒都能讀）。
// 其他電腦的計數（14.10，tok2）：引擎經 EnginePort（ITokenTallyViewPort）交回伺服器送的其他電腦合計，回 UI 執行緒存下（舊一代的丟掉）、
// 讓中樞重送 tokens；中樞組 tokens 時經 DecorateTokens 加上去（TokenViewRules）。解除配對時清掉
internal sealed partial class SyncCoordinator
{
    /// <summary>其他電腦的格（UI 執行緒；還沒收到過是 null：帳戶卡只算這台）。連線丟掉時不清（計數只會變大，最後收到的仍是對的下限）</summary>
    private IReadOnlyList<TokenTallyRow>? otherTokens;

    void ISyncBridge.OnTokensChanged() => (engine as ISyncEngineTokens)?.TokensChanged();

    void ISyncBridge.DecorateTokens(JObject tokens, DateTime now)
    {
        if (engine is null || catalogView is not { } view || otherTokens is not { } rows)
            return;
        var remoteOnly = RemoteOnlyEnabled() ? RemoteOnlyAccounts(view).Select(a => a.LogicalAccountId) : [];
        TokenViewRules.Decorate(tokens, rows, view.LocalToLogical, remoteOnly, DateOnly.FromDateTime(now));
    }

    /// <summary>引擎送回的其他電腦的計數（任何執行緒）：回 UI 執行緒存下、讓中樞重送 tokens（舊一代、已釋放的丟掉）</summary>
    private void OnOtherTokens(int gen, IReadOnlyList<TokenTallyRow> rows) => host.BeginInvoke(() =>
    {
        if (disposed || gen != Volatile.Read(ref generation))
            return;
        otherTokens = rows;
        hub.RefreshTokens();
    });

    /// <summary>解除配對（UI 執行緒）：只剩這台的計數</summary>
    private void ClearOtherTokens()
    {
        otherTokens = null;
        hub.RefreshTokens();
    }

    private sealed partial class EnginePort : ITokenTallyPort, ITokenTallyViewPort
    {
        public IReadOnlyList<LocalTokenTally> CurrentTokens() => owner.hub.TokenTallySnapshot();

        public void DeliverOthers(IReadOnlyList<TokenTallyRow> rows) => owner.OnOtherTokens(gen, rows);
    }
}
