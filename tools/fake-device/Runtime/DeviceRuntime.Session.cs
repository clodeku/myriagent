using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace FakeDevice;

/// <summary>配對、解除配對與正式站守門的失敗（訊息給使用者看；fake-device 的指令轉成結束碼 1）</summary>
internal sealed class DeviceRuntimeException(string message) : InvalidOperationException(message);

/// <summary>開始配對拿到的配對碼（<see cref="DeviceRuntime.StartPairingAsync"/>）</summary>
internal sealed record PairingCode(string UserCode, string? DeviceFingerprint, string? VerificationUri, string? VerificationUriComplete, long ExpiresAt);

// 配對、解除配對與狀態（WP4.9 步 8 從 Program 搬進 Runtime：pair、session 指令與測試的 FakeDeviceSessionTests 用同一份）。
// 失敗丟 DeviceRuntimeException（訊息給使用者看；Program 轉成結束碼 1）
internal sealed partial class DeviceRuntime
{
    /// <summary>
    /// 走 app 的配對流程（網頁的 syncPair）直到拿到配對碼；配對沒有開始（toast、錯誤、30 秒內沒有配對碼）丟例外。
    /// 呼叫之前先過正式站守門（<see cref="LocalHubTarget.VerifyAsync"/>）
    /// </summary>
    public async Task<PairingCode> StartPairingAsync(Uri server, string deviceName)
    {
        if (await IsPairedAsync())
            throw new DeviceRuntimeException("已經配對（sync\\device.json 存在）：要重新配對先執行 unpair");
        var toastMark = await OnUi(() => View.Toasts.Count);
        await CommandAsync("syncPair", new JObject { ["deviceName"] = deviceName, ["server"] = server.GetLeftPart(UriPartial.Authority) });
        PairingStatus? pairing = null;
        string? fingerprint = null;
        var gotCode = await WaitUntilAsync(() =>
        {
            var status = Sync.Status;
            pairing = status?.Pairing;
            fingerprint = status?.DeviceFingerprint;
            return pairing is { UserCode.Length: > 0 } || pairing?.Error is not null || View.Toasts.Count > toastMark;
        }, TimeSpan.FromSeconds(30));
        var toasts = await OnUi(() => View.Toasts.Skip(toastMark).ToList());
        if (toasts.Count > 0)
            throw new DeviceRuntimeException("配對沒有開始：" + string.Join("；", toasts));
        if (!gotCode || pairing is null)
            throw new DeviceRuntimeException("30 秒內沒有拿到配對碼");
        if (pairing.Error is { } startError)
            throw new DeviceRuntimeException("配對失敗：" + startError);
        return new PairingCode(pairing.UserCode!, fingerprint, pairing.VerificationUri, pairing.VerificationUriComplete, pairing.ExpiresAt);
    }

    /// <summary>等配對提交（device.json 寫好、引擎建好）並連上；配對失敗或逾時丟例外</summary>
    public async Task WaitPairedLiveAsync(TimeSpan timeout)
    {
        string? error = null;
        var paired = await WaitUntilAsync(() =>
        {
            error = Sync.Status?.Pairing?.Error;
            return error is not null || (Sync.IsPaired && Sync.Status?.State == SyncEngineState.Live);
        }, timeout);
        if (error is not null)
            throw new DeviceRuntimeException("配對失敗：" + error);
        if (!paired)
            throw new DeviceRuntimeException(await IsPairedAsync() ? "已配對，但沒有在時限內連上伺服器" : "沒有在時限內完成配對");
    }

    /// <summary>解除配對（網頁的 syncUnpair：停引擎、刪金鑰、pending 檔、sync.db、最後刪 device.json）；回傳原本的裝置 id</summary>
    public async Task<string?> UnpairAsync(TimeSpan timeout)
    {
        if (!await IsPairedAsync())
            throw new DeviceRuntimeException("沒有配對（sync\\device.json 不存在）");
        var deviceId = (await StatusAsync())?.DeviceId;
        await CommandAsync("syncUnpair");
        if (!await WaitUntilAsync(() => !Sync.IsPaired && !File.Exists(Paths.DeviceFile), timeout))
            throw new DeviceRuntimeException($"{timeout.TotalSeconds:0} 秒內沒有解除配對");
        return deviceId;
    }

