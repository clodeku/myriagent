using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.RemoteExec;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 代理經 myriagent-remote 在電腦之間傳檔案（2026-11-15 使用者要求「也要讓對話框 agent 可以直接上傳或下載檔案到另一台電腦」）：
// - push：這台的檔或資料夾 → 上傳到伺服器（E4，同電腦狀態面板的上傳）→ 型錄的 devicePut 交給那台 → 等那台的電腦狀態回報存好（xfers 的 saved）。
// - pull：那台的檔（deviceFetch → fileRef）或資料夾（deviceTree → deviceFetchMany → fileRefs）→ 以電腦狀態面板的下載（PeerXferJob）存進這台的資料夾。
// 那台照舊驗信封、看「可以操作」的政策與閘門（同在面板上傳檔案）。整個流程在背景執行緒，碰到中樞狀態的一律經 UiAsync 回 UI 執行緒
internal sealed partial class AgentHub
{
    /// <summary>進行中的 push、pull（app 自我更新不自動重啟：FileTransferActive）</summary>
    private int cliFileOps;

    /// <summary>pull 的下載在等結果（transferId → 結果；ReplyPeerXfer 交給它，不送給檢視）</summary>
    private readonly Dictionary<string, TaskCompletionSource<(string State, string? Path, string? Error)>> cliXferWaiters = new(StringComparer.Ordinal);

    /// <summary>push 在等那台回報存好：期間這台算「有人在看電腦狀態」，伺服器才把其他電腦的狀態（含 xfers）轉過來</summary>
    private int cliStatsDemand;

    /// <summary>有沒有 myriagent-remote 的 push、pull 在進行（任何執行緒讀）</summary>
    internal bool CliFileTransferActive => Volatile.Read(ref cliFileOps) > 0;

    /// <summary>myriagent-remote 的 push、pull（RemoteExecService 從背景執行緒呼叫）：回一行的內容（{t:"files"} 或 {t:"error"}）；<paramref name="status"/> 送進度給 myriagent-remote</summary>
    internal async Task<JObject> HandleFilesCli(string callerId, RemoteExecPipeRequest request, RemoteExecDirectory directory, Action<string> status, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(status);
        var (device, isSelf, deviceName, problem) = ResolveMessageDevice(directory, request.Device!);
        if (problem != null)
            return CliError(RemoteExecPipeRules.ErrorUnknownDevice, problem);
        if (isSelf || device is null)
            return CliError(RemoteExecPipeRules.ErrorInvalid, "這台的檔案直接用一般的檔案操作就好（push、pull 是和其他電腦之間傳檔案）");
        if (!device.Online)
            return CliError(RemoteExecPipeRules.ErrorOffline, $"「{deviceName}」目前不在線上，沒有傳檔案");
        if (!await UiAsync(() => peerSink != null && PeerLive))
            return CliError(RemoteExecPipeRules.ErrorOffline, "這台目前沒有連上伺服器（或伺服器還不支援跨電腦操作），沒有傳檔案");

        Interlocked.Increment(ref cliFileOps);
        try
        {
            Log.Info($"對話 {callerId} 經 myriagent-remote {request.Op}「{deviceName}」：{string.Join("、", request.Paths ?? [])} → {request.To}");
            var outcomes = request.Op == RemoteExecPipeRules.OpPush
                ? await PushFilesAsync(device, request, status, ct)
                : await PullFilesAsync(device, request, status, ct);
            if (outcomes.Problem != null)
                return CliError(outcomes.Code ?? RemoteExecPipeRules.ErrorInvalid, outcomes.Problem);
            var items = new JArray(outcomes.Items.Select(o => new JObject { ["source"] = o.Source, ["saved"] = o.Saved, ["error"] = o.Error }));
            return new JObject { ["t"] = "files", ["device"] = deviceName, ["ok"] = outcomes.Items.All(o => o.Error is null), ["items"] = items };
        }
        catch (OperationCanceledException)
        {
            return CliError(RemoteExecPipeRules.ErrorUnknown, "已停止（myriagent-remote 中途結束）");
        }
        finally
        {
            Interlocked.Decrement(ref cliFileOps);
        }
    }

    private sealed record CliFilesResult(List<CliFileOutcome> Items, string? Problem = null, string? Code = null);

    // ───────────────────────────── push ─────────────────────────────

