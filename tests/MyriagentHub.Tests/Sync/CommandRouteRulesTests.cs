using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Sync;

/// <summary>
/// 指令路由的純規則（WP4.5 計畫步 1；D1、D4～D8）：哪些指令轉給電腦、投影、對話與 Device 路由的目標、改寫目標與帳戶 id、選連線
/// </summary>
public sealed class CommandRouteRulesTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";

    private static readonly string DeviceA = "d_" + new string('A', 26);

    private static readonly string DeviceB = "d_" + new string('B', 26);

    private static readonly string DeviceC = "d_" + new string('C', 26);

    private static readonly string AccountX = "la_" + new string('X', 26);

    private static RemoteCommandSpec Spec(string name) => RemoteCommandCatalog.Find(name)!;

    private static DeviceFact? Directory(string id) =>
        id == DeviceA ? new DeviceFact(DeviceA, "A", false)
        : id == DeviceB ? new DeviceFact(DeviceB, "B", false)
        : id == DeviceC ? new DeviceFact(DeviceC, "C", true)
        : null;

    /// <summary>la_X 連結 A（本機 a1）與 B（本機 b7）</summary>
    private static readonly AccountLinkFact[] TwoLinks =
    [
        new(AccountX, DeviceA, "a1", "ready"),
        new(AccountX, DeviceB, "b7", "loggedOut"),
    ];

    [Fact]
    public void Routes_伺服器處理的不轉_其他電腦執行的都轉_WP4點7合併後沒有延後的()
    {
        // WP4.7 合併前延後的是檔案的四個；合併後（WP4.7 計畫步 13）清單是空的，型錄的指令都由電腦執行
        CommandRouteRules.Deferred.Should().BeEmpty();
        RemoteCommandCatalog.All.Count(s => CommandRouteRules.Routes(s.Name)).Should().Be(RemoteCommandCatalog.All.Count);
        foreach (var name in new[] { "newConversation", "copyItem", "Send", "takeover", "handoffCancel", "ListFiles" })
            CommandRouteRules.Routes(name).Should().BeFalse(name);
        CommandRouteRules.Routes(null).Should().BeFalse();
        foreach (var name in new[] { "send", "stop", "approve", "accountLogout", "accountLoginDevice", "cliCheck", "addUploaded", "listFiles", "fileSearch", "fileFetch" })
            CommandRouteRules.Routes(name).Should().BeTrue(name);
    }

    [Fact]
    public void Project只留cmd與型錄欄位_不改原物件()
    {
        var original = new JObject { ["cmd"] = "send", ["paneId"] = "p1", ["conversationId"] = Conv, ["text"] = "你好" };
        var projected = CommandRouteRules.Project(original, Spec("send"));
        projected.Properties().Select(p => p.Name).Should().Equal(["cmd", "conversationId", "text"]);
        projected["text"]!.Value<string>().Should().Be("你好");
        original.ContainsKey("paneId").Should().BeTrue("不改原物件");

        projected["text"] = "改了";
        original["text"]!.Value<string>().Should().Be("你好", "值是深複製");
    }

    [Fact]
    public void Project的鍵比對大小寫有別()
    {
        var original = new JObject { ["cmd"] = "send", ["ConversationId"] = Conv, ["conversationId"] = Conv, ["text"] = "x" };
        CommandRouteRules.Project(original, Spec("send")).Properties().Select(p => p.Name).Should().Equal(["cmd", "conversationId", "text"]);
    }

    [Fact]
    public void Project之後ShapeRules接受_原物件被拒()
    {
        var original = new JObject { ["cmd"] = "send", ["conversationId"] = Conv, ["text"] = "你好", ["paneId"] = "p1" };
        RemoteCommandShapeRules.Check(original).Should().NotBeNull("網頁多帶的鍵讓嚴格檢查失敗（正向對照）");
        RemoteCommandShapeRules.Check(CommandRouteRules.Project(original, Spec("send"))).Should().BeNull();
    }

    [Fact]
    public void 每個Device路由指令改寫後都過ShapeRules()
    {
        var devices = RemoteCommandCatalog.All.Where(s => s.Route == RemoteRoute.Device).ToList();
        devices.Should().Contain(s => s.Name == RemoteCommandCatalog.ConversationCreate);
        foreach (var spec in devices)
        {
            var minimal = Minimal(spec);
            RemoteCommandShapeRules.Check(minimal).Should().BeNull($"{spec.Name} 的最小物件本身要合格");
            var targeted = CommandRouteRules.WithTarget(minimal, spec, DeviceA);
            var (mapped, rejection) = CommandRouteRules.MapAccounts(targeted, spec, DeviceA, TwoLinks);
            rejection.Should().BeNull(spec.Name);
            RemoteCommandShapeRules.Check(mapped).Should().BeNull($"{spec.Name} 改寫後要通過形狀檢查");
            if (spec.Fields.Any(f => f.Kind == RemoteFieldKind.AccountId))
                mapped!["accountId"]!.Value<string>().Should().Be("a1", $"{spec.Name} 的 la_ 換成那台的本機 id");
        }

        var create = CommandRouteRules.WithTarget(Minimal(Spec(RemoteCommandCatalog.ConversationCreate)), Spec(RemoteCommandCatalog.ConversationCreate), DeviceA);
        create.ContainsKey("deviceId").Should().BeFalse("conversationCreate 的型錄沒有 deviceId");
        var logout = CommandRouteRules.WithTarget(Minimal(Spec("accountLogout")), Spec("accountLogout"), DeviceA);
        logout["deviceId"]!.Value<string>().Should().Be(DeviceA);
    }

    [Fact]
    public void WithTarget_型錄有deviceId欄位才寫入_不改原物件()
    {
        var cli = new JObject { ["cmd"] = "cliCheck" };
        CommandRouteRules.WithTarget(cli, Spec("cliCheck"), DeviceA)["deviceId"]!.Value<string>().Should().Be(DeviceA);
        cli.ContainsKey("deviceId").Should().BeFalse("回傳複本");
        CommandRouteRules.WithTarget(Minimal(Spec("conversationCreate")), Spec("conversationCreate"), DeviceA).ContainsKey("deviceId").Should().BeFalse();
        var send = new JObject { ["cmd"] = "send", ["conversationId"] = Conv, ["text"] = "x" };
        CommandRouteRules.WithTarget(send, Spec("send"), DeviceA).ContainsKey("deviceId").Should().BeFalse("Owner 路由沒有 deviceId 欄位");
    }

    [Fact]
    public void CheckConversation_對話不在是Cmd的conflict()
    {
        var rejection = CommandRouteRules.CheckConversation(null, null, "send");
        rejection.Should().Be(new RouteRejection(BrowserErrorCodes.Conflict, CmdError: true));
        CommandRouteRules.CheckConversation(null, null, "stop")!.Code.Should().Be(BrowserErrorCodes.Conflict, "stop 也一樣");
    }

    [Fact]
    public void CheckConversation_擁有者不在目錄或已撤銷是not_owner()
    {
        var owner = new ConversationOwnership(Conv, DeviceA, 3, ConversationSyncPolicies.Full);
        CommandRouteRules.CheckConversation(owner, null, "send").Should().Be(new RouteRejection(RemoteResultCodes.NotOwner, CmdError: false));
        CommandRouteRules.CheckConversation(owner, new DeviceFact(DeviceA, "A", true), "stop")!.Code.Should().Be(RemoteResultCodes.NotOwner);
        CommandRouteRules.CheckConversation(owner, new DeviceFact(DeviceB, "B", false), "send")!.Code.Should().Be(RemoteResultCodes.NotOwner, "給的不是擁有者那台");
    }

    [Theory]
    [InlineData(ConversationSyncPolicies.Metadata)]
    [InlineData(ConversationSyncPolicies.Paused)]
    [InlineData(ConversationSyncPolicies.LocalOnly)]
    [InlineData("Full")]
    [InlineData("someday")]
    public void CheckConversation_政策不是full時只收stop(string policy)
    {
        var owner = new ConversationOwnership(Conv, DeviceA, 1, policy);
        var device = new DeviceFact(DeviceA, "A", false);
        CommandRouteRules.CheckConversation(owner, device, "send").Should().Be(new RouteRejection(RemoteResultCodes.Policy, CmdError: false));
        CommandRouteRules.CheckConversation(owner, device, "approve")!.Code.Should().Be(RemoteResultCodes.Policy);
        CommandRouteRules.CheckConversation(owner, device, RemoteCommandCatalog.Stop).Should().BeNull("stop 一律可以");
    }

    [Fact]
    public void CheckConversation_full配上send可以轉()
    {
        var owner = new ConversationOwnership(Conv, DeviceA, 1, ConversationSyncPolicies.Full);
        CommandRouteRules.CheckConversation(owner, new DeviceFact(DeviceA, "A", false), "send").Should().BeNull();
    }

    [Fact]
    public void ResolveDevices_有deviceId_一台非durable()
    {
        var cmd = new JObject { ["cmd"] = "accountLogout", ["accountId"] = AccountX, ["deviceId"] = DeviceB };
        var targets = CommandRouteRules.ResolveDevices(Spec("accountLogout"), cmd, true, TwoLinks, Directory);
        targets.Kind.Should().Be(DeviceTargetKind.Single);
        targets.DeviceIds.Should().Equal([DeviceB]);
        targets.Rejection.Should().BeNull();
    }

    [Fact]
    public void ResolveDevices_deviceId已撤銷或不存在_Cmd的invalid()
    {
        foreach (var id in new[] { DeviceC, "d_" + new string('Z', 26) })
        {
            var cmd = new JObject { ["cmd"] = "cliCheck", ["deviceId"] = id };
            var targets = CommandRouteRules.ResolveDevices(Spec("cliCheck"), cmd, null, [], Directory);
            targets.Rejection.Should().Be(new RouteRejection(BrowserErrorCodes.Invalid, CmdError: true));
            targets.DeviceIds.Should().BeEmpty();
        }
    }

    [Fact]
    public void ResolveDevices_accountLogout沒有deviceId_連結的每台未撤銷電腦_依id排序_FanOut()
    {
        AccountLinkFact[] links =
        [
            new(AccountX, DeviceC, "c1", "ready"),
            new(AccountX, DeviceB, "b7", "ready"),
            new(AccountX, DeviceA, "a1", "ready"),
            new("la_" + new string('Y', 26), "d_" + new string('D', 26), "y1", "ready"),
        ];
        var cmd = new JObject { ["cmd"] = "accountLogout", ["accountId"] = AccountX };
        var targets = CommandRouteRules.ResolveDevices(Spec("accountLogout"), cmd, true, links, Directory);
        targets.Kind.Should().Be(DeviceTargetKind.FanOut);
        targets.DeviceIds.Should().Equal([DeviceA, DeviceB], "撤銷的 C、別的帳戶的電腦都不在");
        targets.Rejection.Should().BeNull();
    }

    [Fact]
    public void ResolveDevices_accountLogout沒有任何未撤銷的連結_立即invalid()
    {
        var cmd = new JObject { ["cmd"] = "accountLogout", ["accountId"] = AccountX };
        var targets = CommandRouteRules.ResolveDevices(Spec("accountLogout"), cmd, true, [new(AccountX, DeviceC, "c1", "ready")], Directory);
        targets.Rejection.Should().Be(new RouteRejection(RemoteResultCodes.Invalid, CmdError: false, CommandRouteRules.NoDevicesMessage));
    }

    [Fact]
    public void ResolveDevices_帳戶只在一台_就是那台()
    {
        var cmd = new JObject { ["cmd"] = "accountRefresh", ["accountId"] = AccountX };
        var targets = CommandRouteRules.ResolveDevices(Spec("accountRefresh"), cmd, true, [TwoLinks[1], new(AccountX, DeviceC, "c1", "ready")], Directory);
        targets.Kind.Should().Be(DeviceTargetKind.Single);
        targets.DeviceIds.Should().Equal([DeviceB], "撤銷的 C 不算");
    }

    [Fact]
    public void ResolveDevices_帳戶在兩台_not_available()
    {
        var cmd = new JObject { ["cmd"] = "accountLogin", ["accountId"] = AccountX };
        CommandRouteRules.ResolveDevices(Spec("accountLogin"), cmd, true, TwoLinks, Directory).Rejection
            .Should().Be(new RouteRejection(BrowserErrorCodes.NotAvailable, CmdError: true));
    }

    [Fact]
    public void ResolveDevices_沒有deviceId而判不出目標_not_available()
    {
        var notAvailable = new RouteRejection(BrowserErrorCodes.NotAvailable, CmdError: true);
        CommandRouteRules.ResolveDevices(Spec("cliCheck"), new JObject { ["cmd"] = "cliCheck" }, null, [], Directory).Rejection
            .Should().Be(notAvailable, "cliCheck 沒有帳戶");
        CommandRouteRules.ResolveDevices(Spec("accountRefresh"), new JObject { ["cmd"] = "accountRefresh", ["accountId"] = null }, null, TwoLinks, Directory).Rejection
            .Should().Be(notAvailable, "現在的側欄送 accountRefresh {accountId:null}");
        CommandRouteRules.ResolveDevices(Spec("accountAdd"), new JObject { ["cmd"] = "accountAdd", ["provider"] = "Claude" }, null, [], Directory).Rejection
            .Should().Be(notAvailable, "accountAdd 沒有 deviceId");
        CommandRouteRules.ResolveDevices(Spec("accountRefresh"), new JObject { ["cmd"] = "accountRefresh", ["accountId"] = "a1" }, null, TwoLinks, Directory).Rejection
            .Should().Be(notAvailable, "本機 id 判不出是哪台");
        CommandRouteRules.ResolveDevices(Spec("accountRefresh"), new JObject { ["cmd"] = "accountRefresh", ["accountId"] = AccountX }, true, [], Directory).Rejection
            .Should().Be(notAvailable, "沒有連結（不是所有電腦登出）");
    }

    [Fact]
    public void ResolveDevices_la_已刪除_Cmd的conflict()
    {
        var conflict = new RouteRejection(BrowserErrorCodes.Conflict, CmdError: true);
        CommandRouteRules.ResolveDevices(Spec("accountLogout"), new JObject { ["cmd"] = "accountLogout", ["accountId"] = AccountX }, false, TwoLinks, Directory).Rejection
            .Should().Be(conflict);
        CommandRouteRules.ResolveDevices(Spec("accountRemove"), new JObject { ["cmd"] = "accountRemove", ["accountId"] = AccountX, ["deviceId"] = DeviceA }, false, TwoLinks, Directory).Rejection
            .Should().Be(conflict, "有 deviceId 也一樣");
    }

    [Fact]
    public void ResolveDevices_accountLoginDevice的logicalAccountId已刪除_conflict()
    {
        var cmd = new JObject { ["cmd"] = "accountLoginDevice", ["logicalAccountId"] = AccountX, ["deviceId"] = DeviceA };
        CommandRouteRules.ResolveDevices(Spec("accountLoginDevice"), cmd, false, [], Directory).Rejection
            .Should().Be(new RouteRejection(BrowserErrorCodes.Conflict, CmdError: true));
    }

    [Fact]
    public void ResolveDevices_accountAdd的logicalAccountId不要求連結()
    {
        var cmd = new JObject { ["cmd"] = "accountAdd", ["provider"] = "Codex", ["logicalAccountId"] = AccountX, ["deviceId"] = DeviceA };
        var targets = CommandRouteRules.ResolveDevices(Spec("accountAdd"), cmd, true, [], Directory);
        targets.Rejection.Should().BeNull();
        targets.DeviceIds.Should().Equal([DeviceA]);
    }

    [Fact]
    public void ResolveDevices_有la_參照卻沒查存在與否_丟例外()
    {
        var cmd = new JObject { ["cmd"] = "accountLogout", ["accountId"] = AccountX };
        var act = () => CommandRouteRules.ResolveDevices(Spec("accountLogout"), cmd, null, TwoLinks, Directory);
        act.Should().Throw<ArgumentException>();
        var owner = () => CommandRouteRules.ResolveDevices(Spec("send"), new JObject { ["cmd"] = "send" }, null, [], Directory);
        owner.Should().Throw<ArgumentException>("只處理 Device 路由");
    }

    [Fact]
    public void LogicalAccountRef_la_的accountId或logicalAccountId()
    {
        CommandRouteRules.LogicalAccountRef(Spec("accountLogout"), new JObject { ["cmd"] = "accountLogout", ["accountId"] = AccountX }).Should().Be(AccountX);
        CommandRouteRules.LogicalAccountRef(Spec("accountLogout"), new JObject { ["cmd"] = "accountLogout", ["accountId"] = "a1" }).Should().BeNull("本機 id");
        CommandRouteRules.LogicalAccountRef(Spec("accountAdd"), new JObject { ["cmd"] = "accountAdd", ["logicalAccountId"] = AccountX }).Should().Be(AccountX);
        CommandRouteRules.LogicalAccountRef(Spec("accountAdd"), new JObject { ["cmd"] = "accountAdd", ["logicalAccountId"] = null }).Should().BeNull();
        CommandRouteRules.LogicalAccountRef(Spec("setAccount"), new JObject { ["cmd"] = "setAccount", ["accountId"] = AccountX }).Should().Be(AccountX);
        CommandRouteRules.LogicalAccountRef(Spec("cliCheck"), new JObject { ["cmd"] = "cliCheck" }).Should().BeNull();
    }

    [Fact]
    public void MapAccounts_la_在兩台各自換成本機id()
    {
        var cmd = new JObject { ["cmd"] = "accountLogout", ["accountId"] = AccountX };
        var (toA, _) = CommandRouteRules.MapAccounts(cmd, Spec("accountLogout"), DeviceA, TwoLinks);
        var (toB, _) = CommandRouteRules.MapAccounts(cmd, Spec("accountLogout"), DeviceB, TwoLinks);
        toA!["accountId"]!.Value<string>().Should().Be("a1");
        toB!["accountId"]!.Value<string>().Should().Be("b7");
        cmd["accountId"]!.Value<string>().Should().Be(AccountX, "回傳複本");
    }

    [Fact]
    public void MapAccounts_Owner路由的setAccount與conversationCreate也換()
    {
        var set = new JObject { ["cmd"] = "setAccount", ["conversationId"] = Conv, ["accountId"] = AccountX };
        CommandRouteRules.MapAccounts(set, Spec("setAccount"), DeviceB, TwoLinks).Cmd!["accountId"]!.Value<string>().Should().Be("b7");
        var create = Minimal(Spec("conversationCreate"));
        CommandRouteRules.MapAccounts(create, Spec("conversationCreate"), DeviceA, TwoLinks).Cmd!["accountId"]!.Value<string>().Should().Be("a1");
    }

    [Fact]
    public void MapAccounts_那台沒有連結_立即拒絕invalid帶訊息()
    {
        var cmd = new JObject { ["cmd"] = "accountLogout", ["accountId"] = AccountX, ["deviceId"] = DeviceC };
        var (mapped, rejection) = CommandRouteRules.MapAccounts(cmd, Spec("accountLogout"), DeviceC, TwoLinks);
        mapped.Should().BeNull();
        rejection.Should().Be(new RouteRejection(RemoteResultCodes.Invalid, CmdError: false, "那台電腦沒有登入這個帳戶"));
    }

    [Fact]
    public void MapAccounts_本機id與null與logicalAccountId原樣()
    {
        var local = new JObject { ["cmd"] = "accountLogout", ["accountId"] = "a1" };
        CommandRouteRules.MapAccounts(local, Spec("accountLogout"), DeviceB, TwoLinks).Cmd!["accountId"]!.Value<string>().Should().Be("a1", "本機 id 原樣");

        var refresh = new JObject { ["cmd"] = "accountRefresh", ["accountId"] = null, ["deviceId"] = DeviceA };
        var (mapped, rejection) = CommandRouteRules.MapAccounts(refresh, Spec("accountRefresh"), DeviceA, []);
        rejection.Should().BeNull();
        mapped!["accountId"]!.Type.Should().Be(JTokenType.Null, "accountRefresh 的 null＝那台的所有帳戶");

        var login = new JObject { ["cmd"] = "accountLoginDevice", ["logicalAccountId"] = AccountX, ["deviceId"] = DeviceC };
        CommandRouteRules.MapAccounts(login, Spec("accountLoginDevice"), DeviceC, []).Cmd!["logicalAccountId"]!.Value<string>()
            .Should().Be(AccountX, "logicalAccountId 不換（也不要求連結）");
    }

    [Fact]
    public void BrowserAccountId_輸出要換回的id()
    {
        CommandRouteRules.BrowserAccountId(Spec("accountAdd"), new JObject { ["cmd"] = "accountAdd", ["logicalAccountId"] = AccountX }).Should().Be(AccountX);
        CommandRouteRules.BrowserAccountId(Spec("accountAdd"), new JObject { ["cmd"] = "accountAdd" }).Should().BeNull("沒有 logicalAccountId＝原樣轉送");
        CommandRouteRules.BrowserAccountId(Spec("accountLoginDevice"), new JObject { ["cmd"] = "accountLoginDevice", ["logicalAccountId"] = AccountX }).Should().Be(AccountX);
        CommandRouteRules.BrowserAccountId(Spec("accountLogout"), new JObject { ["cmd"] = "accountLogout", ["accountId"] = AccountX }).Should().Be(AccountX);
        CommandRouteRules.BrowserAccountId(Spec("accountLogin"), new JObject { ["cmd"] = "accountLogin", ["accountId"] = "a1" }).Should().Be("a1");
        CommandRouteRules.BrowserAccountId(Spec("cliCheck"), new JObject { ["cmd"] = "cliCheck" }).Should().BeNull();
    }

    [Fact]
    public void ChooseConnection_沒有已證明的_offline()
    {
        CommandRouteRules.ChooseConnection([]).Should().BeEquivalentTo(new ConnectionChoice(null, RemoteResultCodes.Offline, []));
        var choice = CommandRouteRules.ChooseConnection([new("c1", 5, Proven: false, Cmd1: true, CanSign: true)]);
        choice.ConnectionId.Should().BeNull();
        choice.RejectCode.Should().Be(RemoteResultCodes.Offline);
    }

    [Fact]
    public void ChooseConnection_只有沒協商cmd1的_unsupported()
    {
        var choice = CommandRouteRules.ChooseConnection([new("c1", 1, true, false, true), new("c2", 2, true, false, false)]);
        choice.ConnectionId.Should().BeNull();
        choice.RejectCode.Should().Be(RemoteResultCodes.Unsupported);
        choice.Abort.Should().BeEmpty("沒協商 cmd1 的連線不送信封，不必中斷");
    }

    [Fact]
    public void ChooseConnection_cmd1但不能簽_列入Abort_沒有剩下就offline()
    {
        var choice = CommandRouteRules.ChooseConnection([new("c1", 1, true, true, false), new("c2", 2, true, true, false), new("c3", 3, true, false, true)]);
        choice.ConnectionId.Should().BeNull();
        choice.RejectCode.Should().Be(RemoteResultCodes.Offline);
        choice.Abort.Should().Equal(["c1", "c2"]);
    }

    [Fact]
    public void ChooseConnection_取Order最大的合格連線()
    {
        var choice = CommandRouteRules.ChooseConnection([new("c1", 4, true, true, true), new("c2", 9, true, true, true), new("c3", 7, true, true, true)]);
        choice.ConnectionId.Should().Be("c2");
        choice.RejectCode.Should().BeNull();
        choice.Abort.Should().BeEmpty();
    }

    [Fact]
    public void ChooseConnection_還沒證明的那條Order最大也不選()
    {
        var choice = CommandRouteRules.ChooseConnection([new("old", 3, true, true, true), new("new", 8, Proven: false, Cmd1: true, CanSign: true)]);
        choice.ConnectionId.Should().Be("old");
    }

    [Fact]
    public void ChooseConnection_不能簽的那條中斷_能簽的照送()
    {
        var choice = CommandRouteRules.ChooseConnection([new("stale", 9, true, true, false), new("ok", 2, true, true, true), new("legacy", 10, true, false, true)]);
        choice.ConnectionId.Should().Be("ok");
        choice.RejectCode.Should().BeNull();
        choice.Abort.Should().Equal(["stale"]);
    }

    [Fact]
    public void 型錄唯一durable的是accountLogout()
    {
        RemoteCommandCatalog.All.Where(s => s.DurableAllowed).Select(s => s.Name).Should().Equal([CommandRouteRules.AccountLogout]);
    }

    /// <summary>依型錄產生最小的合格物件：必填欄位加上所有帳戶欄位（帳戶用 la_X，裝置用 A）</summary>
    private static JObject Minimal(RemoteCommandSpec spec)
    {
        var o = new JObject { ["cmd"] = spec.Name };
        foreach (var field in spec.Fields)
        {
            if (!field.Required && field.Kind != RemoteFieldKind.AccountId)
                continue;
            o[field.Name] = field.Kind switch
            {
                RemoteFieldKind.ConversationId => Conv,
                RemoteFieldKind.ItemId => "item1",
                RemoteFieldKind.Text => "x",
                RemoteFieldKind.Choice => field.Values![0],
                RemoteFieldKind.Bool => false,
                RemoteFieldKind.Digest => "rd1:" + new string('0', 64),
                RemoteFieldKind.AccountId => AccountX,
                RemoteFieldKind.LogicalAccountId => AccountX,
                RemoteFieldKind.DeviceId => DeviceA,
                RemoteFieldKind.RequestId => "r1",
                RemoteFieldKind.Answers => new JObject { ["q1"] = new JArray("a") },
                RemoteFieldKind.Uploads => new JArray(new JObject { ["uploadId"] = "u1", ["name"] = "a.txt", ["size"] = 1, ["sha256"] = new string('a', 64) }),
                RemoteFieldKind.OptionValue => "x",
                _ => throw new ArgumentOutOfRangeException(nameof(spec), field.Kind, "未知的欄位型別"),
            };
        }

        return o;
    }
}
