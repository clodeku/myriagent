using System.Diagnostics;
using System.Globalization;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace FakeDevice;

/// <summary>chat 操作要做的一輪</summary>
/// <param name="Conversation">對話：32 個十六進位字元是既有對話的 id；其他的是別名（第一次用時以 <paramref name="Account"/> 開新對話，之後指到同一個）</param>
/// <param name="Account">開新對話用的帳戶別名（既有對話不看）</param>
/// <param name="Message">送出的訊息</param>
/// <param name="Attachments">附件（本機磁碟上已存在的檔案；送出前等中樞處理完）</param>
/// <param name="Turn">這一輪的劇本；null＝固定回覆（<see cref="ScriptedSession.Reply"/>）</param>
/// <param name="Timeout">整個操作的時限</param>
internal sealed record ChatSpec(string Conversation, string? Account, string Message, IReadOnlyList<string> Attachments, ScriptedTurn? Turn, TimeSpan Timeout);

/// <summary>chat 的結果</summary>
/// <param name="Created">這次開了新對話</param>
/// <param name="Waiting">停在劇本沒有替使用者回應的卡片（這一輪還沒結束，<paramref name="Outcome"/> 是 null）</param>
/// <param name="Items">結束（或停下）時對話的項目數</param>
internal sealed record ChatResult(string ConversationId, bool Created, bool Waiting, AgentTurnOutcome? Outcome, int Items, double Seconds);

// 對話的操作（WP4.3 計畫步 13）：以網頁的指令開對話、附檔、送出，劇本化的會話（ScriptedSession）照劇本回；卡片照劇本經中樞的指令回應。
// 會話向這裡要這一輪的劇本（TakePendingTurn），所以一次只能有一個 chat 在送出
internal sealed partial class DeviceRuntime
{
    /// <summary>對話別名 → id 的存檔（資料根底下；真的 app 不讀它）</summary>
    public const string ConversationAliasFile = "fake-device-conversations.json";

    private readonly object turnGate = new();
    private PendingTurn? pendingTurn;

    /// <summary>對話別名 → id（UI 執行緒）</summary>
    private readonly Dictionary<string, string> conversationAliases = new(StringComparer.Ordinal);

    /// <summary>
    /// 會話送出時取這一輪（任何執行緒；取走就清掉，下一次送出不會拿到同一輪）：chat 放的優先；沒有的話取 <see cref="EnqueueRemoteTurn"/> 排的下一份
    /// （WP4.9 步 11：網頁或手機送來的訊息照這份劇本回）；都沒有是 null（固定回覆）
    /// </summary>
    private PendingTurn? TakePendingTurn()
    {
        lock (turnGate)
        {
            var turn = pendingTurn;
            pendingTurn = null;
            if (turn is null && remoteTurns.TryDequeue(out var remote))
            {
                remoteTurnsTaken++;
                turn = new PendingTurn(remote);
            }

            return turn;
        }
    }

    /// <summary>
    /// 送出一則訊息並跑完這一輪（或停在沒有回應的卡片）：必要時開新對話、附檔並等處理完、送出（被中樞擋下時丟出 toast 的內容）、
    /// 照劇本回應卡片、等這一輪的完成事件交給中樞。格式或狀態不對丟 <see cref="InvalidOperationException"/>（訊息給使用者看）。
    /// <paramref name="onStarted"/>（WP4.9 步 11 的背景串流）：會話收下這一輪之後、在等它結束之前呼叫一次，參數是對話 id
    /// </summary>
    public async Task<ChatResult> ChatAsync(ChatSpec spec, Action<string>? onStarted = null)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan Remaining() => spec.Timeout - watch.Elapsed;

        var (id, created) = await OnUi(() => ResolveConversation(spec));
        if (spec.Attachments.Count > 0)
            await AttachAsync(id, spec.Attachments, Remaining);

        var pending = new PendingTurn(spec.Turn);
        var toastMark = await OnUi(() =>
        {
            if (Hub.IsRunning(id))
                throw new InvalidOperationException($"對話 {id} 還在執行上一輪（可能停在沒有回應的卡片）：等它結束再送");
            lock (turnGate)
            {
                if (pendingTurn is not null)
                    throw new InvalidOperationException("上一個 chat 還沒送出");
                pendingTurn = pending;
            }

            var mark = View.Toasts.Count;
            Hub.HandleCommand(View, new JObject { ["cmd"] = "send", ["conversationId"] = id, ["text"] = spec.Message });
            return mark;
        });

