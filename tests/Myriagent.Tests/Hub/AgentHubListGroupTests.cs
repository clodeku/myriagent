using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Hub;

/// <summary>對話清單的日期分組由宿主算（網頁照用 group）：跨日時要重送，否則整晚開著的 app 隔天還把昨天的對話列在「今天」</summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubListGroupTests
{
    [Fact]
    public async Task 跨日_重送一次對話清單_同一天不重送()
    {
        using var h = new HubHarness();
        var today = DateTime.Now.Date;
        var mark = h.Main.Mark();

        h.Ui.Invoke(() => h.Hub.RefreshListOnNewDay(today.AddHours(12)));
        await Task.Delay(200);
        h.Main.Messages("conversations", mark).Should().BeEmpty("還是同一天，分組不會變");

        h.Ui.Invoke(() => h.Hub.RefreshListOnNewDay(today.AddDays(1).AddMinutes(1)));
        await h.Main.WaitForAsync("conversations", since: mark);

        var again = h.Main.Mark();
        h.Ui.Invoke(() => h.Hub.RefreshListOnNewDay(today.AddDays(1).AddMinutes(2)));
        await Task.Delay(200);
        h.Main.Messages("conversations", again).Should().BeEmpty("同一個新日期只重送一次");
    }
}
