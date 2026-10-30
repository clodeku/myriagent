using Myriagent.Agents;
using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Realtime;

/// <summary>伺服器草稿上的遠端指令的純函式規則（WP4.5 計畫步 9；D33、K8）</summary>
public sealed class DraftCommandRulesTests
{
    private const string DraftId = "d01J9Q0000000000000000000AA";

    private const string LaA = "la_0000000000000000000000000A";

    private const string LaB = "la_0000000000000000000000000B";

    private const string LaC = "la_0000000000000000000000000C";

    private static readonly DraftAccount A = new(LaA, AgentProvider.Claude);

    private static readonly DraftAccount B = new(LaB, AgentProvider.Codex);

    private static readonly DraftAccount C = new(LaC, AgentProvider.Claude);

    private static readonly IReadOnlyList<DraftAccount> Accounts = [A, B, C];

    private static BrowserDraft Draft => new(DraftId, LaA, AgentProvider.Claude);

    // ───── Apply ─────

    [Fact]
    public void setOption的model寫進草稿()
    {
        var (draft, error) = Apply(Draft, new { cmd = "setOption", conversationId = DraftId, key = "model", value = " claude-opus-4 " });
        error.Should().BeNull();
        draft!.Model.Should().Be("claude-opus-4", "去掉前後空白（同桌面）");
        draft.Id.Should().Be(DraftId);

        // 「預設」（default、空字串、空白）回到 null；強度同一套
        Apply(draft, new { cmd = "setOption", conversationId = DraftId, key = "model", value = "default" }).Draft!.Model.Should().BeNull();
        Apply(draft, new { cmd = "setOption", conversationId = DraftId, key = "model", value = "" }).Draft!.Model.Should().BeNull();
        Apply(Draft, new { cmd = "setOption", conversationId = DraftId, key = "effort", value = "high" }).Draft!.Effort.Should().Be("high");
        Apply(Draft with { Effort = "high" }, new { cmd = "setOption", conversationId = DraftId, key = "effort", value = "  " }).Draft!.Effort.Should().BeNull();
        // 不認得的 key、超過 128 字的值：形狀不合格
        Apply(Draft, new { cmd = "setOption", conversationId = DraftId, key = "theme", value = "x" }).Error.Should().Be(BrowserErrorCodes.Invalid);
        Apply(Draft, new { cmd = "setOption", conversationId = DraftId, key = "model", value = new string('m', 129) }).Error.Should().Be(BrowserErrorCodes.Invalid);
        Apply(Draft, new { cmd = "setOption", conversationId = DraftId, key = "model", value = 5 }).Error.Should().Be(BrowserErrorCodes.Invalid);
    }

    [Fact]
    public void permission只收這個代理的模式()
    {
        Apply(Draft, new { cmd = "setOption", conversationId = DraftId, key = "permission", value = "AcceptEdits" }).Draft!.PermissionMode.Should().Be("AcceptEdits");
        Apply(Draft with { PermissionMode = "Plan" }, new { cmd = "setOption", conversationId = DraftId, key = "permission", value = "default" }).Draft!.PermissionMode
            .Should().BeNull("回到預設（每步詢問）");
        // Codex 沒有「自動接受編輯」「規劃模式」：網頁的選單照 header.permissionModes，不在裡面的回 invalid（不靜默改成別的模式）
        var codex = new BrowserDraft(DraftId, LaB, AgentProvider.Codex);
        Apply(codex, new { cmd = "setOption", conversationId = DraftId, key = "permission", value = "AcceptEdits" }).Error.Should().Be(BrowserErrorCodes.Invalid);
        Apply(codex, new { cmd = "setOption", conversationId = DraftId, key = "permission", value = "Auto" }).Draft!.PermissionMode.Should().Be("Auto");
        // 不是名稱（數字、大小寫不同）：形狀不合格
        Apply(Draft, new { cmd = "setOption", conversationId = DraftId, key = "permission", value = "1" }).Error.Should().Be(BrowserErrorCodes.Invalid);
        Apply(Draft, new { cmd = "setOption", conversationId = DraftId, key = "permission", value = "ask" }).Error.Should().Be(BrowserErrorCodes.Invalid);
    }

