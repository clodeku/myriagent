using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Data;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.CommandRouterTests;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 伺服器草稿上的指令與第一次 send 的 <c>conversationCreate</c>（WP4.5 計畫步 9；D33、D34、K8）：以 <see cref="CommandRouterTests.Setup"/> 的電腦 A
/// （<c>[conv1, cmd1]</c>）、連結到 A 的邏輯帳戶與 Chrome／Windows 的瀏覽器開草稿（<c>newConversation</c>），A 以 <see cref="DeviceHubClient"/> 扮演 WP4.4 桌面
/// </summary>
public sealed class DraftCreateTests
{
    // ───── 第一次 send ─────

    [Fact]
    public async Task 草稿第一次send_送conversationCreate_cmd沒有conversationId與deviceId_accountId是那台的本機id()
    {
        await using var s = await Setup.StartAsync();
        var (la, draftId) = await DraftAsync(s);
        (await s.Browser.Hub.CmdAsync(new { cmd = "setOption", conversationId = draftId, key = "model", value = "claude-opus-4" }, commandId: NextCommandId()))["accepted"]!
            .Value<bool>().Should().BeTrue();
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "第一則", paneId = "p1" }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();

        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        var verdict = s.Verify(delivery);
        verdict.Step.Should().BeNull(verdict.Detail);
        var body = verdict.Body!;
        body.Cmd.ToString(Formatting.None).Should().Be("""{"cmd":"conversationCreate","accountId":"acct-a","permissionMode":"Ask","model":"claude-opus-4","text":"第一則"}""");
        (body.Conv, body.Epoch, body.Dev, body.Durable).Should().Be((null, null, s.Owner.Id, false), "Device 路由：信封不帶 conv，目標只在 dev");
        var expected = RemoteTierRules.Evaluate(RemoteCommandCatalog.ConversationCreate, body.Cmd, RemoteTierFacts.Unknown, RemoteSide.Server);
        (body.Tier, body.FullAccess).Should().Be((expected.Tier, expected.FullAccess));

