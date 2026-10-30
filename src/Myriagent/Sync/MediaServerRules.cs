using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Media;

namespace Myriagent.Sync;

/// <summary>裝置回覆轉送的狀態與拒絕原因（<see cref="MediaRelayHeaders.Status"/>、<see cref="MediaRelayHeaders.Error"/>）</summary>
internal readonly record struct MediaVerdict(int Status, string? Error);

/// <summary>一段原檔或檔案要回的位元組：<see cref="Status"/> 是 200（整份）、206（<see cref="Range"/>）、416（起點不在檔案裡）或 413（沒帶範圍又太大）</summary>
internal readonly record struct MediaSegment(int Status, ByteRange? Range);

/// <summary>
/// 桌面回覆媒體轉送（E7，WP4.7 計畫 D26、D28、D36）的判斷。依序：
/// <list type="number">
/// <item>開檔前（<see cref="Decide"/>）：參照不明或用途和參照不符 404、對話不可見（不在、草稿、複本、不是本機擁有、政策不是 full）404、
/// 遠端政策 Off 或暫停時原檔與檔案 403 <c>policy</c>（Q1-A：縮圖照常）。</item>
/// <item>守門員（<see cref="AfterGuard"/>）：不存在 404、太大 413、其他 403 <c>forbidden</c>。</item>
/// <item>範圍（<see cref="Segment"/>）：伺服器要的範圍夾到 8 MB；起點不在檔案裡 416。</item>
/// </list>
/// 純函式
/// </summary>
internal static class MediaServerRules
{
    /// <summary>抽屜（listFiles／fileSearch）發的參照的 <c>item_id</c>（D3；項目 id 不會有 <c>@</c>）</summary>
    public const string ListItemId = "@list";

    /// <summary>fileFetch 發的參照的 <c>item_id</c></summary>
    public const string FileItemId = "@file";

    /// <summary>縮圖一律 320 px（伺服器的 <c>s=160</c> 也要 320，瀏覽器縮放）</summary>
    public const int ThumbPixels = 320;

    /// <summary>縮圖回覆的上限（伺服器 <c>Hub:Media:MaxThumbBytes</c> 的預設值；超過回 413）</summary>
    public const long MaxThumbBytes = 512L * 1024;

    /// <summary>檔案下載（fileFetch）中可能是文字的檔的上限：整份讀進記憶體跑 DLP（D36）</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;

    /// <summary>
    /// 檔案下載中開頭就證明是二進位的檔（<see cref="IsProvenBinary"/>：簡報、圖片、壓縮檔…）的上限：不跑 DLP、依要的範圍串流，
    /// 和伺服器 <c>/f/</c> 沒帶 Range 時的上限（<see cref="MediaRelayRules.MaxUnrangedFileBytes"/>）相同
    /// </summary>
    public const long MaxBinaryFileBytes = MediaRelayRules.MaxUnrangedFileBytes;

    /// <summary>判斷是不是二進位時讀的開頭長度</summary>
    public const int BinaryProbeBytes = 64 * 1024;

    /// <summary>一個轉送最久多久（伺服器那邊有首位元組 15 秒的逾時；這裡防瀏覽器讀得極慢時卡住名額）</summary>
    public static readonly TimeSpan RelayTimeout = TimeSpan.FromMinutes(10);

    /// <summary>純文字的副檔名（加上嚴格 UTF-8 判斷取聯集，D36）</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".log", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf",
        ".env", ".properties", ".gitignore", ".gitattributes", ".editorconfig", ".sln", ".slnx", ".csproj", ".props", ".targets",
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly UTF32Encoding Utf32Le = new(bigEndian: false, byteOrderMark: false);

    private static readonly UTF32Encoding Utf32Be = new(bigEndian: true, byteOrderMark: false);

    /// <summary>E7 的路徑（以伺服器的根為準）</summary>
    public static string RelayPath(string relayId) => "/api/v1/device/relay/" + relayId;

    /// <summary>伺服器的轉送 id：128 位元亂數的 Crockford Base32（26 字）；不合格的不能放進網址，直接丟掉</summary>
    public static bool IsRelayId(string? relayId) => relayId is { Length: SyncIds.RandomChars } && CrockfordBase32.IsValid(relayId);

    /// <summary>
    /// 用途和參照的來源相符（D7）：原檔與縮圖只收項目與抽屜的參照，檔案下載只收 fileFetch 的參照；不認得的用途一律不符
    /// </summary>
    public static bool PurposeMatches(string? purpose, string itemId) => purpose switch
    {
        MediaPurposes.Media or MediaPurposes.Thumb => !string.Equals(itemId, FileItemId, StringComparison.Ordinal),
        MediaPurposes.File => string.Equals(itemId, FileItemId, StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>守門員的用途</summary>
    public static RemoteFilePurpose GuardPurpose(string purpose) => purpose switch
    {
        MediaPurposes.Media => RemoteFilePurpose.Media,
        MediaPurposes.Thumb => RemoteFilePurpose.Thumb,
        MediaPurposes.File => RemoteFilePurpose.File,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "不認得的媒體用途"),
    };

    /// <summary>
    /// 守門員開檔的大小上限：檔案下載 512 MB（開檔後再看開頭：可能是文字的另外限 20 MB、整份跑 DLP）；原檔與縮圖不限（原檔一段 ≤8 MB、縮圖另外限輸出）
    /// </summary>
    public static long MaxBytes(string purpose) => purpose switch
    {
        MediaPurposes.File => MaxBinaryFileBytes,
        MediaPurposes.Media or MediaPurposes.Thumb => long.MaxValue,
        _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "不認得的媒體用途"),
    };

