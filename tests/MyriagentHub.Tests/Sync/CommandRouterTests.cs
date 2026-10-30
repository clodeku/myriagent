using System.Diagnostics;
using System.Net;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using MyriagentHub.Audit;
using MyriagentHub.Auth;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Media;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using MyriagentHub.Trust;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;
using HubOptions = MyriagentHub.Hosting.HubOptions;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 指令路由（WP4.5 計畫步 6；D2～D16、D22、D23、D29）：以 <see cref="HubTestHost"/>、扮演 WP4.4 桌面的 <see cref="DeviceHubClient"/>（以
/// <c>[conv1, cmd1]</c> 交握、記下 Hello 的 nonceS）與帶 Chrome／Windows UA 登入的瀏覽器驅動 <see cref="CommandRouter"/>。
/// 信封一律以裝置的 <see cref="CommandEnvelope.Verify"/> 驗（時間界線用手動時鐘，不用 <c>ServerClockRules.Bounds</c>）
/// </summary>
public sealed class CommandRouterTests
{
    internal const string Conv = "0123456789abcdef0123456789abcdef";

    internal const string ChromeWindows = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    private const string Identity = "claude:org1:a@example.com";

    private static int commandIdCounter;

    // ───── Owner 路由：簽章、cid、時機 ─────

