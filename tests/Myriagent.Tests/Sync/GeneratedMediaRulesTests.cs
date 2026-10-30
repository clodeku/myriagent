using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;

namespace Myriagent.Tests.Sync;

/// <summary>
/// Codex 產生的圖片（WP4.7 計畫步 9，D45，Q2-A）：根目錄、路徑判斷與複本檔名（純函式），以及守門員的窄檢查與複製（真的暫存檔）
/// </summary>
public sealed class GeneratedMediaRulesTests : IDisposable
{
    private const string ItemId = "item-1";

    /// <summary>完整的 PNG（魔數與 IEND）：內容檢查認得</summary>
    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 1, 2, 3, 4,
        0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

    private readonly TempDirectory temp = new("generated-media");

    public void Dispose() => temp.Dispose();

    private static RemoteFileGuard Guard() => new(_ => RemoteDriveKind.Fixed, TimeSpan.FromSeconds(10), TimeProvider.System);

    // ── 純函式 ──

    [Fact]
    public void Roots_使用者的codex_繼承的CODEX_HOME_每個帳戶資料夾_不重複()
    {
        var folders = new MediaFolderRoots(@"C:\Users\Amy", @"C:\Users\Amy\AppData\Local", null, @"C:\Users\Amy\AppData\Local\Myriagent",
            [@"C:\Users\Amy\AppData\Local\Myriagent\accounts\a1\", @"C:\Users\Amy\.codex", ""], CodexHome: @"D:\codex-home");

        GeneratedMediaRules.Roots(folders).Should().Equal(
            @"C:\Users\Amy\.codex\generated_images",
            @"D:\codex-home\generated_images",
            @"C:\Users\Amy\AppData\Local\Myriagent\accounts\a1\generated_images");
    }

    [Theory]
    [InlineData(@"C:\Users\Amy\.codex\generated_images\abc\img.png", true)]
    [InlineData(@"c:\users\amy\.codex\GENERATED_IMAGES\img.png", true)]
    [InlineData(@"C:/Users/Amy/.codex/generated_images/img.png", true)]
    [InlineData(@"C:\Users\Amy\.codex\generated_images", false)]
    [InlineData(@"C:\Users\Amy\.codex\generated_images_x\img.png", false)]
    [InlineData(@"C:\Users\Amy\.codex\generated_images\..\auth.json", false)]
    [InlineData(@"C:\Users\Amy\.codex\auth.json", false)]
    [InlineData(@"\\nas\Users\Amy\.codex\generated_images\img.png", false)]
    [InlineData(null, false)]
    public void IsGenerated(string? path, bool expected)
        => GeneratedMediaRules.IsGenerated(path, [@"C:\Users\Amy\.codex\generated_images"]).Should().Be(expected);

    [Fact]
    public void CopyPath_attachments的generated_項目id加序號_副檔名小寫()
    {
        GeneratedMediaRules.CopyPath(@"C:\data\conversations\c1\attachments", ItemId, 1, @"C:\Users\Amy\.codex\generated_images\x\Image.PNG")
            .Should().Be(@"C:\data\conversations\c1\attachments\generated\item-1-1.png");
        GeneratedMediaRules.CopyPath(@"C:\data\attachments", ItemId, 3, @"C:\g\a.jpeg").Should().Be(@"C:\data\attachments\generated\item-1-3.jpeg");
    }

    [Theory]
    [InlineData("item-1", 1, @"C:\g\..\a.png")]
    [InlineData("..", 1, @"C:\g\a.png")]
    [InlineData(@"..\x", 1, @"C:\g\a.png")]
    [InlineData("item-1", 0, @"C:\g\a.png")]
    [InlineData("item-1", 1, @"C:\g\a.bmp")]
    [InlineData("item-1", 1, @"C:\g\a.mp4")]
    [InlineData("item-1", 1, @"C:\g\a.png.exe")]
    [InlineData("item-1", 1, "")]
    public void CopyPath_不合格回null(string itemId, int sequence, string source)
        => GeneratedMediaRules.CopyPath(@"C:\data\attachments", itemId, sequence, source).Should().BeNull();

    [Fact]
    public void Sequence_從1起_不分大小寫_不在回null()
    {
        GeneratedMediaRules.Sequence([@"C:\a.png", @"C:\B.png"], @"c:\b.PNG").Should().Be(2);
        GeneratedMediaRules.Sequence([@"C:\a.png"], @"C:\c.png").Should().BeNull();
        GeneratedMediaRules.Sequence(null, @"C:\a.png").Should().BeNull();
    }

    // ── 守門員的窄檢查與複製 ──

    private (string Root, string Source, string Destination) Layout(byte[]? content = null)
    {
        var root = temp.CreateDirectory(Path.Combine("home", ".codex", "generated_images"));
        var source = Path.Combine(root, "session", "img.png");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, content ?? Png);
        var attachments = temp.CreateDirectory(Path.Combine("data", "conversations", "c1", "attachments"));
        return (root, source, GeneratedMediaRules.CopyPath(attachments, ItemId, 1, source)!);
    }

    [Fact]
    public async Task 複製_位元組相同_不是硬連結_第二次沿用複本()
    {
        var (root, source, destination) = Layout();
        var copier = new GeneratedMediaCopier(Guard());

        (await copier.EnsureCopyAsync(source, destination, [root], CancellationToken.None)).Should().Be(destination);
        File.ReadAllBytes(destination).Should().Equal(Png);
        copier.Copied.Should().Be(1);
        Directory.GetFiles(Path.GetDirectoryName(destination)!).Should().ContainSingle("暫存檔清掉了");

        // 守門員照樣開得了複本（硬連結數是 1）
        File.Delete(source);
        (await copier.EnsureCopyAsync(source, destination, [root], CancellationToken.None)).Should().Be(destination, "複本已經在：不碰原檔");
        copier.Copied.Should().Be(1);
    }

    [Fact]
    public async Task 複製_內容不是PNG或不完整_不複製()
    {
        var (root, source, destination) = Layout([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3]);
        var copier = new GeneratedMediaCopier(Guard());

        (await copier.EnsureCopyAsync(source, destination, [root], CancellationToken.None)).Should().BeNull();
        File.Exists(destination).Should().BeFalse();
    }

    [Fact]
    public async Task 守門員_不在根目錄底下的拒絕_不碰檔案系統()
    {
        var (root, _, _) = Layout();
        var guard = Guard();
        var other = temp.Combine("home", ".codex", "auth.png");
        File.WriteAllBytes(other, Png);

        var opened = await guard.OpenGeneratedSourceAsync(other, [root], GeneratedMediaRules.MaxCopyBytes, CancellationToken.None);

        opened.File.Should().BeNull();
        opened.Reason.Should().Be(RemoteFileGuardReasons.Scope);
        guard.FileSystemStages.Should().Be(0);
        (await guard.OpenGeneratedSourceAsync(Path.Combine(root, "x.json"), [root], 100, CancellationToken.None)).Reason.Should().Be(RemoteFileGuardReasons.Type);
        (await guard.OpenGeneratedSourceAsync(@"\\nas\share\generated_images\x.png", [root], 100, CancellationToken.None)).Reason.Should().Be(RemoteFileGuardReasons.Unc);
    }

    [FileSystemFact(FileSystemFeature.HardLink)]
    public async Task 守門員_硬連結與太大的拒絕()
    {
        var (root, source, _) = Layout();
        var guard = Guard();

        (await guard.OpenGeneratedSourceAsync(source, [root], 4, CancellationToken.None)).Reason.Should().Be(RemoteFileGuardReasons.TooLarge);

        var link = Path.Combine(root, "link.png");
        TestFileSystem.TryCreateHardLink(link, source).Should().BeTrue("建得了硬連結（同一個磁碟）");
        (await guard.OpenGeneratedSourceAsync(link, [root], GeneratedMediaRules.MaxCopyBytes, CancellationToken.None)).Reason.Should().Be(RemoteFileGuardReasons.HardLink);
    }

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task 複本的資料夾是junction_不寫()
    {
        var (root, source, destination) = Layout();
        var attachments = Path.GetDirectoryName(Path.GetDirectoryName(destination)!)!;
        var elsewhere = temp.CreateDirectory("elsewhere");
        Junction.Create(Path.Combine(attachments, GeneratedMediaRules.CopyFolder), elsewhere);
        var copier = new GeneratedMediaCopier(Guard());

        (await copier.EnsureCopyAsync(source, destination, [root], CancellationToken.None)).Should().BeNull();
        Directory.GetFiles(elsewhere).Should().BeEmpty("不經重新解析點寫到別的地方");
    }
}
