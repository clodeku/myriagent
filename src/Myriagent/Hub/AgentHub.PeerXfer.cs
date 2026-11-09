using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Media;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 電腦狀態面板的檔案傳輸在發起的這台（sync-protocol 14.15）：網頁對其他電腦送 deviceFetch 拿到 fileRef（/f/{鍵}，
// AgentHub.Peer.cs 登記過）之後送 peerXferSave {transferId, url, name, folder}，這裡在背景以 E11（存取權杖、每段 ≤8 MB 的 Range）寫進這台的資料夾
// （預設「下載」；同名自動改名、標成從網際網路下載），進度以 xferProgress 回那個檢視。peerXferCancel 取消、peerXferReveal 在檔案總管顯示、
// xferPickFolder 開這台的資料夾選擇視窗（回 xferFolder）。只在 UI 執行緒改狀態
internal sealed partial class AgentHub
{
    /// <summary>同時最多幾個下載（網頁一次送 3 個；多的直接回失敗）</summary>
    internal const int MaxPeerXfers = 6;

    /// <summary>進度最密多久回一次</summary>
    private const long PeerXferProgressMs = 300;

    /// <summary>還在下載的（transferId → 取消）</summary>
    private readonly Dictionary<string, CancellationTokenSource> peerXfers = new(StringComparer.Ordinal);

    /// <summary>下載好的（transferId → 完整路徑；只給 peerXferReveal 用，最多留 500 筆）</summary>
    private readonly Dictionary<string, string> peerXferSaved = new(StringComparer.Ordinal);

    /// <summary>HandleCommand 開頭（擋副本之前）：檔案傳輸在這台的四個指令在這裡處理，回 true</summary>
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
                if (command.Value<string>("transferId") is { } cancelId && peerXfers.TryGetValue(cancelId, out var cancelling))
                    cancelling.Cancel();
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
            || (PeerFileSourceForTest ?? CurrentReplicaMediaSource() as IPeerFileSource) is not { } source)
        {
            ReplyPeerXfer(view.Id, transferId!, "failed", 0, 0, null, "連結已過期或沒有連上伺服器，請重新整理清單再下載");
            return;
        }

        if (folder != null && !AgentAttachmentRules.IsLocalDrivePath(folder))
        {
            ReplyPeerXfer(view.Id, transferId!, "failed", 0, 0, null, "存檔的資料夾要是這台的本機磁碟");
            return;
        }

        if (peerXfers.ContainsKey(transferId!) || peerXfers.Count >= MaxPeerXfers)
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
        var createBase = folder == null;
        var cts = new CancellationTokenSource();
        peerXfers[transferId!] = cts;
        var viewId = view.Id;
        var id = transferId!;
        _ = Task.Run(async () =>
        {
            var lastReport = 0L;
            string? saved = null;
            string? error = null;
            var state = "failed";
            long done = 0, total = 0;
            var part = DeviceTransferRules.PartPath(target, id);
            try
            {
                if (!createBase && !Directory.Exists(baseFolder))
                    throw new DirectoryNotFoundException("存檔的資料夾不存在");
                Directory.CreateDirectory(target);

                PeerDownload? got;
                await using (var stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                {
                    got = await source.DownloadAsync(file.Key, stream, (d, t) =>
                    {
                        done = d;
                        total = t;
                        var now = Environment.TickCount64;
                        if (now - Interlocked.Read(ref lastReport) < PeerXferProgressMs)
                            return;
                        Interlocked.Exchange(ref lastReport, now);
                        host.BeginInvoke(() => ReplyPeerXfer(viewId, id, "running", d, t, null, null));
                    }, cts.Token).ConfigureAwait(false);
                }

                if (got is null)
                {
                    error = "下載失敗（連不上、那台拒絕或檔案在下載途中變了），請再試一次";
                }
                else
                {
                    total = done = got.Total;
                    var final = DeviceTransferRules.UniquePath(target, got.FileName ?? name, p => File.Exists(p) || Directory.Exists(p))
                                ?? throw new IOException("資料夾裡同名的檔太多");
                    File.Move(part, final, overwrite: false);
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
                if (saved == null)
                    PeerUploadRules.TryDelete(part);
            }

            host.BeginInvoke(() =>
            {
                if (peerXfers.Remove(id, out var finished))
                    finished.Dispose();
                if (disposed)
                    return;
                if (saved != null)
                {
                    if (peerXferSaved.Count >= 500)
                        peerXferSaved.Clear();
                    peerXferSaved[id] = saved;
                }

                ReplyPeerXfer(viewId, id, state, done, total, saved, error);
            });
        });
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
