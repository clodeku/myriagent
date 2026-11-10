using Myriagent.Sync;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>輸入框草稿的同步（draft1，sync-protocol 14.17）的格式檢查與序列化</summary>
public sealed class ComposerDraftRulesTests
{
    private static readonly string A = new('a', 32);

    [Fact]
    public void 檢查_線上id_文字不是null_沒超過上限()
    {
        ComposerDraftRules.CheckPublish(new ComposerDraftPublish(A, "幫我")).Should().BeNull();
        ComposerDraftRules.CheckPublish(new ComposerDraftPublish(A, "")).Should().BeNull("空字串＝清掉");
        ComposerDraftRules.CheckPublish(new ComposerDraftPublish(A, new string('x', ComposerDraftRules.MaxTextChars))).Should().BeNull();
        ComposerDraftRules.CheckPublish(new ComposerDraftPublish(A, new string('x', ComposerDraftRules.MaxTextChars + 1))).Should().Be("text.length");
        ComposerDraftRules.CheckPublish(new ComposerDraftPublish(A.ToUpperInvariant(), "x")).Should().Be("conversationId", "大寫一律拒絕");
        ComposerDraftRules.CheckPublish(new ComposerDraftPublish(A, null!)).Should().Be("text");
        ComposerDraftRules.CheckPublish(null).Should().Be("null");
        ComposerDraftRules.MaxTextChars.Should().Be(Myriagent.Remote.RemoteCommandShapeRules.MaxAgentTextLength, "同瀏覽器送給代理的文字");
    }

    [Fact]
    public void 收到的更新_則數_rev_來源()
    {
        ComposerDraftRules.CheckUpdate(new ComposerDraftUpdate([new ComposerDraftView(A, "x", 1, 2, "桌機")], true)).Should().BeNull();
        ComposerDraftRules.CheckUpdate(new ComposerDraftUpdate([], false)).Should().BeNull();
        ComposerDraftRules.CheckUpdate(new ComposerDraftUpdate(null!, false)).Should().Be("drafts");
        ComposerDraftRules.CheckUpdate(new ComposerDraftUpdate([new ComposerDraftView(A, "x", -1, 2, null)], false)).Should().Be("rev");
        ComposerDraftRules.CheckUpdate(new ComposerDraftUpdate([new ComposerDraftView(A, "x", 1, 2, new string('名', 65))], false)).Should().Be("from");
        ComposerDraftRules.CheckUpdate(new ComposerDraftUpdate(Enumerable.Repeat(new ComposerDraftView(A, "x", 1, 2, null), ComposerDraftRules.MaxDrafts + 1).ToList(), true))
            .Should().Be("drafts.count");
    }

    [Fact]
    public void 空白算清掉_來源名稱()
    {
        ComposerDraftRules.IsEmpty(" \n ").Should().BeTrue();
        ComposerDraftRules.IsEmpty(null).Should().BeTrue();
        ComposerDraftRules.IsEmpty("x").Should().BeFalse();
        ComposerDraftRules.FromLabel("  筆電\t ").Should().Be("筆電");
        ComposerDraftRules.FromLabel(((char)1).ToString()).Should().BeNull();
    }

    [Fact]
    public void 疑似權杖的不送_清掉一律可以()
    {
        ComposerDraftRules.Syncable("幫我看一下這個錯誤").Should().BeTrue();
        ComposerDraftRules.Syncable("金鑰是 " + "AK" + "IA" + "QWERTYUIOPASDFGH" + " 幫我設定").Should().BeFalse();
        ComposerDraftRules.Syncable("").Should().BeTrue();
    }

    [Fact]
    public void 序列化是小駝峰()
    {
        SyncJson.Serialize(new ComposerDraftPublish(A, "幫我")).Should().Be("{\"conversationId\":\"" + A + "\",\"text\":\"幫我\"}");
        var update = new ComposerDraftUpdate([new ComposerDraftView(A, "x", 3, 4, null)], true);
        SyncJson.Serialize(update).Should().Contain("\"drafts\":[{\"conversationId\":\"" + A + "\",\"text\":\"x\",\"rev\":3,\"updatedAt\":4").And.Contain("\"full\":true");
        SyncJson.Deserialize<ComposerDraftUpdate>(SyncJson.Serialize(update))!.Drafts.Single().Should().Be(update.Drafts[0]);
    }
}