    [Fact]
    public async Task send轉給擁有者_信封以裝置的Verify驗得過()
    {
        await using var s = await Setup.StartAsync();
        var bcid = NextCommandId();
        var result = await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "hi" }, commandId: bcid);
        result["accepted"]!.Value<bool>().Should().BeTrue();
        result["commandId"]!.Value<string>().Should().Be(bcid);

        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        var verdict = s.Verify(delivery);
        verdict.Step.Should().BeNull(verdict.Detail);
        var body = verdict.Body!;
        body.Cmd.Properties().Select(p => p.Name).Should().Equal("cmd", "conversationId", "text");
        body.Cmd["text"]!.Value<string>().Should().Be("hi");
        body.Conv.Should().Be(Conv);
        body.Epoch.Should().Be(s.Host.Services.GetRequiredService<IConversationDirectory>().Find(Conv)!.Epoch);
        body.Dev.Should().Be(s.Owner.Id);
        body.Conn.Should().Be(s.Hello!.NonceS);
        body.Aud.Should().Be("https://localhost");
        var expected = RemoteTierRules.Evaluate("send", body.Cmd, RemoteTierFacts.Unknown, RemoteSide.Server);
        (body.Tier, body.FullAccess).Should().Be((expected.Tier, expected.FullAccess));
        body.Issuer.Should().Be(new CommandIssuer(CommandIssuerKinds.Browser, "Chrome・Windows"));
        var session = await s.Host.Services.GetRequiredService<SessionService>().FindAsync(s.Browser.Hub.ServerConnection!.SessionId);
        body.Auth.Should().Be(new CommandAuth(session!.LastUvAt, DefaultPassword: true));
        body.Durable.Should().BeFalse();
        (body.Created, body.Iat).Should().Be((s.Host.Clock.NowMs, s.Host.Clock.NowMs));
        (body.Exp - body.Iat).Should().Be(60_000);
        await s.Browser.Hub.AssertNoMessageAsync("commandResult", 200);
    }

    [Fact]
    public async Task cid是伺服器的ULID_另存瀏覽器的commandId()
    {
        await using var s = await Setup.StartAsync();
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "hi" }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();

        SyncIds.IsUlid(delivery.CommandId).Should().BeTrue();
        delivery.CommandId.Should().NotBe(bcid);
        s.Verify(delivery).Body!.Cid.Should().Be(delivery.CommandId);
        var row = ConversationEventLogTests.Rows(s.Host, "SELECT command_id, browser_command_id, state, target_device_id, conversation_id, target_epoch, cmd, durable, " +
            "unsigned_body, created_at, delivered_at, expires_at, issuer FROM commands").Single();
        row[0].Should().Be(delivery.CommandId);
        row[1].Should().Be(bcid);
        row[2].Should().Be(CommandStates.Delivered);
        row[3].Should().Be(s.Owner.Id);
        row[4].Should().Be(Conv);
        row[5].Should().Be(1L);
        row[6].Should().Be("send");
        row[7].Should().Be(0L);
        row[8].Should().BeNull("非 durable 不存未簽的 body");
        (row[9], row[10], row[11]).Should().Be((s.Host.Clock.NowMs, s.Host.Clock.NowMs, s.Host.Clock.NowMs + CommandStateRules.ResultDeadlineMs));
        row[12].Should().Be("""{"kind":"browser","label":"Chrome・Windows"}""");
        s.Tracker.TryGet(delivery.CommandId, out var tracked).Should().BeTrue();
        tracked!.BrowserCommandId.Should().Be(bcid);
        tracked.BrowserConnectionId.Should().Be(s.Browser.Hub.ServerConnection!.ConnectionId);
        (tracked.ExpectedConversationId, tracked.BrowserConversationId, tracked.RestoreText, tracked.DeviceName).Should().Be((Conv, Conv, "hi", "A 電腦"));
    }

    [Fact]
    public async Task Cmd不等電腦就回()
    {
        await using var s = await Setup.StartAsync();
        var watch = Stopwatch.StartNew();
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "hi" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "電腦不回應也不等");
        await s.Device!.WaitForAsync<CommandDelivery>();
        await s.Browser.Hub.AssertNoMessageAsync("commandResult", 300);
    }

    [Fact]
    public async Task 提交之前不送出()
    {
        await using var s = await Setup.StartAsync();
        var writer = s.Host.Services.GetRequiredService<SyncWriter>();
        using var gate = new ManualResetEventSlim(false);
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocker = writer.WriteAsync(_ =>
        {
            running.TrySetResult();
            gate.Wait(TimeSpan.FromSeconds(10));
        });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cmd = s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "hi" }, commandId: NextCommandId());
        await Task.Delay(300);
        cmd.IsCompleted.Should().BeFalse("寫入者擋住時列還沒提交，Cmd 不回");
        await AssertNoDeliveryAsync(s.Device!, 100);
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(0);

        gate.Set();
        await blocker;
        (await cmd)["accepted"]!.Value<bool>().Should().BeTrue();
        await s.Device!.WaitForAsync<CommandDelivery>();
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(1);
    }

    // ───── 立即拒絕（D3） ─────

    [Fact]
    public async Task 擁有者不在線_立即offline_不寫列_送toast與sendRejected()
    {
        await using var s = await Setup.StartAsync(connect: false);
        var bcid = NextCommandId();
        var result = await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "祕密的文字" }, commandId: bcid);
        result["accepted"]!.Value<bool>().Should().BeTrue("立即拒絕也是 accepted，結果看 commandResult");
        result["commandId"]!.Value<string>().Should().Be(bcid);

        var commandResult = await s.Browser.Hub.WaitForAsync("commandResult");
        commandResult.ToString(Newtonsoft.Json.Formatting.None).Should().Be(
            CommandMessageRules.CommandResultJson(bcid, RemoteResultStates.Rejected, RemoteResultCodes.Offline, null, null));
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("那台電腦目前不在線，指令沒有送出");
        var restored = await s.Browser.Hub.WaitForAsync("sendRejected");
        (restored["conversationId"]!.Value<string>(), restored["text"]!.Value<string>()).Should().Be((Conv, "祕密的文字"));
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(0, "立即拒絕不寫列");
        s.Counters.Snapshot().RejectedAtServer.Should().Be(1);

        var audit = await WaitForIssuedAsync(s.Host);
        audit.Outcome.Should().Be(AuditOutcomes.Denied);
        audit.Target.Should().BeNull("沒有 cid");
        audit.ActorType.Should().Be(AuditActors.Owner);
        audit.Detail.Should().NotContain("祕密的文字");
        var detail = JObject.Parse(audit.Detail!);
        (detail["cmd"]!.Value<string>(), detail["device"]!.Value<string>(), detail["conv"]!.Value<string>(), detail["code"]!.Value<string>(), detail["label"]!.Value<string>())
            .Should().Be(("send", s.Owner.Id, Conv, RemoteResultCodes.Offline, "Chrome・Windows"));
        JObject.Parse(detail["masked"]!.Value<string>()!)["text"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Be("""{"len":5}""");
    }

    [Fact]
    public async Task 擁有者在線但沒協商cmd1_unsupported()
    {
        await using var s = await Setup.StartAsync(features: []);
        s.Hello!.Features.Should().Contain(DeviceFeatureRules.Cmd1, "伺服器照樣宣告");
        s.Host.Services.GetRequiredService<ConnectionRegistry>().ConnectionsOf(s.Owner.Id).Single().Features.Should().BeEmpty("裝置沒宣告功能（第 3 階段／WP4.1 的桌面）：協商不到 cmd1");
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "hi" }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();

        (await s.Browser.Hub.WaitForAsync("commandResult"))["code"]!.Value<string>().Should().Be(RemoteResultCodes.Unsupported);
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("那台電腦的 Clode Myriagent 還不支援網頁版遠端操作，請先更新那台電腦");
        (await s.Browser.Hub.WaitForAsync("sendRejected"))["text"]!.Value<string>().Should().Be("hi");
        await AssertNoDeliveryAsync(s.Device!, 300);
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(0);
    }

    [Fact]
    public async Task 對話不存在_Cmd的conflict_送conversationClosed_toast說對話不在了()
    {
        await using var s = await Setup.StartAsync();
        const string missing = "fedcba9876543210fedcba9876543210";
        var result = await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = missing, text = "hi" }, commandId: NextCommandId());
        result["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Conflict);
        (await s.Browser.Hub.WaitForAsync("conversationClosed"))["conversationId"]!.Value<string>().Should().Be(missing);
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be(BrowserCommandRules.ConversationGoneToast);
        await s.Browser.Hub.AssertNoMessageAsync("sendRejected", 200);
        await AssertNoDeliveryAsync(s.Device!, 100);
        s.Counters.Snapshot().RejectedAtServer.Should().Be(0, "Cmd 的錯誤不算立即拒絕");
    }

    [Fact]
    public async Task metadata對話_send是policy_stop照送()
    {
        await using var s = await Setup.StartAsync(policy: ConversationSyncPolicies.Metadata);
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "hi" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        (await s.Browser.Hub.WaitForAsync("commandResult"))["code"]!.Value<string>().Should().Be(RemoteResultCodes.Policy);
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("那台電腦的設定不允許這個遠端操作");
        (await s.Browser.Hub.WaitForAsync("sendRejected"))["text"]!.Value<string>().Should().Be("hi");

        (await s.Browser.Hub.CmdAsync(new { cmd = "stop", conversationId = Conv }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        s.Verify(delivery).Body!.Cmd["cmd"]!.Value<string>().Should().Be("stop");
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(1, "只有 stop 寫了列");
    }

    [Fact]
    public async Task 擁有者已撤銷_not_owner()
    {
        await using var s = await Setup.StartAsync(connect: false);
        var devices = s.Host.Services.GetRequiredService<DeviceDirectory>();
        devices.Put(devices.Get(s.Owner.Id)! with { RevokedAt = s.Host.Clock.NowMs });
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "hi" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        (await s.Browser.Hub.WaitForAsync("commandResult"))["code"]!.Value<string>().Should().Be(RemoteResultCodes.NotOwner);
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("這個對話已不屬於那台電腦，網頁版不能操作它");
        (await s.Browser.Hub.WaitForAsync("sendRejected"))["text"]!.Value<string>().Should().Be("hi");
    }

    [Fact]
    public async Task 伺服器身分輪換後_不簽_中斷那條連線_offline()
    {
        await using var s = await Setup.StartAsync();
        await s.Host.Services.GetRequiredService<HubIdentity>().RotateAsync(CancellationToken.None);
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "hi" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();

        (await s.Browser.Hub.WaitForAsync("commandResult"))["code"]!.Value<string>().Should().Be(RemoteResultCodes.Offline);
        await s.Device!.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        await AssertNoDeliveryAsync(s.Device, 0);
        s.Counters.Snapshot().Aborted.Should().Be(1);
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(0);
    }

    [Fact]
    public async Task 兩條連線_送到Order最大的已證明cmd1連線()
    {
        await using var s = await Setup.StartAsync();
        await using var newer = await DeviceHubClient.ConnectAsync(s.Host, s.Owner.Token);
        var (newerHello, _) = await newer.HandshakeAsync(s.Owner, DeviceHubClient.ObservedSpki(s.Host), new OutboxPosition(NextSeq(s.Host, s.Owner.Id), null, null),
            features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1]);
        var registry = s.Host.Services.GetRequiredService<ConnectionRegistry>();
        registry.ConnectionsOf(s.Owner.Id).Where(c => c.IsProven).Should().HaveCount(2, "夾具：同一台兩條已證明的連線");

        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "hi" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var delivery = await newer.WaitForAsync<CommandDelivery>();
        s.Verify(delivery, newerHello.NonceS).Step.Should().BeNull("conn 是新那條的 nonceS");
        await AssertNoDeliveryAsync(s.Device!, 300);
    }

    // ───── 範圍、投影、遮蔽 ─────

    // ───── WP4.7 的檔案指令（WP4.7 計畫步 13：合併後不再延後） ─────

    /// <summary>記下查過的上傳；<see cref="Known"/> 裡的 id 才算在伺服器上</summary>
    private sealed class FakeUploads : IUploadRegistry
    {
        public HashSet<string> Known { get; } = new(StringComparer.Ordinal);

        public List<IReadOnlyList<UploadClaim>> Calls { get; } = [];

        public bool TryVerify(IReadOnlyList<UploadClaim> uploads)
        {
            lock (Calls)
                Calls.Add(uploads);
            return uploads.All(u => Known.Contains(u.UploadId));
        }
    }

    private static object Upload(string id) => new { uploadId = id, name = "規格.txt", size = 3, sha256 = new string('a', 64) };

    [Fact]
    public async Task listFiles與fileSearch轉給擁有者_只留型錄欄位_信封驗得過()
    {
        await using var s = await Setup.StartAsync();
        CommandRouteRules.Deferred.Should().BeEmpty();
        foreach (var (cmd, fields) in new (object Cmd, string[] Fields)[]
        {
            (new { cmd = "listFiles", conversationId = Conv, requestId = "r1", path = (string?)null, paneId = "p1" }, ["cmd", "conversationId", "requestId", "path"]),
            (new { cmd = "fileSearch", conversationId = Conv, requestId = "r2", query = "a" }, ["cmd", "conversationId", "requestId", "query"]),
        })
        {
            (await s.Browser.Hub.CmdAsync(cmd, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue(JObject.FromObject(cmd).ToString());
            var verdict = s.Verify(await s.Device!.WaitForAsync<CommandDelivery>());
            verdict.Step.Should().BeNull(verdict.Detail);
            verdict.Body!.Cmd.Properties().Select(p => p.Name).Should().Equal(fields);
            verdict.Body.Tier.Should().Be(1);
        }
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(2);
        await s.Browser.Hub.AssertNoMessageAsync("toast", 200);
    }

    [Fact]
    public async Task addUploaded的上傳不在伺服器上_立即invalid_不簽不寫列_查到了才轉送()
    {
        var uploads = new FakeUploads();
        await using var s = await Setup.StartAsync(services: services => services.AddSingleton<IUploadRegistry>(uploads));
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "addUploaded", conversationId = Conv, uploads = new[] { Upload("up_1") } }, commandId: bcid))["accepted"]!
            .Value<bool>().Should().BeTrue("立即拒絕也是 accepted");

        var rejected = await s.Browser.Hub.WaitForAsync("commandResult");
        (rejected["commandId"]!.Value<string>(), rejected["state"]!.Value<string>(), rejected["code"]!.Value<string>(), rejected["message"]!.Value<string>())
            .Should().Be((bcid, RemoteResultStates.Rejected, RemoteResultCodes.Invalid, CommandRouter.UploadsUnknownMessage));
        await AssertNoDeliveryAsync(s.Device!, 200);
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(0, "立即拒絕不寫列");
        uploads.Calls.Single().Should().Equal(new UploadClaim("up_1", 3, new string('a', 64)));

        uploads.Known.Add("up_1");
        (await s.Browser.Hub.CmdAsync(new { cmd = "addUploaded", conversationId = Conv, uploads = new[] { Upload("up_1") } }, commandId: NextCommandId()))["accepted"]!
            .Value<bool>().Should().BeTrue();
        var verdict = s.Verify(await s.Device!.WaitForAsync<CommandDelivery>());
        verdict.Step.Should().BeNull(verdict.Detail);
        verdict.Body!.Cmd["uploads"]![0]!["uploadId"]!.Value<string>().Should().Be("up_1");
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM commands").Should().Be(1);
    }

    [Fact]
    public async Task 不帶uploads的指令不查上傳()
    {
        var uploads = new FakeUploads();
        await using var s = await Setup.StartAsync(services: services => services.AddSingleton<IUploadRegistry>(uploads));
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "hi" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        await s.Device!.WaitForAsync<CommandDelivery>();
        uploads.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 正式的上傳登記_沒有上傳過的id一律invalid()
    {
        await using var s = await Setup.StartAsync();
        s.Host.Services.GetRequiredService<IUploadRegistry>().Should().BeOfType<UploadRegistry>("AddHubMedia 換成正式的（不是 WP4.5 的預設 NoUploadRegistry）");
        (await s.Browser.Hub.CmdAsync(new { cmd = "addUploaded", conversationId = Conv, uploads = new[] { Upload("never_uploaded") } }, commandId: NextCommandId()))["accepted"]!
            .Value<bool>().Should().BeTrue();
        (await s.Browser.Hub.WaitForAsync("commandResult"))["code"]!.Value<string>().Should().Be(RemoteResultCodes.Invalid);
        await AssertNoDeliveryAsync(s.Device!, 200);
    }

    [Fact]
    public async Task cmd_json不含原文_content_sha256等於ContentDigest()
    {
        await using var s = await Setup.StartAsync();
        const string text = "請幫我看這段：祕密的提示";
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var verdict = s.Verify(await s.Device!.WaitForAsync<CommandDelivery>());

        var row = ConversationEventLogTests.Rows(s.Host, "SELECT cmd_json, content_sha256, tier, full_access FROM commands").Single();
        row[0].Should().Be($$$"""{"cmd":"send","conversationId":"{{{Conv}}}","text":{"len":{{{text.Length}}}}}""");
        row[1].Should().Be(CommandEnvelope.ContentDigest(verdict.Body!)).And.Be(verdict.ContentSha256);
        (row[2], row[3]).Should().Be(((long)verdict.Body!.Tier, verdict.Body.FullAccess ? 1L : 0L));

        var audit = await WaitForIssuedAsync(s.Host);
        audit.Outcome.Should().Be(AuditOutcomes.Ok);
        audit.Target.Should().Be(verdict.Body.Cid);
        audit.Detail.Should().NotContain("祕密");
        var detail = JObject.Parse(audit.Detail!);
        detail["masked"]!.Value<string>().Should().Be((string)row[0]!);
        (detail["tier"]!.Value<int>(), detail["durable"]!.Value<bool>(), detail["code"]!.Type).Should().Be((verdict.Body.Tier, false, JTokenType.Null));
        s.Counters.Snapshot().Should().Match<CommandCountersSnapshot>(c => c.Issued == 1 && c.RejectedAtServer == 0);
    }

    [Fact]
    public async Task 網頁多帶的鍵投影後送出()
    {
        await using var s = await Setup.StartAsync();
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", paneId = "p1", conversationId = Conv, text = "hi", extra = new { a = 1 } }, commandId: NextCommandId()))["accepted"]!
            .Value<bool>().Should().BeTrue();
        var body = s.Verify(await s.Device!.WaitForAsync<CommandDelivery>()).Body!;
        body.Cmd.Properties().Select(p => p.Name).Should().Equal(["cmd", "conversationId", "text"], "paneId 與不認得的鍵不進信封（電腦的形狀檢查是嚴格的）");
    }

    [Fact]
    public async Task delete的層級是2()
    {
        // SEC-27：刪除要擁有者確認由電腦端（回收區）把關；伺服器轉送時層級一定是 2
        await using var s = await Setup.StartAsync();
        (await s.Browser.Hub.CmdAsync(new { cmd = "delete", conversationId = Conv }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var body = s.Verify(await s.Device!.WaitForAsync<CommandDelivery>()).Body!;
        body.Tier.Should().Be(2);
        ConversationEventLogTests.Rows(s.Host, "SELECT tier FROM commands").Single()[0].Should().Be(2L);
    }

    [Fact]
    public async Task 稽核的detail一定是完整的JSON_遮蔽後的指令太長就只留長度()
    {
        // 900 個中文字的標題：遮蔽後的 JSON 不到 1000 字（AuditMasked 照留），但稽核以 System.Text.Json 寫、非 ASCII 跳脫成六個字元會超過 2000 字
        await using var s = await Setup.StartAsync(connect: false);
        var title = new string('長', 900);
        (await s.Browser.Hub.CmdAsync(new { cmd = "rename", conversationId = Conv, title }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var audit = await WaitForIssuedAsync(s.Host);
        audit.Detail!.Length.Should().BeLessThan(AuditLog.MaxDetailChars);
        var masked = JObject.Parse(JObject.Parse(audit.Detail)["masked"]!.Value<string>()!);
        masked["truncated"]!.Value<bool>().Should().BeTrue();
        masked["len"]!.Value<int>().Should().BeGreaterThan(900);

        // 正向對照：短的照留遮蔽後的指令
        var detail = CommandRouter.AuditDetail("rename", "d", Conv, null, false, "瀏覽器", null,
            new JObject { ["cmd"] = "rename", ["conversationId"] = Conv, ["title"] = "短" });
        System.Text.Json.JsonSerializer.Serialize(detail, KvStore.Json).Should().Contain("rename").And.NotContain("truncated");
    }

    // ───── Device 路由（D5、D6） ─────

    [Fact]
    public async Task 帳戶只在一台_Device路由送到那台_deviceId寫入_帳戶id是那台的本機id()
    {
        await using var s = await Setup.StartAsync();
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountRefresh", accountId = la }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();

        var verdict = s.Verify(await s.Device!.WaitForAsync<CommandDelivery>());
        verdict.Step.Should().BeNull(verdict.Detail);
        verdict.Body!.Cmd.ToString(Newtonsoft.Json.Formatting.None).Should().Be($$"""{"cmd":"accountRefresh","accountId":"acct-a","deviceId":"{{s.Owner.Id}}"}""");
        (verdict.Body.Conv, verdict.Body.Epoch, verdict.Body.Dev).Should().Be((null, null, s.Owner.Id));
        var row = ConversationEventLogTests.Rows(s.Host, "SELECT target_device_id, conversation_id, target_epoch FROM commands").Single();
        (row[0], row[1], row[2]).Should().Be((s.Owner.Id, null, null));
        s.Tracker.TryGet(verdict.Body.Cid, out var tracked).Should().BeTrue();
        tracked!.BrowserAccountId.Should().Be(la, "輸出的 accountId 換回瀏覽器看到的 la_");
    }

    [Fact]
    public async Task 帳戶在兩台而且沒指定電腦_not_available_toast與現在相同()
    {
        await using var s = await Setup.StartAsync();
        using var other = await TestDevice.AddAsync(s.Host, "B 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        (await LinkAsync(s.Host, other.Id, "acct-b")).Should().Be(la, "夾具：同一個身分是同一個邏輯帳戶");

        var result = await s.Browser.Hub.CmdAsync(new { cmd = "accountRefresh", accountId = la }, commandId: NextCommandId());
        result["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable);
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("網頁版還不能做這件事");
        // 側欄的「重新檢查」（accountId null）也一樣
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountRefresh", accountId = (string?)null }, commandId: NextCommandId()))["error"]!.Value<string>().Should().Be(BrowserErrorCodes.NotAvailable);
        await AssertNoDeliveryAsync(s.Device!, 200);
        await s.Browser.Hub.AssertNoMessageAsync("commandResult", 100);
    }

    [Fact]
    public async Task 指定的電腦沒有這個帳戶_立即invalid()
    {
        await using var s = await Setup.StartAsync();
        using var other = await TestDevice.AddAsync(s.Host, "B 電腦");
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountRefresh", accountId = la, deviceId = other.Id }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();

        var result = await s.Browser.Hub.WaitForAsync("commandResult");
        (result["state"]!.Value<string>(), result["code"]!.Value<string>(), result["message"]!.Value<string>())
            .Should().Be((RemoteResultStates.Rejected, RemoteResultCodes.Invalid, CommandRouteRules.NoLinkMessage));
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be(CommandRouteRules.NoLinkMessage);
        await AssertNoDeliveryAsync(s.Device!, 200);

        // 指定的電腦不存在：Cmd invalid（D5（a））
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountRefresh", accountId = la, deviceId = SyncIds.NewDeviceId() }, commandId: NextCommandId()))["error"]!
            .Value<string>().Should().Be(BrowserErrorCodes.Invalid);
    }

    [Fact]
    public async Task 帶deviceId的accountLogout是單台_非durable()
    {
        // 沒帶 deviceId 的是所有電腦登出（D26），見 DurableCommandTests
        await using var s = await Setup.StartAsync();
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la, deviceId = s.Owner.Id }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var body = s.Verify(await s.Device!.WaitForAsync<CommandDelivery>()).Body!;
        (body.Durable, body.Cmd["accountId"]!.Value<string>()).Should().Be((false, "acct-a"), "帶了 deviceId 的是單台、非 durable");
    }

    [Fact]
    public async Task 邏輯帳戶不存在或已刪除_Cmd的conflict_toast說帳戶已刪除()
    {
        await using var s = await Setup.StartAsync();
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountRefresh", accountId = SyncIds.NewLogicalAccountId() }, commandId: NextCommandId()))["error"]!
            .Value<string>().Should().Be(BrowserErrorCodes.Conflict);
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("這個帳戶已從目錄刪除");

        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        await s.Host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute("UPDATE logical_accounts SET deleted_at = 1 WHERE logical_account_id = $id", ("$id", la)));
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogout", accountId = la, deviceId = s.Owner.Id }, commandId: NextCommandId()))["error"]!
            .Value<string>().Should().Be(BrowserErrorCodes.Conflict, "指定了電腦也一樣（D5：不論哪一種）");
        await AssertNoDeliveryAsync(s.Device!, 200);
    }

    // ───── Cmd 的錯誤（D2） ─────

    [Fact]
    public async Task 沒帶commandId_invalid()
    {
        await using var s = await Setup.StartAsync();
        var result = await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "hi" });
        result["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Invalid);
        result.Property("commandId").Should().BeNull();
        (await s.Browser.Hub.WaitForAsync("sendRejected"))["text"]!.Value<string>().Should().Be("hi", "Cmd 的 invalid 也還原文字（D22）");
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("伺服器拒絕了這個要求（格式不正確）");
        await AssertNoDeliveryAsync(s.Device!, 200);
    }

    [Fact]
    public async Task 寫入者已停止_Cmd回unavailable_send還原文字()
    {
        await using var s = await Setup.StartAsync();
        s.Host.Services.GetRequiredService<SyncWriter>().Stop();
        var result = await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text = "還原我" }, commandId: NextCommandId());
        result["error"]!.Value<string>().Should().Be(BrowserErrorCodes.Unavailable);
        (await s.Browser.Hub.WaitForAsync("sendRejected"))["text"]!.Value<string>().Should().Be("還原我");
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("伺服器暫時無法處理，請稍後再試");
        await AssertNoDeliveryAsync(s.Device!, 200);
        s.Counters.Snapshot().Issued.Should().Be(0);
    }

    // ───── aud（D12；與 WP4.4 D5 的固定向量表逐字相同） ─────

    [Theory]
    [InlineData("https://hub.example.com", "https://hub.example.com")]
    [InlineData("HTTPS://Hub.Example.COM:443/", "https://hub.example.com")]
    [InlineData("https://hub.example.com:8443", "https://hub.example.com:8443")]
    [InlineData("https://bücher.example", "https://xn--bcher-kva.example")]
    [InlineData("https://[::1]:8443", "https://[::1]:8443")]
    [InlineData("http://localhost:5000", "http://localhost:5000")]
    [InlineData("https://hub.example.com/x", null)]
    [InlineData("https://hub.example.com?x=1", null)]
    [InlineData("https://hub.example.com#x", null)]
    [InlineData("https://u:p@hub.example.com", null)]
    [InlineData("ftp://hub.example.com", null)]
    [InlineData("", null)]
    public void aud與桌面的Audience同一套規則_NormalizeOrigin的向量表(string origin, string? expected)
    {
        HostingRules.NormalizeOrigin(origin).Should().Be(expected);
        // ASCII 的合格列：等於桌面存的 ServerUrl（配對網址的 GetLeftPart(Authority)）
        if (expected is not null && origin.All(char.IsAscii))
            new Uri(origin).GetLeftPart(UriPartial.Authority).ToLowerInvariant().Should().Be(expected);
    }

    [Fact]
    public void aud與桌面的Audience同一套規則_信封的aud()
    {
        CommandRouter.Audience(new HubOptions { PublicOrigin = "HTTPS://Hub.Example.COM:443/" }).Should().Be("https://hub.example.com");
        CommandRouter.Audience(new HubOptions { ServerDomain = "Hub.Example.COM" }).Should().Be("https://hub.example.com");
        CommandRouter.Audience(new HubOptions { ServerDomain = "bücher.example" }).Should().Be("https://xn--bcher-kva.example", "PublicOrigin 沒設定時不做 IDN 轉換，外面再正規化一次");
        CommandRouter.Audience(new HubOptions { ServerDomain = "hub.example.com", HttpsPort = 8443 }).Should().Be("https://hub.example.com:8443");
        CommandRouter.Audience(new HubOptions { PublicOrigin = "https://hub.example.com/x" }).Should().BeNull("PublicOrigin 不合格：不簽、Cmd unavailable");
        CommandRouter.Audience(new HubOptions { ServerDomain = "bad host" }).Should().BeNull();
    }

    [Fact]
    public async Task aud與桌面的Audience同一套規則_設了PublicOrigin時信封帶正規化的網址()
    {
        const string publicOrigin = "https://hub.example.com";
        await using var s = await Setup.StartAsync(settings: Settings(("Hub:PublicOrigin", "HTTPS://Hub.Example.COM:443/")), origin: publicOrigin);
        (await s.Browser.Hub.CmdAsync(new { cmd = "stop", conversationId = Conv }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var delivery = await s.Device!.WaitForAsync<CommandDelivery>();
        s.Verify(delivery, aud: publicOrigin).Step.Should().BeNull();
        s.Verify(delivery, aud: "HTTPS://Hub.Example.COM:443/").Step.Should().Be(CommandEnvelopeSteps.Audience, "以 Ordinal 比對：簽的是正規化之後的");
    }

    // 往返時間（完成條件：p95 ≤ 50 ms）在 CommandRoundTripTests：量測的集合，不和其他測試同時跑

    // ───── 工具 ─────

    internal static string NextCommandId() =>
        SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + Interlocked.Increment(ref commandIdCounter));

    /// <summary>這段時間內電腦沒有收到 CommandDelivery（其他種類的照樣取走）</summary>
    internal static async Task AssertNoDeliveryAsync(DeviceHubClient device, int milliseconds)
    {
        if (milliseconds > 0)
            await Task.Delay(milliseconds);
        while (device.Received.Reader.TryRead(out var message))
            message.Should().NotBeOfType<CommandDelivery>();
    }

    internal static async Task<AuditRecord> WaitForIssuedAsync(HubTestHost host)
    {
        AuditRecord? found = null;
        for (var i = 0; i < 100 && found is null; i++)
        {
            found = (await ConversationEventLogTests.AuditRowsAsync(host)).SingleOrDefault(a => a.Action == AuditActions.CommandIssued);
            if (found is null)
                await Task.Delay(20);
        }
        return found ?? throw new TimeoutException("沒有 command.issued 稽核");
    }

    /// <summary>這台電腦下一筆的 deviceSeq（每台一條序列，對話與目錄的事件共用）</summary>
    internal static long NextSeq(HubTestHost host, string deviceId) =>
        EventLogTests.Count(host, $"SELECT COALESCE((SELECT next_device_seq FROM device_streams WHERE device_id = '{deviceId}'), 1)");

    /// <summary>這台電腦把身分 <see cref="Identity"/> 連結成本機帳戶 <paramref name="localAccountId"/>；回傳邏輯帳戶 id</summary>
    internal static async Task<string> LinkAsync(HubTestHost host, string deviceId, string localAccountId)
    {
        var outcome = await EventLogTests.AppendAsync(host, deviceId, EventLogTests.Batch(EventLogTests.Ev(NextSeq(host, deviceId), SyncEventKinds.LinkUpsert,
            EventLogTests.Link(localAccountId, Identity))));
        outcome.Ack.Results.Single().Rejected.Should().BeNull();
        using var read = host.Services.GetRequiredService<SyncWriter>().OpenRead();
        return (string)SqliteSetup.Scalar(read, $"SELECT logical_account_id FROM account_links WHERE device_id = '{deviceId}' AND local_account_id = '{localAccountId}'")!;
    }

    /// <summary>
    /// 擁有者電腦 A（名稱「A 電腦」）、它的對話 <see cref="Conv"/>（epoch 1）、A 以 <paramref name="features"/>（null＝<c>[conv1, cmd1]</c>，空的＝不宣告）交握，
    /// 瀏覽器以 Chrome／Windows 的 UA 登入、連上並 ready
    /// </summary>
    internal sealed class Setup : IAsyncDisposable
    {
        private Setup(HubTestHost host, TestDevice owner, BrowserOwner browser)
        {
            Host = host;
            Owner = owner;
            Browser = browser;
        }

        public HubTestHost Host { get; }

        public TestDevice Owner { get; }

        public BrowserOwner Browser { get; }

        public DeviceHubClient? Device { get; private set; }

        public HelloResponse? Hello { get; private set; }

        public CommandTracker Tracker => Host.Services.GetRequiredService<CommandTracker>();

        public CommandCounters Counters => Host.Services.GetRequiredService<CommandCounters>();

        /// <param name="dataDir">資料根（null＝暫存、結束時刪除；給了就留著，重新啟動的測試用）</param>
        /// <param name="services">在正式的登記之後再登記（例如收記錄的 <see cref="CapturingLoggerProvider"/>）</param>
        /// <param name="clock">主機的手動時鐘（null＝預設的固定起點；子程序的測試用真實時間起點）</param>
        public static async Task<Setup> StartAsync(string policy = ConversationSyncPolicies.Full, string[]? features = null, bool connect = true,
            IDictionary<string, string?>? settings = null, string origin = HubTestHost.Origin, string? dataDir = null, Action<IServiceCollection>? services = null,
            ManualTimeProvider? clock = null)
        {
            var host = await HubTestHost.StartAsync(dataDir: dataDir, settings: settings, services: services, clock: clock);
            TestDevice? owner = null;
            BrowserOwner? browser = null;
            try
            {
                owner = await TestDevice.AddAsync(host, "A 電腦");
                var created = await ConversationEventLogTests.Append(host, owner.Id, ConversationEventLogTests.Ev(1, K.Created, ConversationSamples.Meta("標題", policy), Conv));
                created.Ack.Results.Single().Rejected.Should().BeNull();
                await WaitUntilAsync(() => host.Services.GetRequiredService<IConversationDirectory>().Find(Conv) is not null, because: "對話目錄讀到新對話");

                var http = host.CreateClient();
                http.Http.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeWindows);
                using (var login = await http.PostAsync("/api/v1/auth/login", new { userName = OwnerAccount.UserName, password = OwnerAccount.DefaultPassword }, origin: origin))
                    login.StatusCode.Should().Be(HttpStatusCode.OK);
                browser = new BrowserOwner(http, await BrowserHubClient.ConnectAsync(host, http, origin));
                await browser.Hub.ReadyAsync();
                var setup = new Setup(host, owner, browser);
                if (connect)
                {
                    setup.Device = await DeviceHubClient.ConnectAsync(host, owner.Token);
                    (setup.Hello, _) = await setup.Device.HandshakeAsync(owner, DeviceHubClient.ObservedSpki(host), new OutboxPosition(NextSeq(host, owner.Id), null, null),
                        features: features is null ? [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1] : features.Length == 0 ? null : features);
                }
                return setup;
            }
            catch
            {
                if (browser is not null)
                    await browser.DisposeAsync();
                owner?.Dispose();
                await host.DisposeAsync();
                throw;
            }
        }

        /// <summary>以裝置的方式驗信封（釘選的是伺服器目前的身分公鑰；conn 預設是第一條連線的 nonceS；時間界線是手動時鐘的現在）</summary>
        public CommandEnvelopeVerdict Verify(CommandDelivery delivery, string? conn = null, string? aud = null)
        {
            var options = Host.Services.GetRequiredService<IOptions<HubOptions>>().Value;
            var target = new CommandEnvelopeTarget(Host.Services.GetRequiredService<HubIdentity>().Spki,
                aud ?? HostingRules.NormalizeOrigin(HostingRules.PublicOrigin(options))!, Owner.Id, conn ?? Hello!.NonceS);
            return CommandEnvelope.Verify(delivery.Envelope, delivery.CommandId, target, new ServerTimeBounds(Host.Clock.NowMs, Host.Clock.NowMs));
        }

        public async ValueTask DisposeAsync()
        {
            await Browser.DisposeAsync();
            if (Device is not null)
                await Device.DisposeAsync();
            Owner.Dispose();
            await Host.DisposeAsync();
        }
    }
}
