using Myriagent.Remote;
using MyriagentHub.Media;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Sync;

/// <summary>
/// 投影一則電腦的指令輸出時要的、伺服器自己記下的事實（WP4.5 計畫 D6、D20；不是電腦說的）
/// </summary>
/// <param name="CommandName">這個 cid 原本的指令名稱</param>
/// <param name="ExpectedConversationId">
/// <c>sendRejected.conversationId</c> 要等於的線上 id：Owner 路由（<c>send</c>、<c>answer</c>）是指令的 <c>conversationId</c>，
/// <c>conversationCreate</c> 是 <c>RemoteConversationIdRules.FromCommandId(cid)</c>；null＝不收 <c>sendRejected</c>
/// </param>
/// <param name="BrowserConversationId">瀏覽器看到的對話 id（草稿的第一次 send 是草稿 id）；null＝照 <paramref name="ExpectedConversationId"/></param>
/// <param name="RequestId">指令帶的 <c>requestId</c>（WP4.7 的 files／fileSearch／fileRef 核對用）</param>
/// <param name="BrowserAccountId"><c>accountLogin.accountId</c> 要換回的 id（<c>CommandRouteRules.BrowserAccountId</c>）；null＝原樣</param>
internal sealed record OutputContext(string CommandName, string? ExpectedConversationId, string? BrowserConversationId,
    string? RequestId, string? BrowserAccountId);

/// <summary>
/// 電腦的指令輸出（<c>CommandOutput.message</c>）轉給發起的瀏覽器連線之前的投影（WP4.5 計畫 D20；契約 D18、sync-protocol 14.6）：
/// 轉 <c>toast</c>、<c>sendRejected</c>、<c>accountLogin</c>，每種只留已知的鍵（較新的桌面多帶的鍵不進頁面）。
/// 不在型錄白名單、型別不對、超過上限、對話 id 對不上、驗證網址不在白名單，一律回 null（整則丟掉）。
/// WP4.7 起另轉 <c>files</c>、<c>fileSearch</c>、<c>fileRef</c>（<see cref="CommandOutputFileRules"/>；投影之後還要經 WP4.7 的改寫，
/// 媒體參照換成本站網址）。純函式，回傳新的物件
/// </summary>
internal static class CommandOutputRules
{
    public const string Toast = "toast", SendRejected = "sendRejected", AccountLogin = "accountLogin";

    /// <summary>toast 文字的上限（與電腦結果的 <c>message</c> 同一個上限）</summary>
    public const int MaxToastLength = RemoteResultRules.MaxMessageLength;

    /// <summary>sendRejected 還回輸入框的原文上限（送進代理的文字上限）</summary>
    public const int MaxSendRejectedLength = RemoteCommandShapeRules.MaxAgentTextLength;

    /// <summary>accountLogin 的 loginUrl 的上限</summary>
    public const int MaxLoginUrlLength = 4096;

    /// <summary>accountLogin 進度文字的上限</summary>
    public const int MaxLoginMessageLength = 2000;

    /// <summary>裝置碼的上限（Codex 的是 <c>ABCD-EFGH2</c> 這種長度）</summary>
    public const int MaxUserCodeLength = 32;

    /// <summary>toast 的 level（web-protocol 第一節）</summary>
    public static IReadOnlySet<string> ToastLevels { get; } = new HashSet<string>(StringComparer.Ordinal) { "info", "error", "success" };

    /// <summary>投影一則輸出；null＝整則丟掉（呼叫端計數 <c>droppedOutputs</c>）</summary>
    public static JObject? Project(JObject message, OutputContext context)
    {
        if (!RemoteCommandShapeRules.IsString(message["type"], out var type) || !RemoteCommandCatalog.AllowsOutput(context.CommandName, type))
            return null;
        return type switch
        {
            Toast => ProjectToast(message),
            SendRejected => ProjectSendRejected(message, context),
            AccountLogin => ProjectAccountLogin(message, context),
            // WP4.7（計畫步 13）：只留已知的鍵；媒體參照（ref、ver）留給之後的 ICommandOutputRewriter 換成本站網址
            CommandOutputFileRules.Files => CommandOutputFileRules.ProjectFiles(message, context),
            CommandOutputFileRules.FileSearch => CommandOutputFileRules.ProjectFileSearch(message, context),
            CommandOutputFileRules.FileRef => CommandOutputFileRules.ProjectFileRef(message, context),
            // 整個資料夾的傳輸（2026-11-09）：遞迴的檔案清單、一次多個檔的下載參照
            CommandOutputFileRules.FileTree => CommandOutputFileRules.ProjectFileTree(message, context),
            CommandOutputFileRules.FileRefs => CommandOutputFileRules.ProjectFileRefs(message, context),
            _ => null,
        };
    }

