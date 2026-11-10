using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Media;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 電腦狀態面板的檔案傳輸在發起的這台（sync-protocol 14.15）：網頁對其他電腦送 deviceFetch 拿到 fileRef（/f/{鍵}，
// AgentHub.Peer.cs 登記過）之後送 peerXferSave {transferId, url, name, folder}，這裡在背景以 E11（存取權杖、每段 ≤8 MB 的 Range）寫進這台的資料夾
// （預設「下載」；同名自動改名、標成從網際網路下載），進度以 xferProgress 回那個檢視。peerXferCancel 中止、peerXferPause／peerXferResume 暫停與接著下載
// （保留寫到一半的暫存檔，繼續時從那裡往下要）、peerXferReveal 在檔案總管顯示、
// xferPickFolder 開這台的資料夾選擇視窗（回 xferFolder）。只在 UI 執行緒改狀態
internal sealed partial class AgentHub
{
    /// <summary>同時最多幾個下載（網頁一次送 3 個；多的直接回失敗）</summary>
    internal const int MaxPeerXfers = 6;

    /// <summary>進度最密多久回一次</summary>
    private const long PeerXferProgressMs = 300;

    /// <summary>還在下載的（transferId → 下載）</summary>
    private readonly Dictionary<string, PeerXferJob> peerXfers = new(StringComparer.Ordinal);

    /// <summary>暫停的下載（transferId → 下載；暫存檔留著，peerXferResume 接著下載）。不算進同時下載的上限</summary>
    private readonly Dictionary<string, PeerXferJob> pausedXfers = new(StringComparer.Ordinal);

    /// <summary>下載好的（transferId → 完整路徑；只給 peerXferReveal 用，最多留 500 筆）</summary>
    private readonly Dictionary<string, string> peerXferSaved = new(StringComparer.Ordinal);

    /// <summary>HandleCommand 開頭（擋副本之前）：檔案傳輸在這台的指令在這裡處理，回 true</summary>
    private bool TryHandlePeerXfer(IAgentView view, JObject command)
    {
        switch (command.Value<string>("cmd"))
        {
            case "xferPickFolder":
            {
                var initial = command.Value<string>("initial");
                var start = initial != null && AgentAttachmentRules.IsLocalDrivePath(initial) && Directory.Exists(initial) ? initial : PeerDownloadsForTest ?? PeerFileRules.DownloadsFolder();
                var picked = host.PickFolder(view, start);
                Post(view, new JObject
                {
                    ["type"] = "xferFolder",
                    ["requestId"] = command.Value<string>("requestId"),
                    ["path"] = string.IsNullOrWhiteSpace(picked) ? JValue.CreateNull() : picked,
                });
                return true;
            }
            case "peerXferCancel":
                CancelPeerXfer(view, command.Value<string>("transferId"));
                return true;
            case "peerXferPause":
                PausePeerXfer(command.Value<string>("transferId"));
                return true;
            case "peerXferResume":
                ResumePeerXfer(view, command.Value<string>("transferId"));
                return true;
            case "peerXferReveal":
                if (command.Value<string>("transferId") is { } revealId && peerXferSaved.TryGetValue(revealId, out var revealPath) && File.Exists(revealPath))
                    host.RevealInExplorer(revealPath);
                return true;
            case "peerXferSave":
                StartPeerXfer(view, command);
                return true;
            default:
                return false;
        }
    }

    private void StartPeerXfer(IAgentView view, JObject command)
    {
        var transferId = command.Value<string>("transferId");
        if (!UploadIdRules.IsValid(transferId))
            return;
        var parsed = ReplicaMediaRules.KeyOfRelativeUrl(command.Value<string>("url"));
        var folder = command.Value<string>("folder") is { Length: > 0 } chosen ? chosen : null;
        if (!peerLive || parsed is not { Purpose: MediaPurposes.File } file || replicaMediaUrls is not { } urls || !urls.IsAllowed(file.Key, file.Purpose)
            || PeerXferSource() is null)
        {
            ReplyPeerXfer(view.Id, transferId!, "failed", 0, 0, null, "連結已過期或沒有連上伺服器，請重新整理清單再下載");
            return;
        }

        if (folder != null && !AgentAttachmentRules.IsLocalDrivePath(folder))
        {
            ReplyPeerXfer(view.Id, transferId!, "failed", 0, 0, null, "存檔的資料夾要是這台的本機磁碟");
            return;
        }

        if (peerXfers.ContainsKey(transferId!) || pausedXfers.ContainsKey(transferId!) || peerXfers.Count >= MaxPeerXfers)
        {
            ReplyPeerXfer(view.Id, transferId!, "failed", 0, 0, null, "同時下載的檔案太多，請稍候");
            return;
        }

        var name = command.Value<string>("name");
        // 整個資料夾的下載（2026-11-09）：subfolder 是那個資料夾名稱加上檔案所在的相對資料夾，照結構建在存檔的資料夾底下
        var subfolder = DeviceFolderRules.CleanSubfolder(command["subfolder"] is JValue { Type: JTokenType.String, Value: string sub } ? sub : null);
        if (subfolder is null)
        {
            ReplyPeerXfer(view.Id, transferId!, "failed", 0, 0, null, "子資料夾的路徑不合格");
            return;
        }

        var baseFolder = folder ?? PeerDownloadsForTest ?? PeerFileRules.DownloadsFolder();
        var target = subfolder.Length == 0 ? baseFolder : Path.Combine(baseFolder, subfolder);
        var job = new PeerXferJob(transferId!, file.Key, name, baseFolder, target, CreateBase: folder == null, DeviceTransferRules.PartPath(target, transferId!))
        {
            ViewId = view.Id,
        };
        RunPeerXfer(job, resume: false);
    }

