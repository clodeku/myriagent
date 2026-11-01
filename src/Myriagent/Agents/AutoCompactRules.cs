using System.Globalization;

namespace Myriagent.Agents;

/// <summary>
/// 設定頁「模型設定」各家的自動壓縮上限（setConversationDefault 的 autoCompact）：上下文用到這麼多 token 時 CLI 自動壓縮較早的對話。
/// 存在 ProviderConversationDefaults.AutoCompactTokens（null＝CLI 預設），每個對話的程序下次啟動時套用（改了就重開）。
/// 實測（protocol-notes「自動壓縮上限」）：
/// <list type="bullet">
/// <item>Claude：沒有直接的門檻，只有 CLAUDE_CODE_AUTO_COMPACT_WINDOW（自動壓縮計算用的視窗，10 萬～模型的視窗，超出的被夾回），
/// 門檻＝視窗減 33,000（2.1.314：433000→400000、150000→117000、1M→967000，haiku 與 sonnet 一樣）。所以帶「上限＋33,000」；
/// 以 --settings 的 env 帶（優先於程序環境與使用者 settings.json 的 env）</item>
/// <item>Codex：config 的 model_auto_compact_token_limit，以 app-server 的「-c」帶</item>
/// <item>Grok：沒有以 token 指定的門檻，改用百分比（ProviderConversationDefaults.AutoCompactPercent）：環境變數 GROK_AUTO_COMPACT_THRESHOLD_PERCENT
/// （整數 0～100，超出的 grok 忽略；蓋過伺服器給每個模型的 80% 與 config 的 session.auto_compact_threshold_percent，1.0.70 實測 5% 在 9% 就壓縮）。
/// 系統提示就約佔 256k 視窗的 9%，太低每一輪都會壓縮，所以只收 20～95</item>
/// <item>Antigravity：agy 的 AGY_CLI_AUTO_COMPACT_OVERRIDE 格式不明，不提供</item>
/// </list>
/// </summary>
internal static class AutoCompactRules
{
    /// <summary>Claude 的門檻比自動壓縮視窗少這麼多（CLI 的「Autocompact buffer」）</summary>
    public const long ClaudeBufferTokens = 33_000;

    /// <summary>Claude 的環境變數：自動壓縮計算用的視窗（token）</summary>
    public const string ClaudeWindowVariable = "CLAUDE_CODE_AUTO_COMPACT_WINDOW";

    /// <summary>Codex 的 config 鍵</summary>
    public const string CodexConfigKey = "model_auto_compact_token_limit";

    /// <summary>Grok 的環境變數：上下文用到視窗的百分之幾時自動壓縮</summary>
    public const string GrokPercentVariable = "GROK_AUTO_COMPACT_THRESHOLD_PERCENT";

