using System.Buffers;
using Myriagent.Agents;
using Myriagent.Media;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>一個項目欄位 blob 的上傳結果（E5，<see cref="BlobUploader"/>）</summary>
internal enum BlobUploadOutcome
{
    /// <summary>已確認在伺服器上（HEAD 200，或 PUT 200／201）</summary>
    Confirmed,

    /// <summary>伺服器拒收、重試也不會變（422 疑似權杖或不是嚴格 UTF-8、413、411、400）：這個項目在本次執行改走截斷版</summary>
    Rejected,

    /// <summary>暫時不行（沒有權杖、連不上、503、507 等）：之後再試</summary>
    Failed,
}

/// <summary>
/// 項目欄位 blob 的規則（WP4.7 計畫 D14、D15、D31；純函式）：
/// <list type="bullet">
/// <item>每個 blob ≤<see cref="MaxItemBlobBytes"/>（和伺服器 <c>Hub:Media:MaxItemBlobBytes</c> 的預設相同；渲染器讀一個 blob 要約 6 倍記憶體）。
/// 整筆的大欄位合計也不超過 <see cref="MaxItemBytes"/>：渲染器一次換回一筆的所有 blob（WP4.7 偏離，見計畫附錄 B 步 10）。</item>
/// <item>截斷沿用 <see cref="PublishFitRules.Truncate"/>（說明字樣與 DLP 的切點處理相同），只是預算放大到 blob 的上限；遮蔽之後仍放不下（JSON 跳脫、
/// 遮蔽改寫變長）時預算減半重試，最多 <see cref="MaxHalvings"/> 次。</item>
/// <item>權杖 DLP 只解碼 base64 片段的開頭 64 KB（<see cref="CredentialLeakRules"/>）：WP4.3 的截斷每欄 ≤30 KB，片段一定整段解得到；
/// blob 的欄位可以到 4 MB，所以有超過 <see cref="DlpDecodeWindowChars"/> 個 base64 字元的片段的項目不走 blob（照 WP4.3 截斷），
/// 不讓解碼範圍外的憑證整段送上伺服器。</item>
/// </list>
/// </summary>
internal static class ItemBlobRules
{
    /// <summary>每個項目 blob 的上限（位元組）</summary>
    public const long MaxItemBlobBytes = 4L * 1024 * 1024;

    /// <summary>整筆的大欄位合計上限（截斷時的整筆預算，也是一筆所有 blob 的合計上限）</summary>
    public const long MaxItemBytes = MaxItemBlobBytes;

    /// <summary>遮蔽之後放不下時預算減半重試的次數上限</summary>
    public const int MaxHalvings = 3;

    /// <summary>
    /// base64 片段（連續的 base64 字元，可跨折行）最多幾個字元才走 blob：權杖 DLP 解碼開頭的 64 KB（87,380 個 base64 字元），這裡取 65,536 留餘裕
    /// </summary>
    public const int DlpDecodeWindowChars = 64 * 1024;

    /// <summary>等著上傳的項目數上限（記憶體裡帶著 blob 的內容；超過的等之後重排）</summary>
    public const int MaxPendingItems = 64;

    /// <summary>等著上傳的 blob 合計上限（位元組）</summary>
    public const long MaxPendingBytes = 32L * 1024 * 1024;

    /// <summary>確認過在伺服器上的 blob 記多久（之後要用時再問一次：伺服器的每日清理會刪沒有引用的舊 blob）</summary>
    public static readonly TimeSpan ConfirmedTtl = TimeSpan.FromHours(1);

    /// <summary>這次執行最多記幾個確認過的 blob（超過就清掉重記，只是多問幾次 HEAD）</summary>
    public const int MaxRemembered = 50_000;

    /// <summary>上傳暫時失敗之後多久重排一次截斷過的項目</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    /// <summary>base64 本體的字元（同權杖 DLP 的標準字母表）</summary>
    private static readonly SearchValues<char> Base64Body = SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/");

    /// <summary>E5 的路徑（<c>/api/v1/device/blobs/{sha256}</c>）；不是 blob id 丟 <see cref="ArgumentException"/></summary>
    public static string BlobPath(string sha256)
        => BlobIdRules.IsBlobId(sha256) ? "/api/v1/device/blobs/" + sha256 : throw new ArgumentException("不是 blob id（64 個小寫十六進位）", nameof(sha256));

