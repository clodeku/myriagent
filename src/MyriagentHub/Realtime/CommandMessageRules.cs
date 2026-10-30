using Myriagent.Remote;
using Myriagent.Security;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Realtime;

/// <summary>
/// 遠端指令給瀏覽器的訊息（WP4.5 計畫 D21～D24、D26；web-protocol 第一節〔遠端〕）：<c>commandResult</c>、<c>control</c>、
/// <c>sendRejected</c> 的 JSON，被拒時 toast 的文字、要不要把文字還回輸入框、所有電腦登出的彙整文字與各台的提示。
/// 文字一律經 JSON 序列化跳脫，網頁以 textContent 顯示。純函式
/// </summary>
internal static class CommandMessageRules
{
    public const string CommandResultType = "commandResult", ControlType = "control", ControlKey = "control";

    public const string SendRejectedType = "sendRejected";

    /// <summary>狀態 unknown 的提示（電腦沒有回報結果）</summary>
    public const string UnknownText = "那台電腦沒有回報結果，指令可能已經執行，請看對話內容確認";

    /// <summary>所有電腦登出時某台沒有回報結果（沒有電腦的說明時）</summary>
    public const string UnknownLogoutText = "可能已經登出，請到那台電腦確認";

    private const string Send = "send";

    /// <summary>toast 的等級：成功（所有電腦登出的彙整與某台已登出）</summary>
    public const string SuccessLevel = "success";

    private const string ErrorLevel = "error";

    /// <summary>durable 的期限（天）：所有電腦登出的彙整文字用</summary>
    private const long DurableDays = CommandEnvelope.MaxDurableAgeMs / (24 * 60 * 60 * 1000);

    /// <summary>
    /// <c>{type:"commandResult", commandId, state, code, message, tier}</c>（鍵的順序固定）；<paramref name="browserCommandId"/> 是瀏覽器送 Cmd 時帶的 commandId（不是 cid）
    /// </summary>
    public static string CommandResultJson(string browserCommandId, string state, string? code, string? message, int? tier)
        => new JObject
        {
            ["type"] = CommandResultType,
            ["commandId"] = browserCommandId,
            ["state"] = state,
            ["code"] = code,
            ["message"] = message,
            ["tier"] = tier,
        }.ToString(Formatting.None);

    /// <summary><c>{type:"control", tier1Until, tier2Until, defaultPassword}</c>（D24；兩個時間窗目前一律 null，<see cref="RemoteAuthRules"/>）</summary>
    public static string ControlJson(long? tier1Until, long? tier2Until, bool defaultPassword)
        => new JObject
        {
            ["type"] = ControlType,
            ["tier1Until"] = tier1Until,
            ["tier2Until"] = tier2Until,
            ["defaultPassword"] = defaultPassword,
        }.ToString(Formatting.None);

    /// <summary><c>{type:"sendRejected", conversationId, text}</c>（D22；<paramref name="conversationId"/> 是瀏覽器看到的 id）</summary>
    public static string SendRejectedJson(string conversationId, string text)
        => new JObject { ["type"] = SendRejectedType, ["conversationId"] = conversationId, ["text"] = text }.ToString(Formatting.None);

