namespace Myriagent.Health;

/// <summary>
/// 健康度分析讀 TOML 設定（Codex、Grok 的 config.toml）用的最小規則：只認行首的表頭與「鍵 = 值」，不是完整的 TOML。
/// 讀錯只會少列幾個 MCP 伺服器或誤判 Grok 的相容開關，不影響 CLI 本身
/// </summary>
internal static class HealthTomlRules
{
    /// <summary><c>[前綴.名稱]</c>（含 <c>[前綴."有點號的名稱"]</c>）的名稱，依出現順序、不重複；<c>[前綴.名稱.env]</c> 這類子表算同一個</summary>
    public static List<string> TableNames(string toml, string prefix)
    {
        var names = new List<string>();
        foreach (var raw in HealthTextRules.SplitLines(toml))
        {
            var line = StripComment(raw).Trim();
            if (!line.StartsWith('[') || line.StartsWith("[[", StringComparison.Ordinal) || !line.EndsWith(']'))
                continue;
            var path = line[1..^1].Trim();
            if (!path.StartsWith(prefix + ".", StringComparison.Ordinal))
                continue;
            var rest = path[(prefix.Length + 1)..].TrimStart();
            string name;
            if (rest.StartsWith('"') || rest.StartsWith('\''))
            {
                var quote = rest[0];
                var end = rest.IndexOf(quote, 1);
                if (end < 0)
                    continue;
                name = rest[1..end];
            }
            else
            {
                var dot = rest.IndexOf('.');
                name = (dot >= 0 ? rest[..dot] : rest).Trim();
            }
            if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
                names.Add(name);
        }
        return names;
    }

    /// <summary>布林值：<c>[表]</c> 底下的 <c>鍵 = true／false</c>，或最上層的 <c>表.鍵 = …</c>；沒有或不是布林回 null</summary>
    public static bool? BoolValue(string toml, string table, string key)
    {
        string? current = null;
        bool? found = null;
        foreach (var raw in HealthTextRules.SplitLines(toml))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line.TrimStart('[').TrimEnd(']').Trim();
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;
            var name = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            var matches = (current == table && name == key) || (current == null && name == table + "." + key);
            if (!matches)
                continue;
            found = value switch
            {
                "true" => true,
                "false" => false,
                _ => null,
            };
        }
        return found;
    }

    /// <summary>去掉不在字串裡的 # 註解</summary>
    private static string StripComment(string line)
    {
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0')
            {
                if (ch == quote)
                    quote = '\0';
                continue;
            }
            if (ch is '"' or '\'')
                quote = ch;
            else if (ch == '#')
                return line[..i];
        }
        return line;
    }
}
