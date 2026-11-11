using System.Text.RegularExpressions;
using Myriagent.Agents;

namespace Myriagent.Hub;

/// <summary>
/// 模型選單的「預設」實際是哪個模型（網頁在「預設」旁邊顯示，例如「預設（Opus 5.5）」；models 裡 "default" 那一項的 resolved）。
/// <list type="bullet">
/// <item>Claude：initialize 回報的 default 那一項的 description 以模型名稱開頭（「Opus 5.5 · Best for everyday, complex tasks」）</item>
/// <item>Grok：解析 ACP 模型清單時中樞自己寫的 description（「grok 的預設模型（目前是 Grok 4.7）」，GrokAcpRules.ParseModels）</item>
/// <item>Codex：沒指定模型時 codex 用 config.toml 最上層的 model，沒有才是 model/list 標 isDefault 的那個（0.166.0 實測兩者不同：
/// config 是 gpt-6-astra、isDefault 是 gpt-6.1-sol）；ParseModels 把 isDefault 排在最前</item>
/// <item>Antigravity：agy models 不標預設、設定檔也沒有，開始前不知道（null）</item>
/// </list>
/// </summary>
internal static partial class DefaultModelRules
{
    /// <summary>「預設」對應的模型名稱；不知道回 null。codexConfiguredModel＝Codex 的 config.toml 最上層的 model（ParseCodexConfiguredModel）</summary>
    public static string? ResolvedLabel(AgentProvider provider, IReadOnlyList<AgentModelOption> models, string? codexConfiguredModel = null)
    {
        var others = models.Where(m => !IsDefault(m.Value)).ToList();
        switch (provider)
        {
            case AgentProvider.Claude:
            case AgentProvider.Grok:
            {
                var description = models.FirstOrDefault(m => IsDefault(m.Value))?.Description;
                if (string.IsNullOrWhiteSpace(description))
                    return null;
                // 說明裡最早出現的模型名稱（同一個位置取最長的：「Grok 4.7 Fast」不會被當成「Grok 4.7」）
                return others
                    .Where(m => !string.IsNullOrWhiteSpace(m.Label))
                    .Select(m => (m.Label, At: description.IndexOf(m.Label, StringComparison.OrdinalIgnoreCase)))
                    .Where(x => x.At >= 0)
                    .OrderBy(x => x.At)
                    .ThenByDescending(x => x.Label.Length)
                    .Select(x => x.Label)
                    .FirstOrDefault();
            }
            case AgentProvider.Codex:
            {
                if (!string.IsNullOrWhiteSpace(codexConfiguredModel))
                {
                    var configured = codexConfiguredModel.Trim();
                    return others.FirstOrDefault(m => string.Equals(m.Value, configured, StringComparison.OrdinalIgnoreCase))?.Label ?? configured;
                }
                return others.FirstOrDefault()?.Label;
            }
            case AgentProvider.Antigravity:
                return null;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider), provider, null);
        }
    }

    /// <summary>config.toml 最上層（第一個表格之前）的 model = "…"；沒有回 null</summary>
    public static string? ParseCodexConfiguredModel(string? configToml)
    {
        if (string.IsNullOrEmpty(configToml))
            return null;
        foreach (var raw in configToml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
                return null;
            var match = TopLevelModel().Match(line);
            if (match.Success)
                return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        }
        return null;
    }

    /// <summary>config.toml 最上層（第一個表格之前）的 model_reasoning_effort = "…"；沒有回 null（不指定強度時 Codex 用它，不用模型的預設）</summary>
    public static string? ParseCodexConfiguredEffort(string? configToml)
    {
        if (string.IsNullOrEmpty(configToml))
            return null;
        foreach (var raw in configToml.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
                return null;
            var match = TopLevelEffort().Match(line);
            if (match.Success)
                return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        }
        return null;
    }

    [GeneratedRegex("""^model_reasoning_effort\s*=\s*(?:"([^"]+)"|'([^']+)')\s*(?:#.*)?$""")]
    private static partial Regex TopLevelEffort();

    private static bool IsDefault(string? value) => string.Equals(value, "default", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("""^model\s*=\s*(?:"([^"]+)"|'([^']+)')\s*(?:#.*)?$""")]
    private static partial Regex TopLevelModel();
}
