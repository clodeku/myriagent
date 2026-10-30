using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace MyriagentHub.Tests.Rendering;

/// <summary>
/// 伺服器渲染的黃金檔（tests/MyriagentHub.Tests/Rendering/Golden/*.json）的寫出與逐位元組比對，格式同桌面的 GoldenFile：
/// UTF-8 無 BOM、LF、2 空白縮排、結尾一個換行；縮排只影響空白，值與順序和送給瀏覽器的 JSON（Formatting.None）完全一致。
/// 比對讀的是建置時複製到輸出資料夾的檔案；刻意改變輸出時以環境變數 MYRIAGENT_GOLDEN_RECORD=1 重跑，直接寫回 repo 的原始檔，逐項檢查差異再提交
/// </summary>
internal static class HubGoldenFile
{
    public const string RecordVariable = "MYRIAGENT_GOLDEN_RECORD";

    private static readonly UTF8Encoding Utf8 = new(false);

    public static bool Recording => Environment.GetEnvironmentVariable(RecordVariable) == "1";

    public static string Serialize(JToken token)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
        using (var writer = new JsonTextWriter(text) { Formatting = Formatting.Indented, Indentation = 2, IndentChar = ' ' })
            token.WriteTo(writer);
        return text.ToString() + "\n";
    }

    /// <summary>與黃金檔逐位元組比對；錄製模式時改成寫回 repo（黃金檔在呼叫端原始檔旁邊的 Golden 資料夾）</summary>
    public static void Verify(string name, JToken actual, [CallerFilePath] string sourceFile = "")
    {
        var bytes = Utf8.GetBytes(Serialize(actual));
        if (Recording)
        {
            var directory = Path.Combine(Path.GetDirectoryName(sourceFile) ?? string.Empty, "Golden");
            if (!Directory.Exists(Path.GetDirectoryName(directory)))
                throw new XunitException($"找不到測試原始檔的資料夾 {Path.GetDirectoryName(directory)}，無法錄製黃金檔");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, name + ".json"), bytes);
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Rendering", "Golden", name + ".json");
        if (!File.Exists(path))
            throw new XunitException($"黃金檔 {name}.json 不存在（{path}）；第一次請以 {RecordVariable}=1 執行這個測試錄製");
        var expected = File.ReadAllBytes(path);
        if (expected.AsSpan().SequenceEqual(bytes))
            return;

        var actualPath = Path.Combine(Path.GetTempPath(), "MyriagentHub.Tests", "golden-actual", name + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(actualPath)!);
        File.WriteAllBytes(actualPath, bytes);
        throw new XunitException($"伺服器渲染的結果與黃金檔 {name}.json 不同（{Describe(expected, bytes)}）。這次的輸出：{actualPath}。"
                                 + $"若是刻意改變輸出，確認差異後以 {RecordVariable}=1 重跑重錄");
    }

    private static string Describe(byte[] expected, byte[] actual)
    {
        var expectedLines = Utf8.GetString(expected).Split('\n');
        var actualLines = Utf8.GetString(actual).Split('\n');
        for (var i = 0; i < Math.Max(expectedLines.Length, actualLines.Length); i++)
        {
            var e = i < expectedLines.Length ? expectedLines[i] : "（檔案結束）";
            var a = i < actualLines.Length ? actualLines[i] : "（輸出結束）";
            if (e != a)
                return $"第 {i + 1} 行：黃金檔「{e.Trim()}」，這次「{a.Trim()}」";
        }

        return $"長度 {expected.Length} 與 {actual.Length} 位元組，逐行內容相同";
    }
}
