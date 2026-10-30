using Myriagent.Sync;
using MyriagentHub.Media;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 給 WP4.5 的接縫檔（WP4.7 計畫 6.3、D43，全文逐位元組照計畫）：沒有 <c>AddHubMedia</c> 時的預設實作，與 <c>AddHubMedia</c> 換成的實作
/// </summary>
public sealed class MediaSeamsTests
{
    [Fact]
    public async Task 接縫檔的預設實作_files與fileRef丟掉_其他原樣_TryVerify一律false()
    {
        var services = new ServiceCollection().AddMediaSeams();
        using var provider = services.BuildServiceProvider();
        var rewriter = provider.GetRequiredService<ICommandOutputRewriter>();
        var registry = provider.GetRequiredService<IUploadRegistry>();
        rewriter.Should().BeOfType<DefaultCommandOutputRewriter>();
        registry.Should().BeOfType<NoUploadRegistry>();

        foreach (var type in new[] { "files", "fileRef" })
            (await rewriter.RewriteAsync(SyncIds.NewDeviceId(), "cmd", null, new JObject { ["type"] = type }, CancellationToken.None)).Should().BeNull(type);
        foreach (var message in new[] { new JObject { ["type"] = "fileSearch" }, new JObject { ["type"] = "toast" }, new JObject { ["x"] = 1 } })
            (await rewriter.RewriteAsync(SyncIds.NewDeviceId(), "cmd", null, message, CancellationToken.None)).Should().BeSameAs(message);

        registry.TryVerify([]).Should().BeFalse();
        registry.TryVerify([new UploadClaim("u1", 1, new string('a', 64))]).Should().BeFalse();
    }

    [Fact]
    public void AddMediaSeams是TryAdd_已經登記的不換掉_呼叫兩次也只有一份()
    {
        var mine = new DefaultCommandOutputRewriter();
        var services = new ServiceCollection();
        services.AddSingleton<ICommandOutputRewriter>(mine);
        services.AddMediaSeams().AddMediaSeams();

        services.Count(d => d.ServiceType == typeof(ICommandOutputRewriter)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IUploadRegistry)).Should().Be(1);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICommandOutputRewriter>().Should().BeSameAs(mine);
    }

    [Fact]
    public async Task AddHubMedia之後解析出實作()
    {
        await using var host = await HubTestHost.StartAsync();

        host.Services.GetRequiredService<ICommandOutputRewriter>().Should().BeSameAs(host.Services.GetRequiredService<CommandOutputRewriter>());
        host.Services.GetRequiredService<IUploadRegistry>().Should().BeSameAs(host.Services.GetRequiredService<UploadRegistry>());
    }
}
