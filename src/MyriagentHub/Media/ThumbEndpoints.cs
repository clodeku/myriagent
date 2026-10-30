using Myriagent.Media;
using Myriagent.Sync;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Sync;
using Microsoft.Extensions.Options;

namespace MyriagentHub.Media;

/// <summary>
/// 擁有者電腦的縮圖端點（WP4.7 計畫 3.1 的 E8、E10；D30 伺服器那一半）：只收存取權杖 <c>cha_</c>（<see cref="HubPolicies.DeviceAccess"/>），
/// 不用 cookie，所以 PUT 豁免 CSRF 標頭。
/// <list type="bullet">
/// <item>E8 <c>PUT /api/v1/device/thumbs/{ref}</c>（標頭 <c>X-Myriagent-Conversation</c>）：PNG／JPEG（只看魔數）、≤<see cref="MediaOptions.MaxThumbBytes"/>，
/// 存成 blob（<see cref="BlobKinds.Thumb"/>）並綁到 <c>media_refs.thumb_blob</c>（<c>blob_refs(sha, 'thumb', key)</c>；換掉的舊縮圖移除引用）。
/// 對話要是這台的、政策 full（否則 409），參照的列要在、是這個對話的項目參照（查不到時等索引 ≤2 秒），否則 404。</item>
/// <item>E10 <c>GET /api/v1/device/thumbs/missing?before=&amp;limit=</c>：這台還缺縮圖的項目參照（<c>item:*</c>，抽屜的不算），
/// 依 <c>created_at</c> 由新到舊、每頁 ≤200，<c>next</c> 是下一頁的 <c>before</c>。</item>
/// </list>
/// </summary>
internal static class ThumbEndpoints
{
    public const string PutRoute = "/thumbs/{ref}";

    public const string MissingRoute = "/thumbs/missing";

    public static void Map(RouteGroupBuilder device)
    {
        device.MapPut(PutRoute, PutAsync).CsrfExempt();
        device.MapGet(MissingRoute, Missing);
    }

