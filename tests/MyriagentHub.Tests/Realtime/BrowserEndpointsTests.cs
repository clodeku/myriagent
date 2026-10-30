using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.EventLogTests;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Realtime;

/// <summary>
/// 撤銷裝置與刪除帳戶的擁有者端點（設計 14.4、8.2.3、8.2.4；SEC-13）：要近期驗證、錯誤碼、撤銷後立即中斷與權杖失效、瀏覽器收到推送、稽核與事件
/// </summary>
public sealed class BrowserEndpointsTests
{
    // ───── 撤銷裝置 ─────

    [Fact]
    public async Task 撤銷裝置要近期驗證()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        using var owner = await LoginAsync(host);
        host.Clock.Advance(TimeSpan.FromMinutes(6));

        using (var stale = await owner.PostAsync(Revoke(device.Id), new { reason = "retired" }))
        {
            stale.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var body = await Json(stale);
            body.GetProperty("error").GetString().Should().Be("step_up_required");
            body.GetProperty("stepUp").GetBoolean().Should().BeTrue();
        }
        Revoked(host, device.Id).Should().BeFalse("近期驗證不夠新時什麼都不做");

        using (var stepUp = await OwnerFlows.StepUpAsync(owner))
            stepUp.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var ok = await owner.PostAsync(Revoke(device.Id), new { reason = "retired" }))
        {
            ok.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(ok)).GetProperty("ok").GetBoolean().Should().BeTrue();
        }
        Revoked(host, device.Id).Should().BeTrue();
        await WaitForAuditAsync(host, AuditActions.DeviceRevoked);
        var audit = (await AuditAsync(host, AuditActions.DeviceRevoked)).Single();
        audit.Target.Should().Be(device.Id);
        audit.ActorType.Should().Be(AuditActors.Owner);
        audit.ActorId.Should().HaveLength(12, "工作階段 id 只記前 12 碼");
        audit.Detail.Should().Contain("retired");
    }

    [Fact]
    public async Task 撤銷原因不合法回400()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        using var owner = await LoginAsync(host);
        foreach (var body in new object?[] { new { reason = "x" }, new { reason = "Retired" }, new { }, null })
        {
            using var response = await owner.PostAsync(Revoke(device.Id), body);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Json(response)).GetProperty("error").GetString().Should().Be("invalid_request");
        }

        // 完全沒有本文（Content-Length: 0）
        using var request = new HttpRequestMessage(HttpMethod.Post, Revoke(device.Id));
        request.Headers.Add("X-Myriagent-Csrf", "1");
        request.Headers.Add("Origin", HubTestHost.Origin);
        using var empty = await owner.Http.SendAsync(request);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest, "沒有本文也要走到端點（不是比對不到的 404）");
        (await Json(empty)).GetProperty("error").GetString().Should().Be("invalid_request");
        Revoked(host, device.Id).Should().BeFalse();

        // 不是 JSON 的本文
        using var text = new HttpRequestMessage(HttpMethod.Post, Revoke(device.Id)) { Content = new StringContent("retired") };
        text.Headers.Add("X-Myriagent-Csrf", "1");
        text.Headers.Add("Origin", HubTestHost.Origin);
        using var plain = await owner.Http.SendAsync(text);
        plain.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var broken = new HttpRequestMessage(HttpMethod.Post, Revoke(device.Id)) { Content = new StringContent("{\"reason\":", System.Text.Encoding.UTF8, "application/json") };
        broken.Headers.Add("X-Myriagent-Csrf", "1");
        broken.Headers.Add("Origin", HubTestHost.Origin);
        using var malformed = await owner.Http.SendAsync(broken);
        malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(malformed)).GetProperty("error").GetString().Should().Be("invalid_request");
        Revoked(host, device.Id).Should().BeFalse();
    }

    [Fact]
    public async Task 撤銷不存在的裝置回404()
    {
        await using var host = await HubTestHost.StartAsync();
        using var owner = await LoginAsync(host);
        using var response = await owner.PostAsync(Revoke(SyncIds.NewDeviceId()), new { reason = "retired" });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(response)).GetProperty("error").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task 重複撤銷回409()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        using var owner = await LoginAsync(host);
        using (var first = await owner.PostAsync(Revoke(device.Id), new { reason = "compromised" }))
            first.StatusCode.Should().Be(HttpStatusCode.OK);
        using var again = await owner.PostAsync(Revoke(device.Id), new { reason = "retired" });
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(again)).GetProperty("error").GetString().Should().Be("already_revoked");
    }

    [Theory]
    [InlineData("not-a-device")]
    [InlineData("D_01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    [InlineData("la_01ARZ3NDEKTSV4RRFFQ69G5FAV")]
    public async Task id格式不對回404(string id)
    {
        await using var host = await HubTestHost.StartAsync();
        using var owner = await LoginAsync(host);
        using var response = await owner.PostAsync(Revoke(id), new { reason = "retired" });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Json(response)).GetProperty("error").GetString().Should().Be("not_found");
    }

    [Fact]
    public async Task 撤銷後1秒內中斷那台的連線且之後401()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host));
        using var bearer = host.CreateClient(cookies: false);
        bearer.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", device.Token);
        using (var before = await bearer.Http.PostAsync("/hubs/v1/device/negotiate?negotiateVersion=1", null))
            before.StatusCode.Should().Be(HttpStatusCode.OK, "正向對照：撤銷前這個權杖可以升級");
        using var owner = await LoginAsync(host);

        using (var response = await owner.PostAsync(Revoke(device.Id), new { reason = "compromised" }))
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Task.WhenAny(client.Closed, Task.Delay(1000))).Should().Be(client.Closed, "那台的連線 1 秒內中斷（SEC-13）");
        await client.DisposeAsync();
        using var after = await bearer.Http.PostAsync("/hubs/v1/device/negotiate?negotiateVersion=1", null);
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "同一個權杖的升級 401");
    }

    [Fact]
    public async Task 撤銷後瀏覽器收到devices與accounts()
    {
        await using var host = await HubTestHost.StartAsync();
        var devices = await AddDevicesInOrderAsync(host, "甲", "乙");
        try
        {
            (await AppendAsync(host, devices[0].Id, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com"))))).Ack.Results.Single().Rejected.Should().BeNull();
            (await AppendAsync(host, devices[1].Id, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-9", "claude:org1:a@example.com"))))).Ack.Results.Single().Rejected.Should().BeNull();
            await using var owner = await LoginAndConnectAsync(host);
            var init = await owner.Hub.ReadyAsync();
            init["remote"]!["devices"]!.Select(d => d["name"]!.Value<string>()).Should().Equal(["甲", "乙"]);
            init["accounts"]!.Single()["devices"]!.Select(d => d["deviceId"]!.Value<string>()).Should().BeEquivalentTo([devices[0].Id, devices[1].Id]);

            using (var response = await owner.Http.PostAsync(Revoke(devices[0].Id), new { reason = "retired" }))
                response.StatusCode.Should().Be(HttpStatusCode.OK);

            var (accounts, pushed) = await WaitForBothAsync(owner.Hub,
                m => m["accounts"]!.Single()["devices"]!.Count() == 1,
                m => m["devices"]!.Any(d => d["revoked"] is JObject));
            accounts["accounts"]!.Single()["devices"]!.Single()["deviceId"]!.Value<string>().Should().Be(devices[1].Id, "撤銷的那台的連結消失");
            var list = pushed["devices"]!;
            list.Select(d => d["name"]!.Value<string>()).Should().Equal(["乙", "甲"], "已撤銷的排最後");
            list[1]!["revoked"]!["reason"]!.Value<string>().Should().Be("retired");
            list[1]!["online"]!.Value<bool>().Should().BeFalse();
        }
        finally
        {
            foreach (var device in devices)
                device.Dispose();
        }
    }

    [Fact]
    public async Task 寫入者停止時撤銷回503_hub_db那一半已生效且有稽核()
    {
        await using var host = await HubTestHost.StartAsync();
        using var device = await TestDevice.AddAsync(host);
        using var owner = await LoginAsync(host);
        host.Services.GetRequiredService<SyncWriter>().Stop();

        using var response = await owner.PostAsync(Revoke(device.Id), new { reason = "compromised" });
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await Json(response)).GetProperty("error").GetString().Should().Be(BrowserErrorCodes.Unavailable);
        Revoked(host, device.Id).Should().BeTrue("hub.db 那一半已提交：撤銷已生效（sync.db 那一半由重送或下次啟動補做）");
        (await AuditAsync(host, AuditActions.DeviceRevoked)).Should().ContainSingle("稽核與撤銷同一個 hub.db 交易，寫入者停止也留得下");
    }

    // ───── 刪除帳戶 ─────

    [Fact]
    public async Task 刪除帳戶要近期驗證()
    {
        await using var host = await HubTestHost.StartAsync();
        var la = await AddAccountAsync(host);
        using var owner = await LoginAsync(host);
        host.Clock.Advance(TimeSpan.FromMinutes(6));
        using (var stale = await owner.PostAsync(Delete(la)))
        {
            stale.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Json(stale)).GetProperty("error").GetString().Should().Be("step_up_required");
        }
        Count(host, $"SELECT COUNT(*) FROM logical_accounts WHERE logical_account_id = '{la}' AND deleted_at IS NULL").Should().Be(1);

        using (var stepUp = await OwnerFlows.StepUpAsync(owner))
            stepUp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var ok = await owner.PostAsync(Delete(la));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(ok)).GetProperty("ok").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task 刪除不存在的帳戶回404()
    {
        await using var host = await HubTestHost.StartAsync();
        var la = await AddAccountAsync(host);
        using var owner = await LoginAsync(host);
        foreach (var id in new[] { SyncIds.NewLogicalAccountId(), "not-an-account", SyncIds.NewDeviceId() })
        {
            using var response = await owner.PostAsync(Delete(id));
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, id);
            (await Json(response)).GetProperty("error").GetString().Should().Be("not_found");
        }
        using (var first = await owner.PostAsync(Delete(la)))
            first.StatusCode.Should().Be(HttpStatusCode.OK);
        using var again = await owner.PostAsync(Delete(la));
        again.StatusCode.Should().Be(HttpStatusCode.NotFound, "已刪除的帳戶");
        ServerPayloads<AccountDeletedPayload>(host, SyncEventKinds.AccountDeleted).Should().ContainSingle("重複刪除不再發事件");
    }

    [Fact]
    public async Task 刪除後accounts不再有它且寫稽核與account_deleted事件()
    {
        await using var host = await HubTestHost.StartAsync();
        var la = await AddAccountAsync(host);
        var version = LatestAccount(host).Version;
        await using var owner = await LoginAndConnectAsync(host);
        (await owner.Hub.ReadyAsync())["accounts"]!.Single()["id"]!.Value<string>().Should().Be(la);

        using (var response = await owner.Http.PostAsync(Delete(la)))
            response.StatusCode.Should().Be(HttpStatusCode.OK);

        var deleted = ServerPayloads<AccountDeletedPayload>(host, SyncEventKinds.AccountDeleted).Should().ContainSingle().Subject;
        deleted.LogicalAccountId.Should().Be(la);
        deleted.Version.Should().Be(version + 1);
        Count(host, $"SELECT COUNT(*) FROM account_links WHERE logical_account_id = '{la}'").Should().Be(0, "各台解除連結");
        await owner.Hub.WaitForAsync("accounts", m => !m["accounts"]!.Any());
        await WaitForAuditAsync(host, AuditActions.AccountDeleted);
        var audit = (await AuditAsync(host, AuditActions.AccountDeleted)).Single();
        audit.Target.Should().Be(la);
        audit.Outcome.Should().Be(AuditOutcomes.Ok);
        audit.ActorType.Should().Be(AuditActors.Owner);
    }

    [Fact]
    public async Task 被刪除的帳戶在裝置重新回報同一身分時恢復()
    {
        await using var host = await HubTestHost.StartAsync();
        var deviceId = await AddDeviceAsync(host);
        (await AppendAsync(host, deviceId, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com"))))).Ack.Results.Single().Rejected.Should().BeNull();
        var la = LatestAccount(host).LogicalAccountId;
        using var owner = await LoginAsync(host);
        using (var response = await owner.PostAsync(Delete(la)))
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        Count(host, $"SELECT COUNT(*) FROM logical_accounts WHERE logical_account_id = '{la}' AND deleted_at IS NOT NULL").Should().Be(1);

        (await AppendAsync(host, deviceId, Batch(Ev(2, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com"))))).Ack.Results.Single().Rejected.Should().BeNull();
        var restored = LatestAccount(host);
        restored.LogicalAccountId.Should().Be(la, "identity_key 唯一：恢復同一個邏輯帳戶，不另建");
        restored.Links.Should().ContainSingle(l => l.DeviceId == deviceId);
        Count(host, "SELECT COUNT(*) FROM logical_accounts WHERE deleted_at IS NULL").Should().Be(1);
    }

    // ───── 工具 ─────

    private static string Revoke(string deviceId) => $"/api/v1/devices/{deviceId}/revoke";

    private static string Delete(string logicalAccountId) => $"/api/v1/accounts/{logicalAccountId}/delete";

    private static async Task<HubClient> LoginAsync(HubTestHost host)
    {
        var client = host.CreateClient();
        await OwnerFlows.LoginOwnerAsync(client);
        return client;
    }

    private static async Task<string> AddAccountAsync(HubTestHost host)
    {
        var deviceId = await AddDeviceAsync(host);
        (await AppendAsync(host, deviceId, Batch(Ev(1, SyncEventKinds.LinkUpsert, Link("acct-1", "claude:org1:a@example.com")))))
            .Ack.Results.Single().Rejected.Should().BeNull();
        return LatestAccount(host).LogicalAccountId;
    }

    private static bool Revoked(HubTestHost host, string deviceId) =>
        host.Services.GetRequiredService<DeviceDirectory>().Get(deviceId)?.Revoked == true;

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>等到 accounts 與 devices 各有一則符合條件（順序不拘；其他訊息略過）</summary>
    private static async Task<(JObject Accounts, JObject Devices)> WaitForBothAsync(
        BrowserHubClient hub, Func<JObject, bool> accounts, Func<JObject, bool> devices, int timeoutMs = 5000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        JObject? a = null;
        JObject? d = null;
        while (a is null || d is null)
        {
            var message = await hub.Messages.Reader.ReadAsync(cts.Token);
            switch ((string?)message["type"])
            {
                case "accounts" when accounts(message):
                    a = message;
                    break;
                case "devices" when devices(message):
                    d = message;
                    break;
            }
        }
        return (a, d);
    }

    private static async Task<List<AuditRecord>> AuditAsync(HubTestHost host, string action)
    {
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HubDbContext>();
        return await db.Audit.AsNoTracking().Where(a => a.Action == action).OrderBy(a => a.Seq).ToListAsync();
    }
}
