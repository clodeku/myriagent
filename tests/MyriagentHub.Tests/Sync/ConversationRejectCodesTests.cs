using System.Reflection;
using Myriagent.Sync;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 對話事件的拒絕原因（WP4.2 計畫 P1、D6）：線上名稱固定、只加不改；檔案與 WP4.3 逐位元組相同（git hash-object 記在計畫附錄 B）
/// </summary>
public sealed class ConversationRejectCodesTests
{
    [Fact]
    public void 四個代碼的線上名稱固定()
    {
        ConversationRejectCodes.NotOwner.Should().Be("not_owner");
        ConversationRejectCodes.Fenced.Should().Be("fenced");
        ConversationRejectCodes.Deleted.Should().Be("deleted");
        ConversationRejectCodes.Dlp.Should().Be("dlp");
        ConversationRejectCodes.All.Should().Equal("not_owner", "fenced", "deleted", "dlp");
    }

    [Fact]
    public void All有4個不重複_不和SyncErrorCodes重疊()
    {
        var syncCodes = typeof(SyncErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        syncCodes.Should().Contain([SyncErrorCodes.Invalid, SyncErrorCodes.TooLarge], "確認反射讀到的是 SyncErrorCodes 的常數");

        ConversationRejectCodes.All.Should().HaveCount(4).And.OnlyHaveUniqueItems();
        ConversationRejectCodes.All.Intersect(syncCodes, StringComparer.Ordinal).Should().BeEmpty();
        ConversationRejectCodes.All.Should().OnlyContain(code => ConversationRejectCodes.IsKnown(code));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("too_large")]
    [InlineData("unknown_conversation")]
    [InlineData("conflict")]
    [InlineData("NOT_OWNER")]
    [InlineData("")]
    public void IsKnown_null與invalid與unknown_conversation都是false(string? code) =>
        ConversationRejectCodes.IsKnown(code).Should().BeFalse();

    [Fact]
    public void SP與BatchEventResult的說明都列了這四個()
    {
        var sp = File.ReadAllText(RepoFiles.Combine("docs", "sync-protocol.md"));
        var messages = File.ReadAllText(RepoFiles.Combine("src", "Myriagent.Protocol", "Sync", "DeviceHubMessages.cs"));
        foreach (var code in ConversationRejectCodes.All)
        {
            sp.Should().Contain("`" + code + "`", "docs/sync-protocol.md 14.2 要列出 {0}", code);
            messages.Should().Contain("<c>" + code + "</c>", "BatchEventResult 的說明要列出 {0}", code);
        }
        sp.Should().Contain(nameof(ConversationRejectCodes), "SP 14.2 的「伺服器」一段註明常數在哪裡");
    }
}