    private static async Task<IResult> PutAsync(string @ref, HttpContext http, BlobStore store, MediaLookup lookup, IConversationDirectory directory, SyncWriter writer,
        ThumbCache cache, IOptions<HubOptions> options, ILoggerFactory logs)
    {
        var log = logs.CreateLogger(typeof(ThumbEndpoints).FullName!);
        if (http.User.FindFirst(HubClaims.DeviceId)?.Value is not { } deviceId || !SyncIds.IsDeviceId(deviceId) || !ConversationEventRules.IsMediaRef(@ref))
            return MediaResults.Error(http, StatusCodes.Status404NotFound, MediaApiErrors.NotFound);
        var conversations = http.Request.Headers[MediaRelayHeaders.Conversation];
        if (conversations.Count != 1 || !ConversationIds.IsWireId(conversations[0]))
            return MediaResults.Error(http, StatusCodes.Status400BadRequest, MediaApiErrors.Invalid);
        var conversationId = conversations[0]!;
        if (directory.Find(conversationId) is not { } owner || !string.Equals(owner.OwnerDeviceId, deviceId, StringComparison.Ordinal))
            return MediaResults.Error(http, StatusCodes.Status404NotFound, MediaApiErrors.NotFound);
        if (owner.SyncPolicy != ConversationSyncPolicies.Full)
            return MediaResults.Error(http, StatusCodes.Status409Conflict, MediaApiErrors.Policy);

        var key = MediaKeyRules.Format(deviceId, @ref);
        try
        {
            if (await lookup.ReadAsync(key, http.RequestAborted) is not { } target
                || !string.Equals(target.ConversationId, conversationId, StringComparison.Ordinal) || !MediaRefPurposes.IsItem(target.Purpose))
                return MediaResults.Error(http, StatusCodes.Status404NotFound, MediaApiErrors.NotFound);
            if (http.Request.ContentLength is not { } length)
                return MediaResults.Error(http, StatusCodes.Status411LengthRequired, MediaApiErrors.LengthRequired);
            var max = options.Value.Media.MaxThumbBytes;
            if (!RequestBodyLimits.TryAllow(http, max))
                return MediaResults.Error(http, StatusCodes.Status413PayloadTooLarge, MediaApiErrors.TooLarge);

            var result = await store.PutStreamingAsync(null, length, max, BlobKinds.Thumb, contentType: null, http.Request.Body, InspectAsync, http.RequestAborted);
            if (result is { Outcome: BlobPutOutcome.Rejected, Error: MediaApiErrors.UnsupportedType })
                return MediaResults.Error(http, StatusCodes.Status415UnsupportedMediaType, MediaApiErrors.UnsupportedType);
            if (MediaApiRules.PutFailure(result.Outcome, result.Error) is { } failure)
                return MediaResults.Error(http, failure);

            var sha = result.Sha256!;
            var bound = await writer.WriteAsync(c => Bind(c, deviceId, @ref, conversationId, key, sha), CancellationToken.None);
            if (!bound)
                return MediaResults.Error(http, StatusCodes.Status404NotFound, MediaApiErrors.NotFound);
            // 綁了 blob 之後 E2 先取 blob：轉送得到的舊快取用不到了
            cache.Delete(key);
            return MediaResults.Json(http, StatusCodes.Status200OK, new { sha256 = sha, length = result.Length });
        }
        catch (BadHttpRequestException ex)
        {
            return MediaResults.Error(http, ex.StatusCode, MediaApiRules.BadRequestCode(ex.StatusCode));
        }
        catch (Exception ex) when (MediaResults.ClientGone(http, ex))
        {
            return Results.Empty;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "存縮圖失敗：{Key}", key);
            return MediaResults.Error(http, StatusCodes.Status503ServiceUnavailable, MediaApiErrors.Unavailable);
        }
    }

    /// <summary>
    /// 把縮圖綁到參照（寫入者的交易）：列還在而且是這個對話的項目參照才綁；換掉的舊縮圖移除引用並更新 <c>last_ref_at</c>。列不見了（剛被刪）回 false
    /// </summary>
    private static bool Bind(SyncWriteContext c, string deviceId, string mediaRef, string conversationId, string key, string sha)
    {
        string? old;
        using (var cmd = c.Command("SELECT thumb_blob, purpose FROM media_refs WHERE device_id = $d AND ref = $r AND conversation_id = $c",
                   ("$d", deviceId), ("$r", mediaRef), ("$c", conversationId)))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() || !MediaRefPurposes.IsItem(reader.GetString(1)))
                return false;
            old = reader.IsDBNull(0) ? null : reader.GetString(0);
        }

        c.Execute("UPDATE media_refs SET thumb_blob = $s WHERE device_id = $d AND ref = $r", ("$s", sha), ("$d", deviceId), ("$r", mediaRef));
        c.Execute("INSERT OR IGNORE INTO blob_refs(sha256, ref_kind, ref_id) VALUES($s, $k, $id)", ("$s", sha), ("$k", BlobRefKinds.Thumb), ("$id", key));
        if (old is not null && old != sha
            && c.Execute("DELETE FROM blob_refs WHERE sha256 = $s AND ref_kind = $k AND ref_id = $id", ("$s", old), ("$k", BlobRefKinds.Thumb), ("$id", key)) > 0)
            c.Execute("UPDATE blobs SET last_ref_at = $now WHERE sha256 = $s", ("$now", c.Now), ("$s", old));
        return true;
    }

    /// <summary>縮圖只看魔數（D15）：不是 PNG／JPEG 回 <see cref="MediaApiErrors.UnsupportedType"/></summary>
    private static async Task<string?> InspectAsync(string tmpPath, CancellationToken cancellationToken)
    {
        var head = new byte[8];
        int read;
        await using (var file = new FileStream(tmpPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous))
            read = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
        return BlobInspectRules.ThumbnailType(head.AsSpan(0, read)) is null ? MediaApiErrors.UnsupportedType : null;
    }

    private static IResult Missing(HttpContext http, SyncWriter writer)
    {
        if (http.User.FindFirst(HubClaims.DeviceId)?.Value is not { } deviceId)
            return MediaResults.Error(http, StatusCodes.Status404NotFound, MediaApiErrors.NotFound);
        var query = http.Request.Query;
        if (query["before"].Count > 1 || query["limit"].Count > 1 || !MediaRefIndexRules.TryParseCursor(query["before"].ToString(), out var before))
            return MediaResults.Error(http, StatusCodes.Status400BadRequest, MediaApiErrors.Invalid);
        var limit = MediaRefIndexRules.MissingLimit(query["limit"].Count == 1 ? query["limit"][0] : null);

        using var connection = writer.OpenRead();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            "SELECT m.ref, m.conversation_id, m.purpose, m.created_at FROM media_refs m JOIN conversations c ON c.conversation_id = m.conversation_id "
            + "WHERE m.device_id = $d AND m.purpose LIKE 'item:%' AND m.thumb_blob IS NULL "
            + "AND c.deleted_at IS NULL AND c.sync_policy = 'full' AND c.owner_device_id = $d "
            + (before is null ? string.Empty : "AND (m.created_at, m.ref) < ($t, $r) ")
            + "ORDER BY m.created_at DESC, m.ref DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$d", deviceId);
        cmd.Parameters.AddWithValue("$limit", limit);
        if (before is { } cursor)
        {
            cmd.Parameters.AddWithValue("$t", cursor.CreatedAt);
            cmd.Parameters.AddWithValue("$r", cursor.Ref);
        }

        var refs = new List<object>();
        MissingThumbCursor? last = null;
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var mediaRef = reader.GetString(0);
                var purpose = reader.GetString(2);
                refs.Add(new { @ref = mediaRef, conversationId = reader.GetString(1), itemId = purpose[MediaRefPurposes.ItemPrefix.Length..] });
                last = new MissingThumbCursor(reader.GetInt64(3), mediaRef);
            }
        }

        var next = refs.Count == limit && last is { } page ? MediaRefIndexRules.FormatCursor(page) : null;
        return MediaResults.Json(http, StatusCodes.Status200OK, new { refs, next });
    }
}
