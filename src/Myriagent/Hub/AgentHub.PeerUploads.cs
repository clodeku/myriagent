using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Media;
using Newtonsoft.Json.Linq;

namespace Myriagent.Hub;

// 桌面遠端操作的附件（sync-protocol 14.7、web-protocol 的 peerUpload）：網頁對可以操作的副本附加檔案時，以 postWithFiles 送
// peerUpload {uploadId, conversationId}（WebView2 給本機路徑；剪貼簿的圖片沒有路徑，改帶 dataUrl），這裡在背景以裝置的存取權杖上傳到伺服器
// （IPeerUploader，PUT /api/v1/device/uploads/{uploadId}），結果 peerUpload {uploadId, status, json} 只回給那個檢視；
// 網頁之後照瀏覽器版送 addUploaded（經 PeerCmd）。peerUploadCancel {uploadId} 取消還在上傳的。只在 UI 執行緒改狀態
internal sealed partial class AgentHub
{
    /// <summary>網頁送來的剪貼簿圖片（dataUrl）最大多少（同本機的貼上限制）</summary>
    internal const long MaxPeerPasteBytes = 30L * 1024 * 1024;

    /// <summary>還在上傳的（uploadId → 取消）</summary>
    private readonly Dictionary<string, CancellationTokenSource> peerUploads = new(StringComparer.Ordinal);

    /// <summary>測試接縫：取代目前的上傳者</summary>
    internal IPeerUploader? PeerUploaderForTest { get; set; }

    /// <summary>HandleCommand 開頭（擋副本之前）：peerUpload／peerUploadCancel 在這裡處理，回 true</summary>
    private bool TryHandlePeerUpload(IAgentView view, JObject command, IReadOnlyList<string>? additionalPaths)
    {
        var cmd = command.Value<string>("cmd");
        if (cmd == "peerUploadCancel")
        {
            if (command.Value<string>("uploadId") is { } cancelId && peerUploads.TryGetValue(cancelId, out var cancelling))
                cancelling.Cancel();
            return true;
        }
        if (cmd != "peerUpload")
            return false;

        var uploadId = command.Value<string>("uploadId");
        if (!UploadIdRules.IsValid(uploadId))
            return true;
        var conversationId = command.Value<string>("conversationId");
        if (conversationId == null || !conversations.TryGetValue(conversationId, out var c) || !c.IsReplica || !peerLive
            || (PeerUploaderForTest ?? CurrentReplicaMediaSource() as IPeerUploader) is not { } uploader || peerUploads.ContainsKey(uploadId!))
        {
            ReplyPeerUpload(view, uploadId!, PeerUploadRules.Unavailable);
            return true;
        }

        var dataUrl = command.Value<string>("dataUrl");
        var path = additionalPaths is { Count: > 0 } ? additionalPaths[0] : null;
        if (PeerUploadRules.CheckSource(path, dataUrl) is { } refused)
        {
            ReplyPeerUpload(view, uploadId!, refused);
            return true;
        }

        var cts = new CancellationTokenSource();
        peerUploads[uploadId!] = cts;
        var viewId = view.Id;
        _ = Task.Run(async () =>
        {
            PeerUploadResult result;
            string? temp = null;
            try
            {
                var source = path;
                if (source == null)
                {
                    temp = await PeerUploadRules.WriteDataUrlAsync(dataUrl!, MaxPeerPasteBytes, cts.Token).ConfigureAwait(false);
                    source = temp;
                }
                result = source == null ? PeerUploadRules.TooLarge : await uploader.UploadAsync(uploadId!, source, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = PeerUploadRules.Cancelled;
            }
            catch (Exception ex)
            {
                Log.Error($"桌面遠端操作的上傳 {uploadId} 失敗", ex);
                result = new PeerUploadResult(0, null);
            }
            finally
            {
                if (temp != null)
                    PeerUploadRules.TryDelete(temp);
            }

            host.BeginInvoke(() =>
            {
                if (peerUploads.Remove(uploadId!, out var done))
                    done.Dispose();
                if (!disposed && views.TryGetValue(viewId, out var v))
                    ReplyPeerUpload(v, uploadId!, result);
            });
        });
        return true;
    }

    private static void ReplyPeerUpload(IAgentView view, string uploadId, PeerUploadResult result)
        => Post(view, new JObject { ["type"] = "peerUpload", ["uploadId"] = uploadId, ["status"] = result.Status, ["json"] = result.Json });
}

/// <summary>桌面遠端操作的附件規則（sync-protocol 14.7、web-protocol 的 peerUpload）</summary>
internal static class PeerUploadRules
{
    /// <summary>同瀏覽器 E4 的單檔上限</summary>
    public const long MaxUploadBytes = 512L * 1024 * 1024;

    /// <summary>不能上傳（不是副本、桌面遠端操作不能用、同一個 id 已經在上傳）：同伺服器的 503 unavailable</summary>
    public static readonly PeerUploadResult Unavailable = new(503, """{"error":"unavailable"}""");

    /// <summary>太大：同伺服器的 413 too_large</summary>
    public static readonly PeerUploadResult TooLarge = new(413, """{"error":"too_large"}""");

    /// <summary>沒有本機路徑也沒有 dataUrl（網頁照這個改帶 dataUrl 重送）</summary>
    public static readonly PeerUploadResult NoPath = new(400, """{"error":"no_path"}""");

    /// <summary>路徑不是本機磁碟上的檔、讀不到</summary>
    public static readonly PeerUploadResult NotFound = new(404, """{"error":"not_found"}""");

    /// <summary>取消（網頁按了晶片的 ×、關掉分頁）：同中止的 XHR，狀態 0</summary>
    public static readonly PeerUploadResult Cancelled = new(0, null);

    /// <summary>
    /// 來源合不合格（在 UI 執行緒，碰檔案系統的只有本機磁碟上的路徑）：有路徑時要是本機磁碟（不認 UNC，檢查存在就會對外送 SMB 雜湊）、
    /// 存在、≤512 MB；沒有路徑時要有 <c>data:</c> 開頭的 dataUrl。合格回 null
    /// </summary>
    public static PeerUploadResult? CheckSource(string? path, string? dataUrl)
    {
        if (path == null)
            return dataUrl != null && dataUrl.StartsWith("data:", StringComparison.Ordinal) && dataUrl.Contains(";base64,", StringComparison.Ordinal) ? null : NoPath;
        if (!AgentAttachmentRules.IsLocalDrivePath(path))
            return NotFound;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return NotFound;
            return info.Length > MaxUploadBytes ? TooLarge : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return NotFound;
        }
    }

    /// <summary>把 dataUrl 解碼寫進暫存檔（背景執行緒）；超過 <paramref name="maxBytes"/> 或不是 base64 回 null</summary>
    public static async Task<string?> WriteDataUrlAsync(string dataUrl, long maxBytes, CancellationToken ct)
    {
        var comma = dataUrl.IndexOf(";base64,", StringComparison.Ordinal);
        if (comma < 0)
            return null;
        var payload = dataUrl.AsMemory(comma + 8);
        if ((long)payload.Length * 3 / 4 > maxBytes + 3)
            return null;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(payload.ToString());
        }
        catch (FormatException)
        {
            return null;
        }
        if (bytes.LongLength > maxBytes)
            return null;
        var directory = Path.Combine(Path.GetTempPath(), "Myriagent", "peer-uploads");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".bin");
        await File.WriteAllBytesAsync(file, bytes, ct).ConfigureAwait(false);
        return file;
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"刪不掉上傳用的暫存檔：{ex.Message}");
        }
    }
}
