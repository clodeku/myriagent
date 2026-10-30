using System.Globalization;
using Myriagent.Agents;
using Myriagent.Remote;
using Newtonsoft.Json.Linq;

namespace Myriagent.Accounts;

/// <summary>Codex app-server 給的裝置碼（account/login/start {type:"chatgptDeviceCode"} 的回應，已驗證）。ExpiresAt 是毫秒</summary>
internal sealed record CodexDeviceCode(string LoginId, string UserCode, Uri VerificationUrl, long ExpiresAt);

/// <summary>
/// Codex 裝置碼登入（WP3.4 第 10 步；設計 6.4）的規則。第 3 階段只有本機發起、驗證碼只顯示在這台（D1）；
/// 第 4 階段起也可以由網頁發起（WP4.8），驗證碼另經 CommandOutput 只給發起的瀏覽器連線。驗證網址的主機清單只有一份，
/// 在 <see cref="RemoteLoginUrlRules"/>（電腦送出之前與伺服器轉給瀏覽器之前都用它，WP4.8 D19）。
/// 回應的形狀以 G5 實測（codex 0.163.0）為準：{type:"chatgptDeviceCode", loginId, userCode, verificationUrl}，沒有到期欄位；
/// 驗證碼與 loginId 不進記錄檔，看不懂的回應只記欄位名稱（<see cref="FieldNames"/>）
/// </summary>
internal static class CodexDeviceCodeRules
{
    /// <summary>account/login/start 的 type</summary>
    public const string StartType = "chatgptDeviceCode";

    /// <summary>裝置碼登入最長等多久（設計 6.4：15 分鐘逾時就 account/login/cancel）</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(15);

    /// <summary>驗證網址的長度上限</summary>
    public const int MaxUrlLength = 2048;

    /// <summary>loginId 的長度上限（實測是 36 字的 UUID）</summary>
    public const int MaxLoginIdLength = 128;

    /// <summary><see cref="FieldNames"/> 最多幾個名稱、每個名稱最多幾字（看不懂的回應寫進記錄檔時不會塞爆）</summary>
    public const int MaxFieldNames = 32;

    public const int MaxFieldNameLength = 64;

    /// <summary>秒的時間戳記上限：絕對時間小於它就當成秒（1973 年的毫秒值，正常的毫秒時間戳記不會這麼小）</summary>
    private const long SecondsTimestampLimit = 100_000_000_000;

    /// <summary>account/login/start 的參數 {type:"chatgptDeviceCode"}（每次是新的物件）</summary>
    public static JObject StartParams() => new() { ["type"] = StartType };

    /// <summary>
    /// 解讀 account/login/start 的 result（已轉成 JObject；各欄位一律檢查型別，JSON null 當成沒有）：
    /// <list type="bullet">
    /// <item>type 有給就要是 chatgptDeviceCode</item>
    /// <item>loginId：1～128 字的 A-Z a-z 0-9 . _ -</item>
    /// <item>userCode：<see cref="IsValidUserCode"/></item>
    /// <item>網址：verificationUrl，沒有這個欄位才看 verificationUri（RFC 8628 的寫法）；要通過 <see cref="IsAllowedVerificationUrl"/></item>
    /// <item>到期：expiresAt（毫秒，或秒的時間戳記）、expiresIn（秒），和 15 分鐘取較早者；已經到期就不合格</item>
    /// </list>
    /// 任一項不合格回 null（呼叫端要取消登入、不顯示驗證碼）
    /// </summary>
    public static CodexDeviceCode? ParseStart(JObject? result, DateTimeOffset now)
    {
        if (result is null)
            return null;
        var type = result["type"];
        if (type is not null && type.Type != JTokenType.Null && !(type.Type == JTokenType.String && type.Value<string>() == StartType))
            return null;
        if (StringOf(result["loginId"]) is not { } loginId || !IsValidLoginId(loginId))
            return null;
        if (StringOf(result["userCode"]) is not { } userCode || !IsValidUserCode(userCode))
            return null;
        var urlToken = result["verificationUrl"] is { Type: not JTokenType.Null } u ? u : result["verificationUri"];
        if (StringOf(urlToken) is not { } url || !IsAllowedVerificationUrl(url))
            return null;
        var expiresAt = ExpiresAt(now, LongOf(result["expiresAt"]), LongOf(result["expiresIn"]));
        if (expiresAt <= now.ToUnixTimeMilliseconds())
            return null;
        return new CodexDeviceCode(loginId, userCode, new Uri(url, UriKind.Absolute), expiresAt);
    }

