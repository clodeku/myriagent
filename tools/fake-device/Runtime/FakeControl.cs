using System.Diagnostics;
using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FakeDevice;

/// <summary>
/// 控制通道（<c>run --control-stdin</c>）與劇本裡同名 op 的執行端（WP4.9 步 11，給全流程「跑到一半下指令」）。參數已由
/// <see cref="FakeControlRules"/> 檢查過；使用者看得懂的失敗丟 <see cref="DeviceRuntimeException"/>（或 chat 的 <see cref="InvalidOperationException"/>）。
/// 指令一個接一個執行（不並行）；<c>stream</c> 預設在會話收下這一輪之後就回覆、在背景跑完（<c>wait:true</c> 時等它結束），
/// 背景串流的狀態在 <c>status</c> 的 <c>streams</c>。只在一個非同步流程裡用（通道或劇本），不加鎖
/// </summary>
internal sealed class FakeControl(DeviceRuntime device) : IAsyncDisposable
{
    /// <summary>quit、通道結束時停掉背景串流之後最多等多久</summary>
    public static readonly TimeSpan StopStreamsTimeout = TimeSpan.FromSeconds(10);

    /// <summary>劇本結束時等背景串流自己跑完的上限（每個串流另有自己的時限，逾時就以失敗結束）</summary>
    public static readonly TimeSpan ScriptStreamsTimeout = TimeSpan.FromHours(1);

    /// <summary>payloadFile 最大幾個位元組</summary>
    private const long MaxPayloadFileBytes = 32L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(false);

    private static readonly JsonSerializerSettings JsonSettings = new() { DateParseHandling = DateParseHandling.None };

    private readonly List<StreamRun> streams = [];

    /// <summary>一個背景串流（<paramref name="Key"/> 是呼叫端給的別名或 id）</summary>
    private sealed record StreamRun(string Key, string ConversationId, Task<ChatResult> Task);

    /// <summary>執行一個已解析的指令（quit 由通道處理），回傳 result 物件</summary>
    public Task<JObject> ExecuteAsync(FakeControlCommand command) => command.Op switch
    {
        "status" => StatusAsync(),
        "stream" => StreamAsync(command.Args),
        "nextRemoteTurn" => Task.FromResult(NextRemoteTurn(command.Args)),
        "setTitle" => SetTitleAsync(command.Args),
        "setPolicy" => SetPolicyAsync(command.Args),
        "ords" => OrdsAsync(command.Args),
        "remoteAudit" => RemoteAuditAsync(command.Args),
        "xssFill" => XssFillAsync(command.Args),
        "restoreFromTrash" => RestoreFromTrashAsync(command.Args),
        // WP4.9 步 13：假 Codex app-server（要以 --fake-codex 啟動；IO 很小，直接在呼叫端做）
        "fakeCodexComplete" => Task.FromResult(device.FakeCodexComplete()),
        "fakeCodexCalls" => Task.FromResult(device.FakeCodexCalls()),
        // WP4.9 步 16：這台的使用者在桌面上的操作（同步政策、暫停同步、對複本的寫入）與複本的狀態
        "localCommand" => LocalCommandAsync(command.Args),
        "replicas" => ReplicasAsync(command.Args),
        // WP4.9 步 17：在 run 的途中解除配對（FF8 看解除配對前匯出的遠端操作紀錄、FF10 收掉被撤銷的裝置）
        "unpair" => UnpairAsync(),
        "quit" => throw new DeviceRuntimeException("quit 只能在控制通道裡用"),
        _ => throw new DeviceRuntimeException($"不認得的 op：{command.Op}"),
    };

    // ───────────────────────────── 通道 ─────────────────────────────

