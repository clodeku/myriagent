using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>代理經 myriagent-remote 在電腦之間傳檔案的規則（CliFileRules；2026-11-15）</summary>
public sealed class CliFileRulesTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void 展開本機路徑_資料夾連子資料夾_子資料夾以資料夾名稱開頭()
    {
        using var temp = new TempDirectory("cli-files");
        var file = temp.WriteFile("報表.txt", "x");
        temp.WriteFile(Path.Combine("相簿", "a.txt"), "a");
        temp.WriteFile(Path.Combine("相簿", "2024", "b.txt"), "bb");

        var (files, problem) = CliFileRules.ExpandLocal([file, Path.Combine(temp.Root, "相簿")]);

        problem.Should().BeNull();
        files!.Select(f => (f.Name, f.Subfolder, f.Size)).Should().BeEquivalentTo(
        [
            ("報表.txt", string.Empty, 1L),
            ("a.txt", "相簿", 1L),
            ("b.txt", "相簿\\2024", 2L),
        ]);
    }

    [Fact]
    public void 展開本機路徑_不合格的說明原因()
    {
        using var temp = new TempDirectory("cli-files");
        CliFileRules.ExpandLocal(["relative.txt"]).Problem.Should().Contain("完整路徑");
        CliFileRules.ExpandLocal([@"\\server\share\a.txt"]).Problem.Should().Contain("完整路徑");
        CliFileRules.ExpandLocal([Path.Combine(temp.Root, "沒有.txt")]).Problem.Should().Contain("找不到");
        Directory.CreateDirectory(Path.Combine(temp.Root, "空的"));
        CliFileRules.ExpandLocal([Path.Combine(temp.Root, "空的")]).Problem.Should().Contain("沒有要傳的檔");
    }

    [Fact]
    public void 上傳結果轉成devicePut的一筆()
    {
        var (entry, problem) = CliFileRules.UploadEntry(201, $$"""{"uploadId":"U1","sha256":"{{Sha}}","size":12}""", "U1", "報表.txt");
        problem.Should().BeNull();
        entry!.ToString(Newtonsoft.Json.Formatting.None).Should().Be($$"""{"uploadId":"U1","name":"報表.txt","size":12,"sha256":"{{Sha}}"}""");

        CliFileRules.UploadEntry(201, $$"""{"uploadId":"U2","sha256":"{{Sha}}","size":12}""", "U1", "x").Problem.Should().Contain("回應不對");
        CliFileRules.UploadEntry(201, "不是 JSON", "U1", "x").Problem.Should().Contain("回應不對");
        CliFileRules.UploadEntry(0, null, "U1", "x").Problem.Should().Contain("沒有連上");
        CliFileRules.UploadEntry(507, null, "U1", "x").Problem.Should().Contain("磁碟空間");
    }

    [Theory]
    [InlineData("a.jpg", "相簿")]
    [InlineData("2024\\b.jpg", "相簿\\2024")]
    [InlineData("2024/12/c.jpg", "相簿\\2024\\12")]
    public void 資料夾下載的子資料夾(string relative, string expected) => CliFileRules.PullSubfolder("相簿", relative).Should().Be(expected);
}