    /// <summary>以百分比設定的範圍（含兩端）；null＝這一家不以百分比設定</summary>
    public static (int Min, int Max)? PercentRangeOf(AgentProvider provider) => provider switch
    {
        AgentProvider.Grok => (20, 95),
        AgentProvider.Claude => null,
        AgentProvider.Codex => null,
        AgentProvider.Antigravity => null,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 解析百分比（網頁的 settingsrules.parsePercent 同一套）：全形轉半形；空白或 "default"＝CLI 預設（null）；整數，可帶 %（例如 70、70%）。
    /// 小數、0、負數、看不懂的回 false
    /// </summary>
    public static bool TryParsePercent(string? text, out int? percent)
    {
        percent = null;
        var s = (text ?? string.Empty).Normalize(System.Text.NormalizationForm.FormKC).Trim();
        if (s.Length == 0 || s.Equals("default", StringComparison.OrdinalIgnoreCase))
            return true;
        if (s.EndsWith('%'))
            s = s[..^1].TrimEnd();
        if (s.Length == 0 || s.Length > 4 || !s.All(char.IsAsciiDigit) || !int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            return false;
        percent = value;
        return true;
    }

    /// <summary>設定頁的百分比：解析後夾進這一家的範圍；不支援的代理或看不懂的值回 false（不改）</summary>
    public static bool TryNormalizePercent(AgentProvider provider, string? text, out int? percent)
    {
        percent = null;
        if (PercentRangeOf(provider) is not { } range || !TryParsePercent(text, out var parsed))
            return false;
        percent = parsed is { } value ? Math.Clamp(value, range.Min, range.Max) : null;
        return true;
    }

    /// <summary>給這一家的對話用的百分比：不以百分比設定的代理一律 null</summary>
    public static int? PercentForProvider(AgentProvider provider, int? percent)
        => percent is { } value && PercentRangeOf(provider) is { } range ? Math.Clamp(value, range.Min, range.Max) : null;

    /// <summary>這一家可設定的範圍（含兩端）；null＝不支援。Claude 的視窗只收 10 萬～100 萬，門檻因此是 67,000～967,000</summary>
    public static (long Min, long Max)? RangeOf(AgentProvider provider) => provider switch
    {
        AgentProvider.Claude => (100_000 - ClaudeBufferTokens, 1_000_000 - ClaudeBufferTokens),
        AgentProvider.Codex => (20_000, 2_000_000),
        AgentProvider.Antigravity => null,
        AgentProvider.Grok => null,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
    };

    /// <summary>
    /// 解析使用者輸入（網頁的 settingsrules.parseTokens 同一套）：全形轉半形；空白或 "default"＝CLI 預設（null）；數字可帶 k／m（不分大小寫）與千分位逗號、底線、空白，例如 400k、400,000、0.4M。
    /// 其他（負數、0、看不懂的）回 false
    /// </summary>
    public static bool TryParse(string? text, out long? tokens)
    {
        tokens = null;
        var s = (text ?? string.Empty).Normalize(System.Text.NormalizationForm.FormKC).Trim();
        if (s.Length == 0 || s.Equals("default", StringComparison.OrdinalIgnoreCase))
            return true;
        s = s.Replace(",", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
        decimal unit = 1;
        if (s.EndsWith('k') || s.EndsWith('K'))
            unit = 1_000;
        else if (s.EndsWith('m') || s.EndsWith('M'))
            unit = 1_000_000;
        if (unit != 1)
            s = s[..^1];
        if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) || number <= 0 || number > 1_000_000_000)
            return false;
        tokens = (long)Math.Round(number * unit, MidpointRounding.AwayFromZero);
        return tokens > 0;
    }

    /// <summary>設定頁的值：解析後夾進這一家的範圍；不支援的代理或看不懂的值回 false（不改）</summary>
    public static bool TryNormalize(AgentProvider provider, string? text, out long? tokens)
    {
        tokens = null;
        if (RangeOf(provider) is not { } range || !TryParse(text, out var parsed))
            return false;
        tokens = parsed is { } value ? Math.Clamp(value, range.Min, range.Max) : null;
        return true;
    }

    /// <summary>給這一家的對話用的值：不支援的代理一律 null（換了代理種類的對話不帶別家的設定）</summary>
    public static long? ForProvider(AgentProvider provider, long? tokens)
        => tokens is { } value && RangeOf(provider) is { } range ? Math.Clamp(value, range.Min, range.Max) : null;

    /// <summary>Claude 的 CLAUDE_CODE_AUTO_COMPACT_WINDOW 要帶的值（上限＋緩衝）；null＝不帶</summary>
    public static string? ClaudeWindowValue(long? tokens)
        => tokens is { } value and > 0 ? (value + ClaudeBufferTokens).ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>Codex app-server 的「-c」值；null＝不帶</summary>
    public static string? CodexOverride(long? tokens)
        => tokens is { } value and > 0 ? $"{CodexConfigKey}={value.ToString(CultureInfo.InvariantCulture)}" : null;
}
