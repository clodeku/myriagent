using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

/// <summary>
/// 中樞把這台對其他電腦下的指令交給同步（協調者實作；UI 執行緒呼叫）。<see cref="DeviceId"/> 是這台的 deviceId（未配對是 null），
/// 路由 Device 的指令帶的 <c>deviceId</c> 是它時在本機做、不轉送
/// </summary>
internal interface IPeerCommandSink
{
    string? DeviceId { get; }

    /// <summary>送出（結果與輸出之後經 <see cref="AgentHub.OnPeerReply"/> 回來；沒有連線時也一定會回一則 <c>commandResult</c>）</summary>
    void SubmitPeerCommand(string commandId, JObject cmd);
}

// 桌面遠端操作（sync-protocol 14.7；擁有者 2026-10-22 決定「已配對就能操作」）：網頁對副本（其他電腦的對話）送型錄裡路由 Owner 的指令、
// 或對其他電腦送路由 Device 的帳戶與 CLI 指令，而且帶了 commandId 時，不再一律唯讀擋掉，改交給同步（IPeerCommandSink）經伺服器送給那台。
// 結果與輸出（PeerReply）只轉給發起的那個檢視、只收型錄的白名單；等結果 160 秒、結果到了之後再收 30 秒的輸出。
// 被操作的電腦照舊驗信封、本機政策與中樞閘門（遠端操作紀錄與通知的發起者是「電腦「這台的名稱」」）。只在 UI 執行緒
internal sealed partial class AgentHub
{
    private IPeerCommandSink? peerSink;

    private bool peerLive;

    /// <summary>等結果的指令（commandId → 發起的檢視等；依加入的先後）</summary>
    private readonly Dictionary<string, PeerPending> peerPending = new(StringComparer.Ordinal);

    private sealed class PeerPending(string viewId, string cmd, long at)
    {
        public string ViewId { get; } = viewId;

        public string Cmd { get; } = cmd;

        public long At { get; } = at;

        /// <summary>遠端草稿的第一次 send（conversationCreate）：草稿的 id（AgentHub.PeerDrafts.cs）</summary>
        public string? DraftId { get; init; }

        /// <summary>結果到了的時間（之後 <see cref="PeerHubRules.OutputGraceMs"/> 內還收輸出）</summary>
        public long? CompletedAt { get; set; }

        /// <summary>
        /// 不是網頁發起、是中樞自己等結果的（myriagent-remote 的 send、push、pull；<see cref="PeerExchangeAsync"/>）：每則回覆都交給它、不轉給任何檢視；
        /// 被清掉（逾時、解除配對）時給 null
        /// </summary>
        public Action<JObject?>? Listener { get; init; }
    }

    /// <summary>協調者在建立引擎時掛上、解除配對時拿掉（UI 執行緒）；拿掉時等結果的一律忘掉（網頁 150 秒後當成結果不明）</summary>
    internal void AttachPeerSink(IPeerCommandSink? sink)
    {
        peerSink = sink;
        if (sink is null)
        {
            foreach (var p in peerPending.Values.ToList())
                p.Listener?.Invoke(null);
            peerPending.Clear();
            peerLive = false;
        }
    }

    /// <summary>目前能不能對其他電腦下指令（協調者依引擎的回報設定；網頁看 <c>remote.sync.peer</c>）</summary>
    internal bool PeerLive
    {
        get => peerLive;
        set => peerLive = value;
    }

    /// <summary>
    /// HandleCommand 開頭（擋副本之前）呼叫：要交給同步的指令（<see cref="PeerHubRules.Forwardable"/>）送出並記下發起的檢視，回 true；其他回 false
    /// </summary>
    private bool TryForwardPeerCommand(IAgentView view, JObject command)
    {
        var conversationId = command.Value<string>("conversationId");
        var isReplica = conversationId != null && conversations.TryGetValue(conversationId, out var c) && c.IsReplica;
        if (PeerHubRules.Forwardable(command, isReplica, peerSink?.DeviceId) is not { } spec)
            return false;
        var commandId = command.Value<string>("commandId")!;
        if (peerSink is not { } sink)
        {
            // 沒有配對（或正在解除）：當成沒送出，網頁照傳輸層的拒絕處理（不另外提示）
            Post(view, PeerCommandRules.Synthesized(commandId, PeerCommandRules.NotSent).Message);
            return true;
        }

        PrunePeerPending();
        peerPending[commandId] = new PeerPending(view.Id, spec.Name, Environment.TickCount64);
        try
        {
            sink.SubmitPeerCommand(commandId, PeerHubRules.PeerBody(command));
            if (spec.Name == RemoteCommandCatalog.DeskOpen)
                NoteDeskOpen(view, command);
        }
        catch (Exception ex)
        {
            Log.Error($"桌面遠端操作的指令 {spec.Name} 交給同步失敗", ex);
            peerPending.Remove(commandId);
            Post(view, PeerCommandRules.Synthesized(commandId, PeerCommandRules.NotSent).Message);
        }

        return true;
    }

