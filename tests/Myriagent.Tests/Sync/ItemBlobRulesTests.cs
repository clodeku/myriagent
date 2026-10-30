using System.Text;
using Myriagent.Agents;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync;

/// <summary>項目欄位 blob 的規則（WP4.7 計畫步 10，D14、D15、D31）</summary>
public sealed class ItemBlobRulesTests
{
    private const long Mb = 1024 * 1024;

    [Fact]
    public void 常數_每個blob與整筆都是4MB_和伺服器的預設相同()
    {
        // 寫死數字：伺服器 Hub:Media:MaxItemBlobBytes 的預設（MediaOptions）改了要一起改
        ItemBlobRules.MaxItemBlobBytes.Should().Be(4 * Mb);
        ItemBlobRules.MaxItemBytes.Should().Be(4 * Mb);
        ItemBlobRules.DlpDecodeWindowChars.Should().Be(65_536).And.BeLessThan(64 * 1024 / 3 * 4, "比權杖 DLP 實際解碼的 87,380 個 base64 字元小");
    }

    // ───────────────────────────── CapFields ─────────────────────────────

    [Fact]
    public void CapFields_沒有超過的回同一個實例()
    {
        var item = new AgentChatItem { Id = "a", Kind = AgentItemKind.Assistant, Text = new string('x', 100_000) };

        var result = ItemBlobRules.CapFields(item, ItemBlobRules.MaxItemBlobBytes, ItemBlobRules.MaxItemBytes, out var capped);

        result.Should().BeSameAs(item);
        capped.Should().BeEmpty();
    }

    [Fact]
    public void CapFields_超過4MB的欄位截到4MB以內_原本的項目不動()
    {
        var text = new string('x', (int)(5 * Mb));
        var item = new AgentChatItem { Id = "a", Kind = AgentItemKind.Assistant, Text = text };

        var result = ItemBlobRules.CapFields(item, ItemBlobRules.MaxItemBlobBytes, ItemBlobRules.MaxItemBytes, out var capped);

        capped.Should().Equal("text");
        Encoding.UTF8.GetByteCount(result.Text).Should().BeLessThanOrEqualTo((int)ItemBlobRules.MaxItemBlobBytes);
        result.Text.Should().EndWith("完整內容在擁有者電腦）", "說明字樣同 WP4.3 的截斷");
        item.Text.Should().BeSameAs(text);
    }

    [Fact]
    public void CapFields_每欄都不到4MB但整筆超過時也截()
    {
        var item = new AgentChatItem { Id = "a", Kind = AgentItemKind.Tool, Output = new string('o', (int)(3 * Mb)), Detail = new string('d', (int)(3 * Mb)) };

        var result = ItemBlobRules.CapFields(item, ItemBlobRules.MaxItemBlobBytes, ItemBlobRules.MaxItemBytes, out var capped);

        capped.Should().NotBeEmpty();
        PublishFitRules.EstimateBytes(result).Should().BeLessThanOrEqualTo(ItemBlobRules.MaxItemBytes);
    }

    [Fact]
    public void CapFields_預算不合格丟例外()
    {
        var item = new AgentChatItem { Id = "a", Text = "x" };
        FluentActions.Invoking(() => ItemBlobRules.CapFields(item, 0, 10, out _)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => ItemBlobRules.CapFields(item, 10, 0, out _)).Should().Throw<ArgumentOutOfRangeException>();
    }

    // ───────────────────────────── BlobsFit ─────────────────────────────

    [Fact]
    public void BlobsFit_每個與合計都在上限內()
    {
        static ConversationBlob Blob(long size) => new(new string('0', 64), new byte[size]);

        ItemBlobRules.BlobsFit([], 4 * Mb, 4 * Mb).Should().BeTrue("沒有 blob");
        ItemBlobRules.BlobsFit([Blob(4 * Mb)], 4 * Mb, 4 * Mb).Should().BeTrue();
        ItemBlobRules.BlobsFit([Blob(4 * Mb + 1)], 4 * Mb, 8 * Mb).Should().BeFalse("單一個超過");
        ItemBlobRules.BlobsFit([Blob(3 * Mb), Blob(2 * Mb)], 4 * Mb, 4 * Mb).Should().BeFalse("合計超過");
        ItemBlobRules.BlobsFit([Blob(0)], 4 * Mb, 4 * Mb).Should().BeFalse("伺服器的 blob 參照至少 1 位元組");
    }

    // ───────────────────────────── base64 片段（DLP 的解碼範圍） ─────────────────────────────

