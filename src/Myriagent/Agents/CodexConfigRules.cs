using System.Text;
using System.Text.RegularExpressions;

namespace Myriagent.Agents;

/// <summary>Codex 設定資料夾（CODEX_HOME）與 config.toml 的讀取規則</summary>
internal static partial class CodexConfigRules
{
    /// <summary>帳戶的 CODEX_HOME；null＝繼承的環境變數，沒有就是 %USERPROFILE%\.codex</summary>
    public static string ResolveHome(string? codexHome)
    {
        if (!string.IsNullOrWhiteSpace(codexHome))
            return codexHome;
        var inherited = Environment.GetEnvironmentVariable("CODEX_HOME");
        return !string.IsNullOrWhiteSpace(inherited)
            ? inherited
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    /// <summary>讀 config.toml；讀不到回 null</summary>
    public static string? ReadConfig(string? codexHome)
    {
        try
        {
            var path = Path.Combine(ResolveHome(codexHome), "config.toml");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// config.toml 裡定義的 MCP 伺服器名稱（精簡模式要逐一停用）。認得三種寫法：
    /// 標頭「[mcp_servers.名稱]」（子表「[mcp_servers.名稱.env]」不重複算）、「[mcp_servers]」表內的「名稱 = {…}」或「名稱.command = …」、
    /// 根層的「mcp_servers.名稱.command = …」；名稱可以加引號（可含 . : @ / 等字元）。根層整個寫成行內表（mcp_servers = {…}）的不認得。
    /// 多行字串與跨行陣列裡的文字不當成設定：抓到不存在的名稱等於設定裡多一個沒有 command 的伺服器，Codex 會整個起不來。
    /// </summary>
    public static List<string> ParseMcpServerNames(string? configToml)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(configToml))
            return result;

        // 目前所在的表：空的＝根層；null＝陣列表或認不得的標頭（裡面的鍵不管）
        List<string>? table = new();
        var value = new ValueState();
        foreach (var raw in configToml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int i = 0;
            if (value.Pending)
            {
                // 上一行的值還沒結束（多行字串、跨行陣列）：這一行整行都屬於那個值
                ScanValue(line, ref i, value);
                continue;
            }

            SkipWhitespace(line, ref i);
            if (i >= line.Length || line[i] == '#')
                continue;

            if (line[i] == '[')
            {
                bool arrayTable = i + 1 < line.Length && line[i + 1] == '[';
                i += arrayTable ? 2 : 1;
                table = !arrayTable && TryReadKey(line, ref i, out var header) && Expect(line, ref i, ']') ? header : null;
                if (table is { Count: >= 2 } && table[0] == "mcp_servers")
                    AddName(result, table[1]);
                continue;
            }

            if (!TryReadKey(line, ref i, out var key) || !Expect(line, ref i, '='))
                continue;
            if (table != null)
            {
                var path = table.Concat(key).ToList();
                if (path.Count >= 2 && path[0] == "mcp_servers")
                    AddName(result, path[1]);
            }
            ScanValue(line, ref i, value);
        }

        return result;
    }

    /// <summary>
    /// 共用給隔離帳戶的 MCP 伺服器（SharedCustomizations）：來源 config.toml 裡每個 mcp_servers.名稱 的每個鍵，轉成 app-server 的
    /// 「-c mcp_servers.名稱.鍵=值」（-c 後面那個參數；值照原文，Codex 以 TOML 解析，行尾註解與跨行的值都合法）。認得的寫法同
    /// <see cref="ParseMcpServerNames"/>，子表（[mcp_servers.名稱.env]）的鍵接在後面。-c 的鍵只用「.」切開：名稱或鍵含點號、或用到陣列表
    /// （[[mcp_servers.名稱.…]]）的伺服器整個略過——只給一部分等於少了 command，Codex 會整個起不來。帳戶自己的 config.toml 已有的名稱
    /// （<paramref name="excludeNames"/>）不給：-c 會蓋過帳戶自己的設定
    /// </summary>
    public static List<string> BuildSharedMcpOverrides(string? sourceToml, IReadOnlyCollection<string> excludeNames)
    {
        var entries = new List<(string Server, List<string> Keys, string Value)>();
        var unsupported = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(sourceToml))
            return new List<string>();

        List<string>? table = new();
        var value = new ValueState();
        (string Server, List<string> Keys, StringBuilder Text)? pending = null;
        foreach (var raw in sourceToml.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int i = 0;
            if (value.Pending)
            {
                ScanValue(line, ref i, value);
                if (pending is { } open)
                {
                    open.Text.Append('\n').Append(line);
                    if (!value.Pending)
                    {
                        entries.Add((open.Server, open.Keys, open.Text.ToString().Trim()));
                        pending = null;
                    }
                }
                continue;
            }

            SkipWhitespace(line, ref i);
            if (i >= line.Length || line[i] == '#')
                continue;

            if (line[i] == '[')
            {
                bool arrayTable = i + 1 < line.Length && line[i + 1] == '[';
                i += arrayTable ? 2 : 1;
                bool parsed = TryReadKey(line, ref i, out var header) && Expect(line, ref i, ']');
                if (arrayTable && parsed && header is { Count: >= 2 } && header[0] == "mcp_servers")
                    unsupported.Add(header[1]);
                table = !arrayTable && parsed ? header : null;
                continue;
            }

            if (!TryReadKey(line, ref i, out var key) || !Expect(line, ref i, '='))
                continue;
            int valueStart = i;
            ScanValue(line, ref i, value);
            if (table == null)
                continue;
            var path = table.Concat(key).ToList();
            if (path.Count < 2 || path[0] != "mcp_servers")
                continue;
            var text = line[valueStart..].Trim();
            if (value.Pending)
                pending = (path[1], path.Skip(2).ToList(), new StringBuilder(text));
            else
                entries.Add((path[1], path.Skip(2).ToList(), text));
        }
        // 檔案在值的中間結束：那個伺服器不完整
        if (pending is { } unfinished)
            unsupported.Add(unfinished.Server);

        foreach (var entry in entries)
        {
            if (!SharedMcpServerName().IsMatch(entry.Server) || entry.Value.Length == 0
                || entry.Keys.Any(k => k.Length == 0 || k.Contains('.') || k.Contains('=') || k.Trim() != k))
                unsupported.Add(entry.Server);
        }
        var exclude = new HashSet<string>(excludeNames ?? Array.Empty<string>(), StringComparer.Ordinal);
        return entries
            .Where(e => !unsupported.Contains(e.Server) && !exclude.Contains(e.Server))
            .Select(e => $"mcp_servers.{e.Server}{string.Concat(e.Keys.Select(k => "." + k))}={e.Value}")
            .ToList();
    }

