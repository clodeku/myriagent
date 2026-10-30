using Myriagent.Tests.TestSupport;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>同一個測試類別共用的 MyriagentHub 子程序與已登入的擁有者（類別結束時正常停止）</summary>
public sealed class MyriagentHubFixture : IAsyncLifetime
{
    internal MyriagentHubProcess Hub { get; private set; } = null!;

    internal OwnerClient Owner { get; private set; } = null!;

    internal HubDb Db => new(Hub.DataDir);

    /// <summary>啟動前就有的、本程序的 MyriagentHub 建的金鑰檔（之前的測試刻意強制結束留下的）</summary>
    private IReadOnlyList<string> orphanKeysBefore = [];

    public async Task InitializeAsync()
    {
        orphanKeysBefore = UserKeyStore.FilesCreatedByTestHub();
        Hub = await MyriagentHubProcess.StartAsync();
        Owner = await OwnerClient.LoginAsync(Hub);
    }

    public async Task DisposeAsync()
    {
        Owner?.Dispose();
        if (Hub is not null)
        {
            await Hub.DisposeAsync();
            Hub.ForcedKill.Should().BeFalse("MyriagentHub 要能正常停止（強制結束會留下孤兒金鑰檔）");
            UserKeyStore.FilesCreatedByTestHub().Except(orphanKeysBefore).Should().BeEmpty("正常停止的 MyriagentHub 不留下金鑰檔（D5）");
        }
    }
}

/// <summary>暫存資料夾裡一台裝置的同步資料位置</summary>
internal sealed class DeviceDir : IDisposable
{
    private readonly TempDirectory dir = new("sync-withhub");

    public DeviceDir()
    {
        Paths = new Myriagent.Sync.SyncPaths(dir.Combine("sync"));
        Directory.CreateDirectory(Paths.Directory);
    }

    public Myriagent.Sync.SyncPaths Paths { get; }

    public string Root => dir.Root;

    public void Dispose()
    {
        SyncSecrets.RememberDeviceFile(Paths.DeviceFile);
        dir.Dispose();
    }
}