    private async Task<CliFilesResult> PushFilesAsync(RemoteExecDevice device, RemoteExecPipeRequest request, Action<string> status, CancellationToken ct)
    {
        var (files, problem) = await Task.Run(() => CliFileRules.ExpandLocal(request.Paths!), ct);
        if (files is null)
            return new CliFilesResult([], problem);
        if (await UiAsync(() => PeerUploaderForTest ?? CurrentReplicaMediaSource() as IPeerUploader) is not { } uploader)
            return new CliFilesResult([], "這台目前沒有連上伺服器，沒有傳檔案", RemoteExecPipeRules.ErrorOffline);

        await UiAsync(() => SetCliStatsDemand(+1));
        var items = new List<CliFileOutcome>();
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = files[i];
                var shown = f.Subfolder.Length == 0 ? f.Name : f.Subfolder + "\\" + f.Name;
                status($"上傳 {i + 1}/{files.Count}：{shown}（{CliFileRules.Size(f.Size)}）");
                items.Add(await PushOneAsync(device, request.To!, f, uploader, status, ct));
            }
        }
        finally
        {
            await UiAsync(() => SetCliStatsDemand(-1));
        }

        return new CliFilesResult(items);
    }

    private async Task<CliFileOutcome> PushOneAsync(RemoteExecDevice device, string folder, CliPushFile f, IPeerUploader uploader, Action<string> status, CancellationToken ct)
    {
        var uploadId = SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var uploaded = await uploader.UploadAsync(uploadId, f.Path, null, ct);
        var (entry, problem) = CliFileRules.UploadEntry(uploaded.Status, uploaded.Json, uploadId, f.Name);
        if (entry is null)
            return new CliFileOutcome(f.Path, null, problem);

        var put = new JObject
        {
            ["cmd"] = RemoteCommandCatalog.DevicePut,
            ["deviceId"] = device.Id,
            ["requestId"] = "cp-" + uploadId,
            ["folder"] = folder,
            ["uploads"] = new JArray(entry),
        };
        if (f.Subfolder.Length > 0)
            put[RemoteCommandCatalog.DevicePutSubfolder] = f.Subfolder;
        var replies = await PeerExchangeAsync(put, m => m.Value<string>("type") == "commandResult", TimeSpan.FromMilliseconds(PeerHubRules.PendingMs));
        if (replies?.LastOrDefault() is not { } result)
            return new CliFileOutcome(f.Path, null, $"等不到「{device.Name}」的結果，不知道檔案有沒有收下");
        if (result.Value<string>("state") != RemoteResultStates.Done)
            return new CliFileOutcome(f.Path, null, result.Value<string>("message") ?? $"「{device.Name}」拒絕了（{result.Value<string>("code") ?? "沒有原因"}；那台要開「可以操作」）");

        // 那台接下了：在背景從伺服器下載、存檔，進度與結果在它的電腦狀態（xfers）
        var lastDone = -1L;
        var lastChange = DateTime.UtcNow;
        var lastReport = DateTime.UtcNow;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var x = await UiAsync(() => RemoteXfer(device.Id, uploadId));
            if (x is { State: "saved" })
            {
                var name = x.Saved ?? x.Name;
                return new CliFileOutcome(f.Path, f.Subfolder.Length == 0 ? folder.TrimEnd('\\') + "\\" + name : folder.TrimEnd('\\') + "\\" + f.Subfolder + "\\" + name, null);
            }

            if (x is { State: "failed" })
                return new CliFileOutcome(f.Path, null, $"「{device.Name}」存檔失敗（下載或寫入出錯）");
            var done = x?.Done ?? 0;
            if (done != lastDone)
            {
                lastDone = done;
                lastChange = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - lastChange > CliFileRules.SaveStallLimit)
            {
                return new CliFileOutcome(f.Path, null, $"「{device.Name}」{(x is null ? "一直沒有回報接收的進度" : "存檔的進度停住了")}，不知道檔案有沒有存好");
            }

            if (DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(15))
            {
                lastReport = DateTime.UtcNow;
                status($"「{device.Name}」接收中：{CliFileRules.Size(done)}／{CliFileRules.Size(f.Size)}");
            }

            await Task.Delay(1000, ct);
        }
    }

    /// <summary>其他電腦最近一次回報的這個上傳的接收狀態（UI 執行緒）</summary>
    private XferStats? RemoteXfer(string deviceId, string uploadId)
        => remoteStats.FirstOrDefault(e => e.DeviceId == deviceId)?.Stats.Xfers?.FirstOrDefault(x => x.Id == uploadId);

    /// <summary>push 等結果期間算「有人在看電腦狀態」（UI 執行緒）</summary>
    private bool SetCliStatsDemand(int delta)
    {
        var before = monitorViews.Count > 0 || cliStatsDemand > 0;
        cliStatsDemand = Math.Max(0, cliStatsDemand + delta);
        var now = monitorViews.Count > 0 || cliStatsDemand > 0;
        if (now != before)
            sync?.OnMonitorWatchChanged(now);
        return now;
    }

    // ───────────────────────────── pull ─────────────────────────────

    private async Task<CliFilesResult> PullFilesAsync(RemoteExecDevice device, RemoteExecPipeRequest request, Action<string> status, CancellationToken ct)
    {
        var to = request.To!.Trim();
        if (!Path.IsPathFullyQualified(to) || !AgentAttachmentRules.IsLocalDrivePath(to))
            return new CliFilesResult([], $"存檔的資料夾「{to}」要是這台本機磁碟上的完整路徑");
        try
        {
            Directory.CreateDirectory(to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CliFilesResult([], $"建不了存檔的資料夾「{to}」：{ex.Message}");
        }

        var items = new List<CliFileOutcome>();
        var seq = 0;
        foreach (var path in request.Paths!)
        {
            ct.ThrowIfCancellationRequested();
            status($"向「{device.Name}」要 {path}");
            var rid = "cf-" + ++seq;
            var fetched = await PeerExchangeAsync(new JObject { ["cmd"] = RemoteCommandCatalog.DeviceFetch, ["deviceId"] = device.Id, ["requestId"] = rid, ["path"] = path },
                m => IsFailedResult(m) || m.Value<string>("type") == "fileRef" && m.Value<string>("requestId") == rid, TimeSpan.FromMilliseconds(PeerHubRules.PendingMs));
            var reply = fetched?.LastOrDefault();
            if (reply is null)
            {
                items.Add(new CliFileOutcome(path, null, $"等不到「{device.Name}」的結果"));
                continue;
            }

            if (IsFailedResult(reply))
            {
                items.Add(new CliFileOutcome(path, null, reply.Value<string>("message") ?? $"「{device.Name}」拒絕了（{reply.Value<string>("code") ?? "沒有原因"}；那台要開「可以操作」）"));
                continue;
            }

            if (reply.Value<string>("url") is { Length: > 0 } url)
            {
                status($"下載 {reply.Value<string>("name")}（{CliFileRules.Size(reply.Value<long?>("size") ?? 0)}）");
                items.Add(await PullOneAsync(path, url, reply.Value<string>("name"), to, string.Empty, status, ct));
                continue;
            }

            // 不是檔（或讀不了）：試著當資料夾整個下載
            var folder = await PullFolderAsync(device, path, to, status, items, ct);
            if (folder != null)
                items.Add(new CliFileOutcome(path, null, reply.Value<string>("error") is { Length: > 0 } fileError && folder.Contains("不是資料夾", StringComparison.Ordinal) ? fileError : folder));
        }

        return new CliFilesResult(items);
    }

    /// <summary>整個資料夾：列檔、每 200 個要一次下載參照、逐一下載（結果加進 <paramref name="items"/>）；列不出來回原因</summary>
    private async Task<string?> PullFolderAsync(RemoteExecDevice device, string path, string to, Action<string> status, List<CliFileOutcome> items, CancellationToken ct)
    {
        var rid = "ct-" + SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var listed = await PeerExchangeAsync(new JObject { ["cmd"] = RemoteCommandCatalog.DeviceTree, ["deviceId"] = device.Id, ["requestId"] = rid, ["path"] = path },
            m => IsFailedResult(m) || m.Value<string>("type") == "fileTree" && m.Value<string>("requestId") == rid, TimeSpan.FromMilliseconds(PeerHubRules.PendingMs));
        if (listed?.LastOrDefault() is not { } tree || IsFailedResult(tree))
            return listed?.LastOrDefault()?.Value<string>("message") ?? $"「{device.Name}」沒有回應";
        if (tree.Value<string>("root") is not { Length: > 0 } root)
            return tree.Value<string>("error") is { Length: > 0 } error ? error + "（不是資料夾，或讀不了）" : "不是資料夾，或讀不了";
        var name = tree.Value<string>("name") is { Length: > 0 } n ? n : Path.GetFileName(root.TrimEnd('\\'));
        var files = (tree["files"] as JArray ?? []).OfType<JObject>().Select(f => f.Value<string>("path")).OfType<string>().ToList();
        if (files.Count > CliFileRules.MaxFiles)
            return $"資料夾裡有 {files.Count} 個檔，一次最多 {CliFileRules.MaxFiles} 個";
        if (files.Count == 0)
        {
            Directory.CreateDirectory(Path.Combine(to, name));
            items.Add(new CliFileOutcome(path, Path.Combine(to, name), null));
            return null;
        }

        status($"資料夾「{name}」有 {files.Count} 個檔");
        var index = 0;
        foreach (var batch in files.Chunk(200))
        {
            ct.ThrowIfCancellationRequested();
            var mid = "cm-" + SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var got = await PeerExchangeAsync(new JObject
            {
                ["cmd"] = RemoteCommandCatalog.DeviceFetchMany, ["deviceId"] = device.Id, ["requestId"] = mid, ["root"] = root,
                ["pathsJson"] = new JArray(batch).ToString(Newtonsoft.Json.Formatting.None),
            }, m => IsFailedResult(m) || m.Value<string>("type") == "fileRefs" && m.Value<string>("requestId") == mid, TimeSpan.FromMilliseconds(PeerHubRules.PendingMs));
            if (got?.LastOrDefault() is not { } refs || IsFailedResult(refs))
            {
                var why = got?.LastOrDefault()?.Value<string>("message") ?? $"「{device.Name}」沒有回應";
                items.AddRange(batch.Select(p => new CliFileOutcome(root.TrimEnd('\\') + "\\" + p, null, why)));
                continue;
            }

            foreach (var r in (refs["refs"] as JArray ?? []).OfType<JObject>())
            {
                var rel = r.Value<string>("path") ?? string.Empty;
                var source = root.TrimEnd('\\') + "\\" + rel;
                status($"下載 {++index}/{files.Count}：{name}\\{rel}");
                items.Add(r.Value<string>("url") is { Length: > 0 } url
                    ? await PullOneAsync(source, url, r.Value<string>("name"), to, CliFileRules.PullSubfolder(name, rel), status, ct)
                    : new CliFileOutcome(source, null, r.Value<string>("error") ?? "那台不給這個檔"));
            }
        }

        return null;
    }

    /// <summary>一個檔：以電腦狀態面板的下載（PeerXferJob）存進 <paramref name="to"/>（加上子資料夾），等它結束</summary>
    private async Task<CliFileOutcome> PullOneAsync(string source, string url, string? name, string to, string subfolder, Action<string> status, CancellationToken ct)
    {
        if (ReplicaMediaRules.KeyOfRelativeUrl(url) is not { Purpose: MediaPurposes.File } file)
            return new CliFileOutcome(source, null, "那台給的下載連結不對");
        var transferId = SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var done = new TaskCompletionSource<(string State, string? Path, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = await UiAsync(() =>
        {
            if (disposed || PeerXferSource() is null)
                return "這台目前沒有連上伺服器";
            if (peerXfers.Count >= MaxPeerXfers)
                return "同時下載的檔案太多，請稍候再試";
            var target = subfolder.Length == 0 ? to : Path.Combine(to, subfolder);
            cliXferWaiters[transferId] = done;
            RunPeerXfer(new PeerXferJob(transferId, file.Key, name, to, target, CreateBase: false, DeviceTransferRules.PartPath(target, transferId)), resume: false);
            return null;
        });
        if (started != null)
            return new CliFileOutcome(source, null, started);

        using var cancel = ct.Register(() => host.BeginInvoke(() =>
        {
            if (peerXfers.TryGetValue(transferId, out var job))
                job.Cts?.Cancel();
        }));
        var waited = 0;
        while (await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(15), CancellationToken.None)) != done.Task)
        {
            waited += 15;
            status($"下載中（已經 {waited} 秒）：{name}");
        }

        var (state, path, error) = await done.Task;
        return state == "done" ? new CliFileOutcome(source, path, null) : new CliFileOutcome(source, null, state == "cancelled" ? "已停止" : error ?? "下載失敗");
    }

    /// <summary>結果是 commandResult 而且不是 done（被拒、結果不明）</summary>
    private static bool IsFailedResult(JObject m) => m.Value<string>("type") == "commandResult" && m.Value<string>("state") != RemoteResultStates.Done;

    /// <summary>ReplyPeerXfer 開頭：是 pull 在等的下載就交給它（結束的狀態才算），回 true（不送給檢視）</summary>
    private bool TryDeliverCliXfer(string transferId, string state, string? path, string? error)
    {
        if (!cliXferWaiters.TryGetValue(transferId, out var waiter))
            return false;
        if (state is "done" or "failed" or "cancelled")
        {
            cliXferWaiters.Remove(transferId);
            waiter.TrySetResult((state, path, error));
        }

        return true;
    }
}