    /// <summary>
    /// 控制通道：先寫一行 <c>{"event":"ready",…}</c>，之後每讀一行指令就寫一行回覆（<see cref="FakeControlRules.Ok"/>、<see cref="FakeControlRules.Fail"/>，
    /// 另帶 <c>ms</c>）；一行不合格或執行失敗只回 <c>ok:false</c>、照常讀下一行。<c>quit</c> 或輸入結束（EOF）就停：還在跑的背景串流先以網頁的 <c>stop</c> 停掉、
    /// 最多等 <see cref="StopStreamsTimeout"/>。回傳摘要（指令數、失敗數、怎麼結束的、每個背景串流的結果）
    /// </summary>
    public async Task<JObject> RunChannelAsync(TextReader input, TextWriter output)
    {
        var (paired, state) = await device.OnUi(() => (device.Sync.IsPaired, device.Sync.Status?.State.ToString()));
        Write(output, new JObject
        {
            ["event"] = "ready",
            ["pid"] = Environment.ProcessId,
            ["paired"] = paired,
            ["state"] = state,
            ["ops"] = new JArray(FakeControlRules.Ops),
        });
        var commands = 0;
        var errors = 0;
        var quit = false;
        while (!quit)
        {
            var line = await input.ReadLineAsync();
            if (line is null)
                break;
            if (string.IsNullOrWhiteSpace(line))
                continue;
            commands++;
            var watch = Stopwatch.StartNew();
            var parsed = FakeControlRules.Parse(line);
            JObject reply;
            if (parsed.Command is not { } command)
            {
                reply = FakeControlRules.Fail(parsed.Id, parsed.Op, parsed.Error ?? "不合格的指令");
            }
            else if (command.Op == "quit")
            {
                quit = true;
                reply = FakeControlRules.Ok(command.Id, command.Op, new JObject { ["commands"] = commands });
            }
            else
            {
                reply = await RunOneAsync(command);
            }

            if (reply.Value<bool>("ok") == false)
                errors++;
            reply["ms"] = watch.ElapsedMilliseconds;
            Write(output, reply);
        }

        return new JObject
        {
            ["commands"] = commands,
            ["errors"] = errors,
            ["ended"] = quit ? "quit" : "eof",
            ["streams"] = await FinishStreamsAsync(stop: true, StopStreamsTimeout),
        };
    }

    private async Task<JObject> RunOneAsync(FakeControlCommand command)
    {
        try
        {
            return FakeControlRules.Ok(command.Id, command.Op, await ExecuteAsync(command));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or IOException or UnauthorizedAccessException or JsonException or TimeoutException)
        {
            return FakeControlRules.Fail(command.Id, command.Op, ex.Message);
        }
        catch (Exception ex)
        {
            // 程式錯誤也不結束通道（呼叫端看得到型別，記錄檔有堆疊）
            Log.Error($"fake-device 控制通道的 {command.Op} 出錯", ex);
            return FakeControlRules.Fail(command.Id, command.Op, $"{ex.GetType().Name}：{ex.Message}");
        }
    }

    private static void Write(TextWriter output, JObject message)
    {
        output.WriteLine(message.ToString(Formatting.None));
        output.Flush();
    }

    /// <summary>
    /// 收掉背景串流：<paramref name="stop"/> 時先對還在跑的送網頁的 <c>stop</c>（以 Interrupted 結束），再最多等 <paramref name="timeout"/>；回傳每個串流的結果
    /// </summary>
    public async Task<JArray> FinishStreamsAsync(bool stop, TimeSpan timeout)
    {
        var running = streams.Where(s => !s.Task.IsCompleted).ToList();
        if (stop)
        {
            foreach (var run in running)
                await device.CommandAsync("stop", new JObject { ["conversationId"] = run.ConversationId });
        }

        if (running.Count > 0)
            await Task.WhenAny(Task.WhenAll(running.Select(r => (Task)r.Task)), Task.Delay(timeout));
        return new JArray(streams.Select(Describe));
    }

    public async ValueTask DisposeAsync() => await FinishStreamsAsync(stop: true, StopStreamsTimeout);

    // ───────────────────────────── status、stream、nextRemoteTurn ─────────────────────────────

    private async Task<JObject> StatusAsync()
    {
        var result = await device.DescribeAsync();
        result["streams"] = new JArray(streams.Select(Describe));
        var (queued, taken) = device.RemoteTurns;
        result["remoteTurns"] = new JObject { ["queued"] = queued, ["taken"] = taken };
        return result;
    }

    private static JObject Describe(StreamRun run)
    {
        var result = new JObject { ["conversation"] = run.Key, ["conversationId"] = run.ConversationId, ["done"] = run.Task.IsCompleted };
        if (run.Task.IsCompletedSuccessfully)
        {
            var chat = run.Task.Result;
            result["outcome"] = chat.Outcome?.ToString();
            result["waiting"] = chat.Waiting;
            result["items"] = chat.Items;
            result["seconds"] = chat.Seconds;
        }
        else if (run.Task.IsFaulted)
        {
            result["error"] = run.Task.Exception!.GetBaseException().Message;
        }
        else if (run.Task.IsCanceled)
        {
            result["error"] = "取消";
        }

        return result;
    }

