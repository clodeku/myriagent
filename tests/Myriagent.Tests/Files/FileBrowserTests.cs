using System.Security.AccessControl;
using System.Security.Principal;
using Myriagent.Files;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Files;

/// <summary>對話內的檔案瀏覽（資料夾在前、自然排序）與 @ 檔案提示（索引略過建置輸出與版控、模糊比對排序）</summary>
public sealed class FileBrowserTests : IDisposable
{
    private readonly TempDirectory temp = new("files");

    public void Dispose() => temp.Dispose();

    [Fact]
    public void Score_檔名完全相符大於開頭大於包含大於路徑包含大於子序列()
    {
        var exact = FileBrowser.Score("app", @"tools\app");
        var prefix = FileBrowser.Score("app", @"src\app.js");
        var contains = FileBrowser.Score("app", @"lib\myapp.ts");
        var pathContains = FileBrowser.Score("app", @"app\index.html");
        var subsequence = FileBrowser.Score("app", @"a\plan\p.txt");

        exact.Should().BeGreaterThan(prefix);
        prefix.Should().BeGreaterThan(contains);
        contains.Should().BeGreaterThan(pathContains);
        pathContains.Should().BeGreaterThan(subsequence);
        subsequence.Should().BeGreaterThan(0);
        FileBrowser.Score("app", @"readme.md").Should().Be(0, "字元沒有依序出現就不命中");
        FileBrowser.Score("APP", @"src\App.js").Should().Be(prefix, "不分大小寫");
    }

    [Fact]
    public void Score_同一級距裡路徑短的優先_子序列在分隔字元後加分()
    {
        FileBrowser.Score("app", @"src\app.js").Should().BeGreaterThan(FileBrowser.Score("app", @"src\deep\folder\app.js"));
        FileBrowser.Score("ab", @"x\a_b").Should().BeGreaterThan(FileBrowser.Score("ab", @"x\axxb"), "b 緊接在分隔字元後面");
    }

    [Fact]
    public void Search_依分數排序_同分依長度與名稱_限制筆數()
    {
        var files = new[] { @"a\plan\p.txt", @"readme.md", @"app\index.html", @"lib\myapp.ts", @"src\app.js", @"tools\app" };

        var results = FileBrowser.Search(files, @"C:\proj", "app", 30);

        results.Select(r => r.RelativePath).Should().Equal(@"tools\app", @"src\app.js", @"lib\myapp.ts", @"app\index.html", @"a\plan\p.txt");
        results[1].FullPath.Should().Be(@"C:\proj\src\app.js");
        results[1].Kind.Should().Be("code");
        FileBrowser.Search(files, @"C:\proj", "app", 2).Should().HaveCount(2);
    }

    [Fact]
    public void Search_斜線方向不影響_空查詢依路徑長度()
    {
        var files = new[] { @"src\web\app.js", @"b.txt", @"docs\guide.md" };

        FileBrowser.Search(files, @"C:\p", "web/app", 30).Single().RelativePath.Should().Be(@"src\web\app.js");
        FileBrowser.Search(files, @"C:\p", "  ", 30).Select(r => r.RelativePath).Should().Equal(@"b.txt", @"docs\guide.md", @"src\web\app.js");
    }

    [Fact]
    public void BuildIndex_略過NodeModules_Obj與Git_NET專案的Bin()
    {
        temp.WriteFile("a.txt");
        temp.WriteFile("app.csproj");
        temp.WriteFile(@"src\b.cs");
        temp.WriteFile(@"src\obj\generated.cs");
        temp.WriteFile(@"node_modules\pkg\index.js");
        temp.WriteFile(@"bin\Release\app.dll");
        temp.WriteFile(@"obj\project.assets.json");
        temp.WriteFile(@".git\HEAD");
        temp.WriteFile(@"Bin2\keep.txt");

        var index = FileBrowser.BuildIndex(temp.Root, CancellationToken.None);

        index.Should().BeEquivalentTo(new[] { "a.txt", "app.csproj", @"src\b.cs", @"Bin2\keep.txt" });
        index[0].Should().Be("a.txt", "廣度優先：根目錄的檔案先列");
    }

    [Fact]
    public void BuildIndex_JS專案的packages與bin照樣索引_NET方案旁的packages與專案旁的bin才略過()
    {
        temp.WriteFile("package.json");
        temp.WriteFile(@"packages\ui\src\Button.tsx");
        temp.WriteFile(@"packages\core\src\index.ts");
        temp.WriteFile(@"apps\web\src\main.ts");
        temp.WriteFile(@"bin\cli.js");
        temp.WriteFile(@"dotnet\App.csproj");
        temp.WriteFile(@"dotnet\Program.cs");
        temp.WriteFile(@"dotnet\bin\Release\App.dll");
        temp.WriteFile(@"legacy\Legacy.sln");
        temp.WriteFile(@"legacy\packages\Newtonsoft.Json.13.0.1\lib\Newtonsoft.Json.dll");

        var index = FileBrowser.BuildIndex(temp.Root, CancellationToken.None);

        index.Should().BeEquivalentTo(new[]
        {
            "package.json", @"packages\ui\src\Button.tsx", @"packages\core\src\index.ts", @"apps\web\src\main.ts", @"bin\cli.js",
            @"dotnet\App.csproj", @"dotnet\Program.cs", @"legacy\Legacy.sln",
        });
        FileBrowser.Search(index, temp.Root, "Button", 30).Select(r => r.RelativePath).Should().Equal(@"packages\ui\src\Button.tsx");
    }

