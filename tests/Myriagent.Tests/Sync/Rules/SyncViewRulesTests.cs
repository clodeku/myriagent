using Myriagent.Security;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Rules;

public sealed class SyncViewRulesTests
{
    private readonly TestDevice self = TestDevice.Create("OFFICE-PC");
    private readonly TestDevice home = TestDevice.Create("HOME-PC");
    private readonly TestDevice old = TestDevice.Create("OLD-PC");
    private readonly TestDevice stranger = TestDevice.Create("STRANGER");

    private static SyncStatusSnapshot Status(PairingStatus? pairing = null, string? deviceId = "d_0123456789ABCDEFGHJKMNPQRS") =>
        new(SyncEngineState.Live, SyncStopReason.None, null, DevTrustRules.DefaultServer, 2, 1791580000000, 185000, deviceId,
            "ABCD-EFGH", "ABCD-EFGH-JKMN-PQRS", pairing, null, false);

    private DeviceUpsertPayload Up(TestDevice d, string? name = null, bool online = true, IReadOnlyDictionary<string, DeviceCliView>? cli = null) =>
        new(d.DeviceId, name ?? d.Name, d.Name, "0.3.0", "software", P256KeyRules.Fingerprint(d.PubKey), 1791500000000, 1791580000000, online,
            new DevicePolicy("Off", false), cli);

    private static AccountLinkView Link(string deviceId, string localId, string status = "ready", bool poller = false) =>
        new(deviceId, localId, "isolated", status, "已登入", "me@example.com", "org", "Claude Max", 1791600000000, null, false, true,
            new LinkSessions(1, 0, 0), 1791580000000, poller);

    private SyncCatalogView View(IReadOnlyDictionary<string, AccountUpsertPayload>? accounts = null, IReadOnlyDictionary<string, string>? localToLogical = null)
    {
        var devices = new Dictionary<string, DeviceUpsertPayload>
        {
            [self.DeviceId] = Up(self),
            [home.DeviceId] = Up(home, name: "家裡的電腦", online: false),
            [old.DeviceId] = Up(old),
            [stranger.DeviceId] = Up(stranger),
        };
        return new SyncCatalogView(
            self.DeviceId,
            accounts ?? new Dictionary<string, AccountUpsertPayload>(),
            devices,
            [self.Entry(), home.Entry(), old.Entry(1791570000000, DeviceRegistryRules.RevokedRetired)],
            localToLogical ?? new Dictionary<string, string>(),
            new Dictionary<string, DeviceRevokedPayload>());
    }

    [Fact]
    public void StatusView_欄位名與WPR一致_含deviceId()
    {
        var view = SyncViewRules.StatusView(Status());

        view.Properties().Select(p => p.Name).Should().BeEquivalentTo(
            ["state", "detail", "server", "pending", "lastSyncAt", "clockSkewMs", "deviceId", "deviceFingerprint", "hubFingerprint", "pairing", "confirm"]);
        view.Value<string>("state").Should().Be("online");
        view.Value<int>("pending").Should().Be(2);
        view.Value<string>("deviceId").Should().Be("d_0123456789ABCDEFGHJKMNPQRS");
        view["pairing"]!.Type.Should().Be(JTokenType.Null);
        view["confirm"]!.Type.Should().Be(JTokenType.Null);

        var confirm = SyncViewRules.StatusView(Status() with { State = SyncEngineState.NeedsConfirmation, ConfirmFingerprint = "WXYZ-WXYZ-WXYZ-WXYZ" });
        confirm.Value<string>("state").Should().Be("stopped");
        confirm["confirm"]!.Value<string>("kind").Should().Be("hubIdentity");
        confirm["confirm"]!.Value<string>("fingerprint").Should().Be("WXYZ-WXYZ-WXYZ-WXYZ");
    }

