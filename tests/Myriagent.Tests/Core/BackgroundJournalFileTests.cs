using Myriagent.Core;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Core;

/// <summary>背景活動日誌的檔案（logs\background\yyyy-MM-dd.jsonl）：一行一筆、依日期分檔、太大換檔、保留 30 天</summary>
public sealed class BackgroundJournalFileTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "myriagent-bgjournal-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void 一行一筆_依寫入的日期分檔_Flush之後都在檔案裡()
    {
        var now = new DateTime(2026, 10, 27, 23, 59, 59);
        using var journal = new BackgroundJournalFile(dir, () => now);
        journal.Write(new JObject { ["ev"] = "a", ["text"] = "中文\n換行" });
        journal.Write(new JObject { ["ev"] = "b" });
        now = now.AddSeconds(2);
        journal.Write(new JObject { ["ev"] = "c" });
        journal.Flush();

        var first = File.ReadAllLines(Path.Combine(dir, "2026-10-27.jsonl"));
        first.Select(l => JObject.Parse(l).Value<string>("ev")).Should().Equal("a", "b");
        JObject.Parse(first[0]).Value<string>("text").Should().Be("中文\n換行", "JSON 字串裡的換行跳脫掉，一筆仍是一行");
        File.ReadAllLines(Path.Combine(dir, "2026-10-28.jsonl")).Should().ContainSingle();
        File.ReadAllBytes(Path.Combine(dir, "2026-10-28.jsonl"))[0].Should().Be((byte)'{', "不帶 BOM");
    }

    [Fact]
    public async Task 背景寫檔_不呼叫Flush也會寫出()
    {
        using var journal = new BackgroundJournalFile(dir);
        journal.Write(new JObject { ["ev"] = "x" });
        var path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(File.Exists(path) && File.ReadAllText(path).Contains("\"x\"")) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        File.ReadAllText(path).Should().Contain("\"ev\":\"x\"");
    }

    [Fact]
    public void 超過大小上限換成點1的檔_取代上一份()
    {
        var now = new DateTime(2026, 10, 27, 9, 0, 0);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "2026-10-27.jsonl");
        using (var fs = File.Create(path))
            fs.SetLength(BackgroundJournalFile.MaxFileBytes);
        File.WriteAllText(Path.Combine(dir, "2026-10-27.1.jsonl"), "舊的");
        using var journal = new BackgroundJournalFile(dir, () => now);

        journal.Write(new JObject { ["ev"] = "after" });
        journal.Flush();

        new FileInfo(Path.Combine(dir, "2026-10-27.1.jsonl")).Length.Should().Be(BackgroundJournalFile.MaxFileBytes);
        File.ReadAllLines(path).Should().ContainSingle().Which.Should().Contain("after");
    }

    [Fact]
    public void 寫檔時刪掉30天前的日誌_別的檔不動()
    {
        var now = new DateTime(2026, 10, 27, 9, 0, 0);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "2026-09-20.jsonl"), "{}");
        File.WriteAllText(Path.Combine(dir, "2026-09-20.1.jsonl"), "{}");
        File.WriteAllText(Path.Combine(dir, "2026-10-01.jsonl"), "{}");
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "x");
        using var journal = new BackgroundJournalFile(dir, () => now);

        journal.Write(new JObject { ["ev"] = "x" });
        journal.Flush();

        Directory.GetFiles(dir).Select(Path.GetFileName).Should().BeEquivalentTo("2026-10-01.jsonl", "2026-10-27.jsonl", "readme.txt");
    }

    [Fact]
    public void Dispose之後寫入不做事也不丟例外()
    {
        var journal = new BackgroundJournalFile(dir);
        journal.Write(new JObject { ["ev"] = "before" });
        journal.Dispose();
        journal.Write(new JObject { ["ev"] = "after" });
        journal.Flush();
        File.ReadAllText(Directory.GetFiles(dir).Single()).Should().Contain("before").And.NotContain("after");
    }
}