        // 等會話收下（中樞送出前可能在背景檢查工作資料夾）；中樞擋下時以 toast 說明
        while (!pending.Started.Task.IsCompleted)
        {
            var toasts = await OnUi(() => View.Toasts.Skip(toastMark).ToList());
            if (toasts.Count > 0 || Remaining() <= TimeSpan.Zero)
            {
                Withdraw(pending);
                throw new InvalidOperationException(toasts.Count > 0 ? "送出被擋下：" + string.Join("；", toasts) : "時限內會話沒有收到這則訊息");
            }

            await Task.WhenAny(pending.Started.Task, Task.Delay(50));
        }

        if (created)
            await OnUi(SaveConversationAliases);
        onStarted?.Invoke(id);

        var responded = new HashSet<string>(StringComparer.Ordinal);
        while (!pending.Completed.Task.IsCompleted)
        {
            if (await OnUi(() => RespondToCards(id, spec.Turn, responded)))
                return new ChatResult(id, created, Waiting: true, null, await OnUi(() => Hub.GetItems(id).Count), Seconds(watch));
            if (Remaining() <= TimeSpan.Zero)
                throw new InvalidOperationException($"時限內這一輪沒有結束（對話 {id}）");
            await Task.WhenAny(pending.Completed.Task, Task.Delay(50));
        }