    /// <summary>可以放進 -c 鍵的伺服器名稱（同 CodexAppServerRules 的名稱規則，但不能有點號：-c 的鍵只用「.」切開）</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_:@/-]+\z")]
    private static partial Regex SharedMcpServerName();

    private static void AddName(List<string> names, string name)
    {
        if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
            names.Add(name);
    }

    /// <summary>一個值還沒掃完的部分：多行字串的結尾記號（三個雙引號或三個單引號）、還沒關上的 [ 與 { 層數</summary>
    private sealed class ValueState
    {
        public string? Multiline;
        public int Depth;

        public bool Pending => Multiline != null || Depth > 0;
    }

    /// <summary>掃過一行裡屬於值的部分（字串、陣列、行內表、行尾註解），記下跨到下一行的狀態</summary>
    private static void ScanValue(string line, ref int i, ValueState state)
    {
        while (i < line.Length)
        {
            if (state.Multiline != null)
            {
                int end = FindMultilineEnd(line, i, state.Multiline);
                if (end < 0)
                {
                    i = line.Length;
                    return;
                }
                state.Multiline = null;
                i = end;
                continue;
            }

            char ch = line[i];
            if (ch == '#')
            {
                i = line.Length;
                return;
            }
            if (ch is '"' or '\'')
            {
                var triple = new string(ch, 3);
                if (string.CompareOrdinal(line, i, triple, 0, 3) == 0)
                {
                    state.Multiline = triple;
                    i += 3;
                }
                else
                {
                    ReadQuoted(line, ref i);
                }
                continue;
            }
            if (ch is '[' or '{')
                state.Depth++;
            else if (ch is ']' or '}' && state.Depth > 0)
                state.Depth--;
            i++;
        }
    }

    /// <summary>多行字串的結尾位置（結尾記號之後）；這一行沒結束回 -1。基本字串的反斜線跳脫下一個字元；結尾記號前最多再多兩個引號（屬於內容）</summary>
    private static int FindMultilineEnd(string line, int start, string delimiter)
    {
        char quote = delimiter[0];
        for (int i = start; i < line.Length; i++)
        {
            if (quote == '"' && line[i] == '\\')
            {
                i++;
                continue;
            }
            if (string.CompareOrdinal(line, i, delimiter, 0, 3) != 0)
                continue;
            int end = i + 3;
            for (int extra = 0; extra < 2 && end < line.Length && line[end] == quote; extra++)
                end++;
            return end;
        }

        return -1;
    }

    /// <summary>
    /// 讀一個點號分隔的鍵（裸鍵 A-Za-z0-9_-、"基本字串"、'字面字串'，點號前後可有空白）。
    /// 基本字串的跳脫不解碼：含反斜線的名稱 Codex 本來就不接受，之後組參數時會被濾掉。
    /// </summary>
    private static bool TryReadKey(string line, ref int i, out List<string> segments)
    {
        segments = new List<string>();
        while (true)
        {
            SkipWhitespace(line, ref i);
            if (i >= line.Length)
                return false;

            string? segment;
            if (line[i] is '"' or '\'')
            {
                segment = ReadQuoted(line, ref i);
            }
            else
            {
                int start = i;
                while (i < line.Length && (char.IsAsciiLetterOrDigit(line[i]) || line[i] is '_' or '-'))
                    i++;
                segment = i > start ? line[start..i] : null;
            }
            if (segment == null)
                return false;
            segments.Add(segment);

            SkipWhitespace(line, ref i);
            if (i < line.Length && line[i] == '.')
            {
                i++;
                continue;
            }
            return true;
        }
    }

    /// <summary>讀一段單行的引號字串，回傳引號內的原文；沒有收尾的引號回 null（游標停在行尾）</summary>
    private static string? ReadQuoted(string line, ref int i)
    {
        char quote = line[i];
        int start = ++i;
        while (i < line.Length)
        {
            if (quote == '"' && line[i] == '\\')
            {
                i += 2;
                continue;
            }
            if (line[i] == quote)
                return line[start..i++];
            i++;
        }

        i = line.Length;
        return null;
    }

    private static bool Expect(string line, ref int i, char expected)
    {
        SkipWhitespace(line, ref i);
        if (i >= line.Length || line[i] != expected)
            return false;
        i++;
        return true;
    }

    private static void SkipWhitespace(string line, ref int i)
    {
        while (i < line.Length && line[i] is ' ' or '\t')
            i++;
    }
}
