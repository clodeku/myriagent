using Myriagent.Agents;

namespace Myriagent.Sync;

/// <summary>
/// 媒體參照的配發規則（WP4.7 計畫 D3～D5；純函式）：
/// <list type="bullet">
/// <item>只發給可顯示的媒體（D5）：附件是 Image／Video、代理產出的 <see cref="AgentChatItem.MediaPaths"/>，而且都是本機磁碟路徑（<see cref="AgentAttachmentRules.IsLocalDrivePath"/>）、
/// 副檔名可顯示（<see cref="AgentAttachmentRules.IsDisplayableMedia"/>，與 <see cref="AgentAttachmentRules.GetMediaContentType"/> 同一組）。只看字串、不碰檔案系統
/// （UNC 光是檢查存不存在就會對外送 SMB 雜湊）。文件附件不發（要經 fileFetch）。</item>
/// <item>同一個（對話、項目、附件 id 或媒體路徑）沿用 <c>media_refs_local</c> 既有的列（附件 id 與媒體路徑都以序數比對，同 <see cref="ConversationWire"/>）。</item>
/// </list>
/// </summary>
internal static class MediaRefIssueRules
{
    /// <summary>參照的亂數位元組數（128 位元 → 26 個 Crockford Base32 字）</summary>
    public const int RefBytes = 16;

    /// <summary>新的參照：<paramref name="random"/> 給 16 個亂數位元組（正式是 RandomNumberGenerator），編成 26 字的 Crockford Base32</summary>
    public static string NewRef(Func<int, byte[]> random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var bytes = random(RefBytes);
        if (bytes is not { Length: RefBytes })
            throw new InvalidOperationException("亂數來源沒有給 16 個位元組");
        var value = CrockfordBase32.Encode(bytes);
        return ConversationEventRules.IsMediaRef(value) ? value : throw new InvalidOperationException("產生的參照不合格");
    }

    /// <summary>附件要不要參照：Image／Video、id 上得了線、存放路徑是本機磁碟而且副檔名可顯示</summary>
    public static bool NeedsRef(AgentAttachment? attachment)
        => attachment is { Kind: AgentAttachmentKind.Image or AgentAttachmentKind.Video }
           && ConversationIds.IsItemId(attachment.Id)
           && NeedsRef(attachment.StoredPath);

    /// <summary>媒體路徑要不要參照：本機磁碟路徑、副檔名可顯示（只看字串）</summary>
    public static bool NeedsRef(string? mediaPath)
        => AgentAttachmentRules.IsLocalDrivePath(mediaPath) && AgentAttachmentRules.IsDisplayableMedia(mediaPath!);

    /// <summary>這個項目有沒有要參照的媒體（不必查 <c>media_refs_local</c> 就能跳過的快速判斷）</summary>
    public static bool HasDisplayableMedia(AgentChatItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return (item.Attachments?.Any(NeedsRef) ?? false) || (item.MediaPaths?.Any(NeedsRef) ?? false);
    }

    /// <summary>
    /// 這個項目要的參照（依附件、再依媒體路徑的順序；同一個媒體路徑只發一個）：<paramref name="existing"/>（這個項目在 <c>media_refs_local</c> 的列）
    /// 有的沿用，沒有的以 <paramref name="newRef"/> 配新的、放進 <paramref name="added"/>（呼叫端先寫 <c>media_refs_local</c>）。
    /// 沒有要參照的媒體回空清單
    /// </summary>
    public static IReadOnlyList<AgentMediaRef> Merge(AgentChatItem item, string conversationId, IReadOnlyList<LocalMediaRefRow> existing, Func<string> newRef,
        long nowMs, out IReadOnlyList<LocalMediaRefRow> added)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(newRef);
        var refs = new List<AgentMediaRef>();
        var created = new List<LocalMediaRefRow>();
        var usedRefs = new HashSet<string>(StringComparer.Ordinal);
        var seenAttachments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attachment in item.Attachments ?? [])
        {
            if (!NeedsRef(attachment) || !seenAttachments.Add(attachment.Id))
                continue;
            var row = existing.FirstOrDefault(r => Mine(r, item.Id, conversationId) && string.Equals(r.AttachmentId, attachment.Id, StringComparison.Ordinal));
            var value = row?.Ref ?? Fresh(newRef, usedRefs, existing);
            if (row is null)
                created.Add(new LocalMediaRefRow(value, conversationId, item.Id, attachment.Id, null, nowMs));
            usedRefs.Add(value);
            refs.Add(new AgentMediaRef { Ref = value, AttachmentId = attachment.Id });
        }

        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in item.MediaPaths ?? [])
        {
            if (!NeedsRef(path) || !seenPaths.Add(path))
                continue;
            var row = existing.FirstOrDefault(r => Mine(r, item.Id, conversationId) && r.AttachmentId is null && string.Equals(r.MediaPath, path, StringComparison.Ordinal));
            var value = row?.Ref ?? Fresh(newRef, usedRefs, existing);
            if (row is null)
                created.Add(new LocalMediaRefRow(value, conversationId, item.Id, null, path, nowMs));
            usedRefs.Add(value);
            refs.Add(new AgentMediaRef { Ref = value, MediaPath = path });
        }

        added = created;
        return refs;
    }

    /// <summary>項目有要參照的媒體、但 <paramref name="existing"/> 缺列（重整時摘要相同也要重新投影，D4）</summary>
    public static bool Missing(AgentChatItem item, string conversationId, IReadOnlyList<LocalMediaRefRow> existing)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(existing);
        foreach (var attachment in item.Attachments ?? [])
        {
            if (NeedsRef(attachment)
                && !existing.Any(r => Mine(r, item.Id, conversationId) && string.Equals(r.AttachmentId, attachment.Id, StringComparison.Ordinal)))
                return true;
        }

        foreach (var path in item.MediaPaths ?? [])
        {
            if (NeedsRef(path)
                && !existing.Any(r => Mine(r, item.Id, conversationId) && r.AttachmentId is null && string.Equals(r.MediaPath, path, StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    private static bool Mine(LocalMediaRefRow row, string itemId, string conversationId)
        => string.Equals(row.ItemId, itemId, StringComparison.Ordinal) && string.Equals(row.ConversationId, conversationId, StringComparison.Ordinal);

    /// <summary>新的參照（和這一筆已用的、既有的都不同；128 位元亂數撞到的機率可以不計，撞到就再取，壞掉的亂數來源最多試 4 次）</summary>
    private static string Fresh(Func<string> newRef, HashSet<string> used, IReadOnlyList<LocalMediaRefRow> existing)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var value = newRef();
            if (!ConversationEventRules.IsMediaRef(value))
                throw new InvalidOperationException("產生的參照不合格");
            if (!used.Contains(value) && !existing.Any(r => string.Equals(r.Ref, value, StringComparison.Ordinal)))
                return value;
        }

        throw new InvalidOperationException("亂數來源一直給重複的參照");
    }
}
