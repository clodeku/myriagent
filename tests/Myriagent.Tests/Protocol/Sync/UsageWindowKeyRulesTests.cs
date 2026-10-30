using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>時間窗鍵的線上格式（sync-protocol 12 節；桌面與伺服器共用，WP3.7 步驟 2 從 UsageSyncRules 搬進 Protocol）</summary>
public sealed class UsageWindowKeyRulesTests
{
    private const string ServerChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

    [Theory]
    [InlineData("session", "session")]
    [InlineData("weekly", "weekly")]
    [InlineData("weekly:Opus", "weekly_cOpus")]
    [InlineData("weekly:claude-opus-4.5", "weekly_cclaude-opus-4_d5")]
    [InlineData("codex:300", "codex_c300")]
    [InlineData("codex_other:10080", "codex__other_c10080")]
    [InlineData("a b", "a_sb")]
    [InlineData("_", "__")]
    [InlineData("::", "_c_c")]
    public void 跳脫可以還原且只用伺服器收的字元(string key, string encoded)
    {
        UsageWindowKeyRules.Encode(key).Should().Be(encoded);
        encoded.AsSpan().IndexOfAnyExcept(ServerChars).Should().Be(-1);
        UsageWindowKeyRules.Decode(encoded).Should().Be(key);
    }

    [Fact]
    public void 空字串與不收的字元回null()
    {
        UsageWindowKeyRules.Encode(null).Should().BeNull();
        UsageWindowKeyRules.Encode("").Should().BeNull();
        UsageWindowKeyRules.Encode("中文").Should().BeNull();
        UsageWindowKeyRules.Encode("a/b").Should().BeNull();
        UsageWindowKeyRules.Decode("").Should().Be("", "空字串照原樣");
    }

    [Fact]
    public void 三十二字上限以跳脫後的長度算()
    {
        UsageWindowKeyRules.MaxLength.Should().Be(32);
        UsageWindowKeyRules.Encode(new string('k', 32)).Should().Be(new string('k', 32));
        UsageWindowKeyRules.Encode(new string('k', 33)).Should().BeNull();
        UsageWindowKeyRules.Encode(new string(':', 16)).Should().HaveLength(32, "16 個冒號跳脫成 32 字，剛好收");
        UsageWindowKeyRules.Encode(new string(':', 17)).Should().BeNull();
        UsageWindowKeyRules.Encode(new string('k', 31) + ":").Should().BeNull("跳脫後 33 字");
    }

    [Theory]
    [InlineData("end_", "尾端單獨的 _")]
    [InlineData("raw_x", "不認得的跳脫")]
    [InlineData("five_hour", "不認得的跳脫（_h）")]
    [InlineData("a_cb_", "前面合格式、尾端單獨的 _：整個照原樣")]
    [InlineData("a_cb_q", "前面合格式、後面不認得的跳脫：整個照原樣")]
    public void 不是這種跳脫格式的鍵照原樣(string key, string because) => UsageWindowKeyRules.Decode(key).Should().Be(key, because);

    [Fact]
    public void 解碼不是一對一()
    {
        // 照原樣的 a_x 與合格式的 a__x 都得到 a_x：拿鍵當識別的呼叫端（伺服器的 AccountViewRules.Usage）自己處理撞名
        UsageWindowKeyRules.Decode("a_x").Should().Be("a_x");
        UsageWindowKeyRules.Decode("a__x").Should().Be("a_x");
        UsageWindowKeyRules.Encode("a_x").Should().Be("a__x", "合格式的往返照樣成立");
    }

    [Theory]
    [InlineData(new[] { "a_x", "a__x" }, new[] { "a_x", "a__x" })]
    [InlineData(new[] { "a__x", "a_x" }, new[] { "a__x", "a_x" })]
    [InlineData(new[] { "five__hour", "five_hour" }, new[] { "five__hour", "five_hour" })]
    [InlineData(new[] { "a_x", "a__x", "a____x" }, new[] { "a_x", "a__x", "a____x" })]
    [InlineData(new[] { "a____x", "a__x", "a_x" }, new[] { "a____x", "a__x", "a_x" })]
    [InlineData(new[] { "weekly_cOpus", "session", "a__x", "a_x" }, new[] { "weekly:Opus", "session", "a__x", "a_x" })]
    public void 一起還原時還原後等於另一個原本的鍵就送原本的鍵(string[] keys, string[] expected)
    {
        // 合併前審查 WK-1／F2：逐一還原、撞名才退回原本的鍵，只在照原樣的鍵排在前面時有效（a__x 在前時兩個都送 a_x）
        var decoded = UsageWindowKeyRules.DecodeAll(keys);
        decoded.Should().Equal(expected);
        decoded.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void 一起還原_沒有撞名時全部還原()
    {
        UsageWindowKeyRules.DecodeAll(["weekly_cOpus", "session", "codex_c300", "weekly_cclaude-opus-4_d5"])
            .Should().Equal(["weekly:Opus", "session", "codex:300", "weekly:claude-opus-4.5"]);
        UsageWindowKeyRules.DecodeAll([]).Should().BeEmpty();
    }

    [Fact]
    public void 一起還原_不是線上字元集而仍撞名時整份送原本的鍵()
    {
        // 伺服器只收 [A-Za-z0-9-_]，這種鍵到不了這裡；保底：兩個都改了、還原後相同（"::"）而且都不等於原本的鍵
        UsageWindowKeyRules.DecodeAll([":_c", "_c:", "weekly_cOpus"]).Should().Equal([":_c", "_c:", "weekly_cOpus"]);
    }
}
