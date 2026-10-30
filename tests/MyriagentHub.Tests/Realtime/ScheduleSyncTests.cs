using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 各電腦的排程（sync-protocol 14.12，sched1）：裝置的 SchedulesPublish 寫進 device_schedules（整份取代），協商出 sched1 的其他電腦收 SchedulesUpdate
/// （它以外的、連上就送、有變才送），瀏覽器的 init 與 schedules 訊息帶各台的摘要；沒協商出 sched1、內容不合格回 invalid；v7 只多一張表
/// </summary>
public sealed class ScheduleSyncTests
{
    private static JObject Snapshot(string name, int failing) => new()
    {
        ["machine"] = name,
        ["failing"] = failing,
        ["entries"] = new JArray(new JObject { ["path"] = "\\" + name, ["name"] = name, ["origin"] = "agent", ["managed"] = true }),
        ["timed"] = new JArray(),
        ["otherCount"] = 3,
    };

    [Fact]
    public async Task 端到端_上傳_其他電腦收到_瀏覽器的init與更新_整份取代()
    {
        await using var host = await HubTestHost.StartAsync();
        using var a = await TestDevice.AddAsync(host, "電腦甲");
        using var b = await TestDevice.AddAsync(host, "電腦乙");
        await using var ca = await DeviceHubClient.ConnectAsync(host, a.Token);
        await using var cb = await DeviceHubClient.ConnectAsync(host, b.Token);
        var observed = DeviceHubClient.ObservedSpki(host);
        (await ca.HandshakeAsync(a, observed, features: [DeviceFeatureRules.Sched1])).Hello.Features.Should().Contain(DeviceFeatureRules.Sched1);
        await cb.HandshakeAsync(b, observed, features: [DeviceFeatureRules.Sched1]);

        (await cb.WaitForAsync<SchedulesUpdate>(timeoutMs: 20_000)).Devices.Should().BeEmpty("連上就送一次（還沒有任何電腦回報）");
        await ca.Connection.InvokeAsync(DeviceHubMethods.SchedulesPublish, new SchedulesPublish(Snapshot("TkReportSync", 1)));
        var toB = await cb.WaitForAsync<SchedulesUpdate>(u => u.Devices.Count == 1, 20_000);
        toB.Devices[0].DeviceId.Should().Be(a.Id);
        toB.Devices[0].Snapshot.Value<int>("failing").Should().Be(1);
        (await ca.WaitForAsync<SchedulesUpdate>(timeoutMs: 20_000)).Devices.Should().BeEmpty("不送自己的");

        await using var owner = await LoginAndConnectAsync(host);
        var init = await owner.Hub.ReadyAsync();
        var schedules = (JObject)init["schedules"]!;
        schedules.Value<int>("failing").Should().Be(0, "瀏覽器沒有這台，數字由網頁把各台加起來");
        var device = (JObject)((JArray)schedules["devices"]!).Single();
        device.Value<string>("deviceId").Should().Be(a.Id);
        device["snapshot"]!.Value<string>("machine").Should().Be("TkReportSync");

        // 整份取代：同一台再送，只留最新的；其他電腦收到新的
        await ca.Connection.InvokeAsync(DeviceHubMethods.SchedulesPublish, new SchedulesPublish(Snapshot("FileMirrorSync", 0)));
        (await cb.WaitForAsync<SchedulesUpdate>(u => u.Devices.Count == 1 && u.Devices[0].Snapshot.Value<string>("machine") == "FileMirrorSync", 20_000)).Should().NotBeNull();
        using (var read = SqliteSetup.Open(host.Paths.SyncDb))
            SqliteSetup.Scalar(read, "SELECT COUNT(*) FROM device_schedules").Should().Be(1L);
    }

    [Fact]
    public async Task 沒協商出sched1_或內容不合格_invalid()
    {
        await using var host = await HubTestHost.StartAsync();
        using var a = await TestDevice.AddAsync(host);
        using var b = await TestDevice.AddAsync(host);
        await using var ca = await DeviceHubClient.ConnectAsync(host, a.Token);
        await using var cb = await DeviceHubClient.ConnectAsync(host, b.Token);
        var observed = DeviceHubClient.ObservedSpki(host);
        await ca.HandshakeAsync(a, observed, features: []);
        await cb.HandshakeAsync(b, observed, features: [DeviceFeatureRules.Sched1]);

        await FluentActions.Invoking(() => ca.Connection.InvokeAsync(DeviceHubMethods.SchedulesPublish, new SchedulesPublish(Snapshot("X", 0))))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid*", "沒協商出 sched1");
        await FluentActions.Invoking(() => cb.Connection.InvokeAsync(DeviceHubMethods.SchedulesPublish, new SchedulesPublish(new JObject { ["entries"] = "x" })))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid*", "entries 不是陣列");
        var big = Snapshot("Big", 0);
        big["entries"] = new JArray(Enumerable.Range(0, ScheduleSyncRules.MaxEntries + 1).Select(i => (JToken)new JObject { ["path"] = "\\" + i }));
        await FluentActions.Invoking(() => cb.Connection.InvokeAsync(DeviceHubMethods.SchedulesPublish, new SchedulesPublish(big)))
            .Should().ThrowAsync<HubException>().WithMessage("*invalid*", "超過上限");
        await cb.Connection.InvokeAsync(DeviceHubMethods.SchedulesPublish, new SchedulesPublish(Snapshot("Ok", 0)));
    }

    [Fact]
    public void v7只多device_schedules()
    {
        SyncSchema.CurrentVersion.Should().BeGreaterThanOrEqualTo(7);
        SyncSchema.V7Tables.Should().Equal("device_schedules");
        SyncSchema.V7.Should().Contain("CREATE TABLE IF NOT EXISTS device_schedules");
    }
}