    /// <summary>
    /// 一輪只有一段文字串流的 chat（<see cref="ScriptedText"/>）：預設文字是每段一樣長的「第 N 段。」（<see cref="FakeControlRules.StreamText"/>）。
    /// 預設在會話收下這一輪就回覆 <c>{conversationId, background:true, …}</c>，這一輪在背景跑完；<c>wait:true</c> 等它結束，回 chat 的結果
    /// </summary>
    private async Task<JObject> StreamAsync(JObject args)
    {
        var key = args.Value<string>("conversation")!;
        if (streams.Any(s => s.Key == key && !s.Task.IsCompleted))
            throw new DeviceRuntimeException($"對話 {key} 的上一個串流還沒結束");
        var segments = args.Value<int?>("segments") ?? FakeControlRules.DefaultStreamSegments;
        var delayMs = args.Value<int?>("delayMs") ?? FakeControlRules.DefaultStreamDelayMs;
        var stamp = args.Value<bool?>("stamp") ?? false;
        var each = args.Value<bool?>("completeEachSegment") ?? false;
        var text = args.Value<string>("text") ?? FakeControlRules.StreamText(segments);
        var timeout = TimeSpan.FromSeconds(args.Value<int?>("seconds") ?? FakeControlRules.StreamSeconds(segments, delayMs));
        var message = args.Value<string>("message") ?? $"串流 {segments} 段";
        var turn = new ScriptedTurn([new ScriptedText(text, segments, delayMs, stamp, each)]);
        var started = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        var chat = device.ChatAsync(new ChatSpec(key, args.Value<string>("account"), message, [], turn, timeout), id => started.TrySetResult(id));
        if (args.Value<bool?>("wait") == true)
        {
            var done = await chat;
            return ChatJson(done);
        }

        await Task.WhenAny(started.Task, chat);
        if (!started.Task.IsCompleted)
        {
            // chat 在收下之前就失敗（開不了對話、被中樞擋下）：照它的錯誤回
            await chat;
            throw new DeviceRuntimeException("串流沒有開始");
        }

        var id = await started.Task;
        streams.Add(new StreamRun(key, id, chat));
        return new JObject
        {
            ["conversationId"] = id,
            ["background"] = true,
            ["segments"] = segments,
            ["delayMs"] = delayMs,
            ["stamp"] = stamp,
            ["completeEachSegment"] = each,
            ["startMs"] = watch.ElapsedMilliseconds,
        };
    }

    private static JObject ChatJson(ChatResult chat) => new()
    {
        ["conversationId"] = chat.ConversationId,
        ["created"] = chat.Created,
        ["waiting"] = chat.Waiting,
        ["outcome"] = chat.Outcome?.ToString(),
        ["items"] = chat.Items,
        ["seconds"] = chat.Seconds,
    };

    private JObject NextRemoteTurn(JObject args)
    {
        var turn = ScriptedTurn.Parse(args["turn"]!);
        var queued = device.EnqueueRemoteTurn(turn);
        return new JObject { ["queued"] = queued, ["steps"] = turn.Steps.Count };
    }

    // ───────────────────────────── setTitle、setPolicy、ords、remoteAudit ─────────────────────────────

    private async Task<JObject> SetTitleAsync(JObject args)
    {
        var id = await device.ResolveConversationIdAsync(args.Value<string>("conversation")!);
        var title = args.Value<string>("title")!;
        var applied = await RenameAsync(id, title);
        return new JObject { ["conversationId"] = id, ["title"] = applied, ["truncated"] = applied.Length < title.Trim().Length };
    }

    /// <summary>網頁的 rename（中樞去頭尾空白、最多 80 字）；回傳套用後的標題</summary>
    private async Task<string> RenameAsync(string id, string title)
    {
        await device.CommandAsync("rename", new JObject { ["conversationId"] = id, ["title"] = title });
        return await device.OnUi(() => device.Hub.GetConversation(id)?.Title ?? string.Empty);
    }

    private async Task<JObject> SetPolicyAsync(JObject args)
    {
        var (level, fullAccess, notify) = await device.SetPolicyAsync(args.Value<string>("level")!, args.Value<bool?>("fullAccess"), args.Value<bool?>("notify"));
        return new JObject { ["level"] = level, ["fullAccess"] = fullAccess, ["notify"] = notify };
    }

