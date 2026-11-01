using Myriagent.Hub;
using Myriagent.Remote;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 副本在本機唯讀（WP4.4 計畫步 10、D29；步 11 起攔不攔由 WP4.3 的白名單決定）：型錄裡路由 Owner 的指令、本機的寫入與開路徑被擋下時提示「唯讀」；
/// 讀取與帳戶、CLI 的指令不提示
/// </summary>
public sealed class ReplicaCommandRulesTests
{
    /// <summary>計畫 D29 列的本機寫入（加上會只對這個對話重開 CLI 的 cliApplyNow）</summary>
    private static readonly string[] LocalWrites =
    [
        "chooseFolder", "attach", "addFiles", "addPathAttachment", "pasteImage",
        "cliContinue", "cliApplyNow", "syncSetConversationPolicy",
        "openFolder", "openPath", "revealPath", "saveFile", "copyMedia", "docPreview",
    ];

    [Fact]
    public void Writes涵蓋型錄的每個Owner指令_加上本機的寫入指令()
    {
        var owner = RemoteCommandCatalog.All.Where(s => s.Route == RemoteRoute.Owner).Select(s => s.Name).ToList();
        owner.Should().Contain(new[] { "send", "delete", "approve", "answer", "setFolder", "listFiles" }, "前提：型錄的對話指令");

        ReplicaCommandRules.Writes.Should().BeEquivalentTo(owner.Concat(LocalWrites));
        foreach (var name in owner.Concat(LocalWrites))
            ReplicaCommandRules.Blocks(name).Should().BeTrue(name);
        ReplicaCommandRules.Blocks("send").Should().BeTrue("代理輸入");
    }

    [Fact]
    public void 讀取_帳戶與CLI_開新對話_不攔()
    {
        foreach (var name in new[] { "open", "close", "visible", "loadOlder", "copyItem", "layout", "popout", "ready", "newConversation", "copyText", "openLink" })
            ReplicaCommandRules.Blocks(name).Should().BeFalse(name);
        foreach (var spec in RemoteCommandCatalog.All.Where(s => s.Route == RemoteRoute.Device))
            ReplicaCommandRules.Blocks(spec.Name).Should().BeFalse($"{spec.Name} 不帶對話");
    }

    [Fact]
    public void Writes與WP4點3的白名單沒有交集_寫入一定被擋下才提示()
    {
        // 攔不攔由 WP4.3 的白名單決定（fail-closed）；要提示「唯讀」的寫入不能出現在白名單裡，否則它會被執行（WP4.4 計畫步 11）
        AgentHub.ReplicaCommands.Should().BeEquivalentTo(new[] { "open", "close", "loadOlder", "copyItem", "popout", "tabDrag", "tabDragEnd", "tabMoveIn", "moveToNewWindow" },
            "前提：WP4.3 的白名單，加上多視窗的分頁搬移（只換這台的哪個視窗顯示）");
        ReplicaCommandRules.Writes.Intersect(AgentHub.ReplicaCommands).Should().BeEmpty();
        foreach (var name in AgentHub.ReplicaCommands)
            ReplicaCommandRules.Blocks(name).Should().BeFalse($"{name} 是只看的指令，不提示");
    }

    [Fact]
    public void 大小寫不同與null不攔()
    {
        ReplicaCommandRules.Blocks(null).Should().BeFalse();
        ReplicaCommandRules.Blocks("").Should().BeFalse();
        ReplicaCommandRules.Blocks("Send").Should().BeFalse("HandleCommand 的 switch 也只認小寫的 send");
        ReplicaCommandRules.Blocks("send ").Should().BeFalse();
    }
}