    /// <summary>
    /// 同步送回的結果與輸出（UI 執行緒）：還在等的 commandId、型錄白名單裡的種類，才轉給發起的檢視（檢視關了就丟掉）。
    /// <c>commandResult</c> 到了之後再收 <see cref="PeerHubRules.OutputGraceMs"/> 的輸出
    /// </summary>
    internal void OnPeerReply(string commandId, JObject message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (disposed)
            return;
        PrunePeerPending();
        if (!peerPending.TryGetValue(commandId, out var pending))
            return;
        if (!PeerHubRules.IsAllowedReply(pending.Cmd, message))
        {
            Log.Warn($"桌面遠端操作 {pending.Cmd} 收到不在白名單的回覆 {message.Value<string>("type")}：丟掉");
            return;
        }
        if (message.Value<string>("type") == "commandResult")
            pending.CompletedAt ??= Environment.TickCount64;
        if (pending.Listener is { } listener)
        {
            // 下載參照（fileRef、fileRefs）的網址照樣登記成可以轉送的鍵
            AllowPeerReplyUrls(message);
            listener(message);
            return;
        }
        // 遠端草稿（AgentHub.PeerDrafts.cs）：peerCreated 不轉給網頁；被拒時還原草稿的文字
        if (HandlePeerDraftReply(pending, message))
            return;
        // 檔案抽屜的縮圖與原檔、fileFetch 的檔案：伺服器給的 /m/、/t/、/f/ 登記成可以轉送的鍵（AgentWebView 只轉送登記過的）
        AllowPeerReplyUrls(message);
        if (views.TryGetValue(pending.ViewId, out var view))
            Post(view, message);
        else
            peerPending.Remove(commandId);
    }

    /// <summary>
    /// 中樞自己送一個跨電腦的指令並等回覆（myriagent-remote 的 send、push、pull；任何執行緒都能呼叫，登記與送出在 UI 執行緒）：
    /// 回覆一則一則收進清單，<paramref name="isLast"/> 說是最後一則時回整份；沒有配對、送不出去、被清掉或超過 <paramref name="limit"/>（不超過 160 秒）回 null
    /// </summary>
    internal async Task<List<JObject>?> PeerExchangeAsync(JObject cmd, Func<JObject, bool> isLast, TimeSpan limit)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(isLast);
        var got = new List<JObject>();
        var done = new TaskCompletionSource<List<JObject>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandId = SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var name = cmd.Value<string>("cmd") ?? string.Empty;
        var submitted = await UiAsync(() =>
        {
            if (disposed || peerSink is not { } sink)
                return false;
            PrunePeerPending();
            peerPending[commandId] = new PeerPending(string.Empty, name, Environment.TickCount64)
            {
                Listener = message =>
                {
                    if (message is null)
                    {
                        done.TrySetResult(null);
                        return;
                    }

                    got.Add(message);
                    if (isLast(message))
                    {
                        peerPending.Remove(commandId);
                        done.TrySetResult(got);
                    }
                },
            };
            try
            {
                sink.SubmitPeerCommand(commandId, cmd);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"中樞自己送的跨電腦指令 {name} 交給同步失敗", ex);
                peerPending.Remove(commandId);
                return false;
            }
        });
        if (!submitted)
            return null;
        var first = await Task.WhenAny(done.Task, Task.Delay(limit));
        host.BeginInvoke(() => peerPending.Remove(commandId));
        return first == done.Task ? done.Task.Result : null;
    }

    /// <summary>在 UI 執行緒執行並取結果（任何執行緒都能呼叫）</summary>
    private Task<T> UiAsync<T>(Func<T> func)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.BeginInvoke(() =>
        {
            try
            {
                result.TrySetResult(func());
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        });
        return result.Task;
    }

    /// <summary><c>files</c> 每一項的 <c>url</c>、<c>thumbUrl</c>，<c>fileRef</c> 的 <c>url</c> 與 <c>fileRefs</c> 每一項的 <c>url</c></summary>
    private void AllowPeerReplyUrls(JObject message)
    {
        switch (message.Value<string>("type"))
        {
            case "files" when message["entries"] is JArray entries:
                foreach (var entry in entries.OfType<JObject>())
                {
                    ReplicaMediaUrls.AllowRelativeUrl(entry.Value<string>("url"));
                    ReplicaMediaUrls.AllowRelativeUrl(entry.Value<string>("thumbUrl"));
                }
                break;
            case "fileRef":
                ReplicaMediaUrls.AllowRelativeUrl(message.Value<string>("url"));
                break;
            case "fileRefs" when message["refs"] is JArray refs:
                foreach (var r in refs.OfType<JObject>())
                    ReplicaMediaUrls.AllowRelativeUrl(r.Value<string>("url"));
                break;
        }
    }

    /// <summary>忘掉等太久的、結果到了超過 30 秒的；太多時先忘掉最舊的</summary>
    private void PrunePeerPending()
    {
        if (peerPending.Count == 0)
            return;
        var now = Environment.TickCount64;
        foreach (var (id, p) in peerPending.ToList())
        {
            if (now - p.At > PeerHubRules.PendingMs || p.CompletedAt is { } done && now - done > PeerHubRules.OutputGraceMs)
            {
                peerPending.Remove(id);
                p.Listener?.Invoke(null);
            }
        }

        while (peerPending.Count >= PeerHubRules.MaxPending)
        {
            var oldest = peerPending.MinBy(p => p.Value.At);
            peerPending.Remove(oldest.Key);
            oldest.Value.Listener?.Invoke(null);
        }
    }
}