    /// <summary>
    /// 本機項目的 id 與 Ord（FF1 拿來和瀏覽器 open／loadOlder 拿到的比對）：<c>ord</c> 是中樞記憶體裡的（還沒配的是 null），
    /// <c>publishedOrd</c> 是 sync.db 的 <c>published_items</c> 記的（WP4.9 步 15：回補的對話中樞沒有載入、項目檔裡沒有 Ord，
    /// 引擎在執行緒集區重整時規劃、和 outbox 同一個交易記在這裡，之後中樞載入時以它當 hint 配到同一個值；沒配對或沒發布過是 null）。
    /// <c>published</c> 是 published_items 的每一列（含已送 itemRemoved 的，<c>removed</c>）
    /// </summary>
    private async Task<JObject> OrdsAsync(JObject args)
    {
        var id = await device.ResolveConversationIdAsync(args.Value<string>("conversation")!);
        var engine = await device.EngineAsync();
        var published = engine is null
            ? []
            : await engine.InvokeAsync(v => v.Store is { ConversationTablesReady: true } store ? store.LoadPublishedItems(id).ToList() : []);
        var byId = published.ToDictionary(p => p.ItemId, p => p, StringComparer.Ordinal);
        var items = await device.OnUi(() => device.Hub.GetItems(id).Select(i => new JObject
        {
            ["id"] = i.Id,
            ["ord"] = i.Ord,
            ["publishedOrd"] = byId.TryGetValue(i.Id, out var p) ? p.Ord : (long?)null,
            ["kind"] = i.Kind.ToString(),
            ["status"] = i.Status.ToString(),
        }).ToList());
        return new JObject
        {
            ["conversationId"] = id,
            ["count"] = items.Count,
            ["withoutOrd"] = items.Count(i => i["ord"]!.Type == JTokenType.Null),
            ["withoutPublishedOrd"] = items.Count(i => i["publishedOrd"]!.Type == JTokenType.Null),
            ["items"] = new JArray(items),
            ["published"] = new JArray(published.Select(p => new JObject { ["id"] = p.ItemId, ["ord"] = p.Ord, ["removed"] = p.Removed })),
        };
    }

    /// <summary>本機的遠端操作紀錄（引擎的 <see cref="ISyncEngineCommands.LoadRemoteAuditAsync"/>：新到舊；翻頁帶上一頁最後一列的 at 與 seq）</summary>
    private async Task<JObject> RemoteAuditAsync(JObject args)
    {
        if (await device.EngineAsync() is not ISyncEngineCommands commands)
            throw new DeviceRuntimeException("沒有配對：remoteAudit 讀的是 sync.db 的遠端操作紀錄，要先 pair");
        var rows = await commands.LoadRemoteAuditAsync(args.Value<long?>("beforeAt"), args.Value<int?>("limit") ?? 100, CancellationToken.None, args.Value<long?>("beforeSeq"));
        return new JObject
        {
            ["count"] = rows.Count,
            ["rows"] = new JArray(rows.Select(r => new JObject
            {
                ["seq"] = r.Seq,
                ["at"] = r.At,
                ["commandId"] = r.CommandId,
                ["cmd"] = r.Cmd,
                ["conversationId"] = r.ConversationId,
                ["issuerLabel"] = r.IssuerLabel,
                ["declaredTier"] = r.DeclaredTier,
                ["tier"] = r.Tier,
                ["fullAccess"] = r.FullAccess,
                ["policy"] = r.Policy,
                ["paused"] = r.Paused,
                ["state"] = r.State,
                ["code"] = r.Code,
                ["step"] = r.Step,
                ["defaultPassword"] = r.DefaultPassword,
                ["count"] = r.Count,
            })),
        };
    }

    // ───────────────────────────── restoreFromTrash ─────────────────────────────

    /// <summary>
    /// 經中樞的 RestoreFromTrash（同網頁的回收區還原鈕）：先確認回收區在這台的資料根底下（設計:3052）；回傳搬回了沒有、之前在不在回收區、
    /// 對話資料夾有沒有伺服器刪除的旁檔（還原的對話一律只在這台，FF8 斷言它）、清單裡有沒有它。搬不回來（清單已有、資料夾已存在…）不算失敗，<c>restored:false</c>
    /// </summary>
    private async Task<JObject> RestoreFromTrashAsync(JObject args)
    {
        var id = args.Value<string>("conversation")!;
        var trashRoot = await device.OnUi(() => device.Hub.Trash.RootDirectory);
        if (FakeControlRules.CheckTrashRoot(trashRoot, device.DataRoot) is { } problem)
            throw new DeviceRuntimeException(problem);
        var wasInTrash = Directory.Exists(Path.Combine(trashRoot, id));
        var restored = await device.OnUi(() => device.Hub.RestoreFromTrash(id));
        var marker = Path.Combine(device.DataRoot, "conversations", id, ConversationStore.ServerDeletedFileName);
        return new JObject
        {
            ["conversationId"] = id,
            ["restored"] = restored,
            ["wasInTrash"] = wasInTrash,
            ["trashRoot"] = trashRoot,
            ["serverDeleted"] = File.Exists(marker),
            ["inList"] = await device.OnUi(() => device.Hub.GetConversation(id) is not null),
        };
    }

