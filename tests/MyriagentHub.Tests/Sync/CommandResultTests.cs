using System.Security.Cryptography;
using System.Text;
using Myriagent.Media;
using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Media;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.CommandRouterTests;
using static MyriagentHub.Tests.TestSupport.BrowserFixtures;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 電腦回報的結果與輸出、100 秒期限（WP4.5 計畫步 7；D17～D23）：以 <see cref="CommandRouterTests.Setup"/> 的擁有者電腦 A（<c>[conv1, cmd1]</c>）
/// 與 Chrome／Windows 的瀏覽器送出指令，A 以 <see cref="DeviceHubClient.SendResultAsync"/>／<see cref="DeviceHubClient.SendOutputAsync"/> 扮演 WP4.4 桌面回報
/// </summary>
public sealed class CommandResultTests
{
    private const string Digest = "rd1:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string OtherConv = "fedcba9876543210fedcba9876543210";

    // ───── 結果（D17、D18、D21～D23） ─────

    [Fact]
    public async Task done轉給發起的連線_commandId是瀏覽器的_列變done()
    {
        await using var s = await Setup.StartAsync();
        var (bcid, d) = await SendAsync(s, "hi");
        s.Host.Clock.Advance(TimeSpan.FromSeconds(3));
        await s.Device!.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Done, Tier: 1, FullAccess: false));

        var result = await s.Browser.Hub.WaitForAsync("commandResult");
        result.ToString(Formatting.None).Should().Be(CommandMessageRules.CommandResultJson(bcid, RemoteResultStates.Done, null, null, 1));
        var others = await CollectAsync(s.Browser.Hub, 300);
        others.Select(m => (string?)m["type"]).Should().NotContain(["toast", "sendRejected", "commandResult"], "done 不送 toast、不還原文字，結果只有一則");

        var row = Row(s, d.CommandId);
        (row.State, row.DoneAt).Should().Be((CommandStates.Done, s.Host.Clock.NowMs));
        row.ResultJson.Should().Be(CommandStateRules.ResultJson(CommandStates.Done, null, null, 1, false, s.Host.Clock.NowMs), "tier 照電腦回的");
        s.Counters.Snapshot().Done.Should().Be(1);
        s.Tracker.CompletedAt(d.CommandId).Should().Be(s.Host.Clock.NowMs, "之後 30 秒內還收輸出");
    }

    [Fact]
    public async Task rejected_stale_用電腦的訊息當toast_send的文字還回去()
    {
        await using var s = await Setup.StartAsync();
        var (bcid, d) = await SendAsync(s, "還原這段文字");
        await s.Device!.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Stale, "  這張卡片已經在電腦上處理了  "));

        var messages = await CollectUntilAsync(s.Browser.Hub, "sendRejected");
        var relevant = messages.Where(m => (string?)m["type"] is "commandResult" or "toast" or "sendRejected").ToList();
        relevant.Select(m => (string?)m["type"]).Should().Equal(["commandResult", "toast", "sendRejected"], "依序送結果、提示、還原文字");
        relevant[0].ToString(Formatting.None).Should().Be(CommandMessageRules.CommandResultJson(bcid, RemoteResultStates.Rejected, RemoteResultCodes.Stale,
            "  這張卡片已經在電腦上處理了  ", null));
        (relevant[1]["text"]!.Value<string>(), relevant[1]["level"]!.Value<string>()).Should().Be(("這張卡片已經在電腦上處理了", "error"), "電腦的說明去掉前後空白");
        (relevant[2]["conversationId"]!.Value<string>(), relevant[2]["text"]!.Value<string>()).Should().Be((Conv, "還原這段文字"));
        Row(s, d.CommandId).State.Should().Be(CommandStates.Rejected);
        s.Counters.Snapshot().Rejected.Should().Be(1);
    }

    [Fact]
    public async Task 電腦先輸出sendRejected與toast再回rejected_瀏覽器各只收到一則()
    {
        // 照 WP4.4 RejectSend 的順序：sendRejected、toast、rejected conflict（同一條連線依序送）
        await using var s = await Setup.StartAsync();
        var (_, d) = await SendAsync(s, "原文");
        await s.Device!.SendOutputAsync(new CommandOutput(d.CommandId, new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = "電腦還回來的原文" }));
        await s.Device.SendOutputAsync(new CommandOutput(d.CommandId, new JObject { ["type"] = "toast", ["text"] = "那台電腦正在換帳戶，沒有送出", ["level"] = "error" }));
        await s.Device.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Conflict, "那台電腦正在換帳戶，沒有送出"));

        var messages = (await CollectUntilAsync(s.Browser.Hub, "commandResult")).Concat(await CollectAsync(s.Browser.Hub, 500))
            .Where(m => (string?)m["type"] is "commandResult" or "toast" or "sendRejected").ToList();
        messages.Select(m => (string?)m["type"]).Should().Equal(["sendRejected", "toast", "commandResult"], "各一則：伺服器不再為同一個 cid 另送同一種提示");
        messages[0]["text"]!.Value<string>().Should().Be("電腦還回來的原文");
        messages[1]["text"]!.Value<string>().Should().Be("那台電腦正在換帳戶，沒有送出");
        s.Tracker.WasForwarded(d.CommandId, "sendRejected").Should().BeTrue();
        s.Tracker.WasForwarded(d.CommandId, "toast").Should().BeTrue();
    }

    [Fact]
    public async Task answer的sendRejected_以指令的conversationId核對()
    {
        await using var s = await Setup.StartAsync();
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "answer", conversationId = Conv, itemId = "item1", answers = new { 題目 = new[] { "答案" } }, requestDigest = Digest },
            commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        var d = await s.Device!.WaitForAsync<CommandDelivery>();

        await s.Device.SendOutputAsync(new CommandOutput(d.CommandId, new JObject { ["type"] = "sendRejected", ["conversationId"] = OtherConv, ["text"] = "別的對話" }));
        await s.Device.SendOutputAsync(new CommandOutput(d.CommandId, new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = "我的回答：\n答案" }));
        var restored = await s.Browser.Hub.WaitForAsync("sendRejected");
        (restored["conversationId"]!.Value<string>(), restored["text"]!.Value<string>()).Should().Be((Conv, "我的回答：\n答案"));
        await s.Browser.Hub.AssertNoMessageAsync("sendRejected", 300);
        s.Counters.Snapshot().DroppedOutputs.Should().Be(1, "conversationId 是別的對話的那則丟掉");

        // answer 被拒時伺服器自己不還原（文字由電腦的 sendRejected 還，D22）
        await s.Device.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Stale));
        (await s.Browser.Hub.WaitForAsync("commandResult"))["commandId"]!.Value<string>().Should().Be(bcid);
        await s.Browser.Hub.AssertNoMessageAsync("sendRejected", 300);
    }

    [Fact]
    public async Task 別台電腦回報同一個cid_不理會_列不變_ignoredResults加一()
    {
        await using var s = await Setup.StartAsync();
        var (bcid, d) = await SendAsync(s, "hi");
        using var other = await TestDevice.AddAsync(s.Host, "B 電腦");
        await using var b = await ConnectAsync(s, other);

        await b.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Done));
        await b.SendResultAsync(new CommandResult(SyncIds.NewUlid(s.Host.Clock.NowMs), RemoteResultStates.Done));
        s.Counters.Snapshot().IgnoredResults.Should().Be(2, "不是它的 cid 與不存在的 cid：靜靜略過、不丟例外");
        Row(s, d.CommandId).State.Should().Be(CommandStates.Delivered, "列不變");
        // 輸出也一樣：只收目標電腦的
        await b.SendOutputAsync(new CommandOutput(d.CommandId, new JObject { ["type"] = "toast", ["text"] = "B 冒充的提示", ["level"] = "error" }));
        s.Counters.Snapshot().DroppedOutputs.Should().Be(1);
        await s.Browser.Hub.AssertNoMessageAsync("commandResult", 300);
        s.Tracker.WasForwarded(d.CommandId, "toast").Should().BeFalse();

        await s.Device!.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Done));
        (await s.Browser.Hub.WaitForAsync("commandResult"))["commandId"]!.Value<string>().Should().Be(bcid, "目標電腦的結果照收");
    }

    [Fact]
    public async Task 沒協商cmd1的連線回報_不理會()
    {
        await using var s = await Setup.StartAsync();
        var (_, d) = await SendAsync(s, "hi");
        await using var plain = await ConnectAsync(s, s.Owner, cmd1: false);
        s.Host.Services.GetRequiredService<ConnectionRegistry>().ConnectionsOf(s.Owner.Id).Should().Contain(c => c.IsProven && c.Features.Count == 0, "夾具：同一台另一條沒協商功能的連線");

        await plain.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Done));
        s.Counters.Snapshot().IgnoredResults.Should().Be(1);
        Row(s, d.CommandId).State.Should().Be(CommandStates.Delivered);
        await s.Browser.Hub.AssertNoMessageAsync("commandResult", 300);
    }

    [Fact]
    public async Task 同一台的另一條已證明連線回報_接受()
    {
        await using var s = await Setup.StartAsync();
        var (bcid, d) = await SendAsync(s, "hi");
        await using var second = await ConnectAsync(s, s.Owner);
        await second.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Done));
        (await s.Browser.Hub.WaitForAsync("commandResult"))["commandId"]!.Value<string>().Should().Be(bcid);
        Row(s, d.CommandId).State.Should().Be(CommandStates.Done);
    }

    [Fact]
    public async Task 重複回報_只轉一次()
    {
        await using var s = await Setup.StartAsync();
        var (_, d) = await SendAsync(s, "hi");
        await s.Device!.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Done));
        s.Host.Clock.Advance(TimeSpan.FromSeconds(1));
        await s.Device.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Conflict));

        var messages = await CollectUntilAsync(s.Browser.Hub, "commandResult");
        messages.Should().ContainSingle(m => (string?)m["type"] == "commandResult");
        (await CollectAsync(s.Browser.Hub, 400)).Should().NotContain(m => (string?)m["type"] == "commandResult" || (string?)m["type"] == "toast", "第二次是遲到的結果");
        var row = Row(s, d.CommandId);
        row.State.Should().Be(CommandStates.Done);
        var late = (JArray)JObject.Parse(row.ResultJson!)["late"]!;
        late.Should().ContainSingle();
        (late[0]["state"]!.Value<string>(), late[0]["code"]!.Value<string>(), late[0]["at"]!.Value<long>())
            .Should().Be((RemoteResultStates.Rejected, RemoteResultCodes.Conflict, s.Host.Clock.NowMs));
        s.Counters.Snapshot().Should().Match<CommandCountersSnapshot>(c => c.Done == 1 && c.Rejected == 0);
    }

    // ───── 期限（D19） ─────

    [Fact]
    public async Task 一百秒沒結果_unknown_之後的done只記late不轉()
    {
        await using var s = await Setup.StartAsync();
        var (bcid, d) = await SendAsync(s, "hi");
        var maintenance = s.Host.Services.GetRequiredService<CommandMaintenance>();

        s.Host.Clock.Advance(TimeSpan.FromMilliseconds(CommandStateRules.ResultDeadlineMs - 1));
        await maintenance.SweepAsync();
        Row(s, d.CommandId).State.Should().Be(CommandStates.Delivered, "差 1 毫秒還沒到期");
        await s.Browser.Hub.AssertNoMessageAsync("commandResult", 200);

        s.Host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await maintenance.SweepAsync();
        var row = Row(s, d.CommandId);
        (row.State, row.DoneAt).Should().Be((CommandStates.Unknown, s.Host.Clock.NowMs), "iat＋100 秒剛好到期");
        var messages = await CollectUntilAsync(s.Browser.Hub, "toast");
        messages.Single(m => (string?)m["type"] == "commandResult").ToString(Formatting.None)
            .Should().Be(CommandMessageRules.CommandResultJson(bcid, RemoteResultStates.Unknown, null, null, null));
        messages.Single(m => (string?)m["type"] == "toast")["text"]!.Value<string>().Should().Be(CommandMessageRules.UnknownText);
        messages.Should().NotContain(m => (string?)m["type"] == "sendRejected", "unknown 可能已經執行，不還原文字");
        s.Counters.Snapshot().Unknown.Should().Be(1);

        await s.Device!.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Done));
        (await CollectAsync(s.Browser.Hub, 400)).Should().NotContain(m => (string?)m["type"] == "commandResult", "期限之後的結果只記成 late");
        row = Row(s, d.CommandId);
        row.State.Should().Be(CommandStates.Unknown);
        ((JArray)JObject.Parse(row.ResultJson!)["late"]!).Single()["state"]!.Value<string>().Should().Be(RemoteResultStates.Done);
    }

    [Fact]
    public async Task 期限內重連後才送回的結果_照常轉送()
    {
        await using var s = await Setup.StartAsync();
        var (bcid, d) = await SendAsync(s, "hi");
        await s.Device!.Connection.StopAsync();
        await WaitUntilAsync(() => s.Host.Services.GetRequiredService<ConnectionRegistry>().ConnectionsOf(s.Owner.Id).Count == 0, because: "第一條連線斷了");

        s.Host.Clock.Advance(TimeSpan.FromSeconds(80));
        await using var again = await ConnectAsync(s, s.Owner);
        await s.Host.Services.GetRequiredService<CommandMaintenance>().SweepAsync();
        Row(s, d.CommandId).State.Should().Be(CommandStates.Delivered, "80 秒還在期限內");
        await again.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Done));
        var result = await s.Browser.Hub.WaitForAsync("commandResult");
        (result["commandId"]!.Value<string>(), result["state"]!.Value<string>()).Should().Be((bcid, RemoteResultStates.Done));
        Row(s, d.CommandId).State.Should().Be(CommandStates.Done);
    }

    [Fact]
    public async Task tracker被擠掉的列_期限到照樣變unknown()
    {
        await using var s = await Setup.StartAsync();
        var (_, d) = await SendAsync(s, "hi");
        for (var i = 0; i < CommandTracker.MaxEntries; i++)
        {
            s.Tracker.Add(new TrackedCommand(SyncIds.NewUlid(s.Host.Clock.NowMs + i), "other", "b", "send", "d", null, null, null, null, null, FanOut: false,
                DeviceName: null, DeadlineAt: long.MaxValue));
        }
        s.Tracker.TryGet(d.CommandId, out _).Should().BeFalse("夾具：最舊的被擠掉");

        s.Host.Clock.Advance(TimeSpan.FromMilliseconds(CommandStateRules.ResultDeadlineMs));
        await s.Host.Services.GetRequiredService<CommandMaintenance>().SweepAsync();
        Row(s, d.CommandId).State.Should().Be(CommandStates.Unknown, "期限依 sync.db，不靠記憶體的追蹤");
        await s.Browser.Hub.AssertNoMessageAsync("commandResult", 300);
    }

    [Fact]
    public async Task 格式不對的結果_HubException_invalid()
    {
        await using var s = await Setup.StartAsync();
        var (bcid, d) = await SendAsync(s, "hi");
        foreach (var bad in new[]
        {
            new CommandResult(d.CommandId, RemoteResultStates.Rejected),
            new CommandResult(d.CommandId, RemoteResultStates.Done, RemoteResultCodes.Busy),
            new CommandResult(d.CommandId, "finished"),
            new CommandResult("not-a-ulid", RemoteResultStates.Done),
            new CommandResult(d.CommandId, RemoteResultStates.Done, Message: new string('長', RemoteResultRules.MaxMessageLength + 1)),
            new CommandResult(d.CommandId, RemoteResultStates.Done, Tier: 3),
        })
        {
            await FluentActions.Awaiting(() => s.Device!.SendResultAsync(bad)).Should().ThrowAsync<HubException>().WithMessage("*invalid", JsonConvert.SerializeObject(bad));
        }
        await FluentActions.Awaiting(() => s.Device!.Connection.InvokeAsync(DeviceHubMethods.CommandResult, (CommandResult?)null)).Should().ThrowAsync<HubException>()
            .WithMessage("*invalid");
        Row(s, d.CommandId).State.Should().Be(CommandStates.Delivered);

        await s.Device!.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Done));
        (await s.Browser.Hub.WaitForAsync("commandResult"))["commandId"]!.Value<string>().Should().Be(bcid, "連線照常可用");
    }

    // ───── 輸出（D20） ─────

    [Fact]
    public async Task sendRejected輸出_conversationId相同才轉_只給發起的連線_sync_db沒有新增內容()
    {
        await using var s = await Setup.StartAsync();
        await using var sibling = await BrowserHubClient.ConnectAsync(s.Host, s.Browser.Http);
        await sibling.ReadyAsync();
        var (_, d) = await SendAsync(s, "原文");
        var events = EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM events");

        await s.Device!.SendOutputAsync(new CommandOutput(d.CommandId, new JObject { ["type"] = "sendRejected", ["conversationId"] = Conv, ["text"] = "原文", ["extra"] = 1 }));
        var restored = await s.Browser.Hub.WaitForAsync("sendRejected");
        restored.ToString(Formatting.None).Should().Be(CommandMessageRules.SendRejectedJson(Conv, "原文"), "只留已知的鍵");
        await sibling.AssertNoMessageAsync("sendRejected", 300);

        var row = Row(s, d.CommandId);
        (row.State, row.ResultJson).Should().Be((CommandStates.Delivered, null), "輸出不寫 sync.db");
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM events").Should().Be(events);
    }

    [Fact]
    public async Task send的files輸出_不在白名單_丟掉_droppedOutputs加一()
    {
        await using var s = await Setup.StartAsync();
        var (_, d) = await SendAsync(s, "hi");
        await s.Device!.SendOutputAsync(new CommandOutput(d.CommandId, new JObject { ["type"] = "files", ["requestId"] = "r1", ["files"] = new JArray() }));
        await s.Device.SendOutputAsync(new CommandOutput(d.CommandId, new JObject { ["text"] = "沒有 type" }));
        await s.Browser.Hub.AssertNoMessageAsync("files", 300);
        s.Counters.Snapshot().DroppedOutputs.Should().Be(2);
    }

    [Fact]
    public async Task listFiles的files輸出_投影再改寫_參照換成本站網址並登記_結果在輸出之後()
    {
        // WP4.7 計畫步 13：CommandResults.ReceiveOutputAsync 在投影之後等改寫器（寫進 media_refs）再轉；同一條裝置連線的呼叫依序處理，結果不會超前
        await using var s = await Setup.StartAsync();
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "listFiles", conversationId = Conv, requestId = "r1", path = (string?)null }, commandId: bcid))["accepted"]!
            .Value<bool>().Should().BeTrue();
        var d = await s.Device!.WaitForAsync<CommandDelivery>();
        const string mediaRef = "01J9Q0000000000000000000ZZ";
        var entry = new JObject { ["name"] = "a.png", ["path"] = @"C:\work\a.png", ["kind"] = "image", ["size"] = 3, ["ref"] = mediaRef, ["ver"] = 7, ["url"] = "/media?p=x" };
        var plain = new JObject { ["name"] = "b.txt", ["path"] = @"C:\work\b.txt", ["kind"] = "doc", ["size"] = 1, ["ref"] = "01J9Q0000000000000000000ZY" };

        await s.Device.SendOutputAsync(new CommandOutput(d.CommandId,
            new JObject { ["type"] = "files", ["requestId"] = "r1", ["conversationId"] = Conv, ["path"] = @"C:\work", ["entries"] = new JArray(entry, plain), ["secret"] = "x" }));
        await s.Device.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Done, Tier: 1, FullAccess: false));

        var files = await s.Browser.Hub.WaitForAsync("files");
        var key = MediaKeyRules.Format(s.Owner.Id, mediaRef);
        files.ContainsKey("secret").Should().BeFalse("投影只留已知的鍵");
        var entries = (JArray)files["entries"]!;
        (entries[0]["url"]!.Value<string>(), entries[0]["thumbUrl"]!.Value<string>()).Should().Be(("/m/" + key, "/t/" + key + "?s=160&v=7"));
        ((JObject)entries[0]).ContainsKey("ref").Should().BeFalse("參照不給瀏覽器");
        entries[1]["url"]!.Type.Should().Be(JTokenType.Null, "不是圖片影片：沒有網址、也不登記");
        (await s.Browser.Hub.WaitForAsync("commandResult"))["state"]!.Value<string>().Should().Be(RemoteResultStates.Done);
        EventLogTests.Count(s.Host, $"SELECT COUNT(*) FROM media_refs WHERE device_id = '{s.Owner.Id}' AND ref = '{mediaRef}' AND purpose = '{MediaRefPurposes.List}'")
            .Should().Be(1);
        EventLogTests.Count(s.Host, "SELECT COUNT(*) FROM media_refs").Should().Be(1);
        s.Counters.Snapshot().DroppedOutputs.Should().Be(0);
    }

    [Fact]
    public async Task 檔案輸出的requestId對不上_丟掉_droppedOutputs加一()
    {
        await using var s = await Setup.StartAsync();
        (await s.Browser.Hub.CmdAsync(new { cmd = "listFiles", conversationId = Conv, requestId = "r1", path = (string?)null }, commandId: NextCommandId()))["accepted"]!
            .Value<bool>().Should().BeTrue();
        var d = await s.Device!.WaitForAsync<CommandDelivery>();
        await s.Device.SendOutputAsync(new CommandOutput(d.CommandId, new JObject { ["type"] = "files", ["requestId"] = "r2", ["conversationId"] = Conv, ["entries"] = new JArray() }));
        await s.Browser.Hub.AssertNoMessageAsync("files", 300);
        s.Counters.Snapshot().DroppedOutputs.Should().Be(1);
    }

    [Fact]
    public async Task 完成30秒後的輸出丟掉()
    {
        await using var s = await Setup.StartAsync();
        var (_, d) = await SendAsync(s, "hi");
        await s.Device!.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Done));
        await s.Browser.Hub.WaitForAsync("commandResult");

        s.Host.Clock.Advance(TimeSpan.FromMilliseconds(CommandStateRules.OutputGraceMs));
        await s.Device.SendOutputAsync(new CommandOutput(d.CommandId, new JObject { ["type"] = "toast", ["text"] = "剛好 30 秒", ["level"] = "info" }));
        (await s.Browser.Hub.WaitForAsync("toast"))["text"]!.Value<string>().Should().Be("剛好 30 秒");

        s.Host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await s.Device.SendOutputAsync(new CommandOutput(d.CommandId, new JObject { ["type"] = "toast", ["text"] = "超過 30 秒", ["level"] = "info" }));
        await s.Browser.Hub.AssertNoMessageAsync("toast", 300);
        s.Counters.Snapshot().DroppedOutputs.Should().Be(1);
    }

    [Fact]
    public async Task accountLogin輸出_accountId換回la_deviceCode只給發起的連線_稽核與記錄沒有驗證碼()
    {
        const string userCode = "WXYZ-9876";
        var logs = new CapturingLoggerProvider();
        await using var s = await Setup.StartAsync(services: sc => sc.AddSingleton<ILoggerProvider>(logs));
        await using var sibling = await BrowserHubClient.ConnectAsync(s.Host, s.Browser.Http);
        await sibling.ReadyAsync();
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogin", accountId = la, method = "deviceCode" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var d = await s.Device!.WaitForAsync<CommandDelivery>();
        s.Verify(d).Body!.Cmd["accountId"]!.Value<string>().Should().Be("acct-a");

        var expiresAt = s.Host.Clock.NowMs + 600_000;
        await s.Device.SendOutputAsync(new CommandOutput(d.CommandId, new JObject
        {
            ["type"] = "accountLogin",
            ["accountId"] = "acct-a",
            ["message"] = "請在瀏覽器輸入代碼",
            ["done"] = false,
            ["success"] = null,
            ["deviceCode"] = new JObject { ["userCode"] = userCode, ["verificationUrl"] = "https://auth.openai.com/codex/device", ["expiresAt"] = expiresAt },
        }));
        var login = await s.Browser.Hub.WaitForAsync("accountLogin");
        login["accountId"]!.Value<string>().Should().Be(la, "換回瀏覽器看到的 la_");
        login["deviceCode"]!["userCode"]!.Value<string>().Should().Be(userCode);
        login["deviceCode"]!["expiresAt"]!.Value<long>().Should().Be(expiresAt);
        await sibling.AssertNoMessageAsync("accountLogin", 300);
        await s.Device.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Done));
        await s.Browser.Hub.WaitForAsync("commandResult");

        logs.AllText().Should().NotContain(userCode, "記錄沒有驗證碼");
        foreach (var audit in await ConversationEventLogTests.AuditRowsAsync(s.Host))
            string.Join("|", audit.ActorId, audit.Target, audit.Detail).Should().NotContain(userCode, "稽核沒有驗證碼");
        await s.Host.Services.GetRequiredService<MyriagentHub.Data.SyncWriter>().CheckpointAsync();
        foreach (var file in new[] { s.Host.Paths.SyncDb, s.Host.Paths.SyncDb + "-wal" }.Where(File.Exists))
            Contains(ReadShared(file), Encoding.UTF8.GetBytes(userCode)).Should().BeFalse($"{Path.GetFileName(file)} 沒有驗證碼");
    }

    [Fact]
    public async Task 驗證網址不在白名單_整則丟掉()
    {
        await using var s = await Setup.StartAsync();
        var la = await LinkAsync(s.Host, s.Owner.Id, "acct-a");
        (await s.Browser.Hub.CmdAsync(new { cmd = "accountLogin", accountId = la, method = "deviceCode" }, commandId: NextCommandId()))["accepted"]!.Value<bool>().Should().BeTrue();
        var d = await s.Device!.WaitForAsync<CommandDelivery>();
        await s.Device.SendOutputAsync(new CommandOutput(d.CommandId, new JObject
        {
            ["type"] = "accountLogin",
            ["accountId"] = "acct-a",
            ["message"] = "到 https://auth.openai.com.evil.test/device 輸入代碼",
            ["done"] = false,
            ["success"] = null,
            ["deviceCode"] = new JObject { ["userCode"] = "ABCD-1234", ["verificationUrl"] = "https://auth.openai.com.evil.test/device", ["expiresAt"] = s.Host.Clock.NowMs + 600_000 },
        }));
        await s.Browser.Hub.AssertNoMessageAsync("accountLogin", 300);
        s.Counters.Snapshot().DroppedOutputs.Should().Be(1);
    }

    // ───── 發起的連線不在了、伺服器重啟（只寫列） ─────

    [Fact]
    public async Task 發起的連線關了_結果只寫列()
    {
        await using var s = await Setup.StartAsync();
        var (_, d) = await SendAsync(s, "hi");
        var browserConnection = s.Browser.Hub.ServerConnection!.ConnectionId;
        await s.Browser.Hub.Connection.StopAsync();
        await WaitUntilAsync(() => s.Host.Services.GetRequiredService<BrowserConnections>().Get(browserConnection) is null, because: "瀏覽器連線移除了");

        await s.Device!.SendResultAsync(new CommandResult(d.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Policy));
        Row(s, d.CommandId).State.Should().Be(CommandStates.Rejected);
        s.Tracker.CompletedAt(d.CommandId).Should().NotBeNull();
        s.Counters.Snapshot().Rejected.Should().Be(1);
    }

    [Fact]
    public async Task 伺服器重啟後的結果_只寫列()
    {
        var dir = TempDir.Create();
        try
        {
            string cid;
            TestDevice owner;
            await using (var s = await Setup.StartAsync(dataDir: dir))
            {
                cid = (await SendAsync(s, "hi")).Delivery.CommandId;
                owner = TestDevice.Paired(s.Owner.Id, s.Owner.Token, ECDsa.Create(s.Owner.Key.ExportParameters(includePrivateParameters: true)));
            }

            using (owner)
            {
                await using var host = await HubTestHost.StartAsync(dataDir: dir);
                host.Services.GetRequiredService<CommandTracker>().Count.Should().Be(0, "重啟後追蹤是空的");
                await using var device = await DeviceHubClient.ConnectAsync(host, owner.Token);
                await device.HandshakeAsync(owner, DeviceHubClient.ObservedSpki(host), new OutboxPosition(NextSeq(host, owner.Id), null, null),
                    features: [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1]);
                await device.SendResultAsync(new CommandResult(cid, RemoteResultStates.Done));

                // 啟動掃描（步 8、D28）先把非 durable、還沒結束的列標成 unknown：之後才到的 done 是遲到的，只記進 late
                var row = ConversationEventLogTests.Rows(host, $"SELECT state, result_json FROM commands WHERE command_id = '{cid}'").Single();
                row[0].Should().Be(CommandStates.Unknown, "啟動掃描標的；沒有追蹤，結果只寫列");
                var json = JObject.Parse((string)row[1]!);
                json["state"]!.Value<string>().Should().Be(CommandStates.Unknown);
                json["late"]!.Single()["state"]!.Value<string>().Should().Be(RemoteResultStates.Done);
                host.Services.GetRequiredService<CommandCounters>().Snapshot().IgnoredResults.Should().Be(0);
            }
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    // ───── 工具 ─────

    /// <summary>送一則 send，回傳瀏覽器的 commandId 與 A 收到的 CommandDelivery</summary>
    private static async Task<(string Bcid, CommandDelivery Delivery)> SendAsync(Setup s, string text)
    {
        var bcid = NextCommandId();
        (await s.Browser.Hub.CmdAsync(new { cmd = "send", conversationId = Conv, text }, commandId: bcid))["accepted"]!.Value<bool>().Should().BeTrue();
        return (bcid, await s.Device!.WaitForAsync<CommandDelivery>());
    }

    /// <summary><paramref name="device"/> 再開一條連線並交握：<paramref name="cmd1"/> 時宣告 <c>[conv1, cmd1]</c>，否則不宣告功能（第 3 階段／WP4.1 的桌面）</summary>
    private static async Task<DeviceHubClient> ConnectAsync(Setup s, TestDevice device, bool cmd1 = true)
    {
        var client = await DeviceHubClient.ConnectAsync(s.Host, device.Token);
        await client.HandshakeAsync(device, DeviceHubClient.ObservedSpki(s.Host), new OutboxPosition(NextSeq(s.Host, device.Id), null, null),
            features: cmd1 ? [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1] : null);
        return client;
    }

    private sealed record CommandRowView(string State, long? DoneAt, string? ResultJson);

    private static CommandRowView Row(Setup s, string commandId)
    {
        var row = ConversationEventLogTests.Rows(s.Host, $"SELECT state, done_at, result_json FROM commands WHERE command_id = '{commandId}'").Single();
        return new CommandRowView((string)row[0]!, (long?)row[1], (string?)row[2]);
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

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static bool Contains(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle) >= 0;
}
