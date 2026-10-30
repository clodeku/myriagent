using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// 測試用的 MyriagentHub 子程序：強制結束（S8、soak、中間人等長情境用）之後，伺服器來不及刪的 TLS 私鑰檔由 <see cref="MyriagentHubProcess.KillAsync"/>
/// 刪掉——只刪這個實例建的，重啟後的新實例（同一個建立者路徑）的私鑰不動
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class MyriagentHubProcessTests
{
    [Fact]
    public async Task 強制結束_刪掉這個實例的金鑰檔_重啟後的實例不受影響()
    {
        var before = UserKeyStore.FilesCreatedByTestHub();
        await using var hub = await MyriagentHubProcess.StartAsync();
        var first = hub.InstanceKeys.ToList();
        first.Should().NotBeEmpty("file 憑證庫載入 TLS 憑證時私鑰落地成金鑰檔（陽性對照：認得出這個實例建的檔）");
        UserKeyStore.FilesCreatedByTestHub().Should().Contain(first);

        await hub.KillAsync();

        hub.InstanceKeys.Should().BeEmpty();
        UserKeyStore.FilesCreatedByTestHub().Except(before).Should().BeEmpty("強制結束留下的金鑰檔刪掉了");

        // 同一個資料根重啟（S8 的做法）：新實例的金鑰檔另外記，前一個的刪除不碰到它
        await hub.LaunchAsync();
        var second = hub.InstanceKeys.ToList();
        second.Should().NotBeEmpty().And.NotIntersectWith(first);
        await hub.KillAsync();
        UserKeyStore.FilesCreatedByTestHub().Except(before).Should().BeEmpty();
    }
}