    // ───────────────────────────── unpair（WP4.9 步 17） ─────────────────────────────

    /// <summary>
    /// 解除配對（同 <c>unpair</c> 子指令：網頁的 syncUnpair，協調者先把遠端操作紀錄匯出成 <c>{資料根}\remote-audit\{deviceId}.jsonl</c>（D33）、
    /// 再停引擎、刪金鑰、pending 檔、sync.db，最後刪 device.json）；回傳原本的裝置 id 與匯出檔的狀態（在不在、位元組數、行數＝列數）。
    /// 匯出失敗只記錄、解除配對照常（設計），所以檔案不在不算這個 op 失敗。沒有配對時失敗；之後 run 不再等 outbox 清空
    /// </summary>
    private async Task<JObject> UnpairAsync()
    {
        var deviceId = await device.UnpairAsync(TimeSpan.FromSeconds(20))
                       ?? throw new DeviceRuntimeException("解除配對了，但拿不到原本的裝置 id");
        var path = SyncCommandRules.RemoteAuditExportPath(device.Paths, deviceId);
        var exists = File.Exists(path);
        var lines = exists ? File.ReadLines(path, Utf8).Count(l => l.Length > 0) : 0;
        return new JObject
        {
            ["deviceId"] = deviceId,
            ["remoteAudit"] = new JObject
            {
                ["path"] = path,
                ["exists"] = exists,
                ["bytes"] = exists ? new FileInfo(path).Length : 0,
                ["lines"] = lines,
            },
        };
    }

    // ───────────────────────────── localCommand、replicas（WP4.9 步 16） ─────────────────────────────

    /// <summary>
    /// 這台的使用者在桌面上的一個操作（<see cref="FakeControlRules.LocalCommands"/>）：經中樞的 HandleCommand 交給假的主視窗（同 app 的主視窗送的指令），
    /// 等 UI 執行緒把接著排的工作做完，回傳這段時間收到的錯誤 toast（例如對複本的寫入「這是另一台電腦的對話（唯讀）」、已從伺服器刪除的對話改回 full）。
    /// 參數有 conversationId 時另附那個對話的狀態（<see cref="DescribeConversationAsync"/>）；syncPause 另附暫停的設定與同步狀態（暫停＝斷線，伺服器看到電腦離線）
    /// </summary>
    private async Task<JObject> LocalCommandAsync(JObject args)
    {
        var cmd = args.Value<string>("cmd")!;
        var body = args["args"] as JObject ?? new JObject();
        var before = await device.OnUi(() => device.View.Toasts.Count);
        await device.CommandAsync(cmd, body);
        await device.Host.DrainAsync();
        var toasts = await device.OnUi(() => device.View.Toasts.Skip(before).ToList());
        var result = new JObject { ["cmd"] = cmd, ["toasts"] = new JArray(toasts) };
        if (body["conversationId"] is JValue { Type: JTokenType.String, Value: string id })
            result["conversation"] = await DescribeConversationAsync(id);
        if (cmd == "syncPause")
        {
            var (paused, state) = await device.OnUi(() => (device.Settings.Sync.Paused, device.Sync.Status?.State.ToString()));
            result["paused"] = paused;
            result["state"] = state;
        }

        return result;
    }