    [Fact]
    public void setAccount不是目錄的帳戶_invalid()
    {
        Apply(Draft, new { cmd = "setAccount", conversationId = DraftId, accountId = "la_000000000000000000000000ZZ" }).Error.Should().Be(BrowserErrorCodes.Invalid);
        Apply(Draft, new { cmd = "setAccount", conversationId = DraftId, accountId = "acct-1" }).Error.Should().Be(BrowserErrorCodes.Invalid, "本機 id 不在帳戶快照裡");
        Apply(Draft, new { cmd = "setAccount", conversationId = DraftId }).Error.Should().Be(BrowserErrorCodes.Invalid);

        // 同一家：只換帳戶，模型、強度、權限模式照舊
        var tuned = Draft with { Model = "m", Effort = "high", PermissionMode = "Plan" };
        var same = Apply(tuned, new { cmd = "setAccount", conversationId = DraftId, accountId = LaC }).Draft!;
        (same.AccountId, same.Provider, same.Model, same.Effort, same.PermissionMode).Should().Be((LaC, AgentProvider.Claude, "m", "high", "Plan"));
        // 換到另一家：同桌面，模型、強度、權限模式回到預設
        var other = Apply(tuned, new { cmd = "setAccount", conversationId = DraftId, accountId = LaB }).Draft!;
        (other.AccountId, other.Provider, other.Model, other.Effort, other.PermissionMode).Should().Be((LaB, AgentProvider.Codex, null, null, null));
    }

