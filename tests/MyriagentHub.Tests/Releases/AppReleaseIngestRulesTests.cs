using System.Reflection;
using System.Text.RegularExpressions;
using Myriagent.Update;
using MyriagentHub.Releases;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Releases;

/// <summary>
/// 收件匣的純函式規則（U2 T4）：request.json 的解析、大小與磁碟空間的檢查、檔案被鎖住（下次輪詢再試）與其他 I/O 錯誤的分辨、回執的代碼
/// </summary>
public sealed class AppReleaseIngestRulesTests
{
    [Fact]
    public void request的解析_屬性名稱不分大小寫_requestedAt與其他屬性照收不看()
    {
        AppReleaseIngestRules.ParseRequest("{\"id\":\"pub-1\",\"release\":\"0.2.300-ab12cd3\",\"requestedAt\":\"2026-10-19T03:35:00.0000000Z\"}")
            .Should().Be(new AppReleaseRequest("pub-1", "0.2.300-ab12cd3"));
        AppReleaseIngestRules.ParseRequest("{\"ID\":\"desktop-0.2.301-bc23de4_7\",\"Release\":\"0.2.301-bc23de4\",\"extra\":[1,2]}")
            .Should().Be(new AppReleaseRequest("desktop-0.2.301-bc23de4_7", "0.2.301-bc23de4"), "屬性名稱不分大小寫；沒有 requestedAt 也收（服務不看它，D21）");
        // Windows PowerShell 5.1 的 ConvertTo-Json：CRLF、四個空白縮排、冒號後兩個空白；requestedAt 是數字也照收
        AppReleaseIngestRules.ParseRequest("{\r\n    \"id\":  \"pub-3\",\r\n    \"release\":  \"0.2.302-cd34ef5\",\r\n    \"requestedAt\":  1760000000000\r\n}")
            .Should().Be(new AppReleaseRequest("pub-3", "0.2.302-cd34ef5"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("[\"pub-1\",\"0.2.300-ab12cd3\"]")]
    [InlineData("{}")]
    [InlineData("{\"release\":\"0.2.300-ab12cd3\"}")]
    [InlineData("{\"id\":\"pub-1\"}")]
    [InlineData("{\"id\":\"../x\",\"release\":\"0.2.300-ab12cd3\"}")]
    [InlineData("{\"id\":\"a..b\",\"release\":\"0.2.300-ab12cd3\"}")]
    [InlineData("{\"id\":\"a b\",\"release\":\"0.2.300-ab12cd3\"}")]
    [InlineData("{\"id\":12,\"release\":\"0.2.300-ab12cd3\"}")]
    [InlineData("{\"id\":\"pub-1\",\"release\":\"0.2.300-AB12CD3\"}")]
    [InlineData("{\"id\":\"pub-1\",\"release\":\"0.2.300-dev\"}")]
    [InlineData("{\"id\":\"pub-1\",\"release\":\" 0.2.300-ab12cd3\"}")]
    [InlineData("{\"id\":\"pub-1\",\"release\":\"0.2.300\"}")]
    [InlineData("{\"id\":\"pub-1\",\"release\":300}")]
    public void request格式不對回null(string json) =>
        AppReleaseIngestRules.ParseRequest(json).Should().BeNull();

    [Fact]
    public void id最多128字()
    {
        var longest = new string('a', 128);
        AppReleaseIngestRules.ParseRequest($"{{\"id\":\"{longest}\",\"release\":\"0.2.300-ab12cd3\"}}")!.Id.Should().Be(longest);
        AppReleaseIngestRules.ParseRequest($"{{\"id\":\"{longest}a\",\"release\":\"0.2.300-ab12cd3\"}}")
            .Should().BeNull("id 會成為 .partial-<id> 資料夾名稱的一部分（D12）");
    }

    [Fact]
    public void 解析得出合法的id就回執它_否則回執空字串()
    {
        AppReleaseIngestRules.ReadId("{\"id\":\"pub-1\",\"release\":\"0.2.300-AB12CD3\"}").Should().Be("pub-1", "release 不對時回執照樣帶 id，送件腳本才認得出是自己的");
        AppReleaseIngestRules.ReadId("{\"Id\":\"pub-2\"}").Should().Be("pub-2");
        AppReleaseIngestRules.ReadId("{\"id\":\"../x\",\"release\":\"0.2.300-ab12cd3\"}").Should().BeNull("不合法的 id 不寫進回執");
        AppReleaseIngestRules.ReadId("{\"id\":7}").Should().BeNull();
        AppReleaseIngestRules.ReadId("not json").Should().BeNull();
        AppReleaseIngestRules.ReadId("[\"pub-1\"]").Should().BeNull();
    }

    [Fact]
    public void 大小上限與磁碟空間()
    {
        const long mib = 1024L * 1024;
        const long gib = 1024 * mib;
        AppReleaseIngestRules.CheckSize(256 * mib, 256 * mib).Should().BeNull("剛好等於上限可以");
        AppReleaseIngestRules.CheckSize(256 * mib + 1, 256 * mib).Should().Be(AppReleaseIngestCodes.TooLarge);
        AppReleaseIngestRules.CheckSize(ReleaseManifestRules.MaxSignatureBytes + 1L, ReleaseManifestRules.MaxSignatureBytes).Should().Be(AppReleaseIngestCodes.TooLarge);
        AppReleaseIngestRules.CheckSize(0, ReleaseManifestRules.MaxManifestBytes).Should().BeNull();

        AppReleaseIngestRules.HasRoom(10 * gib, 3 * gib, 2 * gib).Should().BeTrue();
        AppReleaseIngestRules.HasRoom(10 * gib, 8 * gib, 2 * gib).Should().BeTrue("複製完剛好剩 2 GiB");
        AppReleaseIngestRules.HasRoom(10 * gib, 8 * gib + 1, 2 * gib).Should().BeFalse();
        AppReleaseIngestRules.HasRoom(1 * gib, 2 * gib, 0).Should().BeFalse("連檔案本身都放不下");
        AppReleaseIngestRules.HasRoom(10 * gib, 0, int.MaxValue * mib).Should().BeFalse("Hub:Releases:MinFreeDiskMb 設成極大值");
    }

    [Fact]
    public void 被鎖住的檔案判成下次再試_其他IO錯誤不算()
    {
        AppReleaseIngestRules.IsLockedFile(unchecked((int)0x80070020)).Should().BeTrue("ERROR_SHARING_VIOLATION");
        AppReleaseIngestRules.IsLockedFile(unchecked((int)0x80070021)).Should().BeTrue("ERROR_LOCK_VIOLATION");
        AppReleaseIngestRules.IsLockedFile(unchecked((int)0x80070070)).Should().BeFalse("ERROR_DISK_FULL");
        AppReleaseIngestRules.IsLockedFile(unchecked((int)0x80070005)).Should().BeFalse("ERROR_ACCESS_DENIED");

        // 實際的作業系統錯誤：送件端（或防毒）獨占開著來源檔時，File.Copy 丟的就是這一種
        var dir = TempDir.Create("myriagenthub-ingest-rules");
        try
        {
            var source = Path.Combine(dir, "a.zip");
            File.WriteAllBytes(source, [1, 2, 3]);
            using (new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var locked = FluentActions.Invoking(() => File.Copy(source, Path.Combine(dir, "b.zip"))).Should().Throw<IOException>().Which;
                AppReleaseIngestRules.IsLockedFile(locked.HResult).Should().BeTrue(locked.Message);
            }
            File.WriteAllBytes(Path.Combine(dir, "c.zip"), [4]);
            var exists = FluentActions.Invoking(() => File.Copy(source, Path.Combine(dir, "c.zip"))).Should().Throw<IOException>().Which;
            AppReleaseIngestRules.IsLockedFile(exists.HResult).Should().BeFalse("目標已存在是別的 I/O 錯誤（收件回 io_error）");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public void 改名成版本資料夾時_裡面有檔案被開著判成下次再試()
    {
        // INGEST-2：第 10 步的 Directory.Move／Directory.Delete。改名被開著的檔擋下是存取被拒（不是共用違規），刪除是共用違規
        AppReleaseIngestRules.IsBusyFolder(unchecked((int)0x80070005)).Should().BeTrue("ERROR_ACCESS_DENIED");
        AppReleaseIngestRules.IsBusyFolder(unchecked((int)0x80070020)).Should().BeTrue("ERROR_SHARING_VIOLATION");
        AppReleaseIngestRules.IsBusyFolder(unchecked((int)0x80070021)).Should().BeTrue("ERROR_LOCK_VIOLATION");
        AppReleaseIngestRules.IsBusyFolder(unchecked((int)0x80070070)).Should().BeFalse("ERROR_DISK_FULL");
        AppReleaseIngestRules.IsBusyFolder(unchecked((int)0x800700B7)).Should().BeFalse("ERROR_ALREADY_EXISTS");

        // 實際的作業系統錯誤：防毒開著副本（連允許刪除也一樣）時資料夾改不了名、刪不掉
        var dir = TempDir.Create("myriagenthub-ingest-rules");
        try
        {
            var partial = Directory.CreateDirectory(Path.Combine(dir, ".partial-a")).FullName;
            var copy = Path.Combine(partial, "a.zip");
            File.WriteAllBytes(copy, [1, 2, 3]);
            foreach (var share in new[] { FileShare.Read, FileShare.ReadWrite | FileShare.Delete })
            {
                using var scanner = new FileStream(copy, FileMode.Open, FileAccess.Read, share);
                var move = FluentActions.Invoking(() => Directory.Move(partial, Path.Combine(dir, "0.2.300-ab12cd3"))).Should().Throw<IOException>().Which;
                AppReleaseIngestRules.IsBusyFolder(move.HResult).Should().BeTrue($"{share}：{move.Message}（0x{move.HResult:X8}）");
                AppReleaseIngestRules.IsLockedFile(move.HResult).Should().BeFalse("前提：改名回的不是共用違規，只看 IsLockedFile 會判成未預期的錯誤");
            }
            using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var delete = FluentActions.Invoking(() => Directory.Delete(partial, recursive: true)).Should().Throw<IOException>().Which;
                AppReleaseIngestRules.IsBusyFolder(delete.HResult).Should().BeTrue(delete.Message);
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public void 回執代碼只有ASCII_跟驗證步驟的代碼不重複()
    {
        static List<string> Codes(Type type) => type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        var ingest = Codes(typeof(AppReleaseIngestCodes));
        var steps = Codes(typeof(ReleasePackageSteps));

        ingest.Should().BeEquivalentTo(new[]
        {
            "ingested", "already_ingested", "invalid_request", "missing_file", "too_large", "disk_full",
            "release_mismatch", "release_exists", "not_newer", "io_error", "internal_error",
        });
        steps.Should().HaveCount(6, "驗證失敗沿用 ReleasePackageSteps 的 6 個代碼");
        ingest.Concat(steps).Should().OnlyHaveUniqueItems()
            .And.OnlyContain(code => Regex.IsMatch(code, "^[a-z_]+$"), "送件腳本以 Windows PowerShell 5.1 讀回執、印到 Actions 的記錄（D19）");
    }
}