    /// <summary>
    /// 中樞裡的一個對話（UI 執行緒）：在不在清單、是不是其他電腦的複本、標題、同步政策（本機的是有效政策：有伺服器刪除的旁檔一律 localOnly；
    /// 複本的是擁有者 meta 的政策）、項目數、執行中、伺服器刪除的旁檔（只看本機的、id 是 app 的資料夾名稱時才查檔案）
    /// </summary>
    private Task<JObject> DescribeConversationAsync(string id) => device.OnUi(() =>
    {
        if (device.Hub.GetConversation(id) is not { } meta)
            return new JObject { ["id"] = id, ["exists"] = false };
        var replica = device.Hub.IsReplicaConversation(id);
        return new JObject
        {
            ["id"] = id,
            ["exists"] = true,
            ["replica"] = replica,
            ["title"] = meta.Title,
            ["syncPolicy"] = replica ? meta.SyncPolicy : device.Hub.GetEffectiveSyncPolicy(id),
            ["items"] = device.Hub.GetItems(id).Count,
            ["running"] = device.Hub.IsRunning(id),
            ["serverDeleted"] = !replica && ConversationTrashRules.IsTrashableId(id)
                && File.Exists(Path.Combine(device.DataRoot, "conversations", id, ConversationStore.ServerDeletedFileName)),
        };
    });

    /// <summary>
    /// 這台的複本（其他電腦的對話；sync.db 的 replica_meta 與 replica_items，在引擎執行緒讀）：每一列的擁有者、epoch、seq、刪除了沒有、
    /// 政策（<see cref="ReplicaRules.PolicyOf"/>）、標題、存著的項目數、引擎有沒有在拉它的串流（開著），加上中樞那邊（顯示中的複本、它的項目數）。
    /// 給 <c>conversation</c> 時只回那一個（沒有那一列時 <c>replicas</c> 是空的）；沒有配對時失敗
    /// </summary>
    private async Task<JObject> ReplicasAsync(JObject args)
    {
        var only = args.Value<string>("conversation")?.ToLowerInvariant();
        if (await device.EngineAsync() is not { } engine)
            throw new DeviceRuntimeException("沒有配對：複本存在 sync.db 的 replica_meta、replica_items，要先 pair");
        var rows = await engine.InvokeAsync(v =>
        {
            if (v.Store is not { ConversationTablesReady: true } store)
                return null;
            return store.LoadReplicaMeta().Values
                .Where(r => only is null || string.Equals(r.ConversationId, only, StringComparison.Ordinal))
                .OrderBy(r => r.ConversationId, StringComparer.Ordinal)
                .Select(r => (Row: r, Items: store.LoadReplicaItems(r.ConversationId).Count, Open: v.Replicas.Open.Contains(r.ConversationId)))
                .ToList();
        });
        if (rows is null)
            throw new DeviceRuntimeException("sync.db 的對話資料表還沒準備好（還沒連上過有 conv1 的伺服器）");
        var hub = await device.OnUi(() => (
            Show: device.Hub.ShowReplicas,
            Items: rows.ToDictionary(r => r.Row.ConversationId,
                r => device.Hub.IsReplicaConversation(r.Row.ConversationId) ? device.Hub.GetItems(r.Row.ConversationId).Count : (int?)null, StringComparer.Ordinal)));
        return new JObject
        {
            ["showReplicas"] = hub.Show,
            ["count"] = rows.Count,
            ["replicas"] = new JArray(rows.Select(r => new JObject
            {
                ["conversationId"] = r.Row.ConversationId,
                ["ownerDeviceId"] = r.Row.OwnerDeviceId,
                ["epoch"] = r.Row.Epoch,
                ["seq"] = r.Row.Seq,
                ["deleted"] = r.Row.Deleted,
                ["policy"] = r.Row.Deleted ? null : ReplicaRules.PolicyOf(r.Row.MetaJson),
                ["title"] = ReplicaRules.ToMeta(r.Row)?.Meta.Title,
                ["items"] = r.Items,
                ["open"] = r.Open,
                // 中樞裡顯示中的複本（null＝中樞沒有它：刪除了、還沒交給中樞，或 ShowReplicas 關著）
                ["hubItems"] = hub.Items[r.Row.ConversationId],
            })),
        };
    }

    // ───────────────────────────── xssFill ─────────────────────────────

