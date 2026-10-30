using System.Net.Http.Headers;
using Myriagent.Core;
using Myriagent.Media;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>
/// 項目欄位 blob 的上傳（WP4.7 計畫 D15、D31 桌面那一半；E5）：先 <c>HEAD /api/v1/device/blobs/{sha256}</c>，伺服器已有就不傳；
/// 沒有才 <c>PUT</c>（<c>application/octet-stream</c>、Content-Length 是內容的長度）。結果照 <see cref="ItemBlobRules.AfterHead"/>、
/// <see cref="ItemBlobRules.AfterPut"/>；伺服器的第二道 DLP 拒收（422）時什麼都不留、回 <see cref="BlobUploadOutcome.Rejected"/>。
/// 任何執行緒都可以呼叫（背景）；例外都在這裡記錄、不往外丟（連線的取消除外）
/// </summary>
internal sealed class BlobUploader : IDisposable
{
    private const string OctetStream = "application/octet-stream";

    private readonly DeviceHttpClient http;

    /// <param name="http">自己的 HTTP 用戶端（Dispose 時一起關）</param>
    public BlobUploader(DeviceHttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        this.http = http;
    }

    /// <summary>上傳一個 blob（<paramref name="ct"/> 是連線的取消：取消照丟）</summary>
    public async Task<BlobUploadOutcome> UploadAsync(ConversationBlob blob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (!BlobIdRules.IsBlobId(blob.Sha256) || blob.Content.LongLength is < 1 or > ItemBlobRules.MaxItemBlobBytes)
        {
            Log.Warn($"項目 blob 的雜湊或大小不合格（{blob.Content.LongLength} 位元組）：不上傳（程式錯誤）");
            return BlobUploadOutcome.Rejected;
        }

        var path = ItemBlobRules.BlobPath(blob.Sha256);
        try
        {
            using (var head = await http.SendAsync(() => new HttpRequestMessage(HttpMethod.Head, path), HttpCompletionOption.ResponseHeadersRead, ct)
                       .ConfigureAwait(false))
            {
                if (head is null)
                    return Failed(blob, "沒有可用的存取權杖");
                if (ItemBlobRules.AfterHead((int)head.StatusCode) is { } known)
                    return known == BlobUploadOutcome.Confirmed ? known : Failed(blob, $"HEAD 回 {(int)head.StatusCode}");
            }

            using var put = await http.SendAsync(() => Put(path, blob.Content), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (put is null)
                return Failed(blob, "沒有可用的存取權杖");
            var status = (int)put.StatusCode;
            var outcome = ItemBlobRules.AfterPut(status);
            switch (outcome)
            {
                case BlobUploadOutcome.Confirmed:
                    return outcome;
                case BlobUploadOutcome.Rejected:
                    Log.Warn($"伺服器拒收項目 blob {blob.Sha256[..12]}…（{status} {await ErrorCodeAsync(put, ct).ConfigureAwait(false)}）：這個項目改送截斷版");
                    return outcome;
                default:
                    return Failed(blob, $"PUT 回 {status}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            return Failed(blob, ex.Message);
        }
    }

    public void Dispose() => http.Dispose();

    private static HttpRequestMessage Put(string path, byte[] content)
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue(OctetStream);
        return new HttpRequestMessage(HttpMethod.Put, path) { Content = body };
    }

    /// <summary>錯誤回應的 <c>error</c> 代碼（記錄用，只收短的英數字與底線）</summary>
    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Length <= 1024 && ConversationEventRules.TryParseStrict(text, out var token) && token is JObject o
                && o["error"] is JValue { Type: JTokenType.String, Value: string code } && code.Length <= 32 && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                return code;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // 只是記錄用
        }

        return "?";
    }

    private static BlobUploadOutcome Failed(ConversationBlob blob, string reason)
    {
        Log.Info($"項目 blob {blob.Sha256[..12]}… 這次沒有傳成（{reason}）：之後再試");
        return BlobUploadOutcome.Failed;
    }
}
