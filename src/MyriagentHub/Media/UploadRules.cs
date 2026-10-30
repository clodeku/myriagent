using System.Globalization;
using Myriagent.Media;

namespace MyriagentHub.Media;

/// <summary><see cref="UploadRules.Decide"/> 的結果</summary>
internal enum UploadPutKind
{
    /// <summary>新的上傳：讀本文、存 blob、登記</summary>
    New,

    /// <summary>這個 id 已經上傳過、還沒過期：讀本文算雜湊，和登記的相同回 200（重傳冪等）、不同回 409</summary>
    Existing,

    /// <summary>409：同一個 id 正在上傳，或已上傳過而宣告的大小不同</summary>
    Conflict,

    /// <summary>413：宣告的大小超過單檔上限</summary>
    TooLarge,

    /// <summary>507：待處理的上傳合計會超過上限</summary>
    PendingFull,
}

/// <param name="Sha256">Existing 時登記的雜湊</param>
/// <param name="Size">Existing 時登記的大小</param>
internal readonly record struct UploadPutDecision(UploadPutKind Kind, string? Sha256 = null, long Size = 0);

/// <summary>
/// 記憶體裡的一筆上傳。<paramref name="Completed"/> 是 false＝正在上傳：<paramref name="Size"/> 是預留的位元組（宣告的大小，不知道時是單檔上限），
/// 不會過期（請求結束就移除）
/// </summary>
/// <param name="AtMs">完成（或開始）的伺服器時間；完成的 24 小時後過期</param>
internal readonly record struct UploadEntry(bool Completed, string? Sha256, long Size, long AtMs);

/// <summary>
/// 瀏覽器上傳的規則（WP4.7 計畫 D21）：一次 PUT、≤512 MB、同一個 id 同一份內容回 200、不同內容 409、待處理合計 ≤4 GB（507）、24 小時過期。
/// 登記存在 <c>blob_refs(sha256, 'upload', '{uploadId}@{unixMs}')</c>，伺服器啟動時從這裡載回記憶體。純函式
/// </summary>
internal static class UploadRules
{
    /// <summary>上傳完成後可以被 <c>addUploaded</c>／<c>conversationCreate</c> 引用的時間</summary>
    public const long LifetimeMs = 24L * 60 * 60 * 1000;

    /// <summary><c>blob_refs.ref_id</c>：<c>{uploadId}@{unixMs}</c>（上傳 id 不會有 <c>@</c>）</summary>
    public static string RefId(string uploadId, long atMs)
    {
        if (!UploadIdRules.IsValid(uploadId))
            throw new ArgumentException("上傳 id 不合格", nameof(uploadId));
        ArgumentOutOfRangeException.ThrowIfNegative(atMs);
        return uploadId + "@" + atMs.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>解析 <see cref="RefId"/>；id 不合格、時間不是十進位數字都回 false</summary>
    public static bool TryParseRefId(string? refId, out string uploadId, out long atMs)
    {
        uploadId = string.Empty;
        atMs = 0;
        var at = refId?.IndexOf('@') ?? -1;
        if (at < 0)
            return false;
        var id = refId![..at];
        if (!UploadIdRules.IsValid(id)
            || !long.TryParse(refId.AsSpan(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var ms))
            return false;
        uploadId = id;
        atMs = ms;
        return true;
    }

    /// <summary>完成在 <paramref name="atMs"/> 的上傳到 <paramref name="nowMs"/> 已過期（滿 24 小時就算）</summary>
    public static bool IsExpired(long atMs, long nowMs) => nowMs - atMs >= LifetimeMs;

    /// <summary>待處理的位元組：正在上傳的預留＋完成而還沒過期的大小</summary>
    public static long PendingBytes(IEnumerable<UploadEntry> entries, long nowMs)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            if (!entry.Completed || !IsExpired(entry.AtMs, nowMs))
                total += entry.Size;
        }

        return total;
    }

    /// <summary>
    /// 開始一個 PUT 之前的判斷（在讀本文之前）。<paramref name="existing"/> 是同一個 id 目前的登記（沒有或已過期傳 null 或過期的那筆），
    /// <paramref name="declaredLength"/> 是 Content-Length（沒帶傳 null，預留時以單檔上限計），<paramref name="pendingBytes"/> 是 <see cref="PendingBytes"/>
    /// </summary>
    public static UploadPutDecision Decide(UploadEntry? existing, long? declaredLength, long maxUploadBytes, long pendingBytes, long maxPendingBytes, long nowMs)
    {
        if (declaredLength > maxUploadBytes)
            return new UploadPutDecision(UploadPutKind.TooLarge);
        if (existing is { Completed: false })
            return new UploadPutDecision(UploadPutKind.Conflict);
        if (existing is { Completed: true } done && !IsExpired(done.AtMs, nowMs))
        {
            return declaredLength is { } declared && declared != done.Size
                ? new UploadPutDecision(UploadPutKind.Conflict)
                : new UploadPutDecision(UploadPutKind.Existing, done.Sha256, done.Size);
        }

        return pendingBytes + (declaredLength ?? maxUploadBytes) > maxPendingBytes
            ? new UploadPutDecision(UploadPutKind.PendingFull)
            : new UploadPutDecision(UploadPutKind.New);
    }
}
