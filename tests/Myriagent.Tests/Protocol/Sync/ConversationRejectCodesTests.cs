using System.Reflection;
using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 對話事件的拒絕碼（WP4.3 計畫步 1、D15；docs/sync-protocol.md 14.2）：與 WP4.2 逐位元組相同的共用檔。
/// 線上名稱固定、只加不改；和第 3 階段的 <see cref="SyncErrorCodes"/> 不重疊（對話事件的被拒原因只有這兩組）。
/// </summary>
public sealed class ConversationRejectCodesTests
{
    [Fact]
    public void 四個碼的值逐字釘住()
    {
        ConversationRejectCodes.NotOwner.Should().Be("not_owner");
        ConversationRejectCodes.Fenced.Should().Be("fenced");
        ConversationRejectCodes.Deleted.Should().Be("deleted");
        ConversationRejectCodes.Dlp.Should().Be("dlp");
        ConversationRejectCodes.All.Should().Equal("not_owner", "fenced", "deleted", "dlp");
    }

    [Fact]
    public void All就是型別裡全部的常數_依宣告順序()
        => ConversationRejectCodes.All.Should().Equal(ConstValues(typeof(ConversationRejectCodes)),
            "新增的碼要一起放進 All（測試與文件都靠它列舉）");

    [Fact]
    public void All有4個_不重複_不和SyncErrorCodes重疊()
    {
        var syncErrors = ConstValues(typeof(SyncErrorCodes));
        syncErrors.Should().Contain(SyncErrorCodes.Invalid, "確認反射讀到的是第 3 階段的錯誤碼")
            .And.Contain(SyncErrorCodes.TooLarge);

        ConversationRejectCodes.All.Should().HaveCount(4);
        ConversationRejectCodes.All.Should().OnlyHaveUniqueItems();
        ConversationRejectCodes.All.Intersect(syncErrors, StringComparer.Ordinal).Should().BeEmpty(
            "對話事件的被拒原因只有 SyncErrorCodes 的 invalid、too_large 與這四個碼，名稱重疊時桌面分不出是哪一種");
    }

    [Fact]
    public void IsKnown_All的每個值為真()
        => ConversationRejectCodes.All.Should().AllSatisfy(code => ConversationRejectCodes.IsKnown(code).Should().BeTrue(code));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("too_large")]
    [InlineData("unknown_conversation")]
    [InlineData("recovering")]
    [InlineData("conflict")]
    [InlineData("NOT_OWNER")]
    [InlineData("Fenced")]
    [InlineData(" dlp")]
    [InlineData("deleted ")]
    public void IsKnown_其他一律為假(string? code)
        => ConversationRejectCodes.IsKnown(code).Should().BeFalse();

    [Fact]
    public void IsKnown_SyncErrorCodes的碼都為假()
        => ConstValues(typeof(SyncErrorCodes)).Should().AllSatisfy(code => ConversationRejectCodes.IsKnown(code).Should().BeFalse(code));

    /// <summary>型別裡 public const string 的值，依宣告順序</summary>
    private static List<string> ConstValues(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .OrderBy(f => f.MetadataToken)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
}