    [Fact]
    public void LinkedLogical_那台的本機帳戶連結到的邏輯帳戶_沒有連結是null()
    {
        var work = PullReduceRulesTests.Upsert("la_0123456789ABCDEFGHJKMNPQR1", "公司的 Claude") with
        {
            Links = [Link(self.DeviceId, "acc-1"), Link(home.DeviceId, "acc-home")],
        };
        var other = PullReduceRulesTests.Upsert("la_0123456789ABCDEFGHJKMNPQR2", "另一個 Claude") with
        {
            Links = [Link(home.DeviceId, "acc-other")],
        };
        var view = View(new Dictionary<string, AccountUpsertPayload> { [work.LogicalAccountId] = work, [other.LogicalAccountId] = other });

        // 副本只帶擁有者那台的本機帳戶 id（2026-10-25：標頭的帳戶晶片與換帳戶選單要邏輯帳戶）
        SyncViewRules.LinkedLogical(view, home.DeviceId, "acc-home").Should().Be(work.LogicalAccountId);
        SyncViewRules.LinkedLogical(view, home.DeviceId, "acc-other").Should().Be(other.LogicalAccountId);
        SyncViewRules.LinkedLogical(view, home.DeviceId, "acc-1").Should().BeNull("acc-1 是這台的，不是那台的");
        SyncViewRules.LinkedLogical(view, old.DeviceId, "acc-home").Should().BeNull();
        SyncViewRules.LinkedLogical(view, home.DeviceId, null).Should().BeNull("擁有者沒送帳戶");
        SyncViewRules.LinkedLogical(view, null, "acc-home").Should().BeNull();
    }

    [Fact]
    public void RemoteOnly_id等於logicalId_status為notLoggedIn_configDirectory為null()
    {
        var account = PullReduceRulesTests.Upsert("la_0123456789ABCDEFGHJKMNPQRS", "家裡的 Claude") with
        {
            Links = [Link(home.DeviceId, "acc-home", poller: true)],
        };
        var usage = new SharedUsage(1791580000000, "api", [new SharedUsageWindow("session", "5 小時", 85, 1791590000000, false)], null, null, null, false);

        var view = SyncViewRules.RemoteOnlyAccount(account, usage, View());

        view.Value<string>("id").Should().Be(account.LogicalAccountId);
        view.Value<string>("logicalId").Should().Be(account.LogicalAccountId);
        view.Value<bool>("remoteOnly").Should().BeTrue();
        view.Value<string>("status").Should().Be("notLoggedIn");
        view["configDirectory"]!.Type.Should().Be(JTokenType.Null);
        view.Value<bool>("isSystemDefault").Should().BeFalse();
        view["usage"]!.Value<string>("source").Should().Be("shared");
        view["usage"]!["windows"]![0]!.Value<string>("severity").Should().Be("high");
        view["sessions"]!.Value<int>("open").Should().Be(0);
        view["devices"]!.Should().HaveCount(3, "沒撤銷的三台（含不在名冊的陌生裝置），撤銷的 OLD-PC 不列");
        SyncFeatures.RemoteOnlyAccounts.Should().BeTrue("D13：WP3.5 的網頁支援與 WP3.4 的「在這台登入」都已合併");
    }