    /// <summary>
    /// 驗證網址：只有可見的 ASCII（沒有空白與控制字元）、不超過 2048 字，其餘（絕對網址、https、沒有帳號密碼、預設埠、
    /// 主機在白名單裡、不分大小寫；設計 6.4：主機不對就中止）交給 <see cref="RemoteLoginUrlRules.IsAllowedVerificationUrl"/>。
    /// 電腦這邊只會比伺服器更嚴：這裡收的網址，伺服器一定照樣轉給瀏覽器（WP4.8 D19）
    /// </summary>
    public static bool IsAllowedVerificationUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Length > MaxUrlLength)
            return false;
        foreach (var c in url)
        {
            if (c <= ' ' || c >= (char)0x7F)
                return false;
        }
        return RemoteLoginUrlRules.IsAllowedVerificationUrl(url);
    }

    /// <summary>驗證碼：4～32 字，只有 A-Z、0-9、'-'（顯示在畫面上、使用者照著輸入）</summary>
    public static bool IsValidUserCode(string? code)
    {
        if (code is null || code.Length is < 4 or > 32)
            return false;
        foreach (var c in code)
        {
            if (!(c is >= 'A' and <= 'Z' || c is >= '0' and <= '9' || c == '-'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// 到期（毫秒）：伺服器給的絕對時間（毫秒；小於 1973 年的毫秒值當成秒的時間戳記）、相對秒數，和 now＋15 分鐘取最早者。
    /// 0、負數、超過 9999 年、溢位的值不採用；都沒有就是 now＋15 分鐘。伺服器說已經過期時照實回傳（比 now 早）
    /// </summary>
    public static long ExpiresAt(DateTimeOffset now, long? serverExpiresAtMs, long? serverExpiresInSec)
    {
        var nowMs = now.ToUnixTimeMilliseconds();
        var result = nowMs + (long)MaxLifetime.TotalMilliseconds;
        if (serverExpiresAtMs is long at && at > 0)
        {
            var ms = at < SecondsTimestampLimit ? at * 1000 : at;
            if (ms <= AccountIdentityRules.MaxUnixMs)
                result = Math.Min(result, ms);
        }
        if (serverExpiresInSec is long sec && sec > 0 && sec <= (AccountIdentityRules.MaxUnixMs - nowMs) / 1000)
            result = Math.Min(result, nowMs + sec * 1000);
        return result;
    }

    /// <summary>
    /// 這個帳戶能不能用裝置碼登入：Codex、Grok（Grok 的登入網址本來就帶裝置碼，設計 2026-10-22-grok-antigravity-sync D5）；隔離帳戶一律可以，
    /// 系統預設帳戶只在本來就未登入時可以（設計 6.4：登在已登入的預設資料夾會撤銷使用者原本的登入，含 VS Code 擴充）
    /// </summary>
    public static bool CanUseDeviceCode(AgentAccount account, AccountStatus status) => account.Provider switch
    {
        AgentProvider.Codex or AgentProvider.Grok => !account.IsSystemDefault || status == AccountStatus.NotLoggedIn,
        AgentProvider.Claude => false,
        AgentProvider.Antigravity => false,
        _ => throw new ArgumentOutOfRangeException(nameof(account), account.Provider, "未知的代理種類"),
    };

    /// <summary>
    /// 不能用裝置碼登入時給使用者的說明（D19：只有 Codex；系統預設帳戶只在未登入時）；可以用回 null。
    /// 拒絕時不起 app-server、也不落進主控台登入
    /// </summary>
    public static string? Refusal(AgentAccount account, AccountStatus status) => account.Provider switch
    {
        AgentProvider.Codex => CanUseDeviceCode(account, status)
            ? null
            : "這台預設的 Codex 帳戶不是未登入狀態，用裝置碼登入會撤銷原本的登入（含 VS Code 擴充）；請改新增一個 Codex 帳戶再用裝置碼登入",
        AgentProvider.Grok => CanUseDeviceCode(account, status)
            ? null
            : "這台預設的 Grok 帳戶不是未登入狀態，用裝置碼登入會換掉原本的登入；請改新增一個 Grok 帳戶再用裝置碼登入",
        AgentProvider.Claude or AgentProvider.Antigravity => "只有 Codex、Grok 帳戶能用裝置碼登入",
        _ => throw new ArgumentOutOfRangeException(nameof(account), account.Provider, "未知的代理種類"),
    };

    /// <summary>accountLogin 的 method（web-protocol）：沒帶或 JSON null＝Default，"deviceCode"＝DeviceCode，其他一律看不懂（null，呼叫端忽略這個指令）</summary>
    public static LoginMethod? ParseLoginMethod(JToken? method) => method switch
    {
        null or { Type: JTokenType.Null } => LoginMethod.Default,
        JValue { Type: JTokenType.String, Value: "deviceCode" } => LoginMethod.DeviceCode,
        _ => null,
    };

    /// <summary>Codex 回的錯誤寫進記錄檔前的處理：遮掉驗證碼與 loginId（不分大小寫）、最多 300 字；空白回 null</summary>
    public static string? Redact(string? text, CodexDeviceCode code)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var redacted = text
            .Replace(code.UserCode, "[已遮蔽]", StringComparison.OrdinalIgnoreCase)
            .Replace(code.LoginId, "[已遮蔽]", StringComparison.OrdinalIgnoreCase);
        return redacted.Length > MaxLoggedErrorLength ? redacted[..MaxLoggedErrorLength] : redacted;
    }

    /// <summary><see cref="Redact"/> 的長度上限</summary>
    public const int MaxLoggedErrorLength = 300;

    /// <summary>登入進度的文字（顯示在這台的帳戶卡；不含 loginId）：「請在手機或其他電腦打開 {網址}，輸入驗證碼 {碼}（{n} 分鐘內有效）」，n 無條件進位、最少 1</summary>
    public static string ProgressText(CodexDeviceCode code, DateTimeOffset now)
    {
        var left = code.ExpiresAt - now.ToUnixTimeMilliseconds();
        var minutes = Math.Max(1, (long)Math.Ceiling(left / 60_000.0));
        return $"請在手機或其他電腦打開 {code.VerificationUrl.AbsoluteUri}，輸入驗證碼 {code.UserCode}（{minutes} 分鐘內有效）";
    }

    /// <summary>回應的欄位名稱（依名稱排序；看不懂回應時寫進記錄檔，不記任何值）：最多 32 個、每個最多 64 字</summary>
    public static IReadOnlyList<string> FieldNames(JObject? result)
    {
        if (result is null)
            return Array.Empty<string>();
        return result.Properties()
            .Select((p) => p.Name.Length > MaxFieldNameLength ? p.Name[..MaxFieldNameLength] : p.Name)
            .OrderBy((n) => n, StringComparer.Ordinal)
            .Take(MaxFieldNames)
            .ToList();
    }

    private static bool IsValidLoginId(string id)
    {
        if (id.Length is 0 or > MaxLoginIdLength)
            return false;
        foreach (var c in id)
        {
            if (!(c is >= 'A' and <= 'Z' || c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || c is '.' or '_' or '-'))
                return false;
        }
        return true;
    }

    private static string? StringOf(JToken? token) => token is JValue { Type: JTokenType.String, Value: string s } ? s : null;

    private static long? LongOf(JToken? token) => token is JValue { Type: JTokenType.Integer, Value: long or int } v ? Convert.ToInt64(v.Value, CultureInfo.InvariantCulture) : null;
}