    /// <summary>
    /// 每個可截斷的欄位（原文 UTF-8）不超過 <paramref name="maxFieldBytes"/>、整筆（JSON 跳脫後的估計）不超過 <paramref name="maxItemBytes"/>：
    /// <see cref="PublishFitRules.Truncate"/>（說明字樣、留頭或留尾、DLP 的切點處理都相同）。沒有截斷時回原本的實例
    /// </summary>
    /// <param name="capped">截斷了哪些欄位（<see cref="PublishFitRules.TruncatableFields"/> 的名稱與順序）</param>
    public static AgentChatItem CapFields(AgentChatItem item, long maxFieldBytes, long maxItemBytes, out IReadOnlyList<string> capped)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFieldBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxItemBytes, 1);
        return PublishFitRules.Truncate(item, (int)Math.Min(maxFieldBytes, int.MaxValue), (int)Math.Min(maxItemBytes, int.MaxValue), out capped);
    }

    /// <summary>每個 blob 都在 1～<paramref name="maxBlobBytes"/> 之內、合計不超過 <paramref name="maxTotalBytes"/></summary>
    public static bool BlobsFit(IReadOnlyList<ConversationBlob> blobs, long maxBlobBytes, long maxTotalBytes)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        long total = 0;
        foreach (var blob in blobs)
        {
            if (blob is null || blob.Content.LongLength is < 1 || blob.Content.LongLength > maxBlobBytes)
                return false;
            total += blob.Content.LongLength;
        }

        return total <= maxTotalBytes;
    }

    /// <summary>投影好的 payload 裡有沒有任何字串（值或欄位名稱）含超過 <paramref name="windowChars"/> 個 base64 字元的片段（見 <see cref="LongestBase64Run"/>）</summary>
    public static bool HasLongBase64Run(JToken payload, int windowChars = DlpDecodeWindowChars)
    {
        ArgumentNullException.ThrowIfNull(payload);
        foreach (var token in payload is JContainer container ? container.DescendantsAndSelf() : [payload])
        {
            var text = token switch
            {
                JValue { Type: JTokenType.String, Value: string s } => s,
                JProperty p => p.Name,
                _ => null,
            };
            if (text is { Length: > 0 } && text.Length > windowChars && LongestBase64Run(text) > windowChars)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 最長的 base64 片段的字數（本體與結尾的 <c>=</c>，不含換行與縮排）。比權杖 DLP 的片段寬鬆（只會更長）：本體字元連續的一段，結尾的 1～2 個 <c>=</c>
    /// 結束片段；行尾接換行（<c>\n</c> 或 <c>\r\n</c>）、下一行去掉開頭的空白與 tab 之後整行都是本體（可帶結尾的 <c>=</c>）時接下去，不比較每行的寬度
    /// </summary>
    public static long LongestBase64Run(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        long longest = 0;
        var i = 0;
        while (i < text.Length)
        {
            var start = text.AsSpan(i).IndexOfAny(Base64Body);
            if (start < 0)
                break;
            i += start;
            long count = 0;
            while (true)
            {
                var rest = text.AsSpan(i).IndexOfAnyExcept(Base64Body);
                var segmentEnd = rest < 0 ? text.Length : i + rest;
                count += segmentEnd - i;
                i = segmentEnd;
                var padding = 0;
                while (i < text.Length && text[i] == '=' && padding < 2)
                {
                    i++;
                    padding++;
                }

                count += padding;
                if (padding > 0 || !TryJoinNextLine(text, ref i))
                    break;
            }

            longest = Math.Max(longest, count);
        }

        return longest;
    }

    /// <summary>
    /// HEAD 的狀態碼：200 已在伺服器上（<see cref="BlobUploadOutcome.Confirmed"/>）、404 要上傳（null）、其他暫時失敗。
    /// 401 已由 <see cref="DeviceHttpClient"/> 換權杖重送過，到這裡仍是 401 也算暫時失敗
    /// </summary>
    public static BlobUploadOutcome? AfterHead(int status) => status switch
    {
        200 => BlobUploadOutcome.Confirmed,
        404 => null,
        _ => BlobUploadOutcome.Failed,
    };

    /// <summary>
    /// PUT 的狀態碼：200（已存在）、201（新存）確認；422（DLP 或不是嚴格 UTF-8）、413（超過伺服器的上限）、411、400（雜湊或長度不符：程式錯誤）
    /// 重試也一樣，算拒收；其他（401、404、409、5xx、507 等）暫時失敗
    /// </summary>
    public static BlobUploadOutcome AfterPut(int status) => status switch
    {
        200 or 201 => BlobUploadOutcome.Confirmed,
        422 or 413 or 411 or 400 => BlobUploadOutcome.Rejected,
        _ => BlobUploadOutcome.Failed,
    };

    /// <summary>折行：<paramref name="i"/> 在換行上、下一行（去掉開頭的空白與 tab）不是空的而且整行都是本體（可帶結尾 1～2 個 <c>=</c>）時，移到下一行的開頭並回 true</summary>
    private static bool TryJoinNextLine(string text, ref int i)
    {
        var next = i;
        if (next < text.Length && text[next] == '\r')
            next++;
        if (next >= text.Length || text[next] != '\n')
            return false;
        next++;
        while (next < text.Length && text[next] is ' ' or '\t')
            next++;
        var lineRest = text.AsSpan(next).IndexOfAnyExcept(Base64Body);
        var lineEnd = lineRest < 0 ? text.Length : next + lineRest;
        var tail = lineEnd;
        while (tail < text.Length && text[tail] == '=' && tail - lineEnd < 2)
            tail++;
        if (lineEnd == next || (tail < text.Length && text[tail] is not ('\r' or '\n')))
            return false;
        i = next;
        return true;
    }
}