    /// <summary>
    /// <c>rejected</c>／<c>unknown</c> 一律另送的 toast 文字（D23）：電腦的 <paramref name="deviceMessage"/> 去掉前後空白後 1～1,000 字
    /// （<see cref="RemoteResultRules.MaxMessageLength"/>）就用它（WP4.4 保證是給人看的臺灣繁體中文），否則用代碼或狀態的文字；<c>done</c> 回 null
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">不認得的狀態，或 rejected 的代碼不是 <see cref="RemoteResultCodes"/> 之一（要先決定瀏覽器看到什麼）</exception>
    public static string? ToastText(string state, string? code, string? deviceMessage)
    {
        switch (state)
        {
            case RemoteResultStates.Done:
                return null;
            case RemoteResultStates.Unknown:
                return DeviceMessage(deviceMessage) ?? UnknownText;
            case RemoteResultStates.Rejected:
                var codeText = CodeText(code);
                return DeviceMessage(deviceMessage) ?? codeText;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "未知的結果狀態");
        }
    }

    /// <summary>
    /// 要不要把文字還回輸入框（D22）：<c>send</c> 與草稿的第一次 send（<c>conversationCreate</c>）結果是 <c>rejected</c>，或 <c>Cmd</c> 回
    /// <c>invalid</c>／<c>unavailable</c>。done、unknown（可能已執行）與 Cmd 的 conflict（對話已不在、分頁會被關掉）不還
    /// </summary>
    /// <param name="outcome"><see cref="RemoteResultStates"/> 的狀態，或 <see cref="BrowserErrorCodes"/> 的 Cmd 錯誤</param>
    public static bool RestoresText(string cmdName, string outcome)
        => cmdName is Send or RemoteCommandCatalog.ConversationCreate
           && outcome is RemoteResultStates.Rejected or BrowserErrorCodes.Invalid or BrowserErrorCodes.Unavailable;

    /// <summary>
    /// 所有電腦登出（D26）的彙整文字：「已送出：在線的 N 台已送出；不在線的 N 台在 7 天內上線時執行；N 台的程式還不支援，要先更新」，
    /// 數字是 0 的那一段省略
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">有負數，或一列都沒寫（<paramref name="delivered"/>＋<paramref name="pending"/> 是 0：那時回 rejected，不用這段文字）</exception>
    public static string FanOutText(int delivered, int pending, int unsupported)
    {
        if (delivered < 0 || pending < 0 || unsupported < 0 || delivered + pending == 0)
            throw new ArgumentOutOfRangeException(nameof(delivered), $"{delivered}/{pending}/{unsupported}", "至少要有一台已送出或排入");
        var parts = new List<string>(3);
        if (delivered > 0)
            parts.Add($"在線的 {delivered} 台已送出");
        if (pending > 0)
            parts.Add($"不在線的 {pending} 台在 {DurableDays} 天內上線時執行");
        if (unsupported > 0)
            parts.Add($"{unsupported} 台的程式還不支援，要先更新");
        return "已送出：" + string.Join("；", parts);
    }

    /// <summary>
    /// 所有電腦登出時各台的最終結果（D26，只給發起的連線）：done 是「『{電腦名}』已登出這個帳戶」（success）；rejected 是
    /// 「『{電腦名}』沒有登出：{原因}」（原因照 <see cref="ToastText"/>）；unknown 是「『{電腦名}』沒有回報登出的結果：{電腦的說明，
    /// 沒有就是 <see cref="UnknownLogoutText"/>}」（都是 error）
    /// </summary>
    public static (string Text, string Level) DeviceToast(string deviceName, string state, string? code, string? deviceMessage)
        => state switch
        {
            RemoteResultStates.Done => ($"『{deviceName}』已登出這個帳戶", SuccessLevel),
            RemoteResultStates.Rejected => ($"『{deviceName}』沒有登出：{ToastText(state, code, deviceMessage)}", ErrorLevel),
            RemoteResultStates.Unknown => ($"『{deviceName}』沒有回報登出的結果：{DeviceMessage(deviceMessage) ?? UnknownLogoutText}", ErrorLevel),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "未知的結果狀態"),
        };

    /// <summary>代碼的 toast 文字（D23 的表；conflict、invalid 照與 WP4.4 對齊的定稿 A10）</summary>
    private static string CodeText(string? code) => code switch
    {
        RemoteResultCodes.Offline => "那台電腦目前不在線，指令沒有送出",
        RemoteResultCodes.Unsupported => "那台電腦的 Clode Myriagent 還不支援網頁版遠端操作，請先更新那台電腦",
        RemoteResultCodes.Policy => "那台電腦的設定不允許這個遠端操作",
        RemoteResultCodes.NotOwner => "這個對話已不屬於那台電腦，網頁版不能操作它",
        RemoteResultCodes.Stale => "這張卡片已在其他地方處理，或內容已經變了",
        RemoteResultCodes.Busy => "那台電腦暫時收不下指令，請稍後再試",
        RemoteResultCodes.Expired => "指令在途中過期，沒有執行，請再送一次",
        RemoteResultCodes.Conflict => "那台電腦目前的狀態不允許這個操作",
        RemoteResultCodes.Invalid => "那台電腦拒絕了這個指令（格式或內容不符合那台電腦）",
        RemoteResultCodes.RateLimited => "遠端操作太頻繁，請稍後再試",
        RemoteResultCodes.StepUpRequired => "要先重新輸入密碼",
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "未知的拒絕代碼"),
    };

    /// <summary>電腦的說明：去掉前後空白後 1～1,000 字才用</summary>
    private static string? DeviceMessage(string? message)
        => message?.Trim() is { Length: > 0 and <= RemoteResultRules.MaxMessageLength } trimmed ? trimmed : null;
}
