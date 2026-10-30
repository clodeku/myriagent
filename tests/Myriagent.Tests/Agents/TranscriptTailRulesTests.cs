using System.Text;
using Myriagent.Agents;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 熱更新契約：重開 CLI 前截掉逐字稿／rollout 寫到一半的尾行（CLI 直接接在半行後面寫會把下一筆黏成永久壞行），切下的位元組先備份
/// </summary>
public sealed class TranscriptTailRulesTests : IDisposable
{
    /// <summary>截尾一律用現在的時間：截尾成功時會清掉超過 7 天的備份，寫死的日期可能讓剛寫的備份被當成過期</summary>
    private readonly DateTime now = DateTime.Now;

    private static readonly DateTime Fixed = new(2026, 10, 10, 9, 30, 15, 123, DateTimeKind.Local);

    private readonly TempDirectory temp = new("tail");

    public void Dispose() => temp.Dispose();

    private string Backups => temp.Combine("sync", "partial");

    private string WriteBytes(string name, byte[] bytes)
    {
        var path = temp.Combine(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string WriteText(string name, string text) => WriteBytes(name, Encoding.UTF8.GetBytes(text));

    [Fact]
    public void 結尾不是LF_截到最後一個LF_切下的半行先備份()
    {
        var path = WriteText("s1.jsonl", "{\"a\":1}\n{\"b\":\"中文\"}\n{\"c\":\"寫到一");

        var result = TranscriptTailRules.TrimPartialLine(path, Backups, "s1", now);

        result.Outcome.Should().Be(TranscriptTailOutcome.Trimmed);
        File.ReadAllText(path).Should().Be("{\"a\":1}\n{\"b\":\"中文\"}\n");
        result.KeptLength.Should().Be(new FileInfo(path).Length);
        result.OriginalLength.Should().Be(result.KeptLength + Encoding.UTF8.GetByteCount("{\"c\":\"寫到一"));
        result.BackupPath.Should().Be(Path.Combine(Backups, TranscriptTailRules.BuildBackupFileName("s1", now)));
        File.ReadAllText(result.BackupPath!).Should().Be("{\"c\":\"寫到一", "切下的位元組原樣保留（UTF-8 字元被切半也一樣）");
    }

    [Fact]
    public void 結尾已是LF或空檔或不存在_都不動()
    {
        var intact = WriteText("ok.jsonl", "{\"a\":1}\r\n{\"b\":2}\n");
        var before = File.ReadAllBytes(intact);
        TranscriptTailRules.TrimPartialLine(intact, Backups, "ok", now).Should().Be(new TranscriptTailResult(TranscriptTailOutcome.Intact, before.Length, before.Length));
        File.ReadAllBytes(intact).Should().Equal(before);

        var empty = WriteBytes("empty.jsonl", Array.Empty<byte>());
        TranscriptTailRules.TrimPartialLine(empty, Backups, "empty", now).Outcome.Should().Be(TranscriptTailOutcome.Intact);

        TranscriptTailRules.TrimPartialLine(temp.Combine("missing.jsonl"), Backups, "missing", now).Outcome.Should().Be(TranscriptTailOutcome.Missing);
        TranscriptTailRules.TrimPartialLine(temp.Combine("no-such-dir", "x.jsonl"), Backups, "x", now).Outcome.Should().Be(TranscriptTailOutcome.Missing);
        Directory.Exists(Backups).Should().BeFalse("沒有截就不建立備份資料夾");
    }

    [Fact]
    public void 整個檔案只有半行_截成空檔()
    {
        var path = WriteText("only.jsonl", "{\"type\":\"user\",\"mess");
        var result = TranscriptTailRules.TrimPartialLine(path, Backups, "only", now);
        result.Outcome.Should().Be(TranscriptTailOutcome.Trimmed);
        result.KeptLength.Should().Be(0);
        new FileInfo(path).Length.Should().Be(0);
        File.ReadAllText(result.BackupPath!).Should().Be("{\"type\":\"user\",\"mess");
    }

    [Fact]
    public void CRLF只以LF為界_CR留在被截掉的那一段()
    {
        var path = WriteText("crlf.jsonl", "a\r\nb\r");
        TranscriptTailRules.TrimPartialLine(path, Backups, "crlf", now).Outcome.Should().Be(TranscriptTailOutcome.Trimmed);
        File.ReadAllText(path).Should().Be("a\r\n");
    }

    [Fact]
    public void 最後一行比一次讀的大小還長_往前多讀幾段才找到LF()
    {
        var head = Encoding.UTF8.GetBytes("{\"first\":true}\n");
        var huge = Encoding.UTF8.GetBytes(new string('x', 200_000));   // 超過 64 KB 的一次讀取量好幾倍
        var path = WriteBytes("big.jsonl", head.Concat(huge).ToArray());

        var result = TranscriptTailRules.TrimPartialLine(path, Backups, "big", now);

        result.Outcome.Should().Be(TranscriptTailOutcome.Trimmed);
        File.ReadAllBytes(path).Should().Equal(head);
        new FileInfo(result.BackupPath!).Length.Should().Be(huge.Length);
    }

    [Fact]
    public void 同一個時間截兩次_備份不互相覆寫()
    {
        var a = WriteText("a.jsonl", "1\npartial-a");
        var b = WriteText("b.jsonl", "1\npartial-b");
        var first = TranscriptTailRules.TrimPartialLine(a, Backups, "same", now).BackupPath;
        var second = TranscriptTailRules.TrimPartialLine(b, Backups, "same", now).BackupPath;
        second.Should().NotBe(first);
        File.ReadAllText(first!).Should().Be("partial-a");
        File.ReadAllText(second!).Should().Be("partial-b");
    }

    [Fact]
    public void 檔案被獨占時不動原檔_回報失敗()
    {
        var path = WriteText("locked.jsonl", "1\npartial");
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = TranscriptTailRules.TrimPartialLine(path, Backups, "locked", now);
            result.Outcome.Should().Be(TranscriptTailOutcome.Failed);
            result.Error.Should().NotBeNullOrWhiteSpace();
        }
        File.ReadAllText(path).Should().Be("1\npartial");
    }

    [Fact]
    public void 備份保留七天_截尾成功時順便清掉過期的()
    {
        Directory.CreateDirectory(Backups);
        var old = Path.Combine(Backups, "old-20261001-000000-000.tail");
        var recent = Path.Combine(Backups, "recent-20261008-000000-000.tail");
        var other = Path.Combine(Backups, "notes.txt");
        foreach (var file in new[] { old, recent, other })
            File.WriteAllText(file, "x");
        File.SetLastWriteTime(old, now.AddDays(-8));
        File.SetLastWriteTime(recent, now.AddDays(-2));
        File.SetLastWriteTime(other, now.AddDays(-30));

        TranscriptTailRules.TrimPartialLine(WriteText("c.jsonl", "1\n2"), Backups, "c", now).Outcome.Should().Be(TranscriptTailOutcome.Trimmed);

        File.Exists(old).Should().BeFalse("超過 7 天");
        File.Exists(recent).Should().BeTrue();
        File.Exists(other).Should().BeTrue("只清 .tail");
        TranscriptTailRules.CleanupBackups(temp.Combine("none"), now).Should().Be(0);
    }

    [Theory]
    [InlineData(new byte[] { (byte)'a', (byte)'\n', (byte)'b' }, 10L, 9L)]
    [InlineData(new byte[] { (byte)'\n' }, 1L, 1L)]
    [InlineData(new byte[] { (byte)'\n', (byte)'x', (byte)'\n', (byte)'y', (byte)'y' }, 5L, 3L)]
    public void FindKeepLength_回傳最後一個LF之後的位置(byte[] tail, long fileLength, long expected)
        => TranscriptTailRules.FindKeepLength(tail, fileLength).Should().Be(expected);

    [Fact]
    public void FindKeepLength_這一段沒有LF回null_備份檔名換掉不能用的字元()
    {
        TranscriptTailRules.FindKeepLength("abc"u8, 3).Should().BeNull();
        TranscriptTailRules.FindKeepLength(ReadOnlySpan<byte>.Empty, 0).Should().BeNull();
        TranscriptTailRules.BuildBackupFileName("a:b/c", Fixed).Should().Be("a_b_c-20261010-093015-123.tail");
        TranscriptTailRules.BuildBackupFileName(" ", Fixed).Should().Be("transcript-20261010-093015-123.tail");
    }
}
