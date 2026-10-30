using Myriagent.Accounts;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 本機送出被拒（RejectSend）的 sendRejected：帶原因（reason，和同時送的錯誤提示同一段文字）、只附檔沒打字的也送（text 是空字串），
/// 網頁據此把對話欄裡「送出中」的那則標成沒有送出並寫出原因（Web/sendrules.js，2026-10-23）。遠端的形狀不變（AgentHubRemoteEntryTests）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubSendRejectTests
{
    /// <summary>自訂工作資料夾、附一個檔，之後把資料夾刪掉：送出時在檢查資料夾那一步被拒</summary>
    private static async Task<string> ConversationWithMissingFolderAsync(HubHarness h)
    {
        var folder = h.Temp.CreateDirectory("proj");
        var id = h.NewConversation(h.Main, h.Claude);
        await h.SetFolderAsync(h.Main, id, folder);
        var file = h.Temp.WriteFile(@"in\資料.csv", "a,b");
        var mark = h.Main.Mark();
        h.Command(h.Main, "addFiles", new { conversationId = id }, new[] { file });
        await h.Main.WaitForAsync("composer", m => m["composer"]!["attachments"]!.Count() == 1, mark, conversationId: id);
        Directory.Delete(folder);
        return id;
    }

    [Fact]
    public async Task 被拒的sendRejected帶原因_和錯誤提示同一段文字()
    {
        using var h = new HubHarness();
        var id = await ConversationWithMissingFolderAsync(h);
        var mark = h.Main.Mark();

        h.Send(h.Main, id, "請整理這份資料");

        var rejected = await h.Main.WaitForAsync("sendRejected", since: mark, conversationId: id);
        var toast = await h.Main.WaitForAsync("toast", m => m.Value<string>("level") == "error", mark);
        rejected.Value<string>("text").Should().Be("請整理這份資料");
        rejected.Value<string>("reason").Should().StartWith("工作資料夾不存在").And.Be(toast.Value<string>("text"));
    }

    [Fact]
    public async Task 只附檔沒打字被拒_也送sendRejected_文字是空的_帶原因()
    {
        using var h = new HubHarness();
        var id = await ConversationWithMissingFolderAsync(h);
        var mark = h.Main.Mark();

        h.Send(h.Main, id, "");

        var rejected = await h.Main.WaitForAsync("sendRejected", since: mark, conversationId: id);
        rejected.Value<string>("text").Should().BeEmpty("網頁以空字串對上只附檔的那則");
        rejected.Value<string>("reason").Should().StartWith("工作資料夾不存在");
        h.Main.Last("composer", id)!["composer"]!["attachments"]!.Should().ContainSingle("沒送出的附件照舊還回輸入區");
        h.Sessions.Should().BeEmpty();
    }

    [Fact]
    public void 帳戶未登入被拒_原因寫出帳戶的狀態()
    {
        using var h = new HubHarness(status: AccountStatus.NotLoggedIn);
        var id = h.NewConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Send(h.Main, id, "你好");

        var rejected = h.Main.Messages("sendRejected", mark, id).Should().ContainSingle().Subject;
        rejected.Value<string>("text").Should().Be("你好");
        rejected.Value<string>("reason").Should().Contain(h.Claude.Label);
    }
}