    /// <summary>下載用的來源（測試換成假的）；沒有連上伺服器時 null</summary>
    private IPeerFileSource? PeerXferSource() => PeerFileSourceForTest ?? CurrentReplicaMediaSource() as IPeerFileSource;

    /// <summary>
    /// 一個下載（電腦狀態面板，2026-11-10 起可以暫停、繼續）：暫停時保留寫到一半的暫存檔（<see cref="Part"/>）與 <see cref="Cursor"/>，
    /// 繼續時從那裡接著要。UI 執行緒建立與讀寫 <see cref="ViewId"/>、<see cref="Cts"/>、<see cref="PauseRequested"/>；<see cref="Cursor"/> 只在下載的執行緒寫
    /// </summary>
    private sealed record PeerXferJob(string Id, string Key, string? Name, string BaseFolder, string Target, bool CreateBase, string Part)
    {
        public string ViewId { get; set; } = string.Empty;

        public PeerDownloadCursor Cursor { get; } = new();

        public CancellationTokenSource? Cts { get; set; }

        /// <summary>這次取消是暫停（保留暫存檔）而不是中止</summary>
        public bool PauseRequested { get; set; }
    }

    /// <summary>開始或接著下載（UI 執行緒）：在背景寫進暫存檔，下載完改成正式的檔名；暫停時留在 <see cref="pausedXfers"/></summary>
    private void RunPeerXfer(PeerXferJob job, bool resume)
    {
        if (PeerXferSource() is not { } source)
        {
            ReplyPeerXfer(job.ViewId, job.Id, resume ? "paused" : "failed", job.Cursor.Offset, job.Cursor.Total ?? 0, null,
                "沒有連上伺服器，連上之後再繼續");
            if (resume)
                pausedXfers[job.Id] = job;
            return;
        }

        var cts = new CancellationTokenSource();
        job.Cts = cts;
        job.PauseRequested = false;
        peerXfers[job.Id] = job;
        var id = job.Id;
        _ = Task.Run(async () =>
        {
            var lastReport = 0L;
            string? saved = null;
            string? error = null;
            var state = "failed";
            long done = job.Cursor.Offset, total = job.Cursor.Total ?? 0;
            try
            {
                if (!job.CreateBase && !Directory.Exists(job.BaseFolder))
                    throw new DirectoryNotFoundException("存檔的資料夾不存在");
                Directory.CreateDirectory(job.Target);

                PeerDownload? got;
                // 接著下載：暫存檔截到上次確實寫進去的長度（暫停時可能有寫到一半的那一塊），從尾端接著寫
                await using (var stream = resume
                                 ? new FileStream(job.Part, FileMode.Open, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true)
                                 : new FileStream(job.Part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                {
                    if (resume)
                    {
                        stream.SetLength(job.Cursor.Offset);
                        stream.Seek(0, SeekOrigin.End);
                    }
                    got = await source.DownloadAsync(job.Key, stream, (d, t) =>
                    {
                        done = d;
                        total = t;
                        var now = Environment.TickCount64;
                        if (now - Interlocked.Read(ref lastReport) < PeerXferProgressMs)
                            return;
                        Interlocked.Exchange(ref lastReport, now);
                        host.BeginInvoke(() => ReplyPeerXfer(job.ViewId, id, "running", d, t, null, null));
                    }, cts.Token, job.Cursor).ConfigureAwait(false);
                }

                if (got is null)
                {
                    error = resume
                        ? "沒辦法接著下載（連不上、那台拒絕、連結過期或檔案在暫停期間變了），請重新下載"
                        : "下載失敗（連不上、那台拒絕或檔案在下載途中變了），請再試一次";
                }
                else
                {
                    total = done = got.Total;
                    var final = DeviceTransferRules.UniquePath(job.Target, got.FileName ?? job.Name, p => File.Exists(p) || Directory.Exists(p))
                                ?? throw new IOException("資料夾裡同名的檔太多");
                    File.Move(job.Part, final, overwrite: false);
                    MarkOfTheWeb.Mark(final);
                    saved = final;
                    state = "done";
                }
            }
            catch (OperationCanceledException)
            {
                state = "cancelled";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or InvalidOperationException)
            {
                Log.Warn($"下載其他電腦的檔案失敗：{ex.Message}");
                error = "下載失敗：" + ex.Message;
            }
            finally
            {
                // 暫停的留著暫存檔等繼續；其他沒有存成正式檔的刪掉（app 結束時 UI 執行緒的收尾可能不會跑，所以在這裡刪）
                if (saved == null && !(state == "cancelled" && job.PauseRequested))
                    PeerUploadRules.TryDelete(job.Part);
            }

            host.BeginInvoke(() =>
            {
                if (peerXfers.Remove(id, out var finished))
                    finished.Cts?.Dispose();
                job.Cts = null;
                // 暫停：保留暫存檔與游標，等繼續；app 已經在結束就不留
                var paused = state == "cancelled" && job.PauseRequested && !disposed;
                if (paused)
                {
                    pausedXfers[id] = job;
                    state = "paused";
                    done = job.Cursor.Offset;
                    total = job.Cursor.Total ?? total;
                }
                else if (saved == null && job.PauseRequested)
                {
                    PeerUploadRules.TryDelete(job.Part);
                }

                if (disposed)
                    return;
                if (saved != null)
                {
                    if (peerXferSaved.Count >= 500)
                        peerXferSaved.Clear();
                    peerXferSaved[id] = saved;
                }

                ReplyPeerXfer(job.ViewId, id, state, done, total, saved, error);
            });
        });
    }

    /// <summary>peerXferPause：下載中的停下來（保留暫存檔）；已經暫停的、找不到的不理</summary>
    private void PausePeerXfer(string? transferId)
    {
        if (transferId is null || !peerXfers.TryGetValue(transferId, out var job) || job.Cts is not { } cts)
            return;
        job.PauseRequested = true;
        cts.Cancel();
    }

    /// <summary>peerXferResume：暫停的接著下載（進度回這次送指令的檢視）；找不到回 failed（例如 app 重開過）</summary>
    private void ResumePeerXfer(IAgentView view, string? transferId)
    {
        if (transferId is null || !UploadIdRules.IsValid(transferId))
            return;
        if (!pausedXfers.Remove(transferId, out var job))
        {
            if (!peerXfers.ContainsKey(transferId))
                ReplyPeerXfer(view.Id, transferId, "failed", 0, 0, null, "暫停的下載找不到了（app 重新啟動過？），請重新下載");
            return;
        }

        job.ViewId = view.Id;
        if (peerXfers.Count >= MaxPeerXfers)
        {
            pausedXfers[transferId] = job;
            ReplyPeerXfer(view.Id, transferId, "paused", job.Cursor.Offset, job.Cursor.Total ?? 0, null, "同時下載的檔案太多，請稍候再繼續");
            return;
        }

        RunPeerXfer(job, resume: true);
        ReplyPeerXfer(view.Id, transferId, "running", job.Cursor.Offset, job.Cursor.Total ?? 0, null, null);
    }

    /// <summary>peerXferCancel：下載中的中止；暫停的刪掉暫存檔、回 cancelled</summary>
    private void CancelPeerXfer(IAgentView view, string? transferId)
    {
        if (transferId is null)
            return;
        if (peerXfers.TryGetValue(transferId, out var running))
        {
            running.PauseRequested = false;
            running.Cts?.Cancel();
            return;
        }

        if (pausedXfers.Remove(transferId, out var paused))
        {
            PeerUploadRules.TryDelete(paused.Part);
            ReplyPeerXfer(view.Id, transferId, "cancelled", paused.Cursor.Offset, paused.Cursor.Total ?? 0, null, null);
        }
    }

    /// <summary>app 結束：暫停的下載不會再繼續（網頁的清單也不在了），刪掉它們的暫存檔；下載中的取消（暫存檔由各自收尾時刪）</summary>
    private void DisposePeerXfers()
    {
        foreach (var job in pausedXfers.Values)
            PeerUploadRules.TryDelete(job.Part);
        pausedXfers.Clear();
        foreach (var job in peerXfers.Values)
        {
            try
            {
                job.Cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 剛好結束
            }
        }
    }

    /// <summary>進度與結果只回發起的那個檢視（檢視關了就丟掉）</summary>
    private void ReplyPeerXfer(string viewId, string transferId, string state, long done, long total, string? path, string? error)
    {
        if (disposed || !views.TryGetValue(viewId, out var view))
            return;
        Post(view, new JObject
        {
            ["type"] = "xferProgress",
            ["transferId"] = transferId,
            ["state"] = state,
            ["done"] = done,
            ["total"] = total,
            ["path"] = path is null ? JValue.CreateNull() : path,
            ["name"] = path is null ? JValue.CreateNull() : Path.GetFileName(path),
            ["error"] = error is null ? JValue.CreateNull() : error,
        });
    }
}
