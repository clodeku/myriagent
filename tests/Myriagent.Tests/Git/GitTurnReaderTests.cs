using Myriagent.Git;

namespace Myriagent.Tests.Git;

public sealed class GitTurnReaderTests : GitStatusReaderTestsBase
{
    [Fact]
    public async Task 排除開始前的修改_包含本輪修改新檔與已提交內容()
    {
        var (_, work) = CreateRemoteAndClone();
        Commit(work, "dirty.txt", "原始\n", "建立基準");
        Commit(work, "unchanged.txt", "原始\n", "建立另一個基準");
        File.WriteAllText(Path.Combine(work, "dirty.txt"), "開始前\n");
        File.WriteAllText(Path.Combine(work, "unchanged.txt"), "開始前就改過\n");
        var compare = new GitTurnReader();
        var before = await compare.CaptureAsync(work, CancellationToken.None);
        before.Should().NotBeNull();
        File.WriteAllText(Path.Combine(work, "dirty.txt"), "這一輪\n");
        Commit(work, "committed.txt", "本輪已提交\n", "提交本輪修改");
        File.WriteAllText(Path.Combine(work, "new.txt"), "未追蹤的新檔\n");
        var result = await compare.CompareAsync(before!, CancellationToken.None);
        result.Files.Select(f => f.Path).Should().Contain(["dirty.txt", "committed.txt", "new.txt"]).And.NotContain("unchanged.txt");
        result.ComparedPaths.Should().Contain("unchanged.txt", "已核對為未改變的檔案，不能再補上 CLI 的舊差異");
        var dirty = result.Files.Single(f => f.Path == "dirty.txt");
        dirty.Lines.Should().Contain(l => l.T == "del" && l.S == "開始前");
        dirty.Lines.Should().NotContain(l => l.S == "原始");
    }

    [Fact]
    public void 檔案標頭不會被內容裡的加減號冒充()
    {
        var parsed = TurnDiffRules.Parse("diff --git a/一.txt b/一.txt\n--- a/一.txt\n+++ b/一.txt\n@@ -1 +1 @@\n--- 內容\n+++ 內容\ndiff --git a/二.txt b/二.txt\n--- a/二.txt\n+++ b/二.txt\n@@ -1 +1 @@\n-a\n+b");
        parsed.Select(f => f.Path).Should().Equal("一.txt", "二.txt");
        TurnDiffRules.Display(parsed).Should().Contain(l => l.T == "hunk" && l.S == "二.txt");
    }
}
