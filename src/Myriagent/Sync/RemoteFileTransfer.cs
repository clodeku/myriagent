using System.Net;
using System.Security.Cryptography;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Media;

namespace Myriagent.Sync;

/// <summary>
/// 中樞的遠端檔案指令要同步那一側做的事（WP4.7 計畫步 11，D22、D33、D36；<see cref="IRemoteFileTransfer"/> 的實作）：
/// <list type="bullet">
/// <item>抽屜與 fileFetch 的媒體參照：排進引擎執行緒依序寫 <c>media_refs_local</c>（<c>register</c>，引擎的 <c>RegisterLooseRefsAsync</c>），寫完才回參照，
/// 所以中樞送出的輸出裡的參照一定已經在這台登記過（伺服器轉送來時查得到）。</item>
/// <item>瀏覽器上傳的檔：以裝置的 blob 端點（E6 <c>GET /api/v1/device/blobs/{sha256}</c>）下載，邊收邊算 SHA-256、計長度，
/// 大小與雜湊都對才留下檔案（不對就刪掉寫了一半的檔、丟 <see cref="InvalidDataException"/>）。</item>
/// </list>
/// 引擎在協商出 media1 時建立、掛到對話的盒子上（<see cref="ConversationChangeBox.RemoteFiles"/>），停止時 Dispose（進行中的下載一起取消）。任何執行緒都可以呼叫
/// </summary>
internal sealed class RemoteFileTransfer : IRemoteFileTransfer, IDisposable
{
    private const int CopyBufferBytes = 64 * 1024;

    private readonly Func<string, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<string>>> register;
    private readonly DeviceHttpClient http;
    private readonly AccessTokenHolder tokens;
    private readonly CancellationTokenSource stopping = new();
    private int disposed;

    /// <param name="register">排進引擎執行緒寫 <c>media_refs_local</c>（對話 id 是線上格式、種類、完整路徑）</param>
    /// <param name="http">自己的 HTTP 用戶端（Dispose 時一起關）</param>
    /// <param name="tokens">引擎的權杖保存處（有權杖＝這條連線協商出 media1、Prove 或換新拿到了 cha_）</param>
    public RemoteFileTransfer(Func<string, string, IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<string>>> register, DeviceHttpClient http,
        AccessTokenHolder tokens)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        this.register = register;
        this.http = http;
        this.tokens = tokens;
    }

    /// <summary>還沒停止、手上有 cha_（權杖只在協商出 media1 的連線上保存，連線丟掉時清掉）</summary>
    public bool CanTransfer => Volatile.Read(ref disposed) == 0 && tokens.Current is not null;

    public Task<IReadOnlyList<string>> RegisterRefsAsync(string conversationId, string kind, IReadOnlyList<string> fullPaths, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fullPaths);
        if (!LooseMediaRefRules.IsKind(kind))
            throw new ArgumentException("種類只能是 @list、@file 或 @xfer", nameof(kind));
        if (!LooseMediaRefRules.IsConversationFor(kind, conversationId))
            throw new ArgumentException("對話 id 要是線上格式（@xfer 是空字串）", nameof(conversationId));
        if (Volatile.Read(ref disposed) != 0)
            throw new InvalidOperationException("同步引擎已經停止");
        if (fullPaths.Count == 0)
            return Task.FromResult<IReadOnlyList<string>>([]);
        return register(conversationId, kind, fullPaths, ct);
    }

    public async Task DownloadUploadAsync(RemoteUpload upload, string destinationFile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentException.ThrowIfNullOrEmpty(destinationFile);
        if (!UploadIdRules.IsValid(upload.UploadId) || !BlobIdRules.IsBlobId(upload.Sha256) || upload.Size is < 0 or > AgentAttachmentRules.MaxAttachmentBytes)
            throw new InvalidDataException("上傳的 id、大小或 SHA-256 不合格");
        if (Volatile.Read(ref disposed) != 0)
            throw new InvalidOperationException("同步引擎已經停止");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping.Token);
        var token = linked.Token;
        var path = ItemBlobRules.BlobPath(upload.Sha256);
        using var response = await http.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, path), HttpCompletionOption.ResponseHeadersRead, token)
                                 .ConfigureAwait(false)
                             ?? throw new IOException("沒有可用的存取權杖（同步沒有連線或伺服器不支援媒體）");
        if (response.StatusCode != HttpStatusCode.OK)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidDataException("伺服器上找不到這個上傳的檔（可能已經過期）");
            throw new IOException($"伺服器回 {(int)response.StatusCode}");
        }

        if (response.Content.Headers.ContentLength is { } declared && declared != upload.Size)
            throw new InvalidDataException($"伺服器的檔案大小（{declared}）和上傳時的（{upload.Size}）不一樣");

        var completed = false;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var target = new FileStream(destinationFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous))
            await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                var buffer = new byte[CopyBufferBytes];
                int read;
                while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > upload.Size)
                        throw new InvalidDataException("下載的內容比上傳的檔大");
                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }

                await target.FlushAsync(token).ConfigureAwait(false);
            }

            if (received != upload.Size)
                throw new InvalidDataException($"下載的內容（{received} 位元組）比上傳的檔（{upload.Size} 位元組）小");
            var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(sha, upload.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException("下載的內容和上傳時的 SHA-256 不一樣");
            completed = true;
        }
        finally
        {
            if (!completed)
                TryDelete(destinationFile);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        // stopping 不 Dispose：沒有計時器，留著讓同時開始的下載建連結權杖時不會碰到已釋放的物件
        try
        {
            stopping.Cancel();
        }
        catch (AggregateException ex)
        {
            Log.Warn($"取消遠端檔案的下載時出錯：{ex.Message}");
        }

        http.Dispose();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"刪不掉下載了一半的上傳檔：{ex.Message}");
        }
    }
}