    /// <summary>
    /// 把 XSS 語料（e2e-fixtures.json 的 <c>xss[].html</c>，或字串陣列）的 [from, from+count) 放進假裝置放得進的欄位：
    /// <list type="bullet">
    /// <item><c>title</c>：每筆一個新對話（別名 <c>xss-title-{序號}</c>，固定回覆），以網頁的 rename 改成這筆（中樞最多 80 字，截掉的記在 <c>titleTruncated</c>）。</item>
    /// <item><c>accountLabel</c>、<c>deviceName</c>：只有一個欄位，放第一筆（裝置名稱要已配對，否則記未覆蓋）。</item>
    /// <item><c>text</c>、<c>tool</c>、<c>question</c>、<c>approval</c>：同一輪（對話 <c>conversation</c>，預設別名 <c>xss-cards</c>），每筆一段代理文字、一個工具
    /// （名稱、標題、細節、輸出）、一張問卷（題目與選項，答這個選項）、一張核准卡（工具名稱、標題、細節，拒絕），這一輪跑完。</item>
    /// <item><c>fileNames</c>：在 <c>folder</c>（TEMP 底下、資料根以外）建每筆一個檔（<see cref="FakeControlRules.SafeFileName"/>），再把卡片那個對話的工作資料夾換成它
    /// （listFiles 只列對話工作資料夾裡的檔）。</item>
    /// <item><c>notifyTitle</c>、<c>toast</c>、<c>fileRefError</c>：假裝置放不進，記在 <c>uncovered</c>。</item>
    /// </list>
    /// </summary>
    private async Task<JObject> XssFillAsync(JObject args)
    {
        var path = args.Value<string>("payloadFile")!;
        if (FakeControlRules.CheckPayloadFile(path, PayloadRoots()) is { } bad)
            throw new DeviceRuntimeException(bad);
        var full = Path.GetFullPath(path);
        var payloads = LoadPayloads(full);
        var from = args.Value<int?>("from") ?? 0;
        if (from >= payloads.Count)
            throw new DeviceRuntimeException($"from {from} 超過語料的筆數 {payloads.Count}");
        var slice = payloads.Skip(from).Take(Math.Min(args.Value<int?>("count") ?? payloads.Count, payloads.Count - from)).ToList();
        var fields = FakeControlRules.XssFieldsOf(args);
        var account = args.Value<string>("account");
        var cardsKey = args.Value<string>("conversation") ?? "xss-cards";
        var covered = new JObject();
        var uncovered = new JArray();
        var result = new JObject { ["payloadFile"] = full, ["total"] = payloads.Count, ["from"] = from, ["count"] = slice.Count };

        foreach (var field in fields.Where(f => FakeControlRules.XssUncoverableFields.Contains(f)))
            uncovered.Add(new JObject { ["field"] = field, ["reason"] = "假裝置放不進這個欄位（由中樞或伺服器產生；conv-probe 與伺服器的測試覆蓋）" });

        if (fields.Contains("title"))
        {
            var ids = new JArray();
            var truncated = 0;
            for (var i = 0; i < slice.Count; i++)
            {
                var alias = $"xss-title-{from + i}";
                var id = await device.HasConversationAsync(alias)
                    ? await device.ResolveConversationIdAsync(alias)
                    : (await device.ChatAsync(new ChatSpec(alias, account, $"XSS 標題 {from + i}", [], null, TimeSpan.FromSeconds(60)))).ConversationId;
                var applied = await RenameAsync(id, slice[i]);
                if (applied.Length < slice[i].Trim().Length)
                    truncated++;
                ids.Add(id);
            }

            covered["title"] = slice.Count;
            result["titleConversations"] = ids;
            result["titleTruncated"] = truncated;
        }

        if (fields.Contains("accountLabel"))
        {
            var target = await device.FindAccountAsync(account!) ?? throw new DeviceRuntimeException($"沒有帳戶 {account}（先 addAccount）");
            await device.CommandAsync("accountUpdate", new JObject { ["accountId"] = target.Id, ["label"] = slice[0] });
            result["accountLabel"] = await device.OnUi(() => device.Accounts.Find(target.Id)?.Label);
            covered["accountLabel"] = 1;
        }

        if (fields.Contains("deviceName"))
        {
            if ((await device.StatusAsync())?.DeviceId is { } deviceId)
            {
                await device.CommandAsync("deviceRename", new JObject { ["deviceId"] = deviceId, ["name"] = slice[0] });
                covered["deviceName"] = 1;
            }
            else
            {
                uncovered.Add(new JObject { ["field"] = "deviceName", ["reason"] = "沒有配對（裝置名稱存在伺服器）" });
            }
        }

        string? cardsId = null;
        var cardFields = fields.Where(f => f is "text" or "tool" or "question" or "approval").ToList();
        if (cardFields.Count > 0)
        {
            var turn = FakeControlRules.XssTurn(slice, from, cardFields);
            var chat = await device.ChatAsync(new ChatSpec(cardsKey, account, $"XSS 卡片 {from}～{from + slice.Count - 1}", [], turn,
                TimeSpan.FromSeconds(Math.Min(3600, 60 + turn.Steps.Count / 5))));
            if (chat.Waiting || chat.Outcome != AgentTurnOutcome.Completed)
                throw new DeviceRuntimeException($"放 XSS 卡片的那一輪沒有完成（waiting {chat.Waiting}、outcome {chat.Outcome}）");
            cardsId = chat.ConversationId;
            foreach (var field in cardFields)
                covered[field] = slice.Count;
            result["cardsConversation"] = cardsId;
            result["cardsItems"] = chat.Items;
        }

        if (fields.Contains("fileNames"))
        {
            var folder = Path.GetFullPath(args.Value<string>("folder")!);
            if (FakeControlRules.CheckXssFolder(folder, Path.GetTempPath(), device.DataRoot) is { } folderProblem)
                throw new DeviceRuntimeException(folderProblem);
            Directory.CreateDirectory(folder);
            var names = new JArray();
            for (var i = 0; i < slice.Count; i++)
            {
                var name = FakeControlRules.SafeFileName(slice[i], from + i);
                File.WriteAllText(Path.Combine(folder, name), $"XSS {from + i}\n", Utf8);
                names.Add(name);
            }

            // listFiles 只列對話工作資料夾裡的檔：卡片那個對話（沒有就以固定回覆開一個）的工作資料夾換成這個資料夾
            cardsId ??= await device.HasConversationAsync(cardsKey)
                ? await device.ResolveConversationIdAsync(cardsKey)
                : (await device.ChatAsync(new ChatSpec(cardsKey, account, "XSS 檔名", [], null, TimeSpan.FromSeconds(60)))).ConversationId;
            var target = cardsId;
            await device.CommandAsync("setFolder", new JObject { ["conversationId"] = target, ["path"] = folder });
            if (!await device.WaitUntilAsync(() => string.Equals(device.Hub.GetConversation(target)?.WorkingDirectory, folder, StringComparison.OrdinalIgnoreCase),
                    TimeSpan.FromSeconds(10)))
                throw new DeviceRuntimeException($"對話 {target} 的工作資料夾沒有換成 {folder}（還在執行、或中樞不接受這個資料夾）");
            covered["fileNames"] = slice.Count;
            result["folder"] = folder;
            result["folderConversation"] = target;
            result["files"] = names;
        }

        result["covered"] = covered;
        result["uncovered"] = uncovered;
        return result;
    }

