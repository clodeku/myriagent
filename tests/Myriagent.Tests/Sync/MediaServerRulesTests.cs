using System.Text;
using Myriagent.Agents;
using Myriagent.Media;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync;

/// <summary>桌面回覆媒體轉送的判斷（WP4.7 計畫步 8，D26、D28、D36）</summary>
public sealed class MediaServerRulesTests
{
    private static readonly DevicePolicy Off = new("Off", false);

    private static readonly DevicePolicy View = new("View", false);

    private static readonly DevicePolicy Control = new("Control", true);

    private const long MB = 1024 * 1024;

    [Fact]
    public void Decide_參照不明與對話不可見都回404()
    {
        MediaServerRules.Decide(false, true, Control, MediaPurposes.Media).Should().Be(new MediaVerdict(404, MediaErrorCodes.NotFound));
        MediaServerRules.Decide(true, false, Control, MediaPurposes.Media).Should().Be(new MediaVerdict(404, MediaErrorCodes.NotFound));
        MediaServerRules.Decide(false, false, Off, MediaPurposes.Thumb).Should().Be(new MediaVerdict(404, MediaErrorCodes.NotFound), "看不到的對話連縮圖都不給");
    }

    [Fact]
    public void Decide_政策Off時原檔與檔案回403policy_縮圖照常()
    {
        MediaServerRules.Decide(true, true, Off, MediaPurposes.Media).Should().Be(new MediaVerdict(403, MediaErrorCodes.Policy));
        MediaServerRules.Decide(true, true, Off, MediaPurposes.File).Should().Be(new MediaVerdict(403, MediaErrorCodes.Policy));
        MediaServerRules.Decide(true, true, Off, MediaPurposes.Thumb).Should().BeNull("Q1-A：縮圖照常");
        MediaServerRules.Decide(true, true, new DevicePolicy("none", false), MediaPurposes.Media).Should().Be(new MediaVerdict(403, MediaErrorCodes.Policy),
            "不認得的層級當成 Off");
    }

    [Fact]
    public void Decide_View與Control轉送原檔與檔案()
    {
        MediaServerRules.Decide(true, true, View, MediaPurposes.Media).Should().BeNull();
        MediaServerRules.Decide(true, true, View, MediaPurposes.File).Should().BeNull();
        MediaServerRules.Decide(true, true, Control, MediaPurposes.Media).Should().BeNull();
        MediaServerRules.Decide(true, true, Control, MediaPurposes.Thumb).Should().BeNull();
    }

    [Theory]
    [InlineData(RemoteFileGuardReasons.NotFound, 404, MediaErrorCodes.NotFound)]
    [InlineData(RemoteFileGuardReasons.NotFile, 404, MediaErrorCodes.NotFound)]
    [InlineData(RemoteFileGuardReasons.TooLarge, 413, MediaErrorCodes.TooLarge)]
    [InlineData(RemoteFileGuardReasons.Forbidden, 403, MediaErrorCodes.Forbidden)]
    [InlineData(RemoteFileGuardReasons.Reparse, 403, MediaErrorCodes.Forbidden)]
    [InlineData(RemoteFileGuardReasons.Name, 403, MediaErrorCodes.Forbidden)]
    [InlineData(RemoteFileGuardReasons.Unc, 403, MediaErrorCodes.Forbidden)]
    [InlineData(RemoteFileGuardReasons.Timeout, 403, MediaErrorCodes.Forbidden)]
    public void AfterGuard_不存在404_太大413_其他403(string reason, int status, string error)
        => MediaServerRules.AfterGuard(reason).Should().Be(new MediaVerdict(status, error));

    [Fact]
    public void Segment_範圍2到5_結尾超過檔尾縮到最後一個位元組()
    {
        MediaServerRules.Segment(new MediaRange(2, 5), 10).Should().Be(new MediaSegment(206, new ByteRange(2, 5)));
        MediaServerRules.Segment(new MediaRange(2, 50), 10).Should().Be(new MediaSegment(206, new ByteRange(2, 9)));
        MediaServerRules.Segment(new MediaRange(0, 0), 10).Should().Be(new MediaSegment(206, new ByteRange(0, 0)), "伺服器以 (0,0) 問總長");
    }

