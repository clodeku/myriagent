using System.Globalization;
using System.Text;

namespace Myriagent.Agents;

/// <summary>
/// 輸入區的語音訊息（麥克風鈕；web-protocol 的 <c>voiceMessage</c>，2026-10-23 使用者要求）：網頁錄下使用者的聲音，存進對話的附件，
/// 由電腦在本機轉成逐字稿（faster-whisper），連同錄音檔交給代理；代理先用文字複誦使用者說的內容、再照做。
/// 這裡是桌面與伺服器共用的純函式：給代理的文字、畫面上的文字、檔名與錄音格式。不呼叫 Windows 的語音輸入
/// </summary>
internal static partial class VoiceMessageRules
{
    /// <summary>一則語音最長幾秒（網頁到時自動停止並送出）</summary>
    public const int MaxSeconds = 300;

    /// <summary>錄音檔最大位元組（網頁以 32 kbps 錄 5 分鐘約 1.2 MB；手機的 AAC 可能大一些）</summary>
    public const int MaxBytes = 20 * 1024 * 1024;

    /// <summary>逐字稿最多交給代理幾個字（5 分鐘的話遠小於這個數；超過的截斷）</summary>
    public const int MaxTranscriptLength = 20_000;

    /// <summary>錄音檔的檔名前綴（網頁上傳與本機存檔都用；電腦以它認出是語音訊息的錄音）</summary>
    public const string FilePrefix = "語音訊息_";

    /// <summary>
    /// 錄音的 MIME → 副檔名：Chromium 的 MediaRecorder 是 WebM（Opus），存成 <c>.weba</c>（<c>.webm</c> 會被當成影片去擷取畫面）；
    /// Safari（iOS）是 MP4（AAC），存成 <c>.m4a</c>；Firefox 是 Ogg。不認得的回 null（不收）
    /// </summary>
    public static string? ExtensionFor(string? mime)
    {
        var type = (mime ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        return type switch
        {
            "audio/webm" or "video/webm" => ".weba",
            "audio/mp4" or "audio/x-m4a" or "audio/aac" or "video/mp4" => ".m4a",
            "audio/ogg" or "audio/opus" => ".ogg",
            "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
            _ => null,
        };
    }

    /// <summary>錄音檔名：<c>語音訊息_yyyyMMdd_HHmmss.副檔名</c>（<paramref name="now"/> 是本機時間）</summary>
    public static string FileName(DateTime now, string extension)
        => FilePrefix + now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + extension;

    /// <summary>是不是語音訊息的錄音檔名（前綴＋認得的副檔名；附件存檔時重名加的「 (2)」不影響）</summary>
    public static bool IsVoiceFileName(string? name)
    {
        if (string.IsNullOrEmpty(name) || !name.StartsWith(FilePrefix, StringComparison.Ordinal))
            return false;
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".weba" or ".m4a" or ".ogg" or ".wav";
    }

    /// <summary>
    /// 網頁送來的 <c>data:audio/…;base64,…</c> → 位元組與副檔名；不是認得的音訊格式、不是 base64、空的或超過 <see cref="MaxBytes"/> 回 false
    /// </summary>
    public static bool TryParseDataUrl(string? dataUrl, out byte[] bytes, out string extension)
    {
        bytes = [];
        extension = string.Empty;
        if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;
        var comma = dataUrl.IndexOf(',');
        if (comma < 0)
            return false;
        var header = dataUrl[5..comma];
        if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase) || ExtensionFor(header[..^";base64".Length]) is not { } ext)
            return false;
        // base64 每 4 字是 3 位元組：先以長度擋掉太大的，不必先解碼
        if ((long)(dataUrl.Length - comma - 1) * 3 / 4 > MaxBytes + 3)
            return false;
        try
        {
            bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
        if (bytes.Length == 0 || bytes.Length > MaxBytes)
        {
            bytes = [];
            return false;
        }
        extension = ext;
        return true;
    }

    /// <summary>逐字稿整理：去掉頭尾空白、連續的空白與換行併成一個空格，超過 <see cref="MaxTranscriptLength"/> 截斷；null 是空字串</summary>
    public static string CleanTranscript(string? transcript)
    {
        var sb = new StringBuilder();
        var space = false;
        foreach (var ch in transcript ?? string.Empty)
        {
            if (char.IsWhiteSpace(ch))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space)
                sb.Append(' ');
            space = false;
            sb.Append(ch);
            if (sb.Length >= MaxTranscriptLength)
                break;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 交給代理的文字（使用者訊息；錄音檔另以附件的說明附上路徑）：說明這是語音、逐字稿可能有辨識錯誤，要求先用文字複誦使用者說的內容
    /// （照語意修正明顯的錯字、不確定的標出來），再照這段內容回應或執行
    /// </summary>
    public static string AgentText(string transcript, double? seconds)
    {
        var length = seconds is > 0 ? $"，長 {AgentAttachmentRules.FormatSeconds(seconds.Value)}" : string.Empty;
        return "（這則是使用者的語音訊息" + length + "。錄音檔在附件裡；下面是這台電腦以本機語音辨識〔faster-whisper〕轉出的逐字稿，"
            + "可能有同音錯字、漏字或標點錯誤。）\n\n"
            + "逐字稿：「" + CleanTranscript(transcript) + "」\n\n"
            + "請先用一段文字複誦使用者說了什麼（照語意修正明顯的辨識錯誤，聽不清楚或不確定的地方標出來），再依這段內容回應或執行。";
    }

    /// <summary>畫面、紀錄與同步看的文字（使用者訊息的泡泡）：「語音訊息（0:12）」；長度不明時只有「語音訊息」</summary>
    public static string DisplayText(double? seconds)
        => seconds is > 0 ? $"語音訊息（{AgentAttachmentRules.FormatSeconds(seconds.Value)}）" : "語音訊息";

    /// <summary>是 <see cref="DisplayText"/> 產生的文字（「語音訊息」或「語音訊息（0:12）」）</summary>
    public static bool IsDisplayText(string? text)
        => text != null && DisplayTextPattern().IsMatch(text);

    [System.Text.RegularExpressions.GeneratedRegex(@"^語音訊息(（\d+(:\d{2}){1,2}）)?$")]
    private static partial System.Text.RegularExpressions.Regex DisplayTextPattern();

    /// <summary>錄音波形最多幾格（網頁 voicemsgrules.WAVE_BARS 是 40）</summary>
    public const int MaxWaveformBars = 64;

    /// <summary>
    /// 錄音的波形（桌面這台的網頁錄音時記下麥克風實際的音量、壓成一格一個 0～9 的數字，voiceMessage 的 wave；存在錄音附件的 Waveform、
    /// 使用者訊息的泡泡裡畫成波形條）：只認 1～MaxWaveformBars 個 0～9 的數字，其他（含 null、空字串、全是 0）回 null（不存）
    /// </summary>
    public static string? NormalizeWaveform(string? wave)
    {
        if (string.IsNullOrEmpty(wave) || wave.Length > MaxWaveformBars)
            return null;
        foreach (var ch in wave)
        {
            if (ch is < '0' or > '9')
                return null;
        }
        return wave.Any(ch => ch != '0') ? wave : null;
    }
}