    /// <summary>payloadFile 可以放的根目錄：TEMP，與 repo 的兩個 Golden 資料夾（從執行檔的資料夾往上找 Myriagent.slnx；找不到就只有 TEMP）</summary>
    internal static IReadOnlyList<string> PayloadRoots()
    {
        var roots = new List<string> { Path.GetTempPath() };
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Myriagent.slnx")))
                continue;
            roots.Add(Path.Combine(dir.FullName, "tests", "MyriagentHub.Tests", "Rendering", "Golden"));
            roots.Add(Path.Combine(dir.FullName, "tests", "Myriagent.Tests", "Rendering", "Golden"));
            break;
        }

        return roots;
    }

    /// <summary>讀 XSS 語料：e2e-fixtures.json（<c>{"xss":[{"name","html"}…]}</c>，取 html）或字串陣列；不是一般檔（重新解析點）、太大、格式不對都丟例外</summary>
    internal static IReadOnlyList<string> LoadPayloads(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new DeviceRuntimeException($"找不到 payloadFile：{path}");
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new DeviceRuntimeException($"payloadFile 是重新解析點（連結），不讀：{path}");
        if (info.Length > MaxPayloadFileBytes)
            throw new DeviceRuntimeException($"payloadFile 超過 {MaxPayloadFileBytes / 1024 / 1024} MB：{path}");
        var token = JsonConvert.DeserializeObject<JToken>(File.ReadAllText(path, Utf8), JsonSettings);
        List<string?>? payloads = token switch
        {
            JObject { } obj when obj["xss"] is JArray cases => cases.Select(c => c is JObject { } one && one["html"] is JValue { Type: JTokenType.String, Value: string html } ? html : null)
                .ToList(),
            JArray array => array.Select(p => p is JValue { Type: JTokenType.String, Value: string text } ? text : null).ToList(),
            _ => null,
        };
        if (payloads is null || payloads.Count == 0 || payloads.Any(string.IsNullOrEmpty))
            throw new DeviceRuntimeException("payloadFile 要是 e2e-fixtures.json 的格式 {\"xss\":[{\"name\",\"html\"}…]} 或非空字串的陣列");
        return payloads.Select(p => p!).ToList();
    }
}