    /// <summary>遠端政策准不准轉送原檔與檔案：View、Control 才准（Off、暫停時的 Off、不認得的層級都不准）</summary>
    public static bool AllowsOriginals(DevicePolicy policy) => policy.RemoteControlLevel is SyncSettings.LevelView or SyncSettings.LevelControl;

    /// <summary>
    /// 開檔前的判斷：null＝繼續（開檔）；否則照回這個狀態。<paramref name="known"/>＝<c>media_refs_local</c> 有這個參照而且用途相符，
    /// <paramref name="visible"/>＝對話存在、不是草稿與複本、本機擁有、政策是 full（D26）
    /// </summary>
    public static MediaVerdict? Decide(bool known, bool visible, DevicePolicy policy, string purpose)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!known || !visible)
            return new MediaVerdict(404, MediaErrorCodes.NotFound);
        // Q1-A：Off 或暫停時原檔與檔案不給、縮圖照常（伺服器對圖片改回縮圖，D29）
        if (purpose is MediaPurposes.Media or MediaPurposes.File && !AllowsOriginals(policy))
            return new MediaVerdict(403, MediaErrorCodes.Policy);
        return null;
    }

    /// <summary>守門員的拒絕原因（<see cref="RemoteFileGuardReasons"/>）→ 回覆：不存在 404、太大 413、其他（範圍外、禁止清單、重新解析點、逾時…）403</summary>
    public static MediaVerdict AfterGuard(string reason) => reason switch
    {
        RemoteFileGuardReasons.NotFound or RemoteFileGuardReasons.NotFile => new MediaVerdict(404, MediaErrorCodes.NotFound),
        RemoteFileGuardReasons.TooLarge => new MediaVerdict(413, MediaErrorCodes.TooLarge),
        _ => new MediaVerdict(403, MediaErrorCodes.Forbidden),
    };

    /// <summary>
    /// 原檔與檔案這次要回的位元組（伺服器的規則：206 的起點等於要的、結尾正好是 min(要的結尾, 長度 − 1)、≤8 MB）。
    /// 有範圍：先以 <see cref="MediaRelayRules.ClampSegment"/> 夾到 8 MB，起點不在檔案裡 416；沒帶範圍：≤8 MB 回 200 整份，否則 413
    /// </summary>
    public static MediaSegment Segment(MediaRange? requested, long length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (requested is null)
            return length <= MediaRelayRules.MaxSegmentBytes ? new MediaSegment(200, null) : new MediaSegment(413, null);
        var clamped = MediaRelayRules.ClampSegment(requested.Start, requested.End);
        if (clamped.Start >= length)
            return new MediaSegment(416, null);
        return new MediaSegment(206, new ByteRange(clamped.Start, Math.Min(clamped.End!.Value, length - 1)));
    }

    /// <summary>伺服器送來的範圍合不合格（起點不是負的、結尾不在起點之前）；不合格的回 404（不讓 ClampSegment 丟例外）</summary>
    public static bool IsValidRange(MediaRange? range) => range is null || (range.Start >= 0 && (range.End is not { } end || end >= range.Start));

    /// <summary>實體標籤：長度與修改時間的 SHA-256（前 32 個十六進位）；檔案改了就不同，伺服器分段接成 200 時以它確認每段是同一份</summary>
    public static string ETag(long length, DateTime lastWriteTimeUtc)
    {
        var text = string.Create(CultureInfo.InvariantCulture, $"{length}:{lastWriteTimeUtc.ToUniversalTime().Ticks}");
        return "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32] + "\"";
    }

    /// <summary>
    /// 檔案下載算不算文字（D36）：副檔名是純文字或程式碼，或內容是嚴格的 UTF-8（沒有不合格的位元組、沒有 NUL），或開頭是 UTF-16／UTF-32 的 BOM
    /// （合併前審查 F1：Windows PowerShell 5.1 的 <c>&gt;</c>、Out-File 預設寫 UTF-16LE 加 BOM，副檔名不是文字類的也要掃）
    /// </summary>
    public static bool IsText(string fileName, ReadOnlySpan<byte> content)
        => IsTextExtension(fileName) || IsStrictUtf8(content) || HasUnicodeBom(content);

    /// <summary>
    /// 只看檔案開頭 <paramref name="prefix"/>（前 <see cref="BinaryProbeBytes"/> 個位元組，檔案比較短時是整份）就能確定整份的 <see cref="IsText"/> 是 false：
    /// 不是文字類副檔名、開頭沒有 UTF-16／UTF-32 的 BOM，而且開頭有 NUL 或不合格的 UTF-8（結尾被切斷的多位元組字不算不合格）。
    /// 是 true 的檔不必整份讀進記憶體跑 DLP（D36 只掃文字），可以依範圍串流
    /// </summary>
    public static bool IsProvenBinary(string fileName, ReadOnlySpan<byte> prefix)
    {
        if (IsTextExtension(fileName) || HasUnicodeBom(prefix))
            return false;
        if (prefix.IndexOf((byte)0) >= 0)
            return true;
        try
        {
            // flush: false＝結尾不完整的多位元組字留給後面的位元組，只有確定不合格的序列才丟例外
            _ = StrictUtf8.GetDecoder().GetCharCount(prefix, flush: false);
            return false;
        }
        catch (DecoderFallbackException)
        {
            return true;
        }
    }

    /// <summary>開頭是 UTF-16 或 UTF-32 的 BOM（UTF-8 的 BOM 不算，那由 <see cref="IsStrictUtf8"/> 收）</summary>
    public static bool HasUnicodeBom(ReadOnlySpan<byte> content) => BomEncodings(content).Length > 0;

    /// <summary>
    /// 文字檔整份跑權杖 DLP（D36，<see cref="CredentialLeakRules.HasLeak"/>）：一律以 UTF-8 解碼掃一次；開頭有 UTF-16／UTF-32 的 BOM 時另以那個編碼
    /// （跳過 BOM）掃；沒有 BOM 但有 NUL 時另以 UTF-16LE 與 UTF-16BE 各掃一次。任何一次命中就回 true（合併前審查 F1：只用 UTF-8 解碼時，
    /// UTF-16 的字之間都夾著 U+0000，權杖的樣式比對不到）。上限 20 MB 的檔每次解碼的字串 ≤40 MB
    /// </summary>
    public static bool HasLeak(ReadOnlySpan<byte> content)
    {
        if (CredentialLeakRules.HasLeak(Encoding.UTF8.GetString(content)))
            return true;
        var boms = BomEncodings(content);
        foreach (var (encoding, skip) in boms)
        {
            if (CredentialLeakRules.HasLeak(encoding.GetString(content[skip..])))
                return true;
        }

        if (boms.Length > 0 || content.IndexOf((byte)0) < 0)
            return false;
        return CredentialLeakRules.HasLeak(Encoding.Unicode.GetString(content)) || CredentialLeakRules.HasLeak(Encoding.BigEndianUnicode.GetString(content));
    }

    /// <summary>
    /// 開頭的 BOM 對到的編碼與 BOM 的長度：<c>FF FE 00 00</c> 是 UTF-32LE（也可能是 UTF-16LE 的 BOM 後面接一個 U+0000，兩個都試）、
    /// <c>00 00 FE FF</c> 是 UTF-32BE、<c>FF FE</c> 是 UTF-16LE、<c>FE FF</c> 是 UTF-16BE；沒有回空的
    /// </summary>
    private static (Encoding Encoding, int Skip)[] BomEncodings(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
            return [(Utf32Le, 4), (Encoding.Unicode, 2)];
        if (content.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
            return [(Utf32Be, 4)];
        if (content.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return [(Encoding.Unicode, 2)];
        if (content.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return [(Encoding.BigEndianUnicode, 2)];
        return [];
    }

    /// <summary>純文字或程式碼的副檔名</summary>
    public static bool IsTextExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        return TextExtensions.Contains(extension) || AgentAttachmentRules.ClassifyForBrowser(fileName ?? string.Empty) == "code";
    }

    /// <summary>嚴格的 UTF-8（開頭的 BOM 可以）而且沒有 NUL</summary>
    public static bool IsStrictUtf8(ReadOnlySpan<byte> content)
    {
        if (content.IndexOf((byte)0) >= 0)
            return false;
        try
        {
            _ = StrictUtf8.GetCharCount(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// 項目參照對到的檔（D3）：附件以 <c>attachment_id</c> 找 <see cref="AgentAttachment.StoredPath"/>（檔名用附件的 <see cref="AgentAttachment.FileName"/>）；
    /// 代理媒體的 <c>media_path</c> 要還在項目的 <see cref="AgentChatItem.MediaPaths"/> 裡。找不到回 null（404）
    /// </summary>
    public static (string Path, string FileName)? Resolve(AgentChatItem? item, string? attachmentId, string? mediaPath)
    {
        if (item is null)
            return null;
        if (attachmentId is not null)
        {
            var attachment = item.Attachments?.FirstOrDefault(a => a is not null && string.Equals(a.Id, attachmentId, StringComparison.Ordinal));
            if (attachment is null || string.IsNullOrEmpty(attachment.StoredPath))
                return null;
            var name = string.IsNullOrWhiteSpace(attachment.FileName) ? Path.GetFileName(attachment.StoredPath) : attachment.FileName;
            return (attachment.StoredPath, name);
        }

        if (mediaPath is not null && item.MediaPaths?.Any(p => string.Equals(p, mediaPath, StringComparison.OrdinalIgnoreCase)) == true)
            return (mediaPath, Path.GetFileName(mediaPath));
        return null;
    }
}