    [Fact]
    public void Decorate_未連結_logicalId為null且devices為null()
    {
        var accountView = new JObject { ["id"] = "acc-claude", ["status"] = "ready" };

        SyncViewRules.Decorate(accountView, View());

        accountView.Value<string>("logicalId").Should().BeNull("序列化成 null");
        accountView.Value<bool>("remoteOnly").Should().BeFalse();
        accountView["devices"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void Decorate_已連結_帶logicalId與矩陣()
    {
        const string logical = "la_0123456789ABCDEFGHJKMNPQRS";
        var account = PullReduceRulesTests.Upsert(logical, "Claude") with { Links = [Link(self.DeviceId, "acc-claude")] };
        var accountView = new JObject { ["id"] = "acc-claude" };

        SyncViewRules.Decorate(accountView, View(new Dictionary<string, AccountUpsertPayload> { [logical] = account }, new Dictionary<string, string> { ["acc-claude"] = logical }));

        accountView.Value<string>("logicalId").Should().Be(logical);
        accountView["devices"]!.Should().HaveCount(3, "撤銷的 OLD-PC 不列");
    }

    [Fact]
    public void Decorate_otherSessions_其他在線電腦的合計_不算這台_離線的與撤銷的()
    {
        const string logical = "la_0123456789ABCDEFGHJKMNPQRS";
        var account = PullReduceRulesTests.Upsert(logical, "Claude") with
        {
            Links =
            [
                Link(self.DeviceId, "acc-claude") with { Sessions = new LinkSessions(5, 5, 5, 5, 5, 5) },
                Link(home.DeviceId, "acc-home") with { Sessions = new LinkSessions(7, 7, 7, 7, 7, 7) },
                Link(old.DeviceId, "acc-old") with { Sessions = new LinkSessions(9, 9, 9, 9, 9, 9) },
                Link(stranger.DeviceId, "acc-b", status: "notLoggedIn") with { Sessions = new LinkSessions(8, 8, 8, 8, 8, 8) },
                Link(stranger.DeviceId, "acc-a") with { Sessions = new LinkSessions(2, 1, 1, 3, 4, 6) },
            ],
        };
        var view = View(new Dictionary<string, AccountUpsertPayload> { [logical] = account }, new Dictionary<string, string> { ["acc-claude"] = logical });
        var accountView = new JObject { ["id"] = "acc-claude", ["sessions"] = new JObject { ["open"] = 1, ["running"] = 1, ["waiting"] = 0 } };

        SyncViewRules.Decorate(accountView, view);

        JToken.DeepEquals(accountView["otherSessions"], JObject.Parse("""{"open":2,"running":1,"waiting":1,"background":3,"programs":4,"subagents":6}""")).Should().BeTrue(
            "只有在線的 STRANGER（同一台兩格取 ready 的那格）；這台自己的、離線的 HOME-PC、撤銷的 OLD-PC 不算：" + accountView["otherSessions"]);
        accountView["sessions"]!.Value<int>("running").Should().Be(1, "這台的 sessions 不動（同步鏡像把它當這台的數字上傳）");

        var unlinked = new JObject { ["id"] = "acc-other" };
        SyncViewRules.Decorate(unlinked, view);
        unlinked["otherSessions"]!.Type.Should().Be(JTokenType.Null, "沒連結");

        var remoteOnly = SyncViewRules.RemoteOnlyAccount(account, null, view);
        remoteOnly["sessions"]!.Value<int>("running").Should().Be(0);
        remoteOnly["otherSessions"]!.Value<int>("running").Should().Be(1, "這台未登入的帳戶也看得到其他電腦的執行中");
    }

    [Fact]
    public void DevicesView_撤銷的排最後且帶revoked()
    {
        var devices = SyncViewRules.DevicesView(View());

        devices.Select(d => d.Value<string>("id")).Last().Should().Be(old.DeviceId);
        devices.Last()["revoked"]!.Value<string>("reason").Should().Be("retired");
        devices.Last()["revoked"]!.Value<long>("at").Should().Be(1791570000000);
        devices.Take(3).Should().AllSatisfy(d => d["revoked"]!.Type.Should().Be(JTokenType.Null));

        var byEvent = View() with { Revoked = new Dictionary<string, DeviceRevokedPayload> { [home.DeviceId] = new(home.DeviceId, 1791575000000, "compromised") } };
        var revokedHome = SyncViewRules.DevicesView(byEvent).Single(d => d.Value<string>("id") == home.DeviceId);
        revokedHome["revoked"]!.Value<string>("reason").Should().Be("compromised", "device.revoked 也算");
        SyncViewRules.DevicesView(byEvent).TakeLast(2).Select(d => d.Value<string>("id")).Should().BeEquivalentTo([home.DeviceId, old.DeviceId]);
    }

    [Fact]
    public void DevicesView_已撤銷而且不在名冊的是已移除_不列()
    {
        // 設計 14.22：STRANGER 不在名冊；撤銷之後（目錄有 device.revoked）就是被擁有者移除了
        var removed = View() with { Revoked = new Dictionary<string, DeviceRevokedPayload> { [stranger.DeviceId] = new(stranger.DeviceId, 1791575000000, "retired") } };

        var ids = SyncViewRules.DevicesView(removed).Select(d => d.Value<string>("id")).ToList();

        ids.Should().NotContain(stranger.DeviceId);
        ids.Should().BeEquivalentTo([self.DeviceId, home.DeviceId, old.DeviceId], "名冊裡撤銷的 OLD-PC 照樣列（排最後）");
        SyncViewRules.DevicesView(View()).Select(d => d.Value<string>("id")).Should().Contain(stranger.DeviceId, "正向對照：沒撤銷的陌生裝置照樣列");

        var noRegistry = removed with { Registry = [] };
        SyncViewRules.DevicesView(noRegistry).Select(d => d.Value<string>("id")).Should().Contain(stranger.DeviceId, "還沒拿到名冊時不判斷");
    }

    [Fact]
    public void DevicesView_signedName取名冊_verified依名冊()
    {
        var devices = SyncViewRules.DevicesView(View()).ToDictionary(d => d.Value<string>("id")!);

        devices[home.DeviceId].Value<string>("name").Should().Be("家裡的電腦", "顯示名稱取 device.upsert");
        devices[home.DeviceId].Value<string>("signedName").Should().Be("HOME-PC", "名冊裡配對時的名稱");
        devices[home.DeviceId].Value<bool>("verified").Should().BeTrue();
        devices[home.DeviceId].Value<bool>("online").Should().BeFalse();
        devices[stranger.DeviceId].Value<bool>("verified").Should().BeFalse("不在名冊裡＝未驗證的裝置");
        devices[stranger.DeviceId].Value<string>("signedName").Should().BeNull();
        devices[self.DeviceId].Value<bool>("self").Should().BeTrue();
        devices[home.DeviceId].Value<bool>("self").Should().BeFalse();
        devices[self.DeviceId].Value<string>("fingerprint").Should().Be(P256KeyRules.Fingerprint(self.PubKey));
        devices[self.DeviceId].Value<string>("remoteControlLevel").Should().Be("Off");

        var onlyRegistry = View() with { Devices = new Dictionary<string, DeviceUpsertPayload>() };
        var fromRegistry = SyncViewRules.DevicesView(onlyRegistry).Single(d => d.Value<string>("id") == home.DeviceId);
        fromRegistry.Value<string>("name").Should().Be("HOME-PC", "沒有 device.upsert 時用名冊的名稱");
        fromRegistry.Value<string>("fingerprint").Should().Be(P256KeyRules.Fingerprint(home.PubKey));
    }

    [Fact]
    public void AccountDeviceView_沒有link的那台為absent_self只標這台()
    {
        const string logical = "la_0123456789ABCDEFGHJKMNPQRS";
        var account = PullReduceRulesTests.Upsert(logical, "Claude") with { Links = [Link(home.DeviceId, "acc-home", poller: true)] };
        var view = View(new Dictionary<string, AccountUpsertPayload> { [logical] = account }, new Dictionary<string, string> { ["acc-claude"] = logical });
        var accountView = new JObject { ["id"] = "acc-claude" };

        SyncViewRules.Decorate(accountView, view);

        var cells = accountView["devices"]!.ToDictionary(c => c.Value<string>("deviceId")!);
        cells[self.DeviceId].Value<string>("status").Should().Be("absent");
        cells[self.DeviceId].Value<bool>("self").Should().BeTrue();
        cells[home.DeviceId].Value<string>("status").Should().Be("ready");
        cells[home.DeviceId].Value<bool>("self").Should().BeFalse();
        cells[home.DeviceId].Value<bool>("usagePoller").Should().BeTrue();
        cells[home.DeviceId].Value<string>("deviceName").Should().Be("家裡的電腦");
        cells[home.DeviceId].Value<bool>("online").Should().BeFalse();
        cells[stranger.DeviceId].Value<string>("status").Should().Be("absent");
        cells.Should().NotContainKey(old.DeviceId);
        cells.Values.Where(c => c.Value<bool>("self")).Should().ContainSingle();
    }

    [Fact]
    public void DevicesCli_各電腦的CLI狀態()
    {
        var cli = new Dictionary<string, DeviceCliView> { ["Claude"] = new(true, "2.1.297", "native", null, false, 0, 0, null, 1) };
        var view = View() with { Devices = new Dictionary<string, DeviceUpsertPayload> { [home.DeviceId] = Up(home, cli: cli), [self.DeviceId] = Up(self) } };

        var result = SyncViewRules.DevicesCli(view);

        result.Properties().Select(p => p.Name).Should().Equal(home.DeviceId);
        result[home.DeviceId]!["Claude"]!.Value<string>("version").Should().Be("2.1.297");
    }

    [Fact]
    public void StatusView_帶local_未給時沒有local()
    {
        SyncViewRules.StatusView(Status()).ContainsKey("local").Should().BeFalse("沒給本機設定（瀏覽器模式的伺服器）時沒有 local");

        var local = new Myriagent.Core.SyncSettings { Paused = true, RemotePaused = true, RemoteControlLevel = "View", AllowRemoteFullAccess = true };
        var view = SyncViewRules.StatusView(Status(), local);

        view.Properties().Select(p => p.Name).Should().BeEquivalentTo(
            ["state", "detail", "server", "pending", "lastSyncAt", "clockSkewMs", "deviceId", "deviceFingerprint", "hubFingerprint", "pairing", "confirm", "local"]);
        var l = (JObject)view["local"]!;
        l.Properties().Select(p => p.Name).Should().Equal("paused", "remotePaused", "remoteControlLevel", "allowRemoteFullAccess");
        l.Value<bool>("paused").Should().BeTrue();
        l.Value<bool>("remotePaused").Should().BeTrue();
        l.Value<string>("remoteControlLevel").Should().Be("View", "原樣輸出 SyncSettings 的字串，和 syncSetPolicy 收的值相同");
        l.Value<bool>("allowRemoteFullAccess").Should().BeTrue();
        view.Value<string>("state").Should().Be("online", "其他欄位不受影響");
    }

    [Fact]
    public void StatusView_第4階段的選用欄位_非null才輸出()
    {
        var local = new Myriagent.Core.SyncSettings { NotifyRemoteActions = false };
        var baseline = SyncViewRules.StatusView(Status(), local);

        SyncViewRules.StatusView(Status(), local, new SyncStatusExtras()).ToString().Should().Be(baseline.ToString(), "全部是 null 時與沒給逐字相同");
        ((JObject)baseline["local"]!).ContainsKey("notifyRemoteActions").Should().BeFalse("WP4.6 之前不輸出");

        var view = SyncViewRules.StatusView(Status(), local, new SyncStatusExtras(NotifyRemoteActions: false, Backfill: new SyncBackfillProgress(12, 52428800), RemoteSupported: false));
        view.Properties().Select(p => p.Name).Should().Equal(
            "state", "detail", "server", "pending", "lastSyncAt", "clockSkewMs", "deviceId", "deviceFingerprint", "hubFingerprint", "pairing", "confirm", "local", "backfill", "remoteSupported");
        ((JObject)view["local"]!).Properties().Select(p => p.Name).Should().Equal("paused", "remotePaused", "remoteControlLevel", "allowRemoteFullAccess", "notifyRemoteActions");
        view["local"]!.Value<bool>("notifyRemoteActions").Should().BeFalse();
        ((JObject)view["backfill"]!).Properties().Select(p => p.Name).Should().Equal("conversationsLeft", "bytesLeft");
        view["backfill"]!.Value<int>("conversationsLeft").Should().Be(12);
        view["backfill"]!.Value<long>("bytesLeft").Should().Be(52428800);
        view.Value<bool>("remoteSupported").Should().BeFalse("false 也是非 null，照輸出");

        var browser = SyncViewRules.StatusView(Status(), null, new SyncStatusExtras(NotifyRemoteActions: true, RemoteSupported: true));
        browser.ContainsKey("local").Should().BeFalse("沒給本機設定就沒有 local，notifyRemoteActions 也不輸出");
        browser.ContainsKey("backfill").Should().BeFalse();
        browser.Value<bool>("remoteSupported").Should().BeTrue();
    }

    [Fact]
    public void InitRemote_帶第4階段的選用欄位()
    {
        var local = new Myriagent.Core.SyncSettings();

        var remote = SyncViewRules.InitRemote(Status(), View(), local, new SyncStatusExtras(Backfill: new SyncBackfillProgress(1, 2)));

        remote["sync"]!["backfill"]!.Value<int>("conversationsLeft").Should().Be(1);
        ((JObject)remote["sync"]!["local"]!).ContainsKey("notifyRemoteActions").Should().BeFalse();
        SyncViewRules.InitRemote(Status(), View(), local).ToString().Should().Be(SyncViewRules.InitRemote(Status(), View(), local, null).ToString());
    }

    [Fact]
    public void StatusView_pairing帶openable()
    {
        var ok = new PairingStatus("MTLZ-XFCH", "https://hub.example.com/#pair", "https://hub.example.com/#pair=MTLZ-XFCH", 1791580600000, null);
        var evil = ok with { VerificationUriComplete = "https://hub.example.com.evil.x/#pair=MTLZ-XFCH" };

        var view = SyncViewRules.StatusView(Status(ok));
        view["pairing"]!.Value<bool>("openable").Should().BeTrue();
        view["pairing"]!.Value<string>("verificationUriComplete").Should().Be(ok.VerificationUriComplete);
        var bad = SyncViewRules.StatusView(Status(evil));
        bad["pairing"]!.Value<bool>("openable").Should().BeFalse();
        bad["pairing"]!.Value<string>("verificationUriComplete").Should().Be(evil.VerificationUriComplete, "網址文字照常顯示，只是不給「在瀏覽器開啟」");
    }

    [Theory]
    [InlineData("https://hub.example.com/#pair=ABCD", "https://hub.example.com", true)]
    [InlineData("https://Hub.Example.COM/#pair=ABCD", "https://hub.example.com", true)]
    [InlineData("https://hub.example.com#pair=ABCD", "https://hub.example.com/", true)]
    [InlineData("http://hub.example.com/#pair=ABCD", "https://hub.example.com", false)]
    [InlineData("https://hub.example.com.evil.x/#pair=ABCD", "https://hub.example.com", false)]
    [InlineData("https://evil.x/#pair=ABCD", "https://hub.example.com", false)]
    [InlineData("https://user:pw@hub.example.com/#pair=ABCD", "https://hub.example.com", false)]
    [InlineData("https://user@hub.example.com/#pair=ABCD", "https://hub.example.com", false)]
    [InlineData("https://hub.example.com/pair#pair=ABCD", "https://hub.example.com", false)]
    [InlineData("https://hub.example.com/?x=1#pair=ABCD", "https://hub.example.com", false)]
    [InlineData("https://hub.example.com/#other=ABCD", "https://hub.example.com", false)]
    [InlineData("https://hub.example.com/", "https://hub.example.com", false)]
    [InlineData("https://hub.example.com:8443/#pair=ABCD", "https://hub.example.com", false)]
    [InlineData("/#pair=ABCD", "https://hub.example.com", false)]
    [InlineData("javascript:alert(1)//#pair", "https://hub.example.com", false)]
    [InlineData(null, "https://hub.example.com", false)]
    [InlineData("https://hub.example.com/#pair=ABCD", null, false)]
    [InlineData("https://localhost/#pair=X", "https://127.0.0.1:5443", true)]
    [InlineData("https://127.0.0.1:5001/#pair=X", "https://localhost:5443", true)]
    [InlineData("https://[::1]/#pair=X", "https://127.0.0.1:5443", true)]
    [InlineData("https://localhost/#pair=X", "https://hub.example.com", false)]
    [InlineData("https://hub.example.com/#pair=X", "https://127.0.0.1:5443", false)]
    [InlineData("http://localhost/#pair=X", "https://127.0.0.1:5443", false)]
    public void IsOpenablePairingUri(string? uri, string? server, bool expected)
    {
        SyncViewRules.IsOpenablePairingUri(uri, server).Should().Be(expected);
    }

    [Fact]
    public void InitRemote_帶local()
    {
        var local = new Myriagent.Core.SyncSettings { RemoteControlLevel = "Control" };

        var remote = SyncViewRules.InitRemote(Status(), View(), local);

        remote["sync"]!["local"]!.Value<string>("remoteControlLevel").Should().Be("Control");
        remote["sync"]!["local"]!.Value<bool>("paused").Should().BeFalse();
        ((JObject)SyncViewRules.InitRemote(Status(), View())["sync"]!).ContainsKey("local").Should().BeFalse();
    }

    [Fact]
    public void InitRemote_配對中也有sync()
    {
        var pairing = new PairingStatus("MTLZ-XFCH", "https://hub.example.com/#pair", "https://hub.example.com/#pair=MTLZ-XFCH", 1791580600000, null);

        var remote = SyncViewRules.InitRemote(Status(pairing, deviceId: null), null);

        remote.Value<string>("mode").Should().Be("desktop");
        remote.Value<string>("deviceId").Should().BeNull();
        remote["devices"]!.Should().BeEmpty();
        remote["sync"]!.Value<string>("state").Should().Be("pairing");
        remote["sync"]!["pairing"]!.Value<string>("userCode").Should().Be("MTLZ-XFCH");

        var paired = SyncViewRules.InitRemote(Status(), View());
        paired["devices"]!.Should().HaveCount(4);
        paired["sync"]!.Value<string>("state").Should().Be("online");
    }
}