    [Fact]
    public void Segment_夾到8MB_起點不在檔案裡416()
    {
        MediaServerRules.Segment(new MediaRange(0, null), 30 * MB).Should().Be(new MediaSegment(206, new ByteRange(0, 8 * MB - 1)));
        MediaServerRules.Segment(new MediaRange(MB, 20 * MB), 30 * MB).Should().Be(new MediaSegment(206, new ByteRange(MB, 9 * MB - 1)));
        MediaServerRules.Segment(new MediaRange(10, 20), 10).Should().Be(new MediaSegment(416, null));
        MediaServerRules.Segment(new MediaRange(0, 0), 0).Should().Be(new MediaSegment(416, null), "空檔：伺服器的整份轉送把 416 當成 0 個位元組");
    }

    [Fact]
    public void Segment_沒帶範圍_8MB以內200_更大413()
    {
        MediaServerRules.Segment(null, 8 * MB).Should().Be(new MediaSegment(200, null));
        MediaServerRules.Segment(null, 8 * MB + 1).Should().Be(new MediaSegment(413, null));
    }

    [Fact]
    public void IsValidRange_負的起點與結尾在起點之前不收()
    {
        MediaServerRules.IsValidRange(null).Should().BeTrue();
        MediaServerRules.IsValidRange(new MediaRange(0, null)).Should().BeTrue();
        MediaServerRules.IsValidRange(new MediaRange(5, 5)).Should().BeTrue();
        MediaServerRules.IsValidRange(new MediaRange(-1, 5)).Should().BeFalse();
        MediaServerRules.IsValidRange(new MediaRange(6, 5)).Should().BeFalse();
    }

    [Theory]
    [InlineData(MediaPurposes.Media, "item-1", true)]
    [InlineData(MediaPurposes.Thumb, "item-1", true)]
    [InlineData(MediaPurposes.Media, "@list", true)]
    [InlineData(MediaPurposes.Thumb, "@list", true)]
    [InlineData(MediaPurposes.Media, "@file", false)]
    [InlineData(MediaPurposes.Thumb, "@file", false)]
    [InlineData(MediaPurposes.File, "@file", true)]
    [InlineData(MediaPurposes.File, "@list", false)]
    [InlineData(MediaPurposes.File, "item-1", false)]
    [InlineData("download", "@file", false)]
    [InlineData(null, "item-1", false)]
    public void PurposeMatches_原檔與縮圖收項目與抽屜_檔案只收fileFetch(string? purpose, string itemId, bool matches)
        => MediaServerRules.PurposeMatches(purpose, itemId).Should().Be(matches);

    [Fact]
    public void IsRelayId_只收26字的CrockfordBase32()
    {
        MediaServerRules.IsRelayId("0123456789ABCDEFGHJKMNPQRS").Should().BeTrue();
        MediaServerRules.IsRelayId("0123456789ABCDEFGHJKMNPQR").Should().BeFalse();
        MediaServerRules.IsRelayId("0123456789ABCDEFGHJKMNPQR/").Should().BeFalse("不能放進網址的字元");
        MediaServerRules.IsRelayId("relay-1").Should().BeFalse();
        MediaServerRules.IsRelayId(null).Should().BeFalse();
        MediaServerRules.RelayPath("0123456789ABCDEFGHJKMNPQRS").Should().Be("/api/v1/device/relay/0123456789ABCDEFGHJKMNPQRS");
    }

    [Fact]
    public void ETag_長度或修改時間不同就不同_格式是引號裡的十六進位()
    {
        var at = new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);
        var tag = MediaServerRules.ETag(10, at);

