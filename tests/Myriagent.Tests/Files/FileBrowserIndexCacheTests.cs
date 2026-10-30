using Myriagent.Files;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Files;

/// <summary>@ 檔案提示的索引快取有上限：用過很多工作資料夾，也只保留最近的幾個</summary>
public sealed class FileBrowserIndexCacheTests : IDisposable
{
    private readonly TempDirectory temp = new("index-cache");

    public void Dispose() => temp.Dispose();

    [Fact]
    public async Task 用過很多資料夾_索引快取不超過上限_最近用的仍可搜尋()
    {
        string? last = null;
        for (int i = 0; i < FileBrowser.MaxCachedIndexes + 4; i++)
        {
            last = temp.CreateDirectory($"proj{i}");
            temp.WriteFile($@"proj{i}\file{i}.txt");
            (await FileBrowser.SearchAsync(last, $"file{i}", CancellationToken.None)).Should().ContainSingle();
        }

        // 其他測試（FileBrowserTests、中樞的 @ 提示）可能剛好同時加一個還沒修剪，最多多出兩個；沒有上限時這裡至少是 12 個
        FileBrowser.CachedIndexCount.Should().BeLessThanOrEqualTo(FileBrowser.MaxCachedIndexes + 2);
        (await FileBrowser.SearchAsync(last!, "file", CancellationToken.None)).Should().ContainSingle();
    }
}
