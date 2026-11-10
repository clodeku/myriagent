using Myriagent.Remote;
using MyriagentHub.Realtime;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 遠端指令的型錄（Protocol 的 RemoteCommandCatalog，設計 14.11 D53）與 web-protocol 第二節、瀏覽器模式的分類（BrowserCommandRules）一致：
/// 第二節的每個指令都在型錄（電腦執行或伺服器處理）或明列為只在電腦本機；型錄裡由電腦執行的指令，WP4.1 在瀏覽器模式一律還是 not_available
/// </summary>
public sealed class RemoteCommandCatalogConsistencyTests
{
    /// <summary>第 5 階段的指令：不在型錄（閘門 5a 拒絕），瀏覽器模式照樣 not_available</summary>
    private static readonly string[] Phase5Commands = ["takeover", "handoffCancel"];

    [Fact]
    public void web_protocol的每個指令都在型錄或明列只在本機()
    {
        var table = BrowserCommandRulesTests.WebProtocolCommands();
        foreach (var name in table)
        {
            var route = RemoteCommandCatalog.RouteOf(name);
            var localOnly = RemoteCommandCatalog.IsLocalOnly(name);
            (route != null || localOnly).Should().BeTrue($"web-protocol 的 {name} 要在 RemoteCommandCatalog 有路由或列為只在本機");
            (route != null && localOnly).Should().BeFalse($"{name} 不能同時在型錄又只在本機");
        }
    }

    [Fact]
    public void 型錄的指令都在web_protocol_只有conversationCreate與remoteExec不是網頁指令()
    {
        var table = BrowserCommandRulesTests.WebProtocolCommands();
        var catalog = RemoteCommandCatalog.All.Select(s => s.Name).ToList();
        catalog.Should().Contain(RemoteCommandCatalog.NonWebCommands);
        table.Should().NotContain(RemoteCommandCatalog.ConversationCreate, "只由伺服器送給電腦（瀏覽器的伺服器草稿第一次 send）");
        table.Should().NotContain(RemoteCommandCatalog.RemoteExec, "只由桌面發起（對話裡的代理經 myriagent-remote，sync-protocol 14.11）");
        BrowserCommandRules.Classify(RemoteCommandCatalog.RemoteExec).Should().BeNull("瀏覽器送了一律 invalid");
        catalog.Where(n => !RemoteCommandCatalog.NonWebCommands.Contains(n)).Should().BeSubsetOf(table);
        RemoteCommandCatalog.ServerOnlyCommands.Should().BeSubsetOf(table);
        RemoteCommandCatalog.LocalOnlyCommands.Should().BeSubsetOf(table);
        foreach (var name in Phase5Commands)
            RemoteCommandCatalog.RouteOf(name).Should().BeNull($"{name} 是第 5 階段");
    }

    [Fact]
    public void 只在本機的指令與瀏覽器模式的desktop_only相同()
    {
        RemoteCommandCatalog.LocalOnlyCommands.Should().BeEquivalentTo(BrowserCommandRules.DesktopOnlyCommands);
        foreach (var name in BrowserCommandRulesTests.WebProtocolCommands().Concat(["syncLoadRemoteAudit", "syncFutureThing", "sync", "syncing", "Sync"]))
        {
            (BrowserCommandRules.Classify(name) == BrowserCommandClass.DesktopOnly).Should().Be(RemoteCommandCatalog.IsLocalOnly(name), name);
        }
    }

    [Fact]
    public void 伺服器處理與接受不處理的_在型錄都是ServerOnly()
    {
        foreach (var name in BrowserCommandRules.ServerCommands.Concat(BrowserCommandRules.IgnoredCommands))
            RemoteCommandCatalog.RouteOf(name).Should().Be(RemoteRoute.ServerOnly, name);
        RemoteCommandCatalog.ServerOnlyCommands.Except(BrowserCommandRules.ServerCommands).Except(BrowserCommandRules.IgnoredCommands)
            .Should().BeEquivalentTo(new[] { "newConversation", "copyItem", "searchConversations", "quickChat" }
                .Concat(BrowserCommandRules.CollaboratorServerCommands),
                "前兩個在 WP4.2 改由伺服器處理（伺服器草稿、伺服器回 clipboard），之前 not_available；searchConversations（2026-10-23）由伺服器搜清單、回 conversationSearch；"
                + "協作人的管理（多人協作）由伺服器處理（CollaboratorCommandService）");
    }

    [Fact]
    public void 還沒開放的指令就是型錄裡電腦執行的加上伺服器草稿與第5階段()
    {
        var expected = RemoteCommandCatalog.All.Select(s => s.Name).Where(n => !RemoteCommandCatalog.NonWebCommands.Contains(n))
            .Concat(["newConversation", "copyItem", "searchConversations", "quickChat"])
            .Concat(BrowserCommandRules.CollaboratorServerCommands).Concat(Phase5Commands);
        BrowserCommandRules.NotAvailableCommands.Should().BeEquivalentTo(expected, "WP4.1 只立型錄，瀏覽器模式照樣回 not_available");
        foreach (var spec in RemoteCommandCatalog.All.Where(s => !RemoteCommandCatalog.NonWebCommands.Contains(s.Name)))
            BrowserCommandRules.Classify(spec.Name).Should().Be(BrowserCommandClass.NotAvailable, spec.Name);
    }
}