    [Fact]
    public void setFolder超過上限_invalid()
    {
        Apply(Draft, new { cmd = "setFolder", conversationId = DraftId, path = @"C:\" + new string('a', 4094) }).Error.Should().Be(BrowserErrorCodes.Invalid, "4097 字");
        Apply(Draft, new { cmd = "setFolder", conversationId = DraftId, path = "relative" }).Error.Should().Be(BrowserErrorCodes.Invalid, "只收本機磁碟的完整路徑");
        Apply(Draft, new { cmd = "setFolder", conversationId = DraftId, path = @"\\server\share" }).Error.Should().Be(BrowserErrorCodes.Invalid, "UNC 也不收");

        // 正向對照：4096 字以內的完整路徑照收（去掉前後空白），resetFolder 清掉
        var path = @"C:\" + new string('a', 4093);
        var set = Apply(Draft, new { cmd = "setFolder", conversationId = DraftId, path }).Draft!;
        (set.WorkingDirectory, set.CustomFolder).Should().Be((path, true));
        Apply(Draft, new { cmd = "setFolder", conversationId = DraftId, path = @"  D:\work  " }).Draft!.WorkingDirectory.Should().Be(@"D:\work");
        var reset = Apply(set, new { cmd = "resetFolder", conversationId = DraftId }).Draft!;
        (reset.WorkingDirectory, reset.CustomFolder).Should().Be((null, false));
    }

    [Fact]
    public void rename去掉空白_最多80字_空的不改()
    {
        Apply(Draft, new { cmd = "rename", conversationId = DraftId, title = "  新標題  " }).Draft!.Title.Should().Be("新標題");
        Apply(Draft, new { cmd = "rename", conversationId = DraftId, title = new string('長', 100) }).Draft!.Title.Should().Be(new string('長', 80));
        var named = Draft with { Title = "原本" };
        Apply(named, new { cmd = "rename", conversationId = DraftId, title = "   " }).Draft!.Title.Should().Be("原本", "空的不改（同桌面）");
        Apply(Draft, new { cmd = "rename", conversationId = DraftId, title = 1 }).Error.Should().Be(BrowserErrorCodes.Invalid);
        // 標頭照改的標題
        BrowserDraftRules.Header(named)["title"]!.Value<string>().Should().Be("原本");
        BrowserDraftRules.Header(Draft)["title"]!.Value<string>().Should().Be(BrowserDraftRules.Title);
    }

    [Fact]
    public void 草稿上的stop或approve_invalid()
    {
        foreach (var cmd in new object[]
        {
            new { cmd = "stop", conversationId = DraftId },
            new { cmd = "approve", conversationId = DraftId, itemId = "i1", choice = "allow", requestDigest = "rd1:" + new string('0', 64) },
            new { cmd = "delete", conversationId = DraftId },
            new { cmd = "send", conversationId = DraftId, text = "hi" },
            new { cmd = "accountRefresh", accountId = LaA },
            new { cmd = "notACommand", conversationId = DraftId },
        })
        {
            var name = (string)JObject.FromObject(cmd)["cmd"]!;
            DraftCommandRules.Apply(Draft, name, JObject.FromObject(cmd), Accounts).Should().Be(((BrowserDraft?)null, BrowserErrorCodes.Invalid), name);
        }
    }

    [Fact]
    public void Apply不改原物件_網頁多帶的鍵忽略()
    {
        var cmd = JObject.FromObject(new { cmd = "rename", conversationId = DraftId, title = "t", paneId = "p1" });
        var before = cmd.ToString();
        DraftCommandRules.Apply(Draft, "rename", cmd, Accounts).Draft!.Title.Should().Be("t");
        cmd.ToString().Should().Be(before);
    }

    // ───── ConversationCreate ─────

    [Fact]
    public void send組出的conversationCreate過ShapeRules_沒有deviceId()
    {
        var draft = Draft with { Model = "m1", Effort = "high", PermissionMode = "Plan", WorkingDirectory = @"C:\work", CustomFolder = true, Title = "標題", DeviceId = SyncIds.NewDeviceId() };
        var create = DraftCommandRules.ConversationCreate(draft, "第一則");
        create.ToString(Newtonsoft.Json.Formatting.None).Should().Be(
            """{"cmd":"conversationCreate","accountId":"la_0000000000000000000000000A","permissionMode":"Plan","model":"m1","effort":"high","folder":"C:\\work","title":"標題","text":"第一則"}""");
        RemoteCommandShapeRules.Check(create).Should().BeNull();
        create.Property("deviceId").Should().BeNull("型錄沒有這個欄位（目標只在信封的 dev）");
        create.Property("conversationId").Should().BeNull();

        // 沒有的欄位省略、空字串的 text 省略（電腦當成沒有文字，契約 C17）、型錄的 title 不收 null
        var minimal = DraftCommandRules.ConversationCreate(Draft, "");
        minimal.Properties().Select(p => p.Name).Should().Equal("cmd", "accountId", "permissionMode");
        RemoteCommandShapeRules.Check(minimal).Should().BeNull();
        // 16,000 字的第一則照收
        RemoteCommandShapeRules.Check(DraftCommandRules.ConversationCreate(Draft, new string('字', RemoteCommandShapeRules.MaxAgentTextLength))).Should().BeNull();
    }

    [Fact]
    public void 沒選權限模式是Ask()
    {
        DraftCommandRules.ConversationCreate(Draft, "hi")["permissionMode"]!.Value<string>().Should().Be(nameof(AgentPermissionMode.Ask), "最低的層級，不用電腦的預設（可能是完全自動）");
        DraftCommandRules.ConversationCreate(new BrowserDraft(DraftId, LaB, AgentProvider.Codex), "hi")["permissionMode"]!.Value<string>().Should().Be("Ask");
        // 同標頭顯示的值：Antigravity 沒有每步詢問，經代理種類正規化成自動接受編輯
        var agy = new BrowserDraft(DraftId, LaA, AgentProvider.Antigravity);
        DraftCommandRules.ConversationCreate(agy, "hi")["permissionMode"]!.Value<string>().Should().Be("AcceptEdits")
            .And.Be(BrowserDraftRules.Header(agy)["permission"]!.Value<string>());
    }

    // ───── DefaultDevice ─────

    private static readonly string DevA = SyncIds.NewDeviceId(), DevB = SyncIds.NewDeviceId(), DevC = SyncIds.NewDeviceId(), DevD = SyncIds.NewDeviceId();

    [Fact]
    public void DefaultDevice取ready_未撤銷_有cmd1連線_Order最大的那台()
    {
        // A：ready、cmd1、Order 5；B：ready、cmd1、Order 9（最大）；C：ready、cmd1、Order 20 但已撤銷；D：不是 ready、cmd1、Order 30
        var links = new[] { Link(DevA, "ready"), Link(DevB, "ready"), Link(DevC, "ready"), Link(DevD, "loggedOut"), new AccountLinkFact(LaB, DevA, "x", "ready") };
        var connections = new Dictionary<string, IReadOnlyList<ConnectionFact>>
        {
            [DevA] = [Conn("a1", 5)],
            [DevB] = [Conn("b1", 3), Conn("b2", 9), Conn("b3", 50, proven: false)],
            [DevC] = [Conn("c1", 20)],
            [DevD] = [Conn("d1", 30)],
        };
        DraftCommandRules.DefaultDevice(LaA, links, Device(revoked: DevC), Connections(connections)).Should().Be(new DraftDeviceChoice(DevB, null),
            "還沒證明的那條 Order 最大也不算；撤銷的、不是 ready 的不選");

        // 只剩 A 在線：A（突變：取最小的 Order 會在上一段選到 A）
        connections[DevB] = [];
        DraftCommandRules.DefaultDevice(LaA, links, Device(revoked: DevC), Connections(connections)).DeviceId.Should().Be(DevA);
    }

    [Fact]
    public void DefaultDevice都沒有時依情況立即拒絕()
    {
        var none = Device(revoked: null);
        // 沒有任何連結（或只連到撤銷的電腦）：invalid
        DraftCommandRules.DefaultDevice(LaA, [], none, Connections(new())).Rejection.Should()
            .Be(new RouteRejection(RemoteResultCodes.Invalid, CmdError: false, CommandRouteRules.NoDevicesMessage));
        DraftCommandRules.DefaultDevice(LaA, [Link(DevA, "ready")], Device(revoked: DevA), Connections(new() { [DevA] = [Conn("a1", 1)] })).Rejection!.Message
            .Should().Be(CommandRouteRules.NoDevicesMessage);

        // 在線而沒有協商 cmd1：unsupported（要先更新）
        DraftCommandRules.DefaultDevice(LaA, [Link(DevA, "ready"), Link(DevB, "ready")], none,
                Connections(new() { [DevA] = [Conn("a1", 1, cmd1: false)], [DevB] = [] })).Rejection!.Code
            .Should().Be(RemoteResultCodes.Unsupported);
        // 在線、cmd1，但連結不是 ready：那台沒有登入這個帳戶
        DraftCommandRules.DefaultDevice(LaA, [Link(DevA, "loggedOut")], none, Connections(new() { [DevA] = [Conn("a1", 1)] })).Rejection.Should()
            .Be(new RouteRejection(RemoteResultCodes.Invalid, CmdError: false, CommandRouteRules.NoLinkMessage));
        // 都不在線（含還沒證明的連線）：offline
        DraftCommandRules.DefaultDevice(LaA, [Link(DevA, "ready")], none, Connections(new() { [DevA] = [Conn("a1", 1, proven: false)] })).Rejection!.Code
            .Should().Be(RemoteResultCodes.Offline);
    }

    private static (BrowserDraft? Draft, string? Error) Apply(BrowserDraft draft, object cmd)
    {
        var o = JObject.FromObject(cmd);
        return DraftCommandRules.Apply(draft, (string)o["cmd"]!, o, Accounts);
    }

    private static AccountLinkFact Link(string deviceId, string status) => new(LaA, deviceId, "acct-" + deviceId[^4..], status);

    private static ConnectionFact Conn(string id, long order, bool proven = true, bool cmd1 = true) => new(id, order, proven, cmd1, CanSign: true);

    private static Func<string, DeviceFact?> Device(string? revoked) => id => new DeviceFact(id, "電腦 " + id[^4..], Revoked: id == revoked);

    private static Func<string, IReadOnlyList<ConnectionFact>> Connections(Dictionary<string, IReadOnlyList<ConnectionFact>> map) =>
        id => map.TryGetValue(id, out var list) ? list : [];
}
