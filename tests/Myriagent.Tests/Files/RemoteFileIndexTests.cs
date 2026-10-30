using Myriagent.Files;
using Myriagent.Sync;
using Myriagent.Tests.Sync;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;

namespace Myriagent.Tests.Files;

/// <summary>
/// 遠端 @ 檔案提示的索引與遠端列目錄（WP4.7 計畫步 11，D33、D35）：junction 背後的檔不會出現也不會走進去（正式的重新解析判斷）、
/// 拒絕清單的檔名不收、禁止的資料夾不走進去、自己的快取 60 秒內重用最多 8 個根目錄；列目錄資料夾在前、自然排序、最多 3000 筆
/// </summary>
public sealed class RemoteFileIndexTests : IDisposable
{
    private readonly TempDirectory temp = new("remote-file-index");
    private readonly TempDirectory outside = new("remote-file-index-outside");

    public void Dispose()
    {
        // junction 先拆（遞迴刪除不會穿過 junction，但先拆掉比較清楚）
        foreach (var link in Directory.EnumerateDirectories(temp.Root, "*", SearchOption.AllDirectories).Where(Junction.IsJunction).ToList())
            Directory.Delete(link);
        temp.Dispose();
        outside.Dispose();
    }

    private string Write(string relative, string content = "x") => temp.WriteFile(relative, content);

    private static Func<string, bool> NoExclusion => _ => false;

    [FileSystemFact(FileSystemFeature.Junction)]
    public void BuildIndex_junction背後的檔不會出現_對照組全放行時會出現()
    {
        Write(@"a.txt");
        Write(@"sub\b.txt");
        outside.WriteFile(@"secret\data.txt", "x");
        Junction.Create(temp.Combine("link"), outside.Combine("secret"));

        var files = RemoteFileIndex.BuildIndex(temp.Root, RemoteFileGuard.IsAllowedReparsePoint, NoExclusion, CancellationToken.None);

        files.Should().BeEquivalentTo([@"a.txt", @"sub\b.txt"]);

        // 對照：重新解析點全放行時，同一棵樹看得到 junction 背後的檔（確認上面不是因為 junction 沒建起來）
        RemoteFileIndex.BuildIndex(temp.Root, _ => true, NoExclusion, CancellationToken.None).Should().Contain(@"link\data.txt");
    }

    [Fact]
    public void BuildIndex_拒絕清單的檔名不收_禁止的資料夾與建置輸出不走進去()
    {
        Write(@"keep.md");
        Write(@".env");
        Write(@".env.local");
        Write(@"config\appsettings.Production.json");
        Write(@"keys\id_rsa");
        Write(@"keys\cert.pfx");
        Write(@".git\config");
        Write(@"node_modules\pkg\index.js");
        Write(@"denied\inside.txt");
        Write(@"src\code.cs");

        var denied = temp.Combine("denied");
        var files = RemoteFileIndex.BuildIndex(temp.Root, _ => false, d => string.Equals(d, denied, StringComparison.OrdinalIgnoreCase), CancellationToken.None);

        files.Should().BeEquivalentTo([@"keep.md", @"src\code.cs"]);
    }

    [Fact]
    public async Task SearchAsync_60秒內重用索引_過了重建_找得到新檔()
    {
        Write(@"docs\readme.md");
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-10-21T00:00:00Z"));
        var index = new RemoteFileIndex(_ => false, clock);

        (await index.SearchAsync(temp.Root, "readme", NoExclusion, CancellationToken.None)).Should().ContainSingle()
            .Which.FullPath.Should().Be(temp.Combine(@"docs\readme.md"));
        Write(@"docs\readme2.md");
        clock.Advance(TimeSpan.FromSeconds(59));
        (await index.SearchAsync(temp.Root, "readme", NoExclusion, CancellationToken.None)).Should().HaveCount(1, "60 秒內用快取");
        index.Builds.Should().Be(1);

        clock.Advance(TimeSpan.FromSeconds(2));
        (await index.SearchAsync(temp.Root, "readme", NoExclusion, CancellationToken.None)).Should().HaveCount(2);
        index.Builds.Should().Be(2);
    }

    [Fact]
    public async Task SearchAsync_快取最多8個根目錄()
    {
        var index = new RemoteFileIndex(_ => false, new ManualTimeProvider(DateTimeOffset.Parse("2026-10-21T00:00:00Z")));
        for (var i = 0; i < RemoteFileIndex.MaxCachedIndexes + 2; i++)
        {
            var root = temp.CreateDirectory($"root{i}");
            await index.SearchAsync(root, "x", NoExclusion, CancellationToken.None);
        }

        index.CachedIndexCount.Should().Be(RemoteFileIndex.MaxCachedIndexes);
        index.Builds.Should().Be(RemoteFileIndex.MaxCachedIndexes + 2);
    }

    [FileSystemFact(FileSystemFeature.Junction)]
    public void ListDirectory_資料夾在前_自然排序_junction不列_點開頭的是隱藏()
    {
        Write(@"file10.txt");
        Write(@"file2.txt");
        Write(@"photo.png", "png");
        Write(@".hidden");
        temp.CreateDirectory("zeta");
        temp.CreateDirectory("Alpha");
        outside.CreateDirectory("elsewhere");
        Junction.Create(temp.Combine("link"), outside.Combine("elsewhere"));

        var (entries, truncated) = RemoteFileIndex.ListDirectory(temp.Root, RemoteFileGuard.IsAllowedReparsePoint, dirsOnly: false);

        truncated.Should().BeFalse();
        var names = entries.Select(e => e.Name).ToList();
        names.Take(2).Should().Equal("Alpha", "zeta");
        names.Skip(2).Should().BeEquivalentTo([".hidden", "file2.txt", "file10.txt", "photo.png"]);
        names.IndexOf("file2.txt").Should().BeLessThan(names.IndexOf("file10.txt"), "自然排序");
        entries.Take(2).Should().AllSatisfy(e =>
        {
            e.Kind.Should().Be("dir");
            e.Size.Should().BeNull();
        });
        entries.Single(e => e.Name == "photo.png").Should().Match<FileEntry>(e => e.Kind == "image" && e.Size == 3 && e.Path == temp.Combine("photo.png"));
        entries.Single(e => e.Name == ".hidden").Hidden.Should().BeTrue();
        entries.Single(e => e.Name == "file2.txt").Hidden.Should().BeFalse();

        // 對照：全放行時 junction 會列出來
        RemoteFileIndex.ListDirectory(temp.Root, _ => true, dirsOnly: false).Entries.Select(e => e.Name).Should().Contain("link");
    }

    [Fact]
    public void ListDirectory_dirsOnly只列資料夾_不存在丟DirectoryNotFound()
    {
        Write(@"a.txt");
        temp.CreateDirectory("sub");

        RemoteFileIndex.ListDirectory(temp.Root, _ => false, dirsOnly: true).Entries.Select(e => e.Name).Should().Equal("sub");
        FluentActions.Invoking(() => RemoteFileIndex.ListDirectory(temp.Combine("missing"), _ => false, dirsOnly: false))
            .Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void ListDirectory_超過3000筆截斷()
    {
        var dir = temp.CreateDirectory("many");
        for (var i = 0; i < FileBrowser.MaxEntries + 5; i++)
            File.WriteAllBytes(Path.Combine(dir, $"f{i:0000}.txt"), []);

        var (entries, truncated) = RemoteFileIndex.ListDirectory(dir, _ => false, dirsOnly: false);

        truncated.Should().BeTrue();
        entries.Should().HaveCount(FileBrowser.MaxEntries);
    }
}
