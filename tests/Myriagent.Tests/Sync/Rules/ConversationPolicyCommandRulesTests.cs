using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// syncSetConversationPolicy 收不收（WP4.3 計畫步 6，D35、D25；答 4：已從伺服器刪除的對話不能再改回同步）
/// </summary>
public sealed class ConversationPolicyCommandRulesTests
{
    private const string S = ConversationPolicyCommandRules.ServerDeletedReason;
    private const string D = ConversationPolicyCommandRules.DraftOrReplicaReason;
    private const string P = ConversationPolicyCommandRules.PausedReason;
    private const string U = ConversationPolicyCommandRules.UnknownReason;
    private const string L = ConversationPolicyCommandRules.LocalOnlyProviderReason;

    [Theory]
    // 一般的對話：三個值都收
    [InlineData("full", true, false, false, false, "full", null)]
    [InlineData("metadata", true, false, false, false, "metadata", null)]
    [InlineData("localOnly", true, false, false, false, "localOnly", null)]
    // paused 不能設、不認得的（含大小寫不同、空字串、null）不收
    [InlineData("paused", true, false, false, false, null, P)]
    [InlineData("Full", true, false, false, false, null, U)]
    [InlineData("LOCALONLY", true, false, false, false, null, U)]
    [InlineData("未來的新值", true, false, false, false, null, U)]
    [InlineData("", true, false, false, false, null, U)]
    [InlineData(null, true, false, false, false, null, U)]
    // Antigravity／Grok：只收 localOnly
    [InlineData("full", false, false, false, false, null, L)]
    [InlineData("metadata", false, false, false, false, null, L)]
    [InlineData("localOnly", false, false, false, false, "localOnly", null)]
    [InlineData("paused", false, false, false, false, null, P)]
    // 已從伺服器刪除：只收 localOnly，full／metadata 被拒（toast 的原因）
    [InlineData("full", true, false, false, true, null, S)]
    [InlineData("metadata", true, false, false, true, null, S)]
    [InlineData("localOnly", true, false, false, true, "localOnly", null)]
    [InlineData("paused", true, false, false, true, null, P)]
    [InlineData("full", false, false, false, true, null, L)]
    // 草稿與複本：一律不收（連 localOnly 也不收）
    [InlineData("localOnly", true, true, false, false, null, D)]
    [InlineData("full", true, true, false, false, null, D)]
    [InlineData("localOnly", true, false, true, false, null, D)]
    [InlineData("metadata", true, false, true, true, null, D)]
    [InlineData("paused", false, true, true, true, null, D)]
    public void 表(string? requested, bool providerSyncable, bool isDraft, bool isReplica, bool serverDeleted, string? expected, string? expectedReason)
    {
        ConversationPolicyCommandRules.Check(requested, providerSyncable, isDraft, isReplica, serverDeleted, out var reason).Should().Be(expected);
        reason.Should().Be(expectedReason);
    }

    [Fact]
    public void 收的值一定是三個之一_不收時一定有原因()
    {
        string?[] values = ["full", "metadata", "localOnly", "paused", "Full", "", null, "x"];
        bool[] flags = [false, true];
        foreach (var value in values)
        foreach (var syncable in flags)
        foreach (var draft in flags)
        foreach (var replica in flags)
        foreach (var deleted in flags)
        {
            var result = ConversationPolicyCommandRules.Check(value, syncable, draft, replica, deleted, out var reason);
            if (result == null)
            {
                reason.Should().NotBeNullOrEmpty();
                continue;
            }
            reason.Should().BeNull();
            result.Should().BeOneOf("full", "metadata", "localOnly");
            result.Should().Be(value, "存的就是要求的值");
            (draft || replica).Should().BeFalse("草稿與複本不收");
            if (deleted || !syncable)
                result.Should().Be("localOnly", "已從伺服器刪除、Antigravity／Grok 只收 localOnly");
        }
    }

    [Fact]
    public void toast的原文逐字釘住()
        => ConversationPolicyCommandRules.ServerDeletedReason.Should().Be("這個對話已從伺服器刪除，不能再同步；要同步請開新對話");

    [Theory]
    [InlineData("full", true)]
    [InlineData("metadata", true)]
    [InlineData(null, true)]
    [InlineData("paused", false)]
    [InlineData("localOnly", false)]
    [InlineData("不認得的", false)]
    public void dlp_比paused寬的才改成paused(string? effective, bool expected)
        => ConversationPolicyCommandRules.PausesForDlp(effective).Should().Be(expected, "已經是 paused 或更嚴格（不認得的當成 localOnly）的不動");
}
