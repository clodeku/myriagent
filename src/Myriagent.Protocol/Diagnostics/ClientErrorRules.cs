using System.Text;
using Newtonsoft.Json.Linq;

namespace Myriagent.Diagnostics;

/// <summary>網頁回報的一則錯誤（web-protocol 第二節 <c>clientError</c>；已驗過、截短、去掉控制字元）</summary>
/// <param name="Kind"><see cref="ClientErrorRules.Kinds"/> 其中一個</param>
/// <param name="Message">錯誤訊息（≤ <see cref="ClientErrorRules.MaxMessageLength"/>）</param>
/// <param name="Source">發生的地方：指令碼的「檔案:行:欄」、載入失敗的媒體網址（≤ <see cref="ClientErrorRules.MaxSourceLength"/>；沒有是 null）</param>
/// <param name="Detail">呼叫堆疊（≤ <see cref="ClientErrorRules.MaxDetailLength"/>；沒有是 null）</param>
internal sealed record ClientErrorReport(string Kind, string Message, string? Source, string? Detail);

/// <summary>
/// 網頁的錯誤回報（2026-10-23；桌面寫進本機記錄檔、瀏覽器版由伺服器寫進它的記錄）：使用者在畫面上看到的破圖、
/// 沒被接住的例外原本都不會留下任何紀錄。桌面與伺服器共用同一份驗證與記錄行格式（純函式）
/// </summary>
internal static class ClientErrorRules
{
    /// <summary>沒被接住的例外（<c>window.onerror</c>）</summary>
    public const string KindError = "error";

    /// <summary>沒被處理的 Promise 拒絕（<c>unhandledrejection</c>）</summary>
    public const string KindRejection = "rejection";

    /// <summary>圖片、影片載入失敗（<c>img</c>／<c>video</c>／<c>source</c> 的 error 事件）</summary>
    public const string KindMedia = "media";

    public static IReadOnlySet<string> Kinds { get; } = new HashSet<string>(StringComparer.Ordinal) { KindError, KindRejection, KindMedia };

    public const int MaxMessageLength = 500;

    public const int MaxSourceLength = 500;

    public const int MaxDetailLength = 2000;

    /// <summary>
    /// 驗證並正規化 <c>clientError</c> 的參數：<c>kind</c> 要是認得的值、<c>message</c> 要是字串；<c>source</c>、<c>detail</c> 可省略或 null，
    /// 有給就要是字串。字串一律去掉控制字元（換行換成「 ⏎ 」，不讓一則回報在記錄檔裡變成好幾行）、去頭尾空白、超過上限截短加「…」。不合格回 null
    /// </summary>
    public static ClientErrorReport? TryParse(JObject? cmd)
    {
        if (cmd is null || cmd["kind"] is not { Type: JTokenType.String } kindToken || (string?)kindToken is not { } kind || !Kinds.Contains(kind))
            return null;
        if (cmd["message"] is not { Type: JTokenType.String } messageToken)
            return null;
        if (!TryOptional(cmd, "source", out var source) || !TryOptional(cmd, "detail", out var detail))
            return null;
        var message = Clean((string)messageToken!, MaxMessageLength);
        return new ClientErrorReport(kind, message.Length == 0 ? "（沒有訊息）" : message, NullIfEmpty(Clean(source, MaxSourceLength)),
            NullIfEmpty(Clean(detail, MaxDetailLength)));
    }

    /// <summary>記錄行（單行）：「{種類}：{訊息}（{來源}）｜{堆疊}」</summary>
    public static string Describe(ClientErrorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var kind = report.Kind switch
        {
            KindError => "例外",
            KindRejection => "未處理的 Promise 拒絕",
            KindMedia => "媒體載入失敗",
            _ => report.Kind,
        };
        var text = new StringBuilder(kind).Append('：').Append(report.Message);
        if (report.Source is { } source)
            text.Append('（').Append(source).Append('）');
        if (report.Detail is { } detail)
            text.Append('｜').Append(detail);
        return text.ToString();
    }

    /// <summary>節流用的鍵：同一種、同一則訊息、同一個來源算同一件事（堆疊不算，同一個錯誤從不同路徑進來也只記一次）</summary>
    public static string ThrottleKey(ClientErrorReport report) => $"{report.Kind}|{report.Message}|{report.Source}";

    private static bool TryOptional(JObject cmd, string name, out string? value)
    {
        value = null;
        if (!cmd.TryGetValue(name, StringComparison.Ordinal, out var token) || token.Type == JTokenType.Null)
            return true;
        if (token.Type != JTokenType.String)
            return false;
        value = (string?)token;
        return true;
    }

    private static string Clean(string? text, int max)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var builder = new StringBuilder(Math.Min(text.Length, max + 8));
        for (var i = 0; i < text.Length && builder.Length <= max; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                continue;
            if (c is '\n' or '\r')
                builder.Append(" ⏎ ");
            else if (char.IsControl(c) || c == (char)0x2028 || c == (char)0x2029)
                builder.Append(' ');
            else
                builder.Append(c);
        }

        var cleaned = builder.ToString().Trim();
        if (cleaned.Length <= max)
            return cleaned;
        // 不切在代理字元對中間
        var cut = char.IsHighSurrogate(cleaned[max - 1]) ? max - 1 : max;
        return cleaned[..cut] + "…";
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
}