    [Fact]
    public async Task SearchAsync_在工作資料夾建索引後搜尋_UNC與不存在的資料夾回空()
    {
        temp.WriteFile(@"src\Program.cs");
        temp.WriteFile(@"node_modules\Program.cs");

        var results = await FileBrowser.SearchAsync(temp.Root, "program", CancellationToken.None);

        results.Select(r => r.RelativePath).Should().Equal(@"src\Program.cs");
        (await FileBrowser.SearchAsync(@"\\server\share", "a", CancellationToken.None)).Should().BeEmpty();
        (await FileBrowser.SearchAsync(temp.Combine("沒有"), "a", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public void List_資料夾在前_自然排序_標示種類大小與隱藏()
    {
        temp.CreateDirectory("zeta");
        temp.CreateDirectory("Alpha");
        temp.WriteFile("img10.png", "1234567890");
        temp.WriteFile("img2.png");
        temp.WriteFile("img1.png");
        temp.WriteFile("notes.md");
        var hidden = temp.WriteFile(".env");
        var hiddenByAttribute = temp.WriteFile("secret.txt");
        File.SetAttributes(hiddenByAttribute, FileAttributes.Hidden);

        var (entries, truncated) = FileBrowser.List(temp.Root);

        truncated.Should().BeFalse();
        entries.Take(2).Select(e => e.Name).Should().Equal("Alpha", "zeta");
        entries.Take(2).Should().OnlyContain(e => e.Kind == "dir" && e.Size == null);
        var names = entries.Select(e => e.Name).ToList();
        names.IndexOf("img1.png").Should().BeLessThan(names.IndexOf("img2.png"));
        names.IndexOf("img2.png").Should().BeLessThan(names.IndexOf("img10.png"), "自然排序：img2 在 img10 前面");
        var img10 = entries.Single(e => e.Name == "img10.png");
        img10.Kind.Should().Be("image");
        img10.Size.Should().Be(10);
        img10.Path.Should().Be(Path.Combine(temp.Root, "img10.png"));
        entries.Single(e => e.Name == "notes.md").Kind.Should().Be("doc");
        entries.Single(e => e.Path == hidden).Hidden.Should().BeTrue("點開頭的檔案視為隱藏");
        entries.Single(e => e.Path == hiddenByAttribute).Hidden.Should().BeTrue();
        entries.Single(e => e.Name == "notes.md").Hidden.Should().BeFalse();
    }

    [Fact]
    public void List_拒絕UNC與相對路徑_找不到資料夾丟例外()
    {
        FluentActions.Invoking(() => FileBrowser.List(@"\\server\share\folder")).Should().Throw<UnauthorizedAccessException>();
        FluentActions.Invoking(() => FileBrowser.List(@"relative\folder")).Should().Throw<UnauthorizedAccessException>();
        FluentActions.Invoking(() => FileBrowser.List(temp.Combine("沒有這個"))).Should().Throw<DirectoryNotFoundException>();
    }

    [Fact]
    public void List_沒有權限的資料夾丟例外_不當成空資料夾()
    {
        var locked = temp.CreateDirectory("locked");
        temp.WriteFile(@"locked\a.txt");
        var info = new DirectoryInfo(locked);
        var security = info.GetAccessControl();
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        security.AddAccessRule(deny);
        info.SetAccessControl(security);
        try
        {
            FluentActions.Invoking(() => FileBrowser.List(locked)).Should().Throw<UnauthorizedAccessException>().WithMessage("*沒有權限*");
        }
        finally
        {
            security.RemoveAccessRule(deny);
            info.SetAccessControl(security);
        }
    }

    [Fact]
    public void List_超過上限時資料夾仍全部列出_檔案才截斷()
    {
        for (int i = 0; i < FileBrowser.MaxEntries + 5; i++)
            File.WriteAllBytes(temp.Combine($"a{i:0000}.txt"), Array.Empty<byte>());
        temp.CreateDirectory("b-subfolder");
        temp.CreateDirectory("zz-subfolder");

        var (entries, truncated) = FileBrowser.List(temp.Root);

        truncated.Should().BeTrue();
        entries.Should().HaveCount(FileBrowser.MaxEntries);
        entries.Take(2).Select(e => e.Name).Should().Equal("b-subfolder", "zz-subfolder");
        entries.Skip(2).Should().OnlyContain(e => e.Kind != "dir");
    }

    [Fact]
    public void NaturalCompare_數字依數值比較()
    {
        FileBrowser.NaturalCompare("img2", "img10").Should().BeNegative();
        FileBrowser.NaturalCompare("img10", "img2").Should().BePositive();
        FileBrowser.NaturalCompare("a", "A").Should().Be(0, "不分大小寫");
        FileBrowser.NaturalCompare(null, "a").Should().BeNegative();
    }

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a")]
    [InlineData(@"C:\a\b\", @"C:\a")]
    [InlineData(@"C:\a", @"C:\")]
    [InlineData(@"D:\", null)]
    [InlineData(@"C:\", null)]
    [InlineData("C:/a/b/", @"C:\a")]
    public void GetParent_上一層_磁碟根目錄為Null(string directory, string? expected)
        => FileBrowser.GetParent(directory).Should().Be(expected);
}