    private static JObject? ProjectToast(JObject message)
    {
        if (!Text(message["text"], MaxToastLength, out var text) || text.Length == 0
            || !RemoteCommandShapeRules.IsString(message["level"], out var level) || !ToastLevels.Contains(level))
            return null;
        return new JObject { ["type"] = Toast, ["text"] = text, ["level"] = level };
    }

    private static JObject? ProjectSendRejected(JObject message, OutputContext context)
    {
        if (context.ExpectedConversationId is not { } expected
            || !RemoteCommandShapeRules.IsString(message["conversationId"], out var conversationId)
            || !string.Equals(conversationId, expected, StringComparison.Ordinal)
            || !Text(message["text"], MaxSendRejectedLength, out var text))
            return null;
        return new JObject { ["type"] = SendRejected, ["conversationId"] = context.BrowserConversationId ?? expected, ["text"] = text };
    }

    private static JObject? ProjectAccountLogin(JObject message, OutputContext context)
    {
        if (!RemoteCommandShapeRules.IsString(message["accountId"], out var accountId) || accountId.Length == 0
            || !Text(message["message"], MaxLoginMessageLength, out var text)
            || message["done"] is not JValue { Type: JTokenType.Boolean, Value: bool done })
            return null;

        JToken success;
        switch (message["success"])
        {
            case null or { Type: JTokenType.Null }:
                success = JValue.CreateNull();
                break;
            case JValue { Type: JTokenType.Boolean } flag:
                success = new JValue((bool)flag);
                break;
            default:
                return null;
        }

        var projected = new JObject
        {
            ["type"] = AccountLogin,
            ["accountId"] = context.BrowserAccountId ?? accountId,
            ["message"] = text,
            ["done"] = done,
            ["success"] = success,
        };
        switch (message["deviceCode"])
        {
            case null or { Type: JTokenType.Null }:
                break;
            case JObject code when ProjectDeviceCode(code) is { } deviceCode:
                projected["deviceCode"] = deviceCode;
                break;
            default:
                return null;   // 驗證網址不在白名單或形狀不對：整則丟掉（message 文字裡也有網址）
        }
        // Claude 的 app 內登入給的登入網址（2026-10-26）：只收 claude.com／claude.ai 的 https 網址，其他整則丟掉
        switch (message["loginUrl"])
        {
            case null or { Type: JTokenType.Null }:
                break;
            case JValue { Type: JTokenType.String } url when RemoteLoginUrlRules.IsAllowedClaudeLoginUrl((string?)url) && ((string?)url)!.Length <= MaxLoginUrlLength:
                projected["loginUrl"] = (string?)url;
                break;
            default:
                return null;
        }

        return projected;
    }

    private static JObject? ProjectDeviceCode(JObject code)
    {
        if (!Text(code["userCode"], MaxUserCodeLength, out var userCode) || userCode.Length == 0
            || !RemoteCommandShapeRules.IsString(code["verificationUrl"], out var url) || !RemoteLoginUrlRules.IsAllowedVerificationUrl(url)
            || code["expiresAt"] is not JValue { Type: JTokenType.Integer, Value: long expiresAt })
            return null;
        return new JObject { ["userCode"] = userCode, ["verificationUrl"] = url, ["expiresAt"] = expiresAt };
    }

    /// <summary>JSON 字串、長度 ≤ <paramref name="max"/>（UTF-16 字元）</summary>
    private static bool Text(JToken? token, int max, out string text)
        => RemoteCommandShapeRules.IsString(token, out text) && text.Length <= max;
}
