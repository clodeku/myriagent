using Myriagent.Agents;
using Newtonsoft.Json;

namespace Myriagent.Git;

internal sealed record TurnFileDiff(string Path, IReadOnlyList<AgentDiffLine> Lines);

/// <summary>整輪差異保留各檔案的標頭，不把不同檔案的區塊混在一起。</summary>
internal static class TurnDiffRules
{
    public const int MaxFiles = 100;
    public static List<TurnFileDiff> Parse(string diff)
    {
        var result = new List<TurnFileDiff>();
        string? path = null;
        bool inHunk = false;
        var lines = new List<string>();
        void Flush()
        {
            if (path != null) result.Add(new(path, DiffRules.FromUnified(string.Join("\n", lines))));
            lines.Clear();
            path = null;
            inHunk = false;
        }
        foreach (var line in diff.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                var split = line.LastIndexOf(" b/", StringComparison.Ordinal);
                path = split >= 0 ? line[(split + 3)..] : line[11..];
            }
            if (line.StartsWith("@@", StringComparison.Ordinal)) inHunk = true;
            if (!inHunk && line.StartsWith("+++ ", StringComparison.Ordinal) && line != "+++ /dev/null") path = HeaderPath(line[4..]);
            else if (!inHunk && line.StartsWith("--- ", StringComparison.Ordinal) && path == null && line != "--- /dev/null") path = HeaderPath(line[4..]);
            lines.Add(line);
        }
        Flush();
        return result;
    }

    private static string HeaderPath(string value)
    {
        if (value.StartsWith('"'))
        {
            try { value = JsonConvert.DeserializeObject<string>(value) ?? value; }
            catch (JsonException) { }
        }
        return value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal) ? value[2..] : value;
    }

    public static List<AgentDiffLine> Display(IReadOnlyList<TurnFileDiff> files)
    {
        var result = new List<AgentDiffLine>();
        foreach (var file in files.Take(MaxFiles))
        {
            result.Add(new("hunk", file.Path));
            result.AddRange(file.Lines);
            if (result.Count > 2400)
            {
                result.Add(new("hunk", "⋯ 差異較長，請在編輯器查看完整內容"));
                break;
            }
        }
        if (files.Count > MaxFiles) result.Add(new("hunk", "⋯ 僅顯示前 100 個檔案"));
        return result;
    }
}