        var outcome = await pending.Completed.Task;
        // 完成事件在 UI 執行緒交給中樞時才設結果，中樞處理它的工作已經排在這個查詢前面（依序執行）
        var items = await OnUi(() => Hub.GetItems(id).Count);
        return new ChatResult(id, created, Waiting: false, outcome, items, Seconds(watch));
    }

    private static double Seconds(Stopwatch watch) => Math.Round(watch.Elapsed.TotalSeconds, 3);

    /// <summary>送出沒成功：這一輪還沒被會話取走就收回</summary>
    private void Withdraw(PendingTurn pending)
    {
        lock (turnGate)
        {
            if (ReferenceEquals(pendingTurn, pending))
                pendingTurn = null;
        }
    }

    /// <summary>對話 id（UI 執行緒）：既有的先 open（輸入區的訊息才送到這個 view）；別名第一次用時開新對話（草稿，送出後才存檔）</summary>
    private (string Id, bool Created) ResolveConversation(ChatSpec spec)
    {
        var key = spec.Conversation;
        var id = key.Length == 32 && key.All(Uri.IsHexDigit) ? key.ToLowerInvariant() : conversationAliases.GetValueOrDefault(key);
        if (id is not null)
        {
            if (Hub.GetConversation(id) is null)
                throw new InvalidOperationException($"沒有對話 {key}");
            Hub.HandleCommand(View, new JObject { ["cmd"] = "open", ["conversationId"] = id });
            return (id, false);
        }

        var accountAlias = spec.Account ?? throw new InvalidOperationException($"對話 {key} 還沒有：開新對話要給 account");
        var account = Accounts.Find(AccountId(accountAlias)) ?? throw new InvalidOperationException($"沒有帳戶 {accountAlias}（先 addAccount）");
        var mark = View.Toasts.Count;
        Hub.HandleCommand(View, new JObject { ["cmd"] = "newConversation", ["accountId"] = account.Id });
        var draft = Hub.LatestDraftId(View.Id);
        if (draft is null || View.Toasts.Count > mark)
            throw new InvalidOperationException("開不了新對話：" + string.Join("；", View.Toasts.Skip(mark)));
        // 存檔等送出成功（草稿送出後才存檔；沒送出的草稿下次執行就不在了）
        conversationAliases[key] = draft;
        return (draft, true);
    }

    /// <summary>逐一附檔，等中樞處理完（輸入區不再處理中、附件數到齊）；失敗以 toast 說明</summary>
    private async Task AttachAsync(string id, IReadOnlyList<string> paths, Func<TimeSpan> remaining)
    {
        var (mark, before) = await OnUi(() => (View.Toasts.Count, AttachmentCount(id)));
        foreach (var path in paths)
            await CommandAsync("addPathAttachment", new JObject { ["conversationId"] = id, ["path"] = path });
        while (true)
        {
            var (toasts, busy, count) = await OnUi(() =>
                (View.Toasts.Skip(mark).ToList(), View.Composers.GetValueOrDefault(id)?.Value<bool?>("busy") ?? false, AttachmentCount(id)));
            if (toasts.Count > 0)
                throw new InvalidOperationException("附件失敗：" + string.Join("；", toasts));
            if (!busy && count >= before + paths.Count)
                return;
            if (remaining() <= TimeSpan.Zero)
                throw new InvalidOperationException($"時限內附件沒有處理完（{count - before}／{paths.Count}）");
            await Task.Delay(50);
        }
    }

    private int AttachmentCount(string id) => (View.Composers.GetValueOrDefault(id)?["attachments"] as JArray)?.Count ?? 0;

    /// <summary>
    /// 照劇本回應等待中的卡片（UI 執行緒；以網頁的 answer、skipQuestion、approve、approvePlan 指令）。回傳 true＝有劇本沒有指定回應的卡片（留著等）
    /// </summary>
    private bool RespondToCards(string id, ScriptedTurn? turn, HashSet<string> responded)
    {
        foreach (var item in Hub.GetItems(id).ToList())
        {
            if (item.Status != AgentItemStatus.Waiting || item.RequestKey is not { } key || responded.Contains(key) || StepOf(turn, key) is not { } step)
                continue;
            JObject? command = step switch
            {
                ScriptedQuestion { Skip: true } => new JObject { ["cmd"] = "skipQuestion" },
                ScriptedQuestion { Answer: { } answer } => new JObject
                {
                    ["cmd"] = "answer",
                    ["answers"] = new JObject(answer.Select(a => new JProperty(a.Key, new JArray(a.Value)))),
                },
                ScriptedApproval { Decision: { } decision } => new JObject { ["cmd"] = "approve", ["choice"] = decision },
                ScriptedPlanApproval { Decision: { } decision } => new JObject { ["cmd"] = "approvePlan", ["choice"] = decision },
                _ => null,
            };
            if (command is null)
                return true;
            command["conversationId"] = id;
            command["itemId"] = item.Id;
            responded.Add(key);
            Hub.HandleCommand(View, command);
        }

        return false;
    }

    /// <summary>請求鍵（fake-req-{輪}-{步}）對到的劇本步驟</summary>
    private static ScriptedStep? StepOf(ScriptedTurn? turn, string requestKey)
    {
        if (turn is null || !requestKey.StartsWith("fake-req-", StringComparison.Ordinal))
            return null;
        var dash = requestKey.LastIndexOf('-');
        return int.TryParse(requestKey.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < turn.Steps.Count
            ? turn.Steps[index]
            : null;
    }

    // ───────────────────────────── 對話別名的存檔 ─────────────────────────────

    private void LoadConversationAliases()
    {
        if (!options.Persist)
            return;
        foreach (var (alias, id) in JsonFile.Read<Dictionary<string, string>>(Path.Combine(options.DataRoot, ConversationAliasFile)) ?? [])
            conversationAliases[alias] = id;
    }

    private void SaveConversationAliases()
    {
        if (options.Persist)
            JsonFile.Write(Path.Combine(options.DataRoot, ConversationAliasFile), conversationAliases);
    }

    // ───────────────────────────── 回補 ─────────────────────────────

    /// <summary>本機擁有的對話數（不含草稿與副本）</summary>
    public Task<int> ConversationCountAsync() => OnUi(() => ((IConversationPublishSource)Hub).ConversationIds().Count);

    /// <summary>回補的進度與暫停（發布者的；沒有發布者——未配對、或引擎不支援——是 Attached＝false）</summary>
    public Task<(bool Attached, SyncBackfillProgress? Progress, BackfillPause? Pause)> BackfillAsync() => OnUi(() =>
        Hub.ConversationSink is ConversationPublisher publisher ? (true, publisher.Progress, publisher.Pause) : (false, (SyncBackfillProgress?)null, (BackfillPause?)null));

    /// <summary>連續維持多久才算回補做完（同探針的 ProbeRules.SettleQuiet）</summary>
    private static readonly TimeSpan BackfillQuiet = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 等回補做完（S6 的量測在這之後才取樣）：照 <see cref="SyncSettleRules.IsSettled"/>（和探針同一組條件）——引擎在 Live、沒有在途的批次、
    /// outbox 每一列都已確認或被拒、重整佇列是空的、沒有進行中的工作、不在重新整理、發布者的進度是 null，連續 1 秒都是這樣。
    /// 沒有 conv1 時回補不會開始，進了 Live、清空就算完成。回傳（是否完成、花的秒數）
    /// </summary>
    public async Task<(bool Done, double Seconds)> WaitBackfillDoneAsync(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan? quietSince = null;
        while (true)
        {
            var done = false;
            if (await EngineAsync() is ISyncSettleProbe engine)
            {
                var snapshot = await engine.SettleSnapshotAsync();
                done = SyncSettleRules.IsSettled(snapshot with { BackfillDone = (await BackfillAsync()).Progress is null });
            }

            if (done)
            {
                quietSince ??= watch.Elapsed;
                if (SyncSettleRules.QuietEnough(watch.Elapsed - quietSince.Value, BackfillQuiet))
                    return (true, Seconds(watch));
            }
            else
            {
                quietSince = null;
            }

            if (watch.Elapsed > timeout)
                return (false, Seconds(watch));
            await Task.Delay(100);
        }
    }
}