        tag.Should().MatchRegex("^\"[0-9a-f]{32}\"$");
        MediaServerRules.ETag(10, at).Should().Be(tag);
        MediaServerRules.ETag(11, at).Should().NotBe(tag);
        MediaServerRules.ETag(10, at.AddTicks(1)).Should().NotBe(tag);
        MediaServerRules.ETag(10, at.ToLocalTime()).Should().Be(tag, "同一個時間點不論時區");
    }

    [Fact]
    public void IsText_副檔名或嚴格UTF8取聯集()
    {
        MediaServerRules.IsText("報告.txt", [0xFF, 0xFE]).Should().BeTrue("副檔名是純文字");
        MediaServerRules.IsText("Program.cs", [0xFF]).Should().BeTrue("程式碼");
        MediaServerRules.IsText("data.bin", Encoding.UTF8.GetBytes("中文 ok")).Should().BeTrue("內容是嚴格的 UTF-8");
        MediaServerRules.IsText("data.bin", [0x41, 0x00, 0x42]).Should().BeFalse("有 NUL");
        MediaServerRules.IsText("photo.png", [0x89, 0x50, 0x4E, 0x47, 0xFF]).Should().BeFalse();
        MediaServerRules.IsText("slides.pptx", [0xC3, 0x28]).Should().BeFalse("不合格的 UTF-8");
        MediaServerRules.IsStrictUtf8([]).Should().BeTrue();
    }

    [Fact]
    public void IsProvenBinary_開頭有NUL或不合格的UTF8才算_文字副檔名與BOM一律不算()
    {
        MediaServerRules.IsProvenBinary("簡報.pptx", [0x50, 0x4B, 0x03, 0x04, 0x14, 0x00]).Should().BeTrue("ZIP 的檔頭有 NUL");
        MediaServerRules.IsProvenBinary("預覽.jpg", [0xFF, 0xD8, 0xFF, 0xE0]).Should().BeTrue("0xFF 不是合格的 UTF-8");
        MediaServerRules.IsProvenBinary("文件.pdf", Encoding.ASCII.GetBytes("%PDF-1.7\n%").Concat(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }).ToArray())
            .Should().BeTrue("E2 後面接的不是延續位元組");
        MediaServerRules.IsProvenBinary("data.bin", Encoding.UTF8.GetBytes("全是文字")).Should().BeFalse("合格的 UTF-8：可能是文字，要整份掃");
        MediaServerRules.IsProvenBinary("data.bin", Encoding.UTF8.GetBytes("中")[..2]).Should().BeFalse("結尾被切斷的多位元組字不算不合格");
        MediaServerRules.IsProvenBinary("notes.txt", [0x41, 0x00, 0xFF]).Should().BeFalse("文字類副檔名一律整份掃");
        MediaServerRules.IsProvenBinary("token", [0xFF, 0xFE, 0x41, 0x00]).Should().BeFalse("UTF-16 的 BOM 一律整份掃");
        MediaServerRules.IsProvenBinary("empty.bin", []).Should().BeFalse();
    }

    [Theory]
    [InlineData("slides.pptx")]
    [InlineData("data.bin")]
    [InlineData("noext")]
    [InlineData("notes.txt")]
    public void IsProvenBinary_成立時整份一定不是文字_不會漏掃(string fileName)
    {
        // 開頭（64 KB）證明是二進位 ⇒ 不論後面接什麼，IsText 整份都是 false（DLP 只掃 IsText 的檔）
        var random = new Random(20261022);
        for (int i = 0; i < 200; i++)
        {
            var length = random.Next(0, 300);
            var content = new byte[length];
            for (int j = 0; j < length; j++)
                content[j] = (byte)(random.Next(4) == 0 ? random.Next(256) : random.Next(0x20, 0x7F));
            var cut = random.Next(0, length + 1);
            if (MediaServerRules.IsProvenBinary(fileName, content.AsSpan(0, cut)))
                MediaServerRules.IsText(fileName, content).Should().BeFalse($"第 {i} 個（長度 {length}、開頭 {cut}）");
        }
    }

    [Fact]
    public void 檔案下載的上限_開檔512MB_可能是文字的20MB()
    {
        MediaServerRules.MaxBytes(MediaPurposes.File).Should().Be(512 * MB);
        MediaServerRules.MaxBinaryFileBytes.Should().Be(MediaRelayRules.MaxUnrangedFileBytes, "伺服器 /f/ 沒帶 Range 時整份給得了");
        MediaServerRules.MaxFileBytes.Should().Be(20 * MB);
        MediaServerRules.BinaryProbeBytes.Should().Be(64 * 1024);
    }

    [Fact]
    public void IsText_UTF16與UTF32的BOM也算文字_副檔名不是文字類的照樣要掃()
    {
        // 合併前審查 F1：沒有文字副檔名的 UTF-16 檔有 NUL、不是嚴格的 UTF-8，原本被當成二進位、完全不掃
        MediaServerRules.IsText("token", [0xFF, 0xFE, 0x41, 0x00]).Should().BeTrue("UTF-16LE 的 BOM");
        MediaServerRules.IsText("token", [0xFE, 0xFF, 0x00, 0x41]).Should().BeTrue("UTF-16BE 的 BOM");
        MediaServerRules.IsText("token", [0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00]).Should().BeTrue("UTF-32LE 的 BOM");
        MediaServerRules.IsText("token", [0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x41]).Should().BeTrue("UTF-32BE 的 BOM");
        MediaServerRules.HasUnicodeBom([0xEF, 0xBB, 0xBF, 0x41]).Should().BeFalse("UTF-8 的 BOM 由嚴格 UTF-8 收");
        MediaServerRules.HasUnicodeBom([0xFF]).Should().BeFalse();
        MediaServerRules.HasUnicodeBom([]).Should().BeFalse();
    }

    [Fact]
    public void HasLeak_UTF16與UTF32的權杖也找得到()
    {
        var text = "部署用的權杖：" + RuntimeGitHubToken(31) + "\r\n";
        byte[] utf16Le = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)];
        var utf32Be = new UTF32Encoding(bigEndian: true, byteOrderMark: true);

        MediaServerRules.HasLeak(Encoding.UTF8.GetBytes(text)).Should().BeTrue("UTF-8");
        MediaServerRules.HasLeak(utf16Le).Should().BeTrue("UTF-16LE 加 BOM（Windows PowerShell 5.1 的 > 與 Out-File）");
        MediaServerRules.HasLeak(Encoding.Unicode.GetBytes(text)).Should().BeTrue("UTF-16LE 沒有 BOM");
        MediaServerRules.HasLeak([.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)]).Should().BeTrue("UTF-16BE 加 BOM");
        MediaServerRules.HasLeak(Encoding.BigEndianUnicode.GetBytes(text)).Should().BeTrue("UTF-16BE 沒有 BOM");
        MediaServerRules.HasLeak([.. Encoding.UTF32.GetPreamble(), .. Encoding.UTF32.GetBytes(text)]).Should().BeTrue("UTF-32LE 加 BOM");
        MediaServerRules.HasLeak([.. utf32Be.GetPreamble(), .. utf32Be.GetBytes(text)]).Should().BeTrue("UTF-32BE 加 BOM");
        CredentialLeakRules.HasLeak(Encoding.UTF8.GetString(utf16Le)).Should().BeFalse("對照：只以 UTF-8 解碼時字之間夾著 U+0000，比對不到");

        MediaServerRules.HasLeak([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("沒有秘密的內容")]).Should().BeFalse();
        MediaServerRules.HasLeak(Encoding.BigEndianUnicode.GetBytes("沒有秘密的內容")).Should().BeFalse();
        MediaServerRules.HasLeak([]).Should().BeFalse();
    }

    /// <summary>執行時組出像 GitHub 權杖的字串（固定種子；repo 裡沒有這種字串）</summary>
    private static string RuntimeGitHubToken(int seed)
    {
        var random = new Random(seed);
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return "gh" + "p_" + new string(Enumerable.Range(0, 36).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }

    [Fact]
    public void Resolve_附件以id找StoredPath_代理媒體要還在MediaPaths()
    {
        var item = new AgentChatItem
        {
            Id = "item-1",
            Attachments = [new AgentAttachment { Id = "att-1", FileName = "螢幕截圖.png", StoredPath = @"C:\data\conversations\c1\attachments\a.png" }],
            MediaPaths = [@"C:\repo\out\chart.png"],
        };

        MediaServerRules.Resolve(item, "att-1", null).Should().Be((@"C:\data\conversations\c1\attachments\a.png", "螢幕截圖.png"));
        MediaServerRules.Resolve(item, "att-2", null).Should().BeNull("附件已經不在");
        MediaServerRules.Resolve(item, null, @"c:\REPO\out\chart.png").Should().Be((@"c:\REPO\out\chart.png", "chart.png"), "路徑不分大小寫");
        MediaServerRules.Resolve(item, null, @"C:\repo\out\other.png").Should().BeNull();
        MediaServerRules.Resolve(null, "att-1", null).Should().BeNull("項目已經不在");
        MediaServerRules.Resolve(item, null, null).Should().BeNull();
    }

    [Fact]
    public void LocalMediaRefRow_項目的列恰有一個來源_抽屜與檔案只有路徑()
    {
        new LocalMediaRefRow("r", "c", "item-1", "att-1", null, 1).IsWellFormed.Should().BeTrue();
        new LocalMediaRefRow("r", "c", "item-1", null, @"C:\a.png", 1).IsWellFormed.Should().BeTrue();
        new LocalMediaRefRow("r", "c", "item-1", "att-1", @"C:\a.png", 1).IsWellFormed.Should().BeFalse();
        new LocalMediaRefRow("r", "c", "item-1", null, null, 1).IsWellFormed.Should().BeFalse();
        new LocalMediaRefRow("r", "c", "@list", null, @"C:\a.png", 1).IsWellFormed.Should().BeTrue();
        new LocalMediaRefRow("r", "c", "@file", "att-1", @"C:\a.txt", 1).IsWellFormed.Should().BeFalse();
        new LocalMediaRefRow("r", "c", "@file", null, null, 1).IsWellFormed.Should().BeFalse();
    }
}