    /// <summary>
    /// 狀態（status 指令、run 與 session 的結果）：配對與連線、outbox、帳戶、對話與回補；WP4.9 步 8 起另有協商出的功能（features）、
    /// 即時更新（liveRelay：送出、遮蔽過、太大、遮蔽過但沒送出）、複本（replicas）、被拒絕的 CLI 指令（refusedCli）與 BatchAck 的延遲（batchAck）；
    /// 步 13 起另有想開瀏覽器的次數（openedBrowser）與假 Codex app-server（fakeCodex）；步 15 起另有會話收到的呼叫次數（session）
    /// </summary>
    public async Task<JObject> DescribeAsync()
    {
        var status = await StatusAsync();
        var paired = await IsPairedAsync();
        var engine = await EngineAsync();
        var outbox = engine is null ? null : await engine.InvokeAsync(v => v.Outbox.GroupBy(e => e.State).ToDictionary(g => g.Key.ToString(), g => g.Count()));
        // WP4.9 步 18（FF9）：outbox 的 deviceSeq 範圍——伺服器的 events＋rejected_events 要正好是 1..max（強制結束、換版前後 0 遺失 0 重複），
        // 換版那一刻的 max 用來分辨舊伺服器記下的被拒列是 K1 凍結的（在那之前寫的）還是之後寫的（一定是 {}）
        var outboxSeq = engine is null
            ? null
            : await engine.InvokeAsync(v => new JObject
            {
                ["max"] = v.Outbox.Select(e => (long?)e.DeviceSeq).Max(),
                ["doneMax"] = v.Outbox.Where(e => e.IsDone).Select(e => (long?)e.DeviceSeq).Max(),
                ["firstUndone"] = v.Outbox.Where(e => !e.IsDone).Select(e => (long?)e.DeviceSeq).Min(),
            });
        var view = engine is null
            ? null
            : await engine.InvokeAsync(v => new
            {
                Features = v.Features.ToList(),
                v.LiveMasked,
                v.LiveTooLarge,
                Known = v.Store is { ConversationTablesReady: true } store ? store.LoadReplicaMeta().Count : (int?)null,
                Open = v.Replicas.Open.Count,
            });
        var accounts = await OnUi(() =>
        {
            // 中樞最後送給主視窗的帳戶卡（含同步裝飾的 logicalId）
            var cards = View.Recent.LastOrDefault(m => m.Value<string>("type") == "accounts")?["accounts"] as JArray;
            return new JArray(Accounts.Accounts.Select(a =>
            {
                var card = cards?.OfType<JObject>().FirstOrDefault(c => c.Value<string>("id") == a.Id);
                var usage = Accounts.GetUsage(a.Id);
                return new JObject
                {
                    ["id"] = a.Id,
                    ["provider"] = a.Provider.ToString(),
                    ["label"] = a.Label,
                    ["email"] = a.Email,
                    ["logicalId"] = card?["logicalId"]?.DeepClone(),
                    ["usageSource"] = usage?.Source,
                    ["usage"] = usage is null ? null : new JObject(usage.Windows.Select(w => new JProperty(w.Key, w.Percent))),
                };
            }));
        });
        var conversations = await ConversationCountAsync();
        var (attached, progress, pause) = await BackfillAsync();
        var sent = Meter.LiveRelays;
        var ack = Meter.BatchAck();
        return new JObject
        {
            ["conversations"] = conversations,
            ["backfill"] = attached
                ? new JObject
                {
                    ["progress"] = progress is null ? null : new JObject { ["conversationsLeft"] = progress.ConversationsLeft, ["bytesLeft"] = progress.BytesLeft },
                    ["pause"] = pause is null ? null : new JObject { ["reason"] = pause.Reason.ToString(), ["sinceMs"] = pause.SinceMs, ["totalMs"] = pause.TotalMs },
                }
                : null,
            ["paired"] = paired,
            ["state"] = status?.State.ToString(),
            ["stopReason"] = status?.StopReason.ToString(),
            ["detail"] = status?.Detail,
            ["server"] = status?.Server,
            ["deviceId"] = status?.DeviceId,
            ["deviceFingerprint"] = status?.DeviceFingerprint,
            ["hubFingerprint"] = status?.HubFingerprint,
            ["pending"] = status?.Pending,
            ["lastSyncAt"] = status?.LastSyncAt,
            ["outbox"] = outbox is null ? null : JObject.FromObject(outbox),
            ["outboxSeq"] = outboxSeq,
            ["accounts"] = accounts,
            // 這條連線協商出的功能（沒有連線時是空的；未配對是 null）
            ["features"] = view is null ? null : new JArray(view.Features),
            // sent：交給連線送出的（只在引擎經 TransportMeter 時有）；masked：做了遮蔽與序列化的；tooLarge：太大不送的；
            // dropped：遮蔽過但沒有送出的（太大、不合格、送出前不再有人看或沒有 live1）
            ["liveRelay"] = view is null
                ? null
                : new JObject
                {
                    ["sent"] = sent,
                    ["masked"] = view.LiveMasked,
                    ["tooLarge"] = view.LiveTooLarge,
                    ["dropped"] = Math.Max(0, view.LiveMasked - sent),
                },
            ["replicas"] = view is null ? null : new JObject { ["known"] = view.Known, ["open"] = view.Open },
            ["refusedCli"] = RefusedCli,
            // WP4.9 步 13：帳戶管理員想開瀏覽器的次數（一律不開）與假 Codex app-server（沒有 --fake-codex 是 null）
            ["openedBrowser"] = OpenedBrowser,
            ["fakeCodex"] = DescribeFakeCodex(),
            ["ephemeralKey"] = UsesEphemeralKey,
            // WP4.9 步 15：會話收到的呼叫次數（FF3：每個遠端動作只到假會話一次）
            ["session"] = SessionCalls.ToJson(),
            ["batchAck"] = new JObject
            {
                ["count"] = ack.Count,
                ["p50Ms"] = ack.P50,
                ["p95Ms"] = ack.P95,
                ["maxMs"] = ack.Max,
                ["engines"] = Meter.Engines,
            },
        };
    }
}
