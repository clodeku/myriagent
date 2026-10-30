using System.Collections.Concurrent;
using System.Security.Cryptography;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using MyriagentHub.Trust;
using Microsoft.AspNetCore.SignalR;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.CommandRouterTests;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 所有電腦登出與 durable 指令（WP4.5 計畫步 8；D26～D28、D30）：每台一列、在線的立刻送、離線的上線後重簽、busy 重送、7 天到期、啟動掃描、30 天清理、
/// 營運計數，以及強制結束伺服器之後照送（S8 的子集，子程序）
/// </summary>
public sealed class DurableCommandTests
{
    private const long Day = 24L * 60 * 60 * 1000;

    // ───── 所有電腦登出（D26） ─────

    [Fact]
    public async Task 所有電腦登出_每台一列_在線的立刻送_離線的pending_沒有cmd1的不排入_瀏覽器收到一則done()
    {
        await using var s = await Setup.StartAsync();
        using var b = await TestDevice.AddAsync(s.Host, "B 電腦");
        using var c = await TestDevice.AddAsync(s.Host, "C 電腦");
        using var d = await TestDevice.AddAsync(s.Host, "D 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        (await LinkAsync(s.Host, b.Id, "acct-b")).Should().Be(la, "同一個登入身分是同一個邏輯帳戶");
        (await LinkAsync(s.Host, c.Id, "acct-c")).Should().Be(la);
        (await LinkAsync(s.Host, d.Id, "acct-d")).Should().Be(la);
        var directory = s.Host.Services.GetRequiredService<DeviceDirectory>();
        directory.Put(directory.Get(d.Id)! with { RevokedAt = s.Host.Clock.NowMs });
        await using var cClient = await ConnectAsync(s.Host, c, cmd1: false);

        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();

        // 一段時間內收到的全部訊息：commandResult 剛好一則（WaitForAsync 會略過別種，數不出多送的）
        var messages = await CollectAsync(s.Browser.Hub, 1000);
        var result = messages.Where(m => (string?)m["type"] == "commandResult").Should().ContainSingle("一個 bcid 至多一則（fan-out 也是一則，D21、D26）").Subject;
        (result["commandId"]!.Value<string>(), result["state"]!.Value<string>(), result["code"]!.Type, result["message"]!.Value<string>())
            .Should().Be((bcid, RemoteResultStates.Done, JTokenType.Null, CommandMessageRules.FanOutText(1, 1, 1)));
        result["message"]!.Value<string>().Should().Be("已送出：在線的 1 台已送出；不在線的 1 台在 7 天內上線時執行；1 台的程式還不支援，要先更新");
        var toast = messages.Where(m => (string?)m["type"] == "toast").Should().ContainSingle().Subject;
        (toast["text"]!.Value<string>(), toast["level"]!.Value<string>()).Should().Be((CommandMessageRules.FanOutText(1, 1, 1), CommandMessageRules.SuccessLevel));

        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        var verdict = s.Verify(delivery);
        verdict.Ok.Should().BeTrue(verdict.Detail);
        (verdict.Body!.Durable, verdict.Body.Cmd["accountId"]!.Value<string>(), verdict.Body.Cmd["deviceId"]!.Value<string>())
            .Should().Be((true, "acct-a", s.Owner.Id), "A 的信封帶 A 的本機帳戶 id");

        var rows = Rows(s.Host, bcid);
        rows.Select(r => (r.Device, r.State)).Should().BeEquivalentTo(new[] { (s.Owner.Id, CommandStates.Delivered), (b.Id, CommandStates.Pending) },
            "在線的 delivered、離線的 pending；沒有 cmd1 的 C 與撤銷的 D 沒有列");
        rows.Select(r => r.Cid).Distinct().Should().HaveCount(2, "每列有自己的 cid");
        foreach (var row in rows)
        {
            row.Durable.Should().BeTrue();
            row.UnsignedBody.Should().NotBeNull();
            (row.ExpiresAt - row.CreatedAt).Should().Be(CommandEnvelope.MaxDurableAgeMs);
            row.Issuer.Should().Be("{\"kind\":\"browser\",\"label\":\"Chrome・Windows\"}");
        }
        rows.Single(r => r.Device == s.Owner.Id).Sha.Should().Be(verdict.ContentSha256);
        rows.Single(r => r.Device == s.Owner.Id).Cid.Should().Be(delivery.CommandId);

        await AssertNoDeliveryAsync(cClient, 0);
        await WaitUntilAsync(() => s.Counters.Snapshot().Delivered == 1, because: "router 送出 A 的那一列");
        s.Counters.Snapshot().Issued.Should().Be(2, "每列一次");
        var audits = await IssuedAuditsAsync(s.Host, 2);
        audits.Select(a => a.Target).Should().BeEquivalentTo(rows.Select(r => r.Cid));
        audits.Should().OnlyContain(a => a.Detail!.Contains("\"durable\":true") && a.Outcome == AuditOutcomes.Ok);
    }

    [Fact]
    public async Task 所有電腦登出_全部不支援_立即拒絕unsupported_不寫列()
    {
        await using var s = await Setup.StartAsync(features: [DeviceFeatureRules.Conv1]);
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        (await s.Browser.Hub.WaitForAsync("commandResult"))["code"]!.Value<string>().Should().Be(RemoteResultCodes.Unsupported);
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(0);
        s.Counters.Snapshot().RejectedAtServer.Should().Be(1);
    }

    [Fact]
    public async Task 所有電腦登出_各台的結果另有toast()
    {
        await using var s = await Setup.StartAsync();
        using var b = await TestDevice.AddAsync(s.Host, "B 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        await LinkAsync(s.Host, b.Id, "acct-b");
        await using var bClient = await ConnectAsync(s.Host, b);

        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        (await s.Browser.Hub.WaitForAsync("commandResult"))["message"]!.Value<string>().Should().Be(CommandMessageRules.FanOutText(2, 0, 0));
        await s.Browser.Hub.WaitForAsync("toast");
        var aDelivery = await s.Device!.WaitForAsync<CommandDelivery>();
        var bDelivery = await bClient.WaitForAsync<CommandDelivery>();

        // 各台的最終結果只送那台的 toast，不送 commandResult（D26）
        await s.Device.SendResultAsync(new CommandResult(aDelivery.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Policy));
        var afterA = await CollectAsync(s.Browser.Hub, 500);
        afterA.Where(m => (string?)m["type"] == "commandResult").Should().BeEmpty();
        var aToast = afterA.Where(m => (string?)m["type"] == "toast").Should().ContainSingle().Subject;
        (aToast["text"]!.Value<string>(), aToast["level"]!.Value<string>()).Should().Be(("『A 電腦』沒有登出：那台電腦的設定不允許這個遠端操作", "error"));
        await bClient.SendResultAsync(new CommandResult(bDelivery.CommandId, RemoteResultStates.Done));
        var afterB = await CollectAsync(s.Browser.Hub, 500);
        afterB.Where(m => (string?)m["type"] == "commandResult").Should().BeEmpty();
        var bToast = afterB.Where(m => (string?)m["type"] == "toast").Should().ContainSingle().Subject;
        (bToast["text"]!.Value<string>(), bToast["level"]!.Value<string>()).Should().Be(("『B 電腦』已登出這個帳戶", CommandMessageRules.SuccessLevel), "每台各一則（不節流）");

        var rows = Rows(s.Host, bcid);
        rows.Single(r => r.Device == s.Owner.Id).State.Should().Be(CommandStates.Rejected);
        rows.Single(r => r.Device == b.Id).State.Should().Be(CommandStates.Done);
    }

    [Fact]
    public async Task 最終結果後unsigned_body清掉()
    {
        await using var s = await Setup.StartAsync();
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        Rows(s.Host, bcid).Single().UnsignedBody.Should().NotBeNull("送出之後還要靠它在重連時重簽");

        await s.Device.SendResultAsync(new CommandResult(delivery.CommandId, RemoteResultStates.Done));
        var row = Rows(s.Host, bcid).Single();
        (row.State, row.UnsignedBody, row.DoneAt).Should().Be((CommandStates.Done, (string?)null, s.Host.Clock.NowMs));
        JObject.Parse(row.ResultJson!)["state"]!.Value<string>().Should().Be(CommandStates.Done);
    }

    // ───── 上線後重簽（D27） ─────

    [Fact]
    public async Task 離線那台Prove後_以新連線重簽_created不變_ContentDigest等於列的content_sha256()
    {
        await using var s = await Setup.StartAsync();
        using var b = await TestDevice.AddAsync(s.Host, "B 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        await LinkAsync(s.Host, b.Id, "acct-b");
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        await s.Device!.WaitForAsync<CommandDelivery>();
        var pending = Rows(s.Host, bcid).Single(r => r.Device == b.Id);
        pending.State.Should().Be(CommandStates.Pending);

        s.Host.Clock.Advance(TimeSpan.FromMinutes(10));
        var (bClient, hello) = await ConnectWithHelloAsync(s.Host, b);
        await using (bClient)
        {
            var delivery = await bClient.WaitForAsync<CommandDelivery>();
            delivery.CommandId.Should().Be(pending.Cid);
            var verdict = Verify(s.Host, delivery, b.Id, hello.NonceS);
            verdict.Ok.Should().BeTrue(verdict.Detail);
            var now = s.Host.Clock.NowMs;
            (verdict.Body!.Conn, verdict.Body.Created, verdict.Body.Iat, verdict.Body.Exp).Should().Be((hello.NonceS, pending.CreatedAt, now, now + CommandEnvelope.MaxLifetimeMs));
            verdict.ContentSha256.Should().Be(pending.Sha, "重簽後內容摘要不變（電腦以 cid＋摘要去重）");
            verdict.Body.Cmd["accountId"]!.Value<string>().Should().Be("acct-b");
            Verify(s.Host, delivery, b.Id, s.Hello!.NonceS).Step.Should().Be(CommandEnvelopeSteps.Connection, "只對 B 這條連線有效");

            var row = Rows(s.Host, bcid).Single(r => r.Device == b.Id);
            (row.State, row.DeliveredAt).Should().Be((CommandStates.Delivered, now));
            s.Counters.Snapshot().Resigned.Should().Be(1);
            await WaitUntilAsync(() => s.Counters.Snapshot().Delivered == 2, because: "A 由 router、B 重簽之後各送一次");
        }
    }

    [Fact]
    public async Task 列的unsigned_body被改過_內容摘要對不上_不送()
    {
        await using var s = await Setup.StartAsync();
        using var b = await TestDevice.AddAsync(s.Host, "B 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        await LinkAsync(s.Host, b.Id, "acct-b");
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        await s.Device!.WaitForAsync<CommandDelivery>();
        var pending = Rows(s.Host, bcid).Single(r => r.Device == b.Id);
        await s.Host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute("UPDATE commands SET unsigned_body = $body WHERE command_id = $id",
            ("$body", pending.UnsignedBody!.Replace("acct-b", "acct-x", StringComparison.Ordinal)), ("$id", pending.Cid)));

        await using var bClient = await ConnectAsync(s.Host, b);
        await s.Host.Services.GetRequiredService<DurableCommands>().WaitIdleAsync();
        await AssertNoDeliveryAsync(bClient, 300);
        Rows(s.Host, bcid).Single(r => r.Device == b.Id).State.Should().Be(CommandStates.Pending, "不送也不動它（記錯誤）");
        s.Counters.Snapshot().Resigned.Should().Be(0);
    }

    [Fact]
    public async Task 送達後斷線沒回結果_下次Prove送同一個cid()
    {
        await using var s = await Setup.StartAsync();
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        var first = s.Verify(await s.Device!.WaitForAsync<CommandDelivery>());
        var registry = s.Host.Services.GetRequiredService<ConnectionRegistry>();
        await s.Device.Connection.StopAsync();
        await WaitUntilAsync(() => registry.ConnectionsOf(s.Owner.Id).Count == 0, because: "A 斷線");
        Rows(s.Host, bcid).Single().State.Should().Be(CommandStates.Delivered, "durable 送達後沒回結果保持 delivered（D19）");

        var (again, hello) = await ConnectWithHelloAsync(s.Host, s.Owner);
        await using (again)
        {
            var delivery = await again.WaitForAsync<CommandDelivery>();
            var verdict = Verify(s.Host, delivery, s.Owner.Id, hello.NonceS);
            verdict.Ok.Should().BeTrue(verdict.Detail);
            (verdict.Body!.Cid, verdict.Body.Created, verdict.ContentSha256).Should().Be((first.Body!.Cid, first.Body.Created, first.ContentSha256), "同一個指令");
            Verify(s.Host, delivery, s.Owner.Id, s.Hello!.NonceS).Step.Should().Be(CommandEnvelopeSteps.Connection, "換成新連線的 conn");
            await AssertNoDeliveryAsync(again, 300);
        }
    }

    [Fact]
    public async Task Prove完成之前不同步送()
    {
        var gate = new GatedDeviceSender();
        await using var s = await Setup.StartAsync(services: sc =>
        {
            sc.AddSingleton(sp => { gate.Hub = sp.GetRequiredService<IHubContext<DeviceHub>>(); return gate; });
            sc.AddSingleton<IDeviceMessageSender>(sp => sp.GetRequiredService<GatedDeviceSender>());
        });
        using var b = await TestDevice.AddAsync(s.Host, "B 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        await LinkAsync(s.Host, b.Id, "acct-b");
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        await s.Device!.WaitForAsync<CommandDelivery>();

        gate.Hold();
        try
        {
            // 處理者只寫進 channel：ProveResponse 照常回來（同步送的話 Prove 會卡在擋住的送出上）
            var (bClient, _) = await ConnectWithHelloAsync(s.Host, b).WaitAsync(TimeSpan.FromSeconds(5));
            await using (bClient)
            {
                await WaitUntilAsync(() => gate.Held == 1, because: "背景迴圈重簽後要送 B 的 CommandDelivery");
                await AssertNoDeliveryAsync(bClient, 200);
                gate.Release();
                var delivery = await bClient.WaitForAsync<CommandDelivery>();
                delivery.CommandId.Should().Be(Rows(s.Host, null).Single(r => r.Device == b.Id).Cid);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task pending提交與Prove掃描的競態_補送()
    {
        var recorder = new GatedDeviceSender();
        await using var s = await Setup.StartAsync(services: sc =>
        {
            sc.AddSingleton(sp => { recorder.Hub = sp.GetRequiredService<IHubContext<DeviceHub>>(); return recorder; });
            sc.AddSingleton<IDeviceMessageSender>(sp => sp.GetRequiredService<GatedDeviceSender>());
        });
        using var b = await TestDevice.AddAsync(s.Host, "B 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        await LinkAsync(s.Host, b.Id, "acct-b");
        var writer = s.Host.Services.GetRequiredService<SyncWriter>();
        var registry = s.Host.Services.GetRequiredService<ConnectionRegistry>();
        var durable = s.Host.Services.GetRequiredService<DurableCommands>();

        // 擋住寫入者：router 判斷 B 不在線之後，寫 pending 的交易排在後面
        using var gate = new ManualResetEventSlim(false);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = writer.WriteAsync(_ =>
        {
            running.TrySetResult();
            gate.Wait(TimeSpan.FromSeconds(10));
        });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cmd = s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: NextCommandId());
        await Task.Delay(300);
        cmd.IsCompleted.Should().BeFalse("寫入者擋住時列還沒提交");

        // B 上線（已證明、cmd1 的連線）：Prove 的掃描在交易提交之前就跑完了，什麼都沒讀到
        const string fakeConnection = "dc-race-b", fakeConn = "nonce-race-b";
        var connection = new DeviceConnection(fakeConnection, b.Id, () => { }, new UpstreamContext(b.Id, null, null),
            [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1], envelopeConn: fakeConn, helloHubSpki: s.Host.Services.GetRequiredService<HubIdentity>().Spki);
        registry.Add(connection, s.Host.Clock.NowMs);
        registry.MarkProven(connection);
        await durable.WaitIdleAsync();
        recorder.Deliveries(fakeConnection).Should().BeEmpty("掃描時列還沒提交");

        gate.Set();
        await blocker;
        (await cmd)["accepted"]!.Value<bool>().Should().BeTrue();
        (await s.Browser.Hub.WaitForAsync("commandResult"))["message"]!.Value<string>().Should().Be(CommandMessageRules.FanOutText(1, 1, 0), "router 判斷時 B 不在線");

        // 提交之後再看一次那台：有可以送的連線就補送（2 秒內）
        await WaitUntilAsync(() => recorder.Deliveries(fakeConnection).Count == 1, 2000, "提交之後補送給 B");
        var delivery = recorder.Deliveries(fakeConnection).Single();
        Verify(s.Host, delivery, b.Id, fakeConn).Ok.Should().BeTrue();
        s.Counters.Snapshot().Resigned.Should().Be(1, "是 DurableCommands 送的");
        registry.Remove(fakeConnection);
    }

    [Fact]
    public async Task router送到舊連線而新連線的Prove掃描先跑完_以新連線補送同一個cid()
    {
        // 合併前審查 P4R-STATE-01：電腦重連，伺服器還留著舊連線；router 選了舊連線（新連線還沒證明），新連線的 Prove 掃描在列提交之前跑完
        var recorder = new GatedDeviceSender();
        await using var s = await Setup.StartAsync(services: sc =>
        {
            sc.AddSingleton(sp => { recorder.Hub = sp.GetRequiredService<IHubContext<DeviceHub>>(); return recorder; });
            sc.AddSingleton<IDeviceMessageSender>(sp => sp.GetRequiredService<GatedDeviceSender>());
        });
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        var writer = s.Host.Services.GetRequiredService<SyncWriter>();
        var registry = s.Host.Services.GetRequiredService<ConnectionRegistry>();
        var durable = s.Host.Services.GetRequiredService<DurableCommands>();
        var old = registry.ConnectionsOf(s.Owner.Id).Should().ContainSingle().Subject.ConnectionId;

        // 擋住寫入者：router 選了 A 的舊連線、簽好信封之後，寫 delivered 的交易排在後面
        using var gate = new ManualResetEventSlim(false);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = writer.WriteAsync(_ =>
        {
            running.TrySetResult();
            gate.Wait(TimeSpan.FromSeconds(10));
        });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var bcid = NextCommandId();
        var cmd = s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: bcid);
        await Task.Delay(300);
        cmd.IsCompleted.Should().BeFalse("寫入者擋住時列還沒提交");

        // A 的新連線（已證明、cmd1）：Prove 的掃描在交易提交之前就跑完了，什麼都沒讀到
        const string fakeConnection = "dc-race-a2", fakeConn = "nonce-race-a2";
        var connection = new DeviceConnection(fakeConnection, s.Owner.Id, () => { }, new UpstreamContext(s.Owner.Id, null, null),
            [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1], envelopeConn: fakeConn, helloHubSpki: s.Host.Services.GetRequiredService<HubIdentity>().Spki);
        registry.Add(connection, s.Host.Clock.NowMs);
        registry.MarkProven(connection);
        await durable.WaitIdleAsync();
        recorder.Deliveries(fakeConnection).Should().BeEmpty("掃描時列還沒提交");

        gate.Set();
        await blocker;
        (await cmd)["accepted"]!.Value<bool>().Should().BeTrue();
        (await s.Browser.Hub.WaitForAsync("commandResult"))["message"]!.Value<string>().Should().Be(CommandMessageRules.FanOutText(1, 0, 0), "router 判斷時 A 在線");
        var first = await s.Device!.WaitForAsync<CommandDelivery>();
        recorder.Deliveries(old).Should().ContainSingle("router 送到舊連線").Which.CommandId.Should().Be(first.CommandId);

        // router 送出之後再看一次那台：有更新的連線就以它重簽補送（2 秒內）
        await WaitUntilAsync(() => recorder.Deliveries(fakeConnection).Count == 1, 2000, "router 送出之後補送給 A 的新連線");
        var delivery = recorder.Deliveries(fakeConnection).Single();
        delivery.CommandId.Should().Be(first.CommandId, "同一個 cid（電腦以 cid＋摘要去重）");
        var verdict = Verify(s.Host, delivery, s.Owner.Id, fakeConn);
        verdict.Ok.Should().BeTrue(verdict.Detail);
        var row = Rows(s.Host, bcid).Single();
        (row.Cid, row.State).Should().Be((first.CommandId, CommandStates.Delivered));
        verdict.ContentSha256.Should().Be(row.Sha, "重簽後內容摘要不變");
        s.Counters.Snapshot().Resigned.Should().Be(1, "是 DurableCommands 送的");
        await durable.WaitIdleAsync();
        recorder.Deliveries(old).Should().ContainSingle("舊連線只有 router 送的那一則");
        recorder.Deliveries(fakeConnection).Should().ContainSingle();
        registry.Remove(fakeConnection);
    }

    [Fact]
    public async Task router直接送出之後沒有更新的連線_再看一次不多送()
    {
        await using var s = await Setup.StartAsync();
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        var durable = s.Host.Services.GetRequiredService<DurableCommands>();
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        // commandResult 在排進「再看一次」之後才送：收到它之後等背景迴圈閒下來，那件工作一定做完了
        (await s.Browser.Hub.WaitForAsync("commandResult"))["message"]!.Value<string>().Should().Be(CommandMessageRules.FanOutText(1, 0, 0));
        await s.Device!.WaitForAsync<CommandDelivery>();
        await durable.WaitIdleAsync();
        await AssertNoDeliveryAsync(s.Device, 300);
        s.Counters.Snapshot().Resigned.Should().Be(0, "最好的連線就是 router 送的那條");
    }

    [Fact]
    public async Task busy_5秒後重送_同一條連線最多3次_之後每60秒的重掃再送()
    {
        await using var s = await Setup.StartAsync();
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        await s.Browser.Hub.WaitForAsync("commandResult");
        await s.Browser.Hub.WaitForAsync("toast");
        var cid = (await s.Device!.WaitForAsync<CommandDelivery>()).CommandId;
        var maintenance = s.Host.Services.GetRequiredService<CommandMaintenance>();
        var durable = s.Host.Services.GetRequiredService<DurableCommands>();
        // 先等背景迴圈把連線證明（Proven）、router 送出之後（Kick、Recheck）排的工作做完：Proven 不看 5 秒的重送排程，
        // 機器忙時它拖到第一次 busy 之後才跑，會把剛回到 pending 的列再送一次（整套測試一起跑時偶發失敗）
        await durable.WaitIdleAsync();
        await AssertNoDeliveryAsync(s.Device, 200);

        for (var retry = 1; retry <= CommandStateRules.MaxBusyRetries; retry++)
        {
            await s.Device.SendResultAsync(new CommandResult(cid, RemoteResultStates.Rejected, RemoteResultCodes.Busy));
            Rows(s.Host, bcid).Single().State.Should().Be(CommandStates.Pending, $"第 {retry} 次 busy：電腦什麼都沒執行，回到 pending");
            s.Host.Clock.Advance(TimeSpan.FromMilliseconds(CommandStateRules.BusyRetryDelayMs - 1));
            await maintenance.SweepAsync();
            await durable.WaitIdleAsync();
            await AssertNoDeliveryAsync(s.Device, 200);

            s.Host.Clock.Advance(TimeSpan.FromMilliseconds(1));
            await maintenance.SweepAsync();
            await durable.WaitIdleAsync();
            var delivery = await s.Device.WaitForAsync<CommandDelivery>();
            delivery.CommandId.Should().Be(cid, $"第 {retry} 次重送");
            var verdict = s.Verify(delivery);
            verdict.Ok.Should().BeTrue(verdict.Detail);
            verdict.Body!.Iat.Should().Be(s.Host.Clock.NowMs, "以新的 iat／exp 重簽");
        }

        // 5 秒的重送滿了之後：busy 的原因可能持續很久（WP4.8：這個帳戶正在那台登入、那台的 CLI 正在更新），一直連著的電腦不會再 Prove，
        // 所以 60 秒的重掃照樣在這條連線送（不看重送次數，WP4.8 合併前審查 RS-1）；之後的 busy 也不再排 5 秒的重送
        for (var rescan = 1; rescan <= 2; rescan++)
        {
            await s.Device.SendResultAsync(new CommandResult(cid, RemoteResultStates.Rejected, RemoteResultCodes.Busy));
            s.Host.Clock.Advance(TimeSpan.FromMilliseconds(CommandStateRules.BusyRetryDelayMs));
            await maintenance.SweepAsync();
            await durable.WaitIdleAsync();
            await AssertNoDeliveryAsync(s.Device, 300);
            Rows(s.Host, bcid).Single().State.Should().Be(CommandStates.Pending, $"第 {rescan} 輪：這條連線的 5 秒重送滿了，不排");
            s.Host.Clock.Advance(TimeSpan.FromMilliseconds(CommandStateRules.RescanIntervalMs));
            await maintenance.SweepAsync();
            await durable.WaitIdleAsync();
            var delivery = await s.Device.WaitForAsync<CommandDelivery>();
            delivery.CommandId.Should().Be(cid, $"第 {rescan} 輪：60 秒的重掃在同一條連線再送一次");
            var verdict = s.Verify(delivery);
            verdict.Ok.Should().BeTrue(verdict.Detail);
            verdict.Body!.Iat.Should().Be(s.Host.Clock.NowMs, "以新的 iat／exp 重簽");
            Rows(s.Host, bcid).Single().State.Should().Be(CommandStates.Delivered);
        }
        s.Counters.Snapshot().PendingDurable.Should().Be(1, "重掃當下它是 pending");
        await s.Browser.Hub.AssertNoMessageAsync("toast", 100);
        await s.Browser.Hub.AssertNoMessageAsync("commandResult", 100);
        s.Counters.Snapshot().Resigned.Should().Be(CommandStateRules.MaxBusyRetries + 2);

        // 新的連線 Prove 之後照送（送在舊連線上的 delivered 換成新連線重簽）
        var (again, hello) = await ConnectWithHelloAsync(s.Host, s.Owner);
        await using (again)
        {
            var delivery = await again.WaitForAsync<CommandDelivery>();
            (delivery.CommandId, Verify(s.Host, delivery, s.Owner.Id, hello.NonceS).Ok).Should().Be((cid, true));
        }
        s.Counters.Snapshot().Resigned.Should().Be(CommandStateRules.MaxBusyRetries + 3);
    }

    [Fact]
    public async Task 七天到期_expired_unsigned_body清掉_不再送()
    {
        await using var s = await Setup.StartAsync();
        using var b = await TestDevice.AddAsync(s.Host, "B 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        await LinkAsync(s.Host, b.Id, "acct-b");
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        await s.Device!.WaitForAsync<CommandDelivery>();

        // 到期的判斷每 60 秒（重掃時）做一次：先停在到期前 60 秒做一輪，再推進 60 秒剛好 7 天
        var maintenance = s.Host.Services.GetRequiredService<CommandMaintenance>();
        s.Host.Clock.Advance(TimeSpan.FromMilliseconds(CommandEnvelope.MaxDurableAgeMs - CommandStateRules.RescanIntervalMs));
        await maintenance.SweepAsync();
        Rows(s.Host, bcid).Select(r => r.State).Should().BeEquivalentTo([CommandStates.Delivered, CommandStates.Pending], "還沒到期");

        s.Host.Clock.Advance(TimeSpan.FromMilliseconds(CommandStateRules.RescanIntervalMs));
        await maintenance.SweepAsync();
        var now = s.Host.Clock.NowMs;
        foreach (var row in Rows(s.Host, bcid))
        {
            (row.State, row.UnsignedBody, row.DoneAt).Should().Be((CommandStates.Expired, (string?)null, now), $"{row.Device}：剛好 7 天");
            JObject.Parse(row.ResultJson!)["state"]!.Value<string>().Should().Be(CommandStates.Expired);
        }
        s.Counters.Snapshot().Expired.Should().Be(2);

        await using var bClient = await ConnectAsync(s.Host, b);
        await s.Host.Services.GetRequiredService<DurableCommands>().WaitIdleAsync();
        await AssertNoDeliveryAsync(bClient, 300);
    }

    // ───── 啟動掃描與清理（D28） ─────

    [Fact]
    public async Task 伺服器重啟_非durable的delivered變unknown_durable的delivered變pending()
    {
        var dir = TempDir.Create();
        try
        {
            string stopCid, logoutCid;
            TestDevice owner;
            await using (var s = await Setup.StartAsync(dataDir: dir))
            {
                var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
                (await s.Browser.Hub.CmdAsync(new { cmd = "stop", conversationId = Conv }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
                stopCid = (await s.Device!.WaitForAsync<CommandDelivery>()).CommandId;
                (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
                logoutCid = (await s.Device.WaitForAsync<CommandDelivery>()).CommandId;
                owner = TestDevice.Paired(s.Owner.Id, s.Owner.Token, ECDsa.Create(s.Owner.Key.ExportParameters(includePrivateParameters: true)));
            }

            using (owner)
            {
                await using var host = await HubTestHost.StartAsync(dataDir: dir);
                var stop = Row(host, stopCid);
                (stop.State, stop.DoneAt).Should().Be((CommandStates.Unknown, host.Clock.NowMs), "非 durable 的絕不重送");
                JObject.Parse(stop.ResultJson!)["state"]!.Value<string>().Should().Be(CommandStates.Unknown);
                var logout = Row(host, logoutCid);
                (logout.State, logout.DoneAt).Should().Be((CommandStates.Pending, (long?)null), "durable 的 delivered 改回 pending、下次 Prove 重簽");
                logout.UnsignedBody.Should().NotBeNull();
                host.Services.GetRequiredService<CommandCounters>().Snapshot().Unknown.Should().Be(1);

                var (device, hello) = await ConnectWithHelloAsync(host, owner);
                await using (device)
                {
                    var delivery = await device.WaitForAsync<CommandDelivery>();
                    delivery.CommandId.Should().Be(logoutCid);
                    Verify(host, delivery, owner.Id, hello.NonceS).Ok.Should().BeTrue();
                    await AssertNoDeliveryAsync(device, 300);
                }
                Row(host, logoutCid).State.Should().Be(CommandStates.Delivered);
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 三十天前結束的列清掉_未結束的保留_一次最多500列()
    {
        await using var host = await HubTestHost.StartAsync();
        var writer = host.Services.GetRequiredService<SyncWriter>();
        var store = host.Services.GetRequiredService<CommandStore>();
        var maintenance = host.Services.GetRequiredService<CommandMaintenance>();
        var now = host.Clock.NowMs;
        var device = SyncIds.NewDeviceId();
        var old = Enumerable.Range(0, CommandStateRules.PruneBatch + 1).Select(i => SyncIds.NewUlid(now - 40 * Day + i)).ToList();
        var recent = SyncIds.NewUlid(now - 31 * Day);
        var open = SyncIds.NewUlid(now - 40 * Day);
        await writer.WriteAsync(context =>
        {
            foreach (var cid in old)
                InsertFinished(context, store, cid, device, doneAt: now - CommandStateRules.RetentionMs);
            InsertFinished(context, store, recent, device, doneAt: now - CommandStateRules.RetentionMs + 1);
            store.Insert(context, TestRow(open, device, CommandStates.Pending, durable: true, created: now - 40 * Day));
        });

        (await maintenance.PruneOnceAsync()).Should().Be((CommandStateRules.PruneBatch, CommandStateRules.PruneBatch), "一個交易最多 500 列");
        (await maintenance.PruneOnceAsync()).Should().Be((1, 1));
        (await maintenance.PruneOnceAsync()).Should().Be((0, 0));
        ConversationEventLogTests.Rows(host, "SELECT command_id FROM commands ORDER BY command_id").Select(r => (string)r[0]!)
            .Should().BeEquivalentTo(new[] { recent, open }, "差 1 毫秒滿 30 天的、還沒結束的（不論多舊）都留著");

        // PruneAsync 一批一批做到沒有
        var more = Enumerable.Range(0, CommandStateRules.PruneBatch * 2 + 3).Select(i => SyncIds.NewUlid(now - 50 * Day + i)).ToList();
        await writer.WriteAsync(context =>
        {
            foreach (var cid in more)
                InsertFinished(context, store, cid, device, doneAt: now - 45 * Day);
        });
        (await maintenance.PruneAsync()).Should().Be(more.Count);
        EventLogTests.Count(host, "SELECT COUNT(*) FROM commands").Should().Be(2);
    }

    // ───── 營運計數（D30） ─────

    [Fact]
    public async Task ops_status有commands計數()
    {
        await using var s = await Setup.StartAsync();
        using var b = await TestDevice.AddAsync(s.Host, "B 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        await LinkAsync(s.Host, b.Id, "acct-b");
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        await s.Device!.WaitForAsync<CommandDelivery>();
        await WaitUntilAsync(() => s.Counters.Snapshot().Delivered == 1, because: "A 的送出完成");
        // 60 秒後的重掃記下 pending 的 durable 列數
        s.Host.Clock.Advance(TimeSpan.FromMilliseconds(CommandStateRules.RescanIntervalMs));
        await s.Host.Services.GetRequiredService<CommandMaintenance>().SweepAsync();

        var status = await s.Browser.Http.GetJsonAsync("/api/v1/ops/status");
        var commands = status.GetProperty("commands");
        commands.EnumerateObject().Select(p => p.Name).Should().Equal(
            "issued", "rejectedAtServer", "delivered", "done", "rejected", "unknown", "expired", "pendingDurable", "ignoredResults", "droppedOutputs", "resigned", "aborted");
        (commands.GetProperty("issued").GetInt64(), commands.GetProperty("delivered").GetInt64(), commands.GetProperty("pendingDurable").GetInt64(),
            commands.GetProperty("resigned").GetInt64()).Should().Be((2L, 1L, 1L, 0L));
    }

    // ───── 強制結束伺服器（S8 的子集，子程序） ─────

    [Fact]
    public async Task 強制結束伺服器_durable指令下次Prove照送_0遺失()
    {
        var dir = TempDir.Create("myriagenthub-durable");
        TestDevice? a = null, b = null;
        try
        {
            string aCid, bCid, aSha, bSha, spki;
            long aNext, bNext;
            // 子程序用真的時鐘：列的 created 與權杖的簽發時間也用真的（SoakTests 的作法）
            await using (var s = await Setup.StartAsync(dataDir: dir, connect: false, clock: new ManualTimeProvider(DateTimeOffset.UtcNow)))
            {
                using var bDevice = await TestDevice.AddAsync(s.Host, "B 電腦");
                var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
                await LinkAsync(s.Host, bDevice.Id, "acct-b");
                var bcid = NextCommandId();
                (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
                (await s.Browser.Hub.WaitForAsync("commandResult"))["message"]!.Value<string>().Should().Be(CommandMessageRules.FanOutText(0, 2, 0));
                var rows = Rows(s.Host, bcid);
                rows.Should().OnlyContain(r => r.State == CommandStates.Pending, "兩台都不在線");
                var ra = rows.Single(r => r.Device == s.Owner.Id);
                var rb = rows.Single(r => r.Device == bDevice.Id);
                (aCid, aSha, bCid, bSha) = (ra.Cid, ra.Sha, rb.Cid, rb.Sha);
                aNext = NextSeq(s.Host, s.Owner.Id);
                bNext = NextSeq(s.Host, bDevice.Id);
                spki = s.Host.Services.GetRequiredService<HubIdentity>().Spki;
                a = TestDevice.Paired(s.Owner.Id, s.Owner.Token, ECDsa.Create(s.Owner.Key.ExportParameters(includePrivateParameters: true)));
                b = TestDevice.Paired(bDevice.Id, bDevice.Token, ECDsa.Create(bDevice.Key.ExportParameters(includePrivateParameters: true)));
            }

            var port = HubProcess.FreePort();
            var server = await HubProcess.StartAsync(dir, port);
            try
            {
                // A 上線：重簽送出；還沒回結果就強制結束伺服器
                var (aClient, aHello) = await ConnectKestrelAsync(server, a, aNext);
                await using (aClient)
                {
                    var first = await aClient.WaitForAsync<CommandDelivery>(timeoutMs: 15_000);
                    first.CommandId.Should().Be(aCid);
                    VerifyReal(first, spki, a.Id, aHello.NonceS, aSha);
                }
                server.Kill();
                server.Dispose();
                server = await HubProcess.StartAsync(dir, port);

                // 重啟後：A 收到同一個 cid（啟動掃描把 delivered 改回 pending）、B 收到它的；兩台回 done
                var (aAgain, aHello2) = await ConnectKestrelAsync(server, a, aNext);
                var (bClient, bHello) = await ConnectKestrelAsync(server, b, bNext);
                await using (aAgain)
                await using (bClient)
                {
                    var aDelivery = await aAgain.WaitForAsync<CommandDelivery>(timeoutMs: 15_000);
                    var bDelivery = await bClient.WaitForAsync<CommandDelivery>(timeoutMs: 15_000);
                    (aDelivery.CommandId, bDelivery.CommandId).Should().Be((aCid, bCid));
                    VerifyReal(aDelivery, spki, a.Id, aHello2.NonceS, aSha);
                    VerifyReal(bDelivery, spki, b.Id, bHello.NonceS, bSha);
                    await aAgain.SendResultAsync(new CommandResult(aCid, RemoteResultStates.Done));
                    await bClient.SendResultAsync(new CommandResult(bCid, RemoteResultStates.Done));
                }
                (await server.StopAsync()).Should().BeTrue("最後正常結束（強制結束只有一次）");
            }
            finally
            {
                server.Dispose();
            }

            await using var check = await HubTestHost.StartAsync(dataDir: dir);
            foreach (var cid in new[] { aCid, bCid })
            {
                var row = Row(check, cid);
                (row.State, row.UnsignedBody).Should().Be((CommandStates.Done, (string?)null), "0 遺失：兩台都執行過、回報了");
            }
        }
        finally
        {
            a?.Dispose();
            b?.Dispose();
            TempDir.TryDelete(dir);
        }
    }

    // ───── 工具 ─────

    /// <summary>sync.db <c>commands</c> 的一列（測試看的欄位）</summary>
    private sealed record CommandRowView(string Cid, string Device, string State, bool Durable, string? UnsignedBody, string Sha, long CreatedAt, long ExpiresAt,
        long? DeliveredAt, long? DoneAt, string? ResultJson, string Issuer);

    private const string RowColumns =
        "command_id, target_device_id, state, durable, unsigned_body, content_sha256, created_at, expires_at, delivered_at, done_at, result_json, issuer";

    /// <summary>這個瀏覽器 commandId 的列（null＝全部），依裝置 id</summary>
    private static List<CommandRowView> Rows(HubTestHost host, string? browserCommandId) =>
        ConversationEventLogTests.Rows(host, $"SELECT {RowColumns} FROM commands"
                + (browserCommandId is null ? "" : $" WHERE browser_command_id = '{browserCommandId}'") + " ORDER BY target_device_id")
            .Select(View).ToList();

    private static CommandRowView Row(HubTestHost host, string cid) =>
        View(ConversationEventLogTests.Rows(host, $"SELECT {RowColumns} FROM commands WHERE command_id = '{cid}'").Single());

    private static CommandRowView View(object?[] r) => new((string)r[0]!, (string)r[1]!, (string)r[2]!, (long)r[3]! != 0, (string?)r[4], (string)r[5]!,
        (long)r[6]!, (long)r[7]!, (long?)r[8], (long?)r[9], (string?)r[10], (string)r[11]!);

    /// <summary>以裝置的方式驗信封（主機目前的身分公鑰與 aud；時間界線是手動時鐘的現在）</summary>
    private static CommandEnvelopeVerdict Verify(HubTestHost host, CommandDelivery delivery, string deviceId, string conn)
    {
        var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<MyriagentHub.Hosting.HubOptions>>().Value;
        var target = new CommandEnvelopeTarget(host.Services.GetRequiredService<HubIdentity>().Spki,
            MyriagentHub.Hosting.HostingRules.NormalizeOrigin(MyriagentHub.Hosting.HostingRules.PublicOrigin(options))!, deviceId, conn);
        return CommandEnvelope.Verify(delivery.Envelope, delivery.CommandId, target, new ServerTimeBounds(host.Clock.NowMs, host.Clock.NowMs));
    }

    /// <summary>子程序送的信封（真實時間；子程序的 aud 是 <c>Hub:PublicOrigin</c>＝https://localhost，和產生列的測試主機相同）</summary>
    private static void VerifyReal(CommandDelivery delivery, string spki, string deviceId, string conn, string sha)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var verdict = CommandEnvelope.Verify(delivery.Envelope, delivery.CommandId, new CommandEnvelopeTarget(spki, HubTestHost.Origin, deviceId, conn),
            new ServerTimeBounds(now, now));
        verdict.Ok.Should().BeTrue(verdict.Detail);
        (verdict.Body!.Durable, verdict.ContentSha256).Should().Be((true, sha));
    }

    /// <summary>以 TestServer 連線並交握（<paramref name="cmd1"/>：宣告 <c>[conv1, cmd1]</c>，否則只宣告 conv1）</summary>
    private static async Task<DeviceHubClient> ConnectAsync(HubTestHost host, TestDevice device, bool cmd1 = true) =>
        (await ConnectWithHelloAsync(host, device, cmd1)).Client;

    private static async Task<(DeviceHubClient Client, HelloResponse Hello)> ConnectWithHelloAsync(HubTestHost host, TestDevice device, bool cmd1 = true)
    {
        var client = await DeviceHubClient.ConnectAsync(host, device.Token);
        var (hello, _) = await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(host), new OutboxPosition(NextSeq(host, device.Id), null, null),
            features: cmd1 ? [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1] : [DeviceFeatureRules.Conv1]);
        return (client, hello);
    }

    private static async Task<(DeviceHubClient Client, HelloResponse Hello)> ConnectKestrelAsync(HubProcess server, TestDevice device, long next)
    {
        var (client, seen) = await DeviceHubClient.ConnectKestrelAsync(server.BaseAddress, device.Token);
        var (hello, _) = await client.HandshakeAsync(device, HandshakeRules.SpkiSha256(seen), new OutboxPosition(next, null, null),
            features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1]);
        return (client, hello);
    }

    /// <summary>收 <paramref name="milliseconds"/> 毫秒內瀏覽器收到的全部訊息（依序）</summary>
    private static async Task<List<JObject>> CollectAsync(BrowserHubClient hub, int milliseconds)
    {
        var list = new List<JObject>();
        using var cts = new CancellationTokenSource(milliseconds);
        try
        {
            while (true)
                list.Add(await hub.Messages.Reader.ReadAsync(cts.Token));
        }
        catch (OperationCanceledException)
        {
        }
        return list;
    }

    /// <summary>等稽核寫完 <paramref name="count"/> 筆 <c>command.issued</c></summary>
    private static async Task<List<AuditRecord>> IssuedAuditsAsync(HubTestHost host, int count)
    {
        for (var i = 0; i < 100; i++)
        {
            var found = (await ConversationEventLogTests.AuditRowsAsync(host)).Where(a => a.Action == AuditActions.CommandIssued).ToList();
            if (found.Count >= count)
                return found;
            await Task.Delay(20);
        }
        throw new TimeoutException("command.issued 稽核不夠");
    }

    private static CommandRow TestRow(string cid, string device, string state, bool durable, long created) =>
        new(cid, "bc-" + cid, "{\"kind\":\"browser\",\"label\":\"瀏覽器\"}", null, device, null, "accountLogout", "{}", 1, false, new string('0', 64), durable,
            durable ? "{}" : null, state, created, null, CommandStateRules.ExpiresAt(durable, created));

    private static void InsertFinished(SyncWriteContext context, CommandStore store, string cid, string device, long doneAt)
    {
        store.Insert(context, TestRow(cid, device, CommandStates.Delivered, durable: false, created: doneAt - 1000));
        context.Execute("UPDATE commands SET state = $state, done_at = $done, result_json = '{}' WHERE command_id = $id",
            ("$state", CommandStates.Done), ("$done", doneAt), ("$id", cid));
    }

    /// <summary>
    /// 經裝置 hub 送出的正式實作外面再包一層：記下送出的 <c>CommandDelivery</c>，<see cref="Hold"/> 之後的送出等到 <see cref="Release"/>（測試「不同步送」用）
    /// </summary>
    private sealed class GatedDeviceSender : IDeviceMessageSender
    {
        private readonly ConcurrentQueue<(string ConnectionId, CommandDelivery Delivery)> deliveries = new();
        private TaskCompletionSource? hold;
        private int held;

        public IHubContext<DeviceHub>? Hub { get; set; }

        /// <summary>正在等放行的 CommandDelivery 數</summary>
        public int Held => Volatile.Read(ref held);

        public void Hold() => hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => hold?.TrySetResult();

        public List<CommandDelivery> Deliveries(string connectionId) => deliveries.Where(d => d.ConnectionId == connectionId).Select(d => d.Delivery).ToList();

        public async Task SendAsync(string connectionId, string method, object message, CancellationToken cancellationToken)
        {
            if (message is CommandDelivery delivery)
            {
                deliveries.Enqueue((connectionId, delivery));
                if (hold is { } waiting)
                {
                    Interlocked.Increment(ref held);
                    await waiting.Task;
                    Interlocked.Decrement(ref held);
                }
            }
            await Hub!.Clients.Client(connectionId).SendAsync(method, message, cancellationToken);
        }
    }
}