        var row = ConversationEventLogTests.Rows(s.Host, "SELECT cmd, browser_command_id, conversation_id, target_device_id, cmd_json FROM commands").Single();
        (row[0], row[1], row[2], row[3]).Should().Be((RemoteCommandCatalog.ConversationCreate, bcid, null, s.Owner.Id));
        ((string)row[4]!).Should().NotContain("第一則", "cmd_json 只留長度");
        s.Tracker.TryGet(delivery.CommandId, out var tracked).Should().BeTrue();
        (tracked!.ExpectedConversationId, tracked.BrowserConversationId, tracked.RestoreText, tracked.BrowserAccountId, tracked.DraftSessionId)
            .Should().Be((RemoteConversationIdRules.FromCommandId(delivery.CommandId), draftId, "第一則", la, s.Browser.Hub.ServerConnection!.SessionId));
        var audit = await WaitForIssuedAsync(s.Host);
        (audit.Outcome, audit.Target).Should().Be((AuditOutcomes.Ok, delivery.CommandId));
        audit.Detail.Should().NotContain("第一則");
        await s.Browser.Hub.AssertNoMessageAsync("sendRejected", 200);
    }

    [Fact]
    public async Task 新對話的id等於FromCommandId_草稿在送出前就記下()
    {
        await using var s = await Setup.StartAsync();
        var (_, draftId) = await DraftAsync(s);
        var session = s.Browser.Hub.ServerConnection!.SessionId;
        var drafts = s.Host.Services.GetRequiredService<IBrowserDrafts>();

        // 寫入者擋住時（列還沒提交）草稿已經記下：conv.created 再早到也對得上
        var writer = s.Host.Services.GetRequiredService<SyncWriter>();
        using var gate = new ManualResetEventSlim(false);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = writer.WriteAsync(_ =>
        {
            running.TrySetResult();
            gate.Wait(TimeSpan.FromSeconds(10));
        });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cmd = s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "hi" }, commandId: NextCommandId());
        await WaitUntilAsync(() => drafts.TryGet(session, draftId, out var d) && d.SentCommandId is not null, because: "寫列之前就在草稿記下 cid");
        cmd.IsCompleted.Should().BeFalse("列還沒提交，Cmd 不回");
        await AssertNoDeliveryAsync(s.Device!, 0);
        gate.Set();
        await blocker;
        (await cmd)["accepted"]!.Value<bool>().Should().BeTrue();

        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        drafts.TryGet(session, draftId, out var draft).Should().BeTrue();
        draft!.SentCommandId.Should().Be(delivery.CommandId);
        draft.PendingConversationId.Should().Be(RemoteConversationIdRules.FromCommandId(delivery.CommandId));
        ConversationIds.IsWireId(draft.PendingConversationId).Should().BeTrue();
    }

    [Fact]
    public async Task conv_created比done先到_草稿仍對得上()
    {
        await using var s = await Setup.StartAsync();
        var (_, draftId) = await DraftAsync(s);
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "hi" }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        var created = RemoteConversationIdRules.FromCommandId(delivery.CommandId)!;
        var drafts = s.Host.Services.GetRequiredService<IBrowserDrafts>();
        var session = s.Browser.Hub.ServerConnection!.SessionId;
        // conv.created 一到，閘道就把草稿換成正式對話（WP4.6 步 7）：草稿在那之前就記著推得的 id
        drafts.TryGet(session, draftId, out var draft).Should().BeTrue();
        draft!.PendingConversationId.Should().Be(created);

        // 電腦以推得的 id 建立對話：conv.created 經事件流先到，指令的 done 後到
        (await ConversationEventLogTests.Append(s.Host, s.Owner.Id,
            ConversationEventLogTests.Ev(NextSeq(s.Host, s.Owner.Id), K.Created, ConversationSamples.Meta("hi", ConversationSyncPolicies.Full), created))).Ack.Results.Single()
            .Rejected.Should().BeNull();
        var directory = s.Host.Services.GetRequiredService<IConversationDirectory>();
        await WaitUntilAsync(() => directory.Find(created) is not null, because: "對話目錄讀到新對話");
        directory.Find(created)!.OwnerDeviceId.Should().Be(s.Owner.Id);
        // WP4.6 步 7（D41）：對發起的工作階段送 conversationClosed 帶 replacedBy，草稿換掉之後就不在了（再送 send 同不存在的草稿）
        var replaced = await s.Browser.Hub.WaitForAsync("conversationClosed");
        (replaced["conversationId"]!.Value<string>(), replaced["replacedBy"]!.Value<string>()).Should().Be((draftId, created));
        drafts.TryGet(session, draftId, out _).Should().BeFalse();

        await s.Device.SendResultAsync(new CommandResult(delivery.CommandId, RemoteResultStates.Done));
        (await s.Browser.Hub.WaitForAsync("commandResult"))["state"]!.Value<string>().Should().Be(RemoteResultStates.Done);
        await s.Browser.Hub.AssertNoMessageAsync("sendRejected", 200);

        // WP4.6 合併前審查 LIVE-1：換掉之後還對草稿送指令（換的那一則沒收到、分頁還開著）：指令照舊 invalid，
        // conversationClosed 帶 replacedBy（網頁在同一個位置換成新對話，不是關掉分頁）
        (await s.Browser.Hub.CmdAsync(new { cmd = "setOption", conversationId = draftId, key = "model", value = "m" }, commandId: NextCommandId()))["error"]!.Value<string>()
            .Should().Be(BrowserErrorCodes.Invalid);
        var again = await s.Browser.Hub.WaitForAsync("conversationClosed");
        (again["conversationId"]!.Value<string>(), again["replacedBy"]?.Value<string>()).Should().Be((draftId, created));
    }

    [Fact]
    public async Task 被拒_sendRejected的conversationId是草稿id_SentCommandId清掉()
    {
        await using var s = await Setup.StartAsync();
        var (_, draftId) = await DraftAsync(s);
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "還原到草稿" }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        await s.Device.SendResultAsync(new CommandResult(delivery.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Conflict, "那台電腦已經有這個 id 的對話"));

        var result = await s.Browser.Hub.WaitForAsync("commandResult");
        (result["commandId"]!.Value<string>(), result["state"]!.Value<string>(), result["code"]!.Value<string>())
            .Should().Be((bcid, RemoteResultStates.Rejected, RemoteResultCodes.Conflict));
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("那台電腦已經有這個 id 的對話");
        var restored = await s.Browser.Hub.WaitForAsync("sendRejected");
        (restored["conversationId"]!.Value<string>(), restored["text"]!.Value<string>()).Should().Be((draftId, "還原到草稿"));
        s.Host.Services.GetRequiredService<IBrowserDrafts>().TryGet(s.Browser.Hub.ServerConnection!.SessionId, draftId, out var draft).Should().BeTrue();
        (draft!.SentCommandId, draft.PendingConversationId).Should().Be((null, null), "rejected 一定什麼都沒建：使用者可以再送");

        // 再送一次：新的 cid、新的對話 id（突變：rejected 不清掉會變成 busy）
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "再送" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var again = await s.Device.WaitForAsync<CommandDelivery>();
        again.CommandId.Should().NotBe(delivery.CommandId);
        s.Verify(again).Body!.Cmd["text"]!.Value<string>().Should().Be("再送");
    }

    [Fact]
    public async Task 電腦輸出的sendRejected_推得的id換成草稿id_伺服器不再另送()
    {
        // 契約 C2：WP4.4 不輸出；萬一輸出，conversationId 是推得的新對話 id，伺服器核對後換成草稿 id
        await using var s = await Setup.StartAsync();
        var (_, draftId) = await DraftAsync(s);
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "原文" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        var created = RemoteConversationIdRules.FromCommandId(delivery.CommandId)!;
        await s.Device.SendOutputAsync(new CommandOutput(delivery.CommandId, new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = "別的對話" }));
        await s.Device.SendOutputAsync(new CommandOutput(delivery.CommandId, new JObject { ["type"] = "sendRejected", ["conversationId"] = created, ["text"] = "電腦還的" }));
        await s.Device.SendResultAsync(new CommandResult(delivery.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Invalid));

        var messages = (await CollectUntilAsync(s.Browser.Hub, "commandResult")).Concat(await CollectAsync(s.Browser.Hub, 400))
            .Where(m => (string?)m["type"] == "sendRejected").ToList();
        messages.Should().ContainSingle("電腦轉來過就不再另送");
        (messages[0]["conversationId"]!.Value<string>(), messages[0]["text"]!.Value<string>()).Should().Be((draftId, "電腦還的"));
        s.Counters.Snapshot().DroppedOutputs.Should().Be(1, "conversationId 不是推得的 id 的那則丟掉");
    }

    [Fact]
    public async Task 建立中又送一次_立即busy_還原文字()
    {
        await using var s = await Setup.StartAsync();
        var (_, draftId) = await DraftAsync(s);
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "第一次" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        await s.Device!.WaitForAsync<CommandDelivery>();

        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "第二次" }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        var result = await s.Browser.Hub.WaitForAsync("commandResult");
        result.ToString(Formatting.None).Should().Be(CommandMessageRules.CommandResultJson(bcid, RemoteResultStates.Rejected, RemoteResultCodes.Busy,
            DraftCommandRules.CreatingMessage, null));
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be(DraftCommandRules.CreatingMessage);
        var restored = await s.Browser.Hub.WaitForAsync("sendRejected");
        (restored["conversationId"]!.Value<string>(), restored["text"]!.Value<string>()).Should().Be((draftId, "第二次"));
        await AssertNoDeliveryAsync(s.Device, 200);
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(1, "立即拒絕不寫列");
        s.Counters.Snapshot().RejectedAtServer.Should().Be(1);
    }

    [Fact]
    public async Task 建立中電腦斷線_再送仍是busy_不是offline()
    {
        // 「新對話還在建立中」先於選電腦：那台斷線了也不讓使用者以為可以換一台再建一個
        await using var s = await Setup.StartAsync();
        var (_, draftId) = await DraftAsync(s);
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "第一次" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        await s.Device!.WaitForAsync<CommandDelivery>();
        await s.Device.Connection.StopAsync();
        var registry = s.Host.Services.GetRequiredService<ConnectionRegistry>();
        await WaitUntilAsync(() => registry.ConnectionsOf(s.Owner.Id).Count == 0, because: "A 斷線");

        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "第二次" }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        var result = await s.Browser.Hub.WaitForAsync("commandResult");
        (result["code"]!.Value<string>(), result["message"]!.Value<string>()).Should().Be((RemoteResultCodes.Busy, DraftCommandRules.CreatingMessage));
        (await s.Browser.Hub.WaitForAsync("sendRejected"))["text"]!.Value<string>().Should().Be("第二次");
    }

    [Fact]
    public async Task 同一份草稿的兩個分頁同時送_只建一個()
    {
        await using var s = await Setup.StartAsync();
        var (_, draftId) = await DraftAsync(s);
        // 同一個登入的另一條連線（同一個工作階段）
        await using var second = await BrowserHubClient.ConnectAsync(s.Host, s.Browser.Http);
        await second.ReadyAsync();
        var results = await Task.WhenAll(
            s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "一" }, commandId: NextCommandId()),
            second.CmdAsync(new { cmd = "send", conversationId = draftId, text = "二" }, commandId: NextCommandId()));
        results.Should().OnlyContain(r => r["accepted"]!.Value<bool>());

        await s.Device!.WaitForAsync<CommandDelivery>();
        await AssertNoDeliveryAsync(s.Device, 300);
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(1, "草稿記下 cid 在鎖裡確認：另一個是 busy");
        s.Counters.Snapshot().RejectedAtServer.Should().Be(1);
    }

    [Fact]
    public async Task 別的工作階段的草稿_invalid()
    {
        await using var s = await Setup.StartAsync();
        var (_, draftId) = await DraftAsync(s);
        await using var other = await LoginAndConnectAsync(s.Host);
        await other.Hub.ReadyAsync();

        foreach (var cmd in new object[]
        {
            new { cmd = "send", conversationId = draftId, text = "hi" },
            new { cmd = "setOption", conversationId = draftId, key = "model", value = "m" },
        })
        {
            (await other.Hub.CmdAsync(cmd, commandId: NextCommandId()))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Invalid);
            (await other.Hub.WaitForAsync("conversationClosed"))["conversationId"]!.Value<string>().Should().Be(draftId);
        }
        await other.Hub.AssertNoMessageAsync("sendRejected", 200);
        await AssertNoDeliveryAsync(s.Device!, 0);
        s.Host.Services.GetRequiredService<IBrowserDrafts>().TryGet(s.Browser.Hub.ServerConnection!.SessionId, draftId, out var draft).Should().BeTrue();
        draft!.Model.Should().BeNull("別的工作階段改不到");

        // 自己的草稿關掉之後也一樣
        (await s.Browser.Hub.CmdAsync(new { cmd = "close", conversationId = draftId }))["accepted"]!.Value<bool>().Should().BeTrue();
        await s.Browser.Hub.WaitForAsync("conversationClosed");
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "hi" }, commandId: NextCommandId()))["error"]!.Value<string>()
            .Should().Be(BrowserErrorCodes.Invalid);
        (await s.Browser.Hub.WaitForAsync("conversationClosed"))["conversationId"]!.Value<string>().Should().Be(draftId);
        await s.Browser.Hub.AssertNoMessageAsync("sendRejected", 200);
    }

    [Fact]
    public async Task newConversation帶deviceId_送到那台()
    {
        await using var s = await Setup.StartAsync();
        using var other = await TestDevice.AddAsync(s.Host, "B 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        (await LinkAsync(s.Host, other.Id, "acct-b")).Should().Be(la);
        // B 比 A 晚連上：預設（Order 最大）是 B
        await using var b = await DeviceHubClient.ConnectAsync(s.Host, other.Token);
        var (bHello, _) = await b.HandshakeAsync(other, DeviceHubClient.ObservedSpki(s.Host), new OutboxPosition(NextSeq(s.Host, other.Id), null, null),
            features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1]);

        // 指定 A：送到 A，帳戶是 A 的本機 id
        var toA = await DraftAsync(s, la, deviceId: s.Owner.Id);
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = toA, text = "給 A" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        s.Verify(delivery).Body!.Cmd["accountId"]!.Value<string>().Should().Be("acct-a");
        await AssertNoDeliveryAsync(b, 200);

        // 沒指定：預設是 Order 最大的 B（帳戶是 B 的本機 id）
        var byDefault = await DraftAsync(s, la);
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = byDefault, text = "預設" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var toB = await b.WaitForAsync<CommandDelivery>();
        var verdict = CommandEnvelope.Verify(toB.Envelope, toB.CommandId, new CommandEnvelopeTarget(
                s.Host.Services.GetRequiredService<MyriagentHub.Trust.HubIdentity>().Spki, "https://localhost", other.Id, bHello.NonceS),
            new ServerTimeBounds(s.Host.Clock.NowMs, s.Host.Clock.NowMs));
        verdict.Step.Should().BeNull(verdict.Detail);
        verdict.Body!.Cmd["accountId"]!.Value<string>().Should().Be("acct-b");
        await AssertNoDeliveryAsync(s.Device, 200);

        // 指定的電腦不存在（已撤銷、不是這台的）：Cmd invalid（同 D5（a））
        var devices = s.Host.Services.GetRequiredService<DeviceDirectory>();
        var revokedTarget = await DraftAsync(s, la, deviceId: other.Id);
        devices.Put(devices.Get(other.Id)! with { RevokedAt = s.Host.Clock.NowMs });
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = revokedTarget, text = "x" }, commandId: NextCommandId()))["error"]!.Value<string>()
            .Should().Be(BrowserErrorCodes.Invalid);
        (await s.Browser.Hub.WaitForAsync("sendRejected"))["conversationId"]!.Value<string>().Should().Be(revokedTarget);
    }

    [Fact]
    public async Task 預設的電腦不在線_立即offline_還原文字_草稿沒記下()
    {
        await using var s = await Setup.StartAsync(connect: false);
        var (_, draftId) = await DraftAsync(s);
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "離線" }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        (await s.Browser.Hub.WaitForAsync("commandResult"))["code"]!.Value<string>().Should().Be(RemoteResultCodes.Offline);
        (await s.Browser.Hub.WaitForAsync("sendRejected"))["conversationId"]!.Value<string>().Should().Be(draftId);
        s.Host.Services.GetRequiredService<IBrowserDrafts>().TryGet(s.Browser.Hub.ServerConnection!.SessionId, draftId, out var draft).Should().BeTrue();
        draft!.SentCommandId.Should().BeNull();
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(0);
        var audit = await WaitForIssuedAsync(s.Host);
        (audit.Outcome, JObject.Parse(audit.Detail!)["cmd"]!.Value<string>()).Should().Be((AuditOutcomes.Denied, RemoteCommandCatalog.ConversationCreate));
        audit.Detail.Should().NotContain("離線");
    }

    [Fact]
    public async Task 寫入者已停止_Cmd回unavailable_還原文字_草稿記下的清掉()
    {
        await using var s = await Setup.StartAsync();
        var (_, draftId) = await DraftAsync(s);
        s.Host.Services.GetRequiredService<SyncWriter>().Stop();
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "還原我" }, commandId: NextCommandId()))["error"]!.Value<string>()
            .Should().Be(BrowserErrorCodes.Unavailable);
        var restored = await s.Browser.Hub.WaitForAsync("sendRejected");
        (restored["conversationId"]!.Value<string>(), restored["text"]!.Value<string>()).Should().Be((draftId, "還原我"));
        s.Host.Services.GetRequiredService<IBrowserDrafts>().TryGet(s.Browser.Hub.ServerConnection!.SessionId, draftId, out var draft).Should().BeTrue();
        (draft!.SentCommandId, draft.PendingConversationId).Should().Be((null, null), "沒寫進去：使用者可以再送");
        await AssertNoDeliveryAsync(s.Device!, 200);
    }

    [Fact]
    public async Task 草稿的帳戶已刪除_立即invalid()
    {
        await using var s = await Setup.StartAsync();
        var (la, draftId) = await DraftAsync(s);
        await s.Host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute("UPDATE logical_accounts SET deleted_at = 1 WHERE logical_account_id = $id", ("$id", la)));
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "hi" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var result = await s.Browser.Hub.WaitForAsync("commandResult");
        (result["code"]!.Value<string>(), result["message"]!.Value<string>()).Should().Be((RemoteResultCodes.Invalid, DraftCommandRules.AccountGoneMessage));
        (await s.Browser.Hub.WaitForAsync("sendRejected"))["text"]!.Value<string>().Should().Be("hi");
        await AssertNoDeliveryAsync(s.Device!, 200);
    }

    // ───── 草稿上的修改 ─────

    [Fact]
    public async Task 草稿上的setOption_推header給這個工作階段的每條連線_發起的連線收到commandResult_done()
    {
        await using var s = await Setup.StartAsync();
        var (_, draftId) = await DraftAsync(s);
        await using var second = await BrowserHubClient.ConnectAsync(s.Host, s.Browser.Http);
        await second.ReadyAsync();
        await using var other = await LoginAndConnectAsync(s.Host);
        await other.Hub.ReadyAsync();

        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "setOption", conversationId = draftId, key = "permission", value = "Plan" }, commandId: bcid))["accepted"]!
            .Value<bool>().Should().BeTrue();
        foreach (var hub in new[] { s.Browser.Hub, second })
        {
            var header = await hub.WaitForAsync("header");
            header["conversationId"]!.Value<string>().Should().Be(draftId);
            (header["header"]!["permission"]!.Value<string>(), header["header"]!["isDraft"]!.Value<bool>()).Should().Be(("Plan", true));
        }
        (await s.Browser.Hub.WaitForAsync("commandResult")).ToString(Formatting.None).Should().Be(
            CommandMessageRules.CommandResultJson(bcid, RemoteResultStates.Done, null, null, null));
        await second.AssertNoMessageAsync("commandResult", 100);
        await other.Hub.AssertNoMessageAsync("header", 100);

        (await s.Browser.Hub.CmdAsync(new { cmd = "rename", conversationId = draftId, title = "  我的新對話  " }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        (await s.Browser.Hub.WaitForAsync("header"))["header"]!["title"]!.Value<string>().Should().Be("我的新對話");
        // 草稿上的修改只改伺服器記憶體：不寫列、不簽、不稽核
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(0);
        await AssertNoDeliveryAsync(s.Device!, 100);
        (await ConversationEventLogTests.AuditRowsAsync(s.Host)).Should().NotContain(a => a.Action == AuditActions.CommandIssued);

        // 改過的值帶進第一次 send
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = draftId, text = "" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        s.Verify(await s.Device!.WaitForAsync<CommandDelivery>()).Body!.Cmd.ToString(Formatting.None).Should().Be(
            """{"cmd":"conversationCreate","accountId":"acct-a","permissionMode":"Plan","title":"我的新對話"}""", "空字串的 text 省略、title 照草稿");
    }

    [Fact]
    public async Task 草稿上的不合格修改與其他指令_Cmd的invalid_草稿不變()
    {
        await using var s = await Setup.StartAsync();
        var (_, draftId) = await DraftAsync(s);
        foreach (var cmd in new object[]
        {
            new { cmd = "setOption", conversationId = draftId, key = "permission", value = "NotAMode" },
            new { cmd = "setAccount", conversationId = draftId, accountId = SyncIds.NewLogicalAccountId() },
            new { cmd = "setFolder", conversationId = draftId, path = "relative" },
            new { cmd = "stop", conversationId = draftId },
            new { cmd = "delete", conversationId = draftId },
        })
        {
            (await s.Browser.Hub.CmdAsync(cmd, commandId: NextCommandId()))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Invalid, JObject.FromObject(cmd).ToString());
        }
        await s.Browser.Hub.AssertNoMessageAsync("header", 200);
        await AssertNoDeliveryAsync(s.Device!, 0);
        s.Host.Services.GetRequiredService<IBrowserDrafts>().TryGet(s.Browser.Hub.ServerConnection!.SessionId, draftId, out var draft).Should().BeTrue();
        (draft!.PermissionMode, draft.WorkingDirectory).Should().Be((null, null));
    }

    // ───── 瀏覽資料夾（listFiles dirsOnly → listFolders，2026-10-27） ─────

    [Fact]
    public async Task 草稿上的瀏覽資料夾_轉成listFolders送到草稿的電腦_files換成草稿id_草稿不變()
    {
        await using var s = await Setup.StartAsync();
        var (la, _) = await DraftAsync(s);
        var draftId = await DraftAsync(s, la, deviceId: s.Owner.Id);
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "listFiles", conversationId = draftId, requestId = "f1", path = (string?)null, dirsOnly = true }, commandId: bcid))["accepted"]!
            .Value<bool>().Should().BeTrue();

        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        var verdict = s.Verify(delivery);
        verdict.Step.Should().BeNull(verdict.Detail);
        var body = verdict.Body!;
        body.Cmd.ToString(Formatting.None).Should().Be("{\"cmd\":\"listFolders\",\"requestId\":\"f1\",\"path\":null,\"deviceId\":\"" + s.Owner.Id + "\"}");
        (body.Conv, body.Epoch, body.Dev, body.Tier).Should().Be((null, null, s.Owner.Id, 2), "Device 路由、同 dirsOnly 是層級 2");

        // 那台還沒有這個對話：conversationId 是 null（帶了別的 id 的丟掉），伺服器換成草稿 id、沒有媒體網址
        await s.Device.SendOutputAsync(new CommandOutput(delivery.CommandId, new JObject
        {
            ["type"] = "files", ["requestId"] = "f1", ["conversationId"] = Conv, ["path"] = null, ["parent"] = null, ["root"] = null,
            ["entries"] = new JArray(), ["truncated"] = false, ["error"] = null,
        }));
        await s.Device.SendOutputAsync(new CommandOutput(delivery.CommandId, new JObject
        {
            ["type"] = "files", ["requestId"] = "f1", ["conversationId"] = null, ["path"] = null, ["parent"] = null, ["root"] = null,
            ["entries"] = new JArray(new JObject { ["name"] = @"C:\repo", ["path"] = @"C:\repo", ["kind"] = "dir", ["size"] = null, ["modified"] = null, ["hidden"] = false }),
            ["truncated"] = false, ["error"] = null,
        }));
        var files = await s.Browser.Hub.WaitForAsync("files");
        (files["conversationId"]!.Value<string>(), files["requestId"]!.Value<string>()).Should().Be((draftId, "f1"));
        var entry = (JObject)files["entries"]!.Single();
        (entry["path"]!.Value<string>(), entry["url"]!.Type).Should().Be((@"C:\repo", JTokenType.Null));
        await s.Device.SendResultAsync(new CommandResult(delivery.CommandId, RemoteResultStates.Done, null));
        var result = await s.Browser.Hub.WaitForAsync("commandResult");
        (result["commandId"]!.Value<string>(), result["state"]!.Value<string>()).Should().Be((bcid, RemoteResultStates.Done));
        s.Counters.Snapshot().DroppedOutputs.Should().Be(1, "conversationId 不是 null 的那則丟掉");

        // 草稿不動（不是第一次 send，不記 cid）；那台拒絕時（舊版不認得 listFolders）只回結果、不跳 toast（抽屜自己說明）
        s.Host.Services.GetRequiredService<IBrowserDrafts>().TryGet(s.Browser.Hub.ServerConnection!.SessionId, draftId, out var draft).Should().BeTrue();
        (draft!.SentCommandId, draft.WorkingDirectory).Should().Be((null, null));
        (await s.Browser.Hub.CmdAsync(new { cmd = "listFiles", conversationId = draftId, requestId = "f2", path = @"C:\repo", dirsOnly = true }, commandId: NextCommandId()))["accepted"]!
            .Value<bool>().Should().BeTrue();
        var second = await s.Device.WaitForAsync<CommandDelivery>();
        s.Verify(second).Body!.Cmd["path"]!.Value<string>().Should().Be(@"C:\repo");
        await s.Device.SendResultAsync(new CommandResult(second.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Invalid, "這不是可以從遠端執行的指令"));
        (await s.Browser.Hub.WaitForAsync("commandResult"))["code"]!.Value<string>().Should().Be(RemoteResultCodes.Invalid);
        await s.Browser.Hub.AssertNoMessageAsync("toast", 300);
    }

    [Fact]
    public async Task 草稿上沒帶dirsOnly的listFiles_照舊invalid_不轉送()
    {
        await using var s = await Setup.StartAsync();
        var (_, draftId) = await DraftAsync(s);
        (await s.Browser.Hub.CmdAsync(new { cmd = "listFiles", conversationId = draftId, requestId = "f1", path = (string?)null }, commandId: NextCommandId()))["error"]!
            .Value<string>().Should().Be(BrowserErrorCodes.Invalid);
        await AssertNoDeliveryAsync(s.Device!, 200);
    }

    // ───── 工具 ─────

    /// <summary>A 連結身分成本機帳戶 acct-a、閘道讀到它之後開一份草稿；回傳邏輯帳戶 id 與草稿 id</summary>
    private static async Task<(string La, string DraftId)> DraftAsync(Setup s)
    {
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        return (la, await DraftAsync(s, la));
    }

    /// <summary>以 <paramref name="la"/> 開一份草稿（<paramref name="deviceId"/> 是 newConversation 選填的電腦）；回傳草稿 id</summary>
    private static async Task<string> DraftAsync(Setup s, string la, string? deviceId = null)
    {
        await WaitUntilAsync(() => s.Host.Services.GetRequiredService<BrowserGateway>().DraftAccounts.Any(a => a.Id == la), because: "閘道讀到帳戶");
        (await s.Browser.Hub.CmdAsync(new { cmd = "newConversation", accountId = la, paneId = "p1", deviceId }))["accepted"]!.Value<bool>().Should().BeTrue();
        var conversation = await s.Browser.Hub.WaitForAsync("conversation", m => BrowserDraftRules.IsDraftId((string?)m["conversationId"]));
        var id = conversation["conversationId"]!.Value<string>()!;
        s.Host.Services.GetRequiredService<IBrowserDrafts>().TryGet(s.Browser.Hub.ServerConnection!.SessionId, id, out var draft).Should().BeTrue();
        draft!.DeviceId.Should().Be(deviceId, "newConversation 選填的 deviceId 記進草稿");
        return id;
    }

    /// <summary>這段時間內收到的所有訊息</summary>
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

    /// <summary>收到 <paramref name="type"/> 為止的所有訊息（含它）</summary>
    private static async Task<List<JObject>> CollectUntilAsync(BrowserHubClient hub, string type, int timeoutMs = 5000)
    {
        var list = new List<JObject>();
        using var cts = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            var message = await hub.Messages.Reader.ReadAsync(cts.Token);
            list.Add(message);
            if ((string?)message["type"] == type)
                return list;
        }
    }
}