    [Fact]
    public void LongestBase64Run_連續的本體字元與結尾的等號()
    {
        ItemBlobRules.LongestBase64Run("").Should().Be(0);
        ItemBlobRules.LongestBase64Run("中文，沒有").Should().Be(0);
        ItemBlobRules.LongestBase64Run("abc def").Should().Be(3);
        ItemBlobRules.LongestBase64Run("QUJD+/09==").Should().Be(10);
        ItemBlobRules.LongestBase64Run(new string('A', 40_000) + "==" + new string('B', 30_000)).Should().Be(40_002, "等號結束片段");
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\n    ")]
    [InlineData("\n\t")]
    public void LongestBase64Run_折行接起來算(string fold)
    {
        var lines = Enumerable.Range(0, 1000).Select(i => new string((char)('A' + (i % 26)), 76));
        ItemBlobRules.LongestBase64Run(string.Join(fold, lines)).Should().Be(76_000);
    }

    [Theory]
    [InlineData("\n#\n")]
    [InlineData(" ")]
    [InlineData("\n\n")]
    [InlineData(".")]
    public void LongestBase64Run_其他字元切斷片段(string separator)
    {
        var lines = Enumerable.Range(0, 1000).Select(_ => new string('A', 76));
        ItemBlobRules.LongestBase64Run(string.Join(separator, lines)).Should().Be(76);
    }

    [Fact]
    public void HasLongBase64Run_超過解碼範圍的才算()
    {
        Payload(new string('A', ItemBlobRules.DlpDecodeWindowChars)).Should().BeFalse("剛好在範圍內");
        Payload(new string('A', ItemBlobRules.DlpDecodeWindowChars + 1)).Should().BeTrue();
        Payload(string.Join(' ', Enumerable.Range(0, 30_000).Select(i => "word" + i))).Should().BeFalse("一般的長文字：空白切斷片段");

        var nested = new JObject { ["diff"] = new JArray(new JObject { ["t"] = "add", ["s"] = new string('Q', 70_000) }) };
        ItemBlobRules.HasLongBase64Run(nested).Should().BeTrue("陣列裡的字串也看");
        var name = new JObject { [new string('K', 70_000)] = "v" };
        ItemBlobRules.HasLongBase64Run(name).Should().BeTrue("欄位名稱也看");

        static bool Payload(string text) => ItemBlobRules.HasLongBase64Run(new JObject { ["text"] = text, ["id"] = "a" });
    }

    [Fact]
    public void HasLongBase64Run_解碼範圍外的憑證_DLP遮不到的片段一定擋下()
    {
        // 前提：權杖 DLP 只解碼片段的開頭 64 KB，憑證放在更後面時整段照原樣（WP4.3 的截斷每欄 ≤30 KB，所以沒有這個問題）
        var secret = RuntimeToken(17);
        var hidden = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', 120_000) + "{\"refresh_token\":\"" + secret + "\"}"));
        CredentialLeakRules.Mask(hidden).Should().Be(hidden, "這個測試的前提：解碼範圍外的憑證遮不到");
        ItemBlobRules.HasLongBase64Run(new JObject { ["output"] = hidden }).Should().BeTrue("這種項目不走 blob、照 WP4.3 截斷");

        // 範圍內的照常遮蔽，可以走 blob
        var visible = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', 30_000) + "{\"refresh_token\":\"" + secret + "\"}"));
        CredentialLeakRules.Mask(visible).Should().NotContain(visible[^64..]).And.Contain(CredentialLeakRules.Replacement);
        ItemBlobRules.HasLongBase64Run(new JObject { ["output"] = visible }).Should().BeFalse();
    }

    // ───────────────────────────── E5 的結果 ─────────────────────────────

    // 結果以名稱傳（BlobUploadOutcome 是 internal，公開的測試方法不能拿它當參數）
    [Theory]
    [InlineData(200, "Confirmed")]
    [InlineData(404, null)]
    [InlineData(401, "Failed")]
    [InlineData(500, "Failed")]
    [InlineData(503, "Failed")]
    public void AfterHead(int status, string? expected)
        => ItemBlobRules.AfterHead(status).Should().Be(expected is null ? null : Enum.Parse<BlobUploadOutcome>(expected));

    [Theory]
    [InlineData(200, "Confirmed")]
    [InlineData(201, "Confirmed")]
    [InlineData(422, "Rejected")]
    [InlineData(413, "Rejected")]
    [InlineData(411, "Rejected")]
    [InlineData(400, "Rejected")]
    [InlineData(401, "Failed")]
    [InlineData(404, "Failed")]
    [InlineData(409, "Failed")]
    [InlineData(503, "Failed")]
    [InlineData(507, "Failed")]
    public void AfterPut(int status, string expected) => ItemBlobRules.AfterPut(status).Should().Be(Enum.Parse<BlobUploadOutcome>(expected));

    [Fact]
    public void BlobPath_只收blob_id()
    {
        var sha = new string('a', 64);
        ItemBlobRules.BlobPath(sha).Should().Be("/api/v1/device/blobs/" + sha);
        FluentActions.Invoking(() => ItemBlobRules.BlobPath(new string('A', 64))).Should().Throw<ArgumentException>("大寫不是 blob id");
        FluentActions.Invoking(() => ItemBlobRules.BlobPath("../x")).Should().Throw<ArgumentException>();
    }

    /// <summary>執行時組出像真憑證的字串（固定種子；repo 裡沒有這種字串）</summary>
    private static string RuntimeToken(int seed)
    {
        var random = new Random(seed);
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return "sk-" + "ant-" + "api03-" + new string(Enumerable.Range(0, 40).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }
}
