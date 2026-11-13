using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using MyriagentHub.Trust;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 桌面遠端操作（sync-protocol 14.7；擁有者 2026-10-22 決定「已配對就能操作」）：另一台已配對的桌面（B）經裝置 hub 的 <c>PeerCmd</c> 對擁有者（A）的對話下指令。
/// 沿用 <see cref="CommandRouterTests.Setup"/>（A 擁有 <see cref="CommandRouterTests.Conv"/>、瀏覽器登入著），另外接上 B
/// </summary>
public sealed class PeerCommandTests
{
    private static readonly string[] PeerFeatures = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1];

    [Fact]
    public async Task 桌面發起的send轉給擁有者_信封的發起者是那台電腦()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: PeerFeatures);
        await using var b = await PeerAsync(s, "B 電腦", PeerFeatures);
        var cid = NewCommandId();

        var result = await b.Client.PeerCmdAsync(new PeerCmd(cid, new JObject { ["cmd"] = "send", ["conversationId"] = CommandRouterTests.Conv, ["text"] = "hi" }));
        result.Should().Be(new PeerCmdResult(true, cid, null));

        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        var verdict = Verify(s, delivery, acceptsDevice: true);
        verdict.Step.Should().BeNull(verdict.Detail);
        verdict.Body!.Issuer.Should().Be(new CommandIssuer(CommandIssuerKinds.Device, "B 電腦"));
        verdict.Body.Auth.UvAt.Should().BeNull("裝置沒有輸入密碼這回事");
        verdict.Body.Cmd["text"]!.Value<string>().Should().Be("hi");
        verdict.Body.Dev.Should().Be(s.Owner.Id);

        var old = Verify(s, delivery, acceptsDevice: false);
        old.Step.Should().Be(CommandEnvelopeSteps.Issuer, "沒有 peer1 的舊版桌面驗不過（伺服器因此不送給它）");

        var row = ConversationEventLogTests.Rows(s.Host, "SELECT browser_command_id, issuer FROM commands").Single();
        row[0].Should().Be(cid);
        row[1].Should().Be("""{"kind":"device","label":"B 電腦"}""");
        s.Tracker.TryGet(delivery.CommandId, out var tracked).Should().BeTrue();
        tracked!.OriginDeviceId.Should().Be(b.Device.Id);
    }

    [Fact]
    public async Task 結果以PeerReply送回發起的電腦_瀏覽器收不到()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: PeerFeatures);
        await using var b = await PeerAsync(s, "B 電腦", PeerFeatures);
        var cid = NewCommandId();
        (await b.Client.PeerCmdAsync(new PeerCmd(cid, new JObject { ["cmd"] = "stop", ["conversationId"] = CommandRouterTests.Conv }))).Accepted.Should().BeTrue();
        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();

        await s.Device.SendResultAsync(new CommandResult(delivery.CommandId, RemoteResultStates.Done));

        var reply = await b.Client.WaitForAsync<PeerReply>(r => (string?)r.Message["type"] == "commandResult");
        reply.CommandId.Should().Be(cid);
        reply.Message["commandId"]!.Value<string>().Should().Be(cid, "commandResult 帶發起時的 commandId");
        reply.Message["state"]!.Value<string>().Should().Be(RemoteResultStates.Done);
        await s.Browser.Hub.AssertNoMessageAsync("commandResult", 300);
    }

    [Fact]
    public async Task 目標沒有peer1_立即拒絕unsupported_send還原文字_不送出()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync();
        await using var b = await PeerAsync(s, "B 電腦", PeerFeatures);
        var cid = NewCommandId();

        (await b.Client.PeerCmdAsync(new PeerCmd(cid, new JObject { ["cmd"] = "send", ["conversationId"] = CommandRouterTests.Conv, ["text"] = "hello" }))).Accepted
            .Should().BeTrue("立即拒絕是 accepted 之後送 commandResult rejected（同瀏覽器）");

        var result = await b.Client.WaitForAsync<PeerReply>(r => (string?)r.Message["type"] == "commandResult");
        result.Message["state"]!.Value<string>().Should().Be(RemoteResultStates.Rejected);
        result.Message["code"]!.Value<string>().Should().Be(RemoteResultCodes.Unsupported);
        result.Message["message"]!.Value<string>().Should().Contain("A 電腦").And.Contain("版本太舊");
        var restored = await b.Client.WaitForAsync<PeerReply>(r => (string?)r.Message["type"] == "sendRejected");
        restored.Message["conversationId"]!.Value<string>().Should().Be(CommandRouterTests.Conv);
        restored.Message["text"]!.Value<string>().Should().Be("hello");
        await CommandRouterTests.AssertNoDeliveryAsync(s.Device!, 200);
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(0);
    }

    [Fact]
    public async Task 目標是發起的電腦自己_立即拒絕invalid()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: PeerFeatures);
        var cid = NewCommandId();

        (await s.Device!.PeerCmdAsync(new PeerCmd(cid, new JObject { ["cmd"] = "stop", ["conversationId"] = CommandRouterTests.Conv }))).Accepted.Should().BeTrue();

        var reply = await s.Device.WaitForAsync<PeerReply>(r => (string?)r.Message["type"] == "commandResult");
        reply.Message["code"]!.Value<string>().Should().Be(RemoteResultCodes.Invalid);
        reply.Message["message"]!.Value<string>().Should().Be(PeerRules.SelfTargetMessage);
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(0);
    }

    [Fact]
    public async Task 沒有協商peer1的連線_回not_available()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: PeerFeatures);
        await using var b = await PeerAsync(s, "B 電腦", [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1]);
        var cid = NewCommandId();

        var result = await b.Client.PeerCmdAsync(new PeerCmd(cid, new JObject { ["cmd"] = "stop", ["conversationId"] = CommandRouterTests.Conv }));

        result.Should().Be(new PeerCmdResult(false, cid, BrowserErrorCodes.NotAvailable));
        await CommandRouterTests.AssertNoDeliveryAsync(s.Device!, 200);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("syncSetPolicy")]
    [InlineData("openPath")]
    [InlineData("noSuchCommand")]
    public async Task 不是電腦執行的指令_不轉送(string name)
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: PeerFeatures);
        await using var b = await PeerAsync(s, "B 電腦", PeerFeatures);
        var cid = NewCommandId();

        var result = await b.Client.PeerCmdAsync(new PeerCmd(cid, new JObject { ["cmd"] = name, ["conversationId"] = CommandRouterTests.Conv }));

        result.Accepted.Should().BeFalse();
        result.Error.Should().BeOneOf(BrowserErrorCodes.NotAvailable, BrowserErrorCodes.Invalid);
        await CommandRouterTests.AssertNoDeliveryAsync(s.Device!, 200);
    }

    [Fact]
    public async Task commandId不是ULID_回invalid()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: PeerFeatures);
        await using var b = await PeerAsync(s, "B 電腦", PeerFeatures);

        var result = await b.Client.PeerCmdAsync(new PeerCmd("not-a-ulid", new JObject { ["cmd"] = "stop", ["conversationId"] = CommandRouterTests.Conv }));

        result.Accepted.Should().BeFalse();
        result.Error.Should().Be(BrowserErrorCodes.Invalid);
    }

    [Fact]
    public async Task 桌面在其他電腦開新對話_帳戶換成那台的本機id_先回peerCreated()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: PeerFeatures);
        await using var b = await PeerAsync(s, "B 電腦", PeerFeatures);
        var logical = await CommandRouterTests.LinkAsync(s.Host, s.Owner.Id, "acc-a");
        var cid = NewCommandId();

        var result = await b.Client.PeerCmdAsync(new PeerCmd(cid, new JObject
        {
            ["cmd"] = "conversationCreate", ["deviceId"] = s.Owner.Id, ["accountId"] = logical, ["permissionMode"] = "Ask", ["text"] = "在那台做事",
        }));
        result.Should().Be(new PeerCmdResult(true, cid, null));

        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        var verdict = Verify(s, delivery, acceptsDevice: true);
        verdict.Step.Should().BeNull(verdict.Detail);
        var cmd = verdict.Body!.Cmd;
        cmd.Value<string>("cmd").Should().Be("conversationCreate");
        cmd.Value<string>("accountId").Should().Be("acc-a", "邏輯帳戶換成那台連結的本機 id");
        cmd.ContainsKey("deviceId").Should().BeFalse("型錄沒有這個欄位");
        verdict.Body.Conv.Should().BeNull();

        var created = await b.Client.WaitForAsync<PeerReply>(r => (string?)r.Message["type"] == "peerCreated");
        created.CommandId.Should().Be(cid);
        created.Message["conversationId"]!.Value<string>().Should().Be(RemoteConversationIdRules.FromCommandId(delivery.CommandId));
    }

    [Fact]
    public async Task 桌面開新對話_目標是自己或帳戶不存在_被拒()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: PeerFeatures);
        await using var b = await PeerAsync(s, "B 電腦", PeerFeatures);
        var logical = await CommandRouterTests.LinkAsync(s.Host, s.Owner.Id, "acc-a");

        var self = NewCommandId();
        (await b.Client.PeerCmdAsync(new PeerCmd(self, new JObject
        {
            ["cmd"] = "conversationCreate", ["deviceId"] = b.Device.Id, ["accountId"] = logical, ["permissionMode"] = "Ask", ["text"] = "x",
        }))).Accepted.Should().BeTrue();
        (await b.Client.WaitForAsync<PeerReply>(r => r.CommandId == self && (string?)r.Message["type"] == "commandResult")).Message["code"]!.Value<string>()
            .Should().Be(RemoteResultCodes.Invalid);

        var noDevice = NewCommandId();
        (await b.Client.PeerCmdAsync(new PeerCmd(noDevice, new JObject { ["cmd"] = "conversationCreate", ["accountId"] = logical, ["permissionMode"] = "Ask" })))
            .Should().Be(new PeerCmdResult(false, noDevice, BrowserErrorCodes.Invalid), "deviceId 必帶");

        var gone = NewCommandId();
        (await b.Client.PeerCmdAsync(new PeerCmd(gone, new JObject
        {
            ["cmd"] = "conversationCreate", ["deviceId"] = s.Owner.Id, ["accountId"] = "la_" + new string('Z', 26), ["permissionMode"] = "Ask",
        }))).Accepted.Should().BeTrue();
        (await b.Client.WaitForAsync<PeerReply>(r => r.CommandId == gone && (string?)r.Message["type"] == "commandResult")).Message["code"]!.Value<string>()
            .Should().Be(RemoteResultCodes.Invalid);
        await CommandRouterTests.AssertNoDeliveryAsync(s.Device!, 200);
    }

    // ───────────────────────────── 遠端執行指令（sync-protocol 14.11） ─────────────────────────────

    private static readonly string[] XferFeatures = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Xfer1];

    [Fact]
    public async Task 檔案傳輸_目標有xfer1才簽_沒有就立即拒絕unsupported()
    {
        await using (var s = await CommandRouterTests.Setup.StartAsync(features: XferFeatures))
        await using (var b = await PeerAsync(s, "B 電腦", XferFeatures))
        {
            var cid = NewCommandId();
            var browse = new JObject { ["cmd"] = "deviceBrowse", ["deviceId"] = s.Owner.Id, ["requestId"] = "b1", ["path"] = JValue.CreateNull() };
            (await b.Client.PeerCmdAsync(new PeerCmd(cid, browse))).Should().Be(new PeerCmdResult(true, cid, null));
            var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
            var verdict = Verify(s, delivery, acceptsDevice: true);
            verdict.Step.Should().BeNull(verdict.Detail);
            verdict.Body!.Tier.Should().Be(2);
            verdict.Body.Conv.Should().BeNull();
            verdict.Body.Cmd.Value<string>("cmd").Should().Be("deviceBrowse");
        }

        await using (var s = await CommandRouterTests.Setup.StartAsync(features: PeerFeatures))
        await using (var b = await PeerAsync(s, "B 電腦", XferFeatures))
        {
            var cid = NewCommandId();
            var fetch = new JObject { ["cmd"] = "deviceFetch", ["deviceId"] = s.Owner.Id, ["requestId"] = "f1", ["path"] = @"C:.txt" };
            (await b.Client.PeerCmdAsync(new PeerCmd(cid, fetch))).Accepted.Should().BeTrue();
            var result = await b.Client.WaitForAsync<PeerReply>(r => (string?)r.Message["type"] == "commandResult");
            result.Message["code"]!.Value<string>().Should().Be(RemoteResultCodes.Unsupported);
            result.Message["message"]!.Value<string>().Should().Contain("版本太舊").And.Contain("傳檔案");
            await CommandRouterTests.AssertNoDeliveryAsync(s.Device!, 200);
        }
    }

    private static readonly string[] AttFeatures = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Att1];

    [Fact]
    public async Task 移除已附加的檔_目標有att1才簽_沒有就立即拒絕unsupported()
    {
        // 2026-11-13：遠端的 removeAttachment 的 id 是網頁的上傳 id；舊版的桌面找不到會回 done 卻沒移除，所以不送給它們
        await using (var s = await CommandRouterTests.Setup.StartAsync(features: AttFeatures))
        await using (var b = await PeerAsync(s, "B 電腦", AttFeatures))
        {
            var cid = NewCommandId();
            var remove = new JObject { ["cmd"] = "removeAttachment", ["conversationId"] = CommandRouterTests.Conv, ["id"] = "01J9QZ3K8M5R7T2V4W6X8Y0A1B" };
            (await b.Client.PeerCmdAsync(new PeerCmd(cid, remove))).Should().Be(new PeerCmdResult(true, cid, null));
            var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
            var verdict = Verify(s, delivery, acceptsDevice: true);
            verdict.Step.Should().BeNull(verdict.Detail);
            verdict.Body!.Cmd.Value<string>("cmd").Should().Be("removeAttachment");
        }

        await using (var s = await CommandRouterTests.Setup.StartAsync(features: PeerFeatures))
        await using (var b = await PeerAsync(s, "B 電腦", AttFeatures))
        {
            var cid = NewCommandId();
            var remove = new JObject { ["cmd"] = "removeAttachment", ["conversationId"] = CommandRouterTests.Conv, ["id"] = "01J9QZ3K8M5R7T2V4W6X8Y0A1B" };
            (await b.Client.PeerCmdAsync(new PeerCmd(cid, remove))).Accepted.Should().BeTrue();
            var result = await b.Client.WaitForAsync<PeerReply>(r => (string?)r.Message["type"] == "commandResult");
            result.Message["code"]!.Value<string>().Should().Be(RemoteResultCodes.Unsupported);
            result.Message["message"]!.Value<string>().Should().Contain("版本太舊").And.Contain("移除已附加的檔案");
            await CommandRouterTests.AssertNoDeliveryAsync(s.Device!, 200);
        }
    }

    private static readonly string[] TimedFeatures = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Timed1];

    [Fact]
    public async Task 定時訊息_目標有timed1才簽_沒有就立即拒絕unsupported()
    {
        // 2026-11-13：其他電腦設定 Myriagent 的定時訊息；舊版的桌面不認得 timedSave、timedAction
        var save = new JObject
        {
            ["cmd"] = "timedSave", ["conversation"] = CommandRouterTests.Conv, ["whenKind"] = "daily", ["whenValue"] = "09:00", ["text"] = "整理報表",
        };
        await using (var s = await CommandRouterTests.Setup.StartAsync(features: TimedFeatures))
        await using (var b = await PeerAsync(s, "B 電腦", TimedFeatures))
        {
            var cid = NewCommandId();
            var cmd = (JObject)save.DeepClone();
            cmd["deviceId"] = s.Owner.Id;
            (await b.Client.PeerCmdAsync(new PeerCmd(cid, cmd))).Should().Be(new PeerCmdResult(true, cid, null));
            var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
            var verdict = Verify(s, delivery, acceptsDevice: true);
            verdict.Step.Should().BeNull(verdict.Detail);
            verdict.Body!.Tier.Should().Be(2);
            verdict.Body.Cmd.Value<string>("cmd").Should().Be("timedSave");
        }

        await using (var s = await CommandRouterTests.Setup.StartAsync(features: PeerFeatures))
        await using (var b = await PeerAsync(s, "B 電腦", TimedFeatures))
        {
            var cid = NewCommandId();
            var act = new JObject { ["cmd"] = "timedAction", ["deviceId"] = s.Owner.Id, ["id"] = "tm_12345678", ["action"] = "pause" };
            (await b.Client.PeerCmdAsync(new PeerCmd(cid, act))).Accepted.Should().BeTrue();
            var result = await b.Client.WaitForAsync<PeerReply>(r => (string?)r.Message["type"] == "commandResult");
            result.Message["code"]!.Value<string>().Should().Be(RemoteResultCodes.Unsupported);
            result.Message["message"]!.Value<string>().Should().Contain("版本太舊").And.Contain("定時訊息");
            await CommandRouterTests.AssertNoDeliveryAsync(s.Device!, 200);
        }
    }

    private static readonly string[] Xfer2Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Xfer1, DeviceFeatureRules.Xfer2, DeviceFeatureRules.Desk1];

    [Fact]
    public async Task 整個資料夾_目標有xfer2才簽_只有xfer1時deviceTree與帶subfolder的devicePut立即拒絕unsupported()
    {
        await using (var s = await CommandRouterTests.Setup.StartAsync(features: Xfer2Features))
        await using (var b = await PeerAsync(s, "B 電腦", Xfer2Features))
        {
            var cid = NewCommandId();
            var tree = new JObject { ["cmd"] = "deviceTree", ["deviceId"] = s.Owner.Id, ["requestId"] = "t1", ["path"] = @"C:\相簿" };
            (await b.Client.PeerCmdAsync(new PeerCmd(cid, tree))).Should().Be(new PeerCmdResult(true, cid, null));
            var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
            var verdict = Verify(s, delivery, acceptsDevice: true);
            verdict.Step.Should().BeNull(verdict.Detail);
            verdict.Body!.Tier.Should().Be(2);
            verdict.Body.Cmd.Value<string>("cmd").Should().Be("deviceTree");
        }

        await using (var s = await CommandRouterTests.Setup.StartAsync(features: XferFeatures))
        await using (var b = await PeerAsync(s, "B 電腦", Xfer2Features))
        {
            var tree = new JObject { ["cmd"] = "deviceTree", ["deviceId"] = s.Owner.Id, ["requestId"] = "t1", ["path"] = @"C:\相簿" };
            (await b.Client.PeerCmdAsync(new PeerCmd(NewCommandId(), tree))).Accepted.Should().BeTrue();
            var result = await b.Client.WaitForAsync<PeerReply>(r => (string?)r.Message["type"] == "commandResult");
            result.Message["code"]!.Value<string>().Should().Be(RemoteResultCodes.Unsupported);
            result.Message["message"]!.Value<string>().Should().Contain("版本太舊");
            await CommandRouterTests.AssertNoDeliveryAsync(s.Device!, 200);
        }
    }

    [Fact]
    public async Task 遠端桌面_目標有desk1才簽_沒有就立即拒絕_同一台第二個工作階段conflict()
    {
        await using (var s = await CommandRouterTests.Setup.StartAsync(features: Xfer2Features))
        await using (var b = await PeerAsync(s, "B 電腦", Xfer2Features))
        {
            var open = new JObject { ["cmd"] = "deskOpen", ["deviceId"] = s.Owner.Id, ["requestId"] = "01J9QZ3K8M5R7T2V4W6X8Y0DSK" };
            var cid = NewCommandId();
            (await b.Client.PeerCmdAsync(new PeerCmd(cid, open))).Should().Be(new PeerCmdResult(true, cid, null));
            var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
            var verdict = Verify(s, delivery, acceptsDevice: true);
            verdict.Step.Should().BeNull(verdict.Detail);
            verdict.Body!.Tier.Should().Be(2);

            var again = new JObject { ["cmd"] = "deskOpen", ["deviceId"] = s.Owner.Id, ["requestId"] = "01J9QZ3K8M5R7T2V4W6X8Y0DS2" };
            (await b.Client.PeerCmdAsync(new PeerCmd(NewCommandId(), again))).Accepted.Should().BeTrue();
            var conflict = await b.Client.WaitForAsync<PeerReply>(r => (string?)r.Message["type"] == "commandResult");
            conflict.Message["code"]!.Value<string>().Should().Be(RemoteResultCodes.Conflict);
        }

        await using (var s = await CommandRouterTests.Setup.StartAsync(features: XferFeatures))
        await using (var b = await PeerAsync(s, "B 電腦", Xfer2Features))
        {
            var open = new JObject { ["cmd"] = "deskOpen", ["deviceId"] = s.Owner.Id, ["requestId"] = "01J9QZ3K8M5R7T2V4W6X8Y0DSK" };
            (await b.Client.PeerCmdAsync(new PeerCmd(NewCommandId(), open))).Accepted.Should().BeTrue();
            var result = await b.Client.WaitForAsync<PeerReply>(r => (string?)r.Message["type"] == "commandResult");
            result.Message["code"]!.Value<string>().Should().Be(RemoteResultCodes.Unsupported);
            result.Message["message"]!.Value<string>().Should().Contain("還不能遠端桌面");
            await CommandRouterTests.AssertNoDeliveryAsync(s.Device!, 200);
        }
    }

    private static readonly string[] SealFeatures = [.. Xfer2Features, DeviceFeatureRules.DeskE2e1];

    [Fact]
    public async Task 遠端桌面_看的一方的公鑰只送給有deske2e1的電腦_沒有的拿掉之後照樣簽()
    {
        // 2026-11-13 輸入的端對端加密：舊版的桌面不認得 viewerKey（閘門會回 invalid）——伺服器替它拿掉，照舊以明文操作
        const string viewerKey = "BFFcPW6545a5BNP-yn9U_c0MwemXvzddylFa0KbDtANfRTa-OlDzGPv5pUdZAqIhUCvvDVfgjFOyzApW8X2fk1Q"; // gitleaks:allow（測試用的固定公鑰，不是秘密）
        JObject Open(string deviceId) => new()
        {
            ["cmd"] = "deskOpen", ["deviceId"] = deviceId, ["requestId"] = "01J9QZ3K8M5R7T2V4W6X8Y0DSK", [DeskSealRules.ViewerKeyField] = viewerKey,
        };

        await using (var s = await CommandRouterTests.Setup.StartAsync(features: SealFeatures))
        await using (var b = await PeerAsync(s, "B 電腦", SealFeatures))
        {
            var cid = NewCommandId();
            (await b.Client.PeerCmdAsync(new PeerCmd(cid, Open(s.Owner.Id)))).Should().Be(new PeerCmdResult(true, cid, null));
            var verdict = Verify(s, await s.Device!.WaitForAsync<CommandDelivery>(), acceptsDevice: true);
            verdict.Step.Should().BeNull(verdict.Detail);
            verdict.Body!.Cmd.Value<string>(DeskSealRules.ViewerKeyField).Should().Be(viewerKey, "公鑰原樣轉給被看的那台");
        }

        await using (var s = await CommandRouterTests.Setup.StartAsync(features: Xfer2Features))
        await using (var b = await PeerAsync(s, "B 電腦", SealFeatures))
        {
            var cid = NewCommandId();
            (await b.Client.PeerCmdAsync(new PeerCmd(cid, Open(s.Owner.Id)))).Should().Be(new PeerCmdResult(true, cid, null));
            var verdict = Verify(s, await s.Device!.WaitForAsync<CommandDelivery>(), acceptsDevice: true);
            verdict.Step.Should().BeNull(verdict.Detail);
            verdict.Body!.Cmd.Value<string>("cmd").Should().Be("deskOpen");
            verdict.Body.Cmd.ContainsKey(DeskSealRules.ViewerKeyField).Should().BeFalse("那台不認得這一欄：拿掉之後照樣開遠端桌面");
        }
    }

    private static readonly string[] ExecFeatures = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Exec1];

    private static JObject RemoteExec(string deviceId, string command = "Get-Date") => new()
    {
        ["cmd"] = RemoteCommandCatalog.RemoteExec, ["deviceId"] = deviceId, ["shell"] = "powershell", ["command"] = command, ["timeoutSec"] = 60,
    };

    [Fact]
    public async Task 遠端執行指令_簽信封給目標_輸出轉回發起的電腦_停止要求轉給目標()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: ExecFeatures);
        await using var b = await PeerAsync(s, "B 電腦", ExecFeatures);
        var cid = NewCommandId();

        (await b.Client.PeerCmdAsync(new PeerCmd(cid, RemoteExec(s.Owner.Id, "dir")))).Should().Be(new PeerCmdResult(true, cid, null));
        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        var verdict = Verify(s, delivery, acceptsDevice: true);
        verdict.Step.Should().BeNull(verdict.Detail);
        verdict.Body!.Tier.Should().Be(2);
        verdict.Body.Conv.Should().BeNull();
        verdict.Body.Cmd.Value<string>("command").Should().Be("dir");
        verdict.Body.Issuer.Should().Be(new CommandIssuer(CommandIssuerKinds.Device, "B 電腦"));

        (await s.Device.ExecOutputAsync(new ExecChunk(delivery.CommandId, 0, RemoteExecRules.KindOut, "輸出"))).Keep.Should().BeTrue();
        var reply = await b.Client.WaitForAsync<ExecChunk>();
        reply.Should().Be(new ExecChunk(cid, 0, RemoteExecRules.KindOut, "輸出"), "commandId 換成發起時的");

        (await b.Client.ExecCancelAsync(new ExecCancel(cid))).Found.Should().BeTrue();
        (await s.Device.WaitForAsync<ExecAbort>()).CommandId.Should().Be(delivery.CommandId);
        (await s.Device.ExecOutputAsync(new ExecChunk(delivery.CommandId, 1, RemoteExecRules.KindExit, "已停止", 1, RemoteExecRules.ExitCancelled))).Keep.Should().BeTrue();
        (await b.Client.WaitForAsync<ExecChunk>()).Code.Should().Be(RemoteExecRules.ExitCancelled);
        (await s.Device.ExecOutputAsync(new ExecChunk(delivery.CommandId, 2, RemoteExecRules.KindOut, "晚到的"))).Keep.Should().BeFalse("結束碼之後就忘掉了");
    }

    [Fact]
    public async Task 遠端執行指令_目標沒有exec1立即拒絕unsupported_發起的連線沒有exec1回not_available()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: PeerFeatures);
        await using var b = await PeerAsync(s, "B 電腦", ExecFeatures);
        var cid = NewCommandId();
        (await b.Client.PeerCmdAsync(new PeerCmd(cid, RemoteExec(s.Owner.Id)))).Accepted.Should().BeTrue();
        var result = await b.Client.WaitForAsync<PeerReply>(r => (string?)r.Message["type"] == "commandResult");
        result.Message["code"]!.Value<string>().Should().Be(RemoteResultCodes.Unsupported);
        result.Message["message"]!.Value<string>().Should().Contain("A 電腦").And.Contain("遠端指令");
        await CommandRouterTests.AssertNoDeliveryAsync(s.Device!, 200);

        await using var s2 = await CommandRouterTests.Setup.StartAsync(features: ExecFeatures);
        await using var old = await PeerAsync(s2, "舊的 B", PeerFeatures);
        var oldCid = NewCommandId();
        (await old.Client.PeerCmdAsync(new PeerCmd(oldCid, RemoteExec(s2.Owner.Id)))).Should().Be(new PeerCmdResult(false, oldCid, BrowserErrorCodes.NotAvailable));
        await CommandRouterTests.AssertNoDeliveryAsync(s2.Device!, 200);
    }

    [Fact]
    public async Task 遠端執行指令_形狀不合格或目標是自己被拒()
    {
        await using var s = await CommandRouterTests.Setup.StartAsync(features: ExecFeatures);
        await using var b = await PeerAsync(s, "B 電腦", ExecFeatures);

        var bad = NewCommandId();
        var shell = RemoteExec(s.Owner.Id);
        shell["shell"] = "bash";
        (await b.Client.PeerCmdAsync(new PeerCmd(bad, shell))).Should().Be(new PeerCmdResult(false, bad, BrowserErrorCodes.Invalid));

        var self = NewCommandId();
        (await b.Client.PeerCmdAsync(new PeerCmd(self, RemoteExec(b.Device.Id)))).Accepted.Should().BeTrue();
        (await b.Client.WaitForAsync<PeerReply>(r => r.CommandId == self && (string?)r.Message["type"] == "commandResult")).Message["code"]!.Value<string>()
            .Should().Be(RemoteResultCodes.Invalid);
        await CommandRouterTests.AssertNoDeliveryAsync(s.Device!, 200);
    }

    private static string NewCommandId() => SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private static CommandEnvelopeVerdict Verify(CommandRouterTests.Setup s, CommandDelivery delivery, bool acceptsDevice)
    {
        var options = s.Host.Services.GetRequiredService<IOptions<HubOptions>>().Value;
        var target = new CommandEnvelopeTarget(s.Host.Services.GetRequiredService<HubIdentity>().Spki,
            HostingRules.NormalizeOrigin(HostingRules.PublicOrigin(options))!, s.Owner.Id, s.Hello!.NonceS, acceptsDevice);
        return CommandEnvelope.Verify(delivery.Envelope, delivery.CommandId, target, new ServerTimeBounds(s.Host.Clock.NowMs, s.Host.Clock.NowMs));
    }

    /// <summary>另一台已配對的桌面，以 <paramref name="features"/> 交握</summary>
    private static async Task<PeerDevice> PeerAsync(CommandRouterTests.Setup s, string name, string[] features)
    {
        var device = await TestDevice.AddAsync(s.Host, name);
        var client = await DeviceHubClient.ConnectAsync(s.Host, device.Token);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(s.Host), new OutboxPosition(CommandRouterTests.NextSeq(s.Host, device.Id), null, null), features: features);
        return new PeerDevice(device, client);
    }

    private sealed record PeerDevice(TestDevice Device, DeviceHubClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            Device.Dispose();
        }
    }
}
