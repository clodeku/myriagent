using Myriagent.Core;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Core;

/// <summary>記錄檔：結束前同步寫出還在佇列裡的記錄；單檔超過上限就換新檔</summary>
[Collection(AppPathsCollection.Name)]
public sealed class LogTests : IDisposable
{
    private readonly TempDirectory temp = new("log");
    private readonly AppPathsScope scope;

    public LogTests() => scope = new AppPathsScope(temp.Root);

    public void Dispose()
    {
        Log.Flush();
        scope.Dispose();
        temp.Dispose();
    }

    /// <summary>讀所有記錄檔（背景執行緒可能正在附加其他測試的記錄，要允許共用寫入）</summary>
    private string ReadLogs()
        => string.Concat(Directory.GetFiles(AppPaths.LogDirectory, "*.log").Select(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));

    [Fact]
    public void Flush_同步寫出還在佇列裡的記錄_不必等背景執行緒()
    {
        var marker = "結束前的最後一行 " + Guid.NewGuid().ToString("N");
        for (int i = 0; i < 5; i++)
            Log.Warn($"{marker} #{i}");

        Log.Flush();

        var text = ReadLogs();
        for (int i = 0; i < 5; i++)
            text.Should().Contain($"{marker} #{i}", "Flush 回來時，之前記下的每一行都已寫進檔案（程序結束時執行緒集區的工作會被直接終止）");
        text.IndexOf($"{marker} #0", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf($"{marker} #4", StringComparison.Ordinal), "順序不變");
    }

    [Fact]
    public void 記錄檔超過上限_改名成1號檔後寫新檔_再滿一次取代上一份()
    {
        var path = temp.WriteFile("2026-10-10.log", new string('x', 2000));
        var rolled = Path.Combine(temp.Root, "2026-10-10.1.log");

        Log.RollIfTooLarge(path, maxBytes: 1000);

        File.Exists(path).Should().BeFalse();
        File.ReadAllText(rolled).Should().HaveLength(2000);

        var small = temp.WriteFile("2026-10-11.log", "短短的");
        Log.RollIfTooLarge(small, maxBytes: 1000);
        File.Exists(small).Should().BeTrue("沒超過上限就不動");

        File.WriteAllText(path, new string('y', 1500));
        Log.RollIfTooLarge(path, maxBytes: 1000);
        File.ReadAllText(rolled).Should().StartWith("y", "每天最多兩個檔，舊的 1 號檔被取代");
    }
}
