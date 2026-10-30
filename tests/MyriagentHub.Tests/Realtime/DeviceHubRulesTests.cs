using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Update;
using MyriagentHub.Realtime;

namespace MyriagentHub.Tests.Realtime;

/// <summary>裝置 hub 的純函式規則：Hello 的版本閘門與格式、呼叫的關卡、連線啟用的功能、outbox 位置、裝置權杖輪換</summary>
public sealed class DeviceHubRulesTests
{
    [Fact]
    public void Prove補送Hello之後的通知()
    {
        // HUB-1：Hello 在排空中就回 draining，所以 Prove 時看到排空＝Hello 之後才開始；latestApp 只在比 Hello 回報的新時補送
        var v300 = new AppReleaseInfo("0.2.300-ab12cd3", "0.2.300");
        var v301 = new AppReleaseInfo("0.2.301-bc23de4", "0.2.301");

        DeviceHubRules.CatchUpNotices(false, 30, v300, v300).Should().BeEmpty();
        DeviceHubRules.CatchUpNotices(false, 30, null, null).Should().BeEmpty();
        DeviceHubRules.CatchUpNotices(true, 45, v300, v300).Should().Equal(new ServerNotice(ServerNoticeKinds.Shutdown, 45));
        DeviceHubRules.CatchUpNotices(false, 30, null, v300).Should().Equal(new ServerNotice(ServerNoticeKinds.AppRelease, Release: v300.Release));
        DeviceHubRules.CatchUpNotices(false, 30, v300, v301).Should().Equal(new ServerNotice(ServerNoticeKinds.AppRelease, Release: v301.Release));
        DeviceHubRules.CatchUpNotices(true, 45, v300, v301).Should().Equal(
            new ServerNotice(ServerNoticeKinds.Shutdown, 45), new ServerNotice(ServerNoticeKinds.AppRelease, Release: v301.Release));
        DeviceHubRules.CatchUpNotices(false, 30, v301, v300).Should().BeEmpty("撤回讓最新版變舊：沒有新版可通知（Hello 之後的 latestApp 也一樣不通知）");
        DeviceHubRules.CatchUpNotices(false, 30, v301, null).Should().BeEmpty("全部撤回");
    }

    [Fact]
    public void Hello的上限_只在協商出live2時帶即時增量的頻率()
    {
        // 沒協商出 live2 的連線（舊桌面、只更新的連線）線上省略 maxLiveDeltaHz：HelloResponse 和第 3 階段逐位元組相同
        SyncLimits.Default.MaxLiveDeltaHz.Should().Be(8);
        DeviceHubRules.HelloLimits(SyncLimits.Default, [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Live2])
            .Should().Be(SyncLimits.Default);
        DeviceHubRules.HelloLimits(SyncLimits.Default, [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1])
            .Should().Be(SyncLimits.Default with { MaxLiveDeltaHz = null });
        DeviceHubRules.HelloLimits(SyncLimits.Default, []).MaxLiveDeltaHz.Should().BeNull();
        DeviceHubRules.HelloLimits(SyncLimits.Default, DeviceHubRules.ConnectionFeatures(HelloGate.UpdateOnly, [DeviceFeatureRules.Live2, DeviceFeatureRules.Update1]))
            .MaxLiveDeltaHz.Should().BeNull("只更新的連線不啟用第 4 階段的功能");
        SyncJson.Serialize(DeviceHubRules.HelloLimits(SyncLimits.Default, [])).Should().NotContain("maxLiveDeltaHz");
    }

    [Fact]
    public void Hello的格式()
    {
        var ok = new HelloRequest(1, SyncIds.NewDeviceId(), "boot-1", "0.1.0", "PC", "User", HandshakeRules.NewNonce(),
            Base64UrlRules.Encode(new byte[32]), null, new OutboxPosition(1, null, null), new Dictionary<string, long>(), new DevicePolicy("Control", false));
        DeviceHubRules.CheckHello(ok).Should().BeNull();
        DeviceHubRules.CheckHello(ok with { BootId = "" }).Should().NotBeNull();
        DeviceHubRules.CheckHello(ok with { BootId = "a b" }).Should().NotBeNull();
        DeviceHubRules.CheckHello(ok with { MachineName = new string('x', 129) }).Should().NotBeNull();
        DeviceHubRules.CheckHello(ok with { WindowsUser = "a\u0000b" }).Should().NotBeNull();
        DeviceHubRules.CheckHello(ok with { NonceD = Base64UrlRules.Encode(new byte[16]) }).Should().NotBeNull("nonce 是 32 位元組");
        DeviceHubRules.CheckHello(ok with { ObservedSpkiSha256 = "x" }).Should().NotBeNull();
        DeviceHubRules.CheckHello(ok with { Outbox = null! }).Should().NotBeNull();
        DeviceHubRules.CheckHello(ok with { Cursors = Enumerable.Range(0, 17).ToDictionary(i => "c" + i, _ => 0L) }).Should().NotBeNull();
        DeviceHubRules.CheckHello(ok with { Cursors = new Dictionary<string, long> { ["catalog"] = -1 } }).Should().NotBeNull();
        DeviceHubRules.CheckHello(ok with { Policy = new DevicePolicy("<script>", false) }).Should().NotBeNull();
        DeviceHubRules.CheckHello(ok with { Policy = null! }).Should().NotBeNull();
    }

    [Theory]
    [InlineData(1L, null, null, true)]
    [InlineData(10L, 5L, 3L, true)]
    [InlineData(10L, 5L, 5L, true)]
    [InlineData(10L, null, 3L, true)]
    [InlineData(0L, null, null, false)]
    [InlineData(10L, 10L, 3L, false)]
    [InlineData(10L, 5L, null, false)]
    [InlineData(10L, 5L, 6L, false)]
    [InlineData(10L, null, 10L, false)]
    [InlineData(10L, 0L, 0L, false)]
    public void outbox位置_保留的在未確認之前_都在next之前(long next, long? unacked, long? retained, bool expected) =>
        DeviceHubRules.IsOutbox(new OutboxPosition(next, unacked, retained)).Should().Be(expected);

    [Fact]
    public void 權杖輪換_30天換新_用舊權杖連上再發一次()
    {
        const long day = 24L * 60 * 60 * 1000;
        DeviceHubRules.RotationFor(false, 0, 29 * day).Should().Be(TokenRotation.None);
        DeviceHubRules.RotationFor(false, 0, 30 * day).Should().Be(TokenRotation.Rotate);
        DeviceHubRules.RotationFor(true, 30 * day, 30 * day + 1).Should().Be(TokenRotation.ReissueKeepPrevious);
    }

    [Fact]
    public void Hello閘門真值表()
    {
        // HelloGate 是 internal：public 測試方法的參數不能用它（CS0051），所以用 [Fact] 加表格，不用 [Theory]
        IReadOnlyList<string> update = [DeviceFeatureRules.Update1];
        IReadOnlyList<string> conv = [DeviceFeatureRules.Conv1];
        IReadOnlyList<string> updateAndFuture = [DeviceFeatureRules.Update1, "future9"];
        var cases = new (int Protocol, string? App, IReadOnlyList<string>? Features, int MinSupported, int Current, HelloGate Expected, string Because)[]
        {
            (1, "0.2.300", null, 1, 1, HelloGate.Full, "支援範圍內、版本夠新"),
            (2, "0.2.300", null, 1, 1, HelloGate.Protocol, "協定比伺服器新（伺服器回滾）"),
            (2, "0.2.300", update, 1, 1, HelloGate.Protocol, "協定比伺服器新：宣告了 update1 也一樣"),
            (1, "0.0.9", null, 1, 1, HelloGate.Protocol, "app 太舊、沒宣告（U3 之前的桌面）"),
            (1, "0.0.9", conv, 1, 1, HelloGate.Protocol, "app 太舊、只宣告別的功能"),
            (1, "0.0.9", update, 1, 1, HelloGate.UpdateOnly, "app 太舊、宣告了 update1"),
            (1, "亂碼", update, 1, 1, HelloGate.UpdateOnly, "版本號解析不了當成太舊"),
            (1, "亂碼", null, 1, 1, HelloGate.Protocol, "版本號解析不了、沒宣告"),
            (0, "0.2.300", update, 1, 1, HelloGate.Protocol, "協定小於 1"),
            (1, "0.2.300", update, 2, 3, HelloGate.UpdateOnly, "協定比支援的最低版本舊（伺服器升版之後）"),
            (1, "0.2.300", updateAndFuture, 1, 1, HelloGate.Full, "版本夠新時宣告 update1 也是完整連線；不認得的名稱不影響"),
        };
        foreach (var c in cases)
            DeviceHubRules.Gate(c.Protocol, c.App, c.Features, "0.1.0", c.MinSupported, c.Current).Should().Be(c.Expected, c.Because);

        // 預設參數＝目前的協定範圍（SyncProtocol.MinSupportedVersion～Version）
        DeviceHubRules.Gate(SyncProtocol.Version, "0.2.300", null, "0.1.0").Should().Be(HelloGate.Full);
        DeviceHubRules.Gate(SyncProtocol.Version + 1, "0.2.300", update, "0.1.0").Should().Be(HelloGate.Protocol);
        DeviceHubRules.Gate(SyncProtocol.Version, "0.0.9", ["UPDATE1"], "0.1.0").Should().Be(HelloGate.Protocol, "功能名稱區分大小寫");
    }

    [Fact]
    public void appVersion格式不對回invalid()
    {
        // 宣告了 update1 的舊版會走到 Prove（版本號解析不了也一樣），appVersion 會寫進 device_streams、device.upsert 與版本頁
        var ok = ValidHello();
        DeviceHubRules.CheckHello(ok with { AppVersion = "亂碼" }).Should().BeNull("解析不了的版本號由閘門當成太舊，格式照收");
        DeviceHubRules.CheckHello(ok with { AppVersion = new string('9', AppVersionRules.MaxLength) }).Should().BeNull("64 字");
        DeviceHubRules.CheckHello(ok with { AppVersion = new string('9', AppVersionRules.MaxLength + 1) }).Should().Be("appVersion 不正確", "超過 64 字");
        DeviceHubRules.CheckHello(ok with { AppVersion = "0.1." + (char)7 }).Should().Be("appVersion 不正確", "控制字元");
        DeviceHubRules.CheckHello(ok with { AppVersion = "" }).Should().Be("appVersion 不正確", "空字串");
        DeviceHubRules.CheckHello(ok with { AppVersion = null! }).Should().Be("appVersion 不正確", "null");
    }

    [Fact]
    public void 呼叫的關卡_只更新的連線Prove之後只放行RefreshAccessToken與Heartbeat()
    {
        foreach (var method in DeviceHubMethods.DeviceToServer)
        {
            var handshake = method is DeviceHubMethods.Hello or DeviceHubMethods.Prove;
            var allowed = method is DeviceHubMethods.RefreshAccessToken or DeviceHubMethods.Heartbeat;
            DeviceHubRules.CheckInvocation(method, proven: false, updateOnly: false).Should().Be(handshake ? null : SyncErrorCodes.NotProven, method);
            DeviceHubRules.CheckInvocation(method, proven: false, updateOnly: true).Should().Be(handshake ? null : SyncErrorCodes.NotProven, "Prove 之前照舊：" + method);
            DeviceHubRules.CheckInvocation(method, proven: true, updateOnly: false).Should().BeNull(method);
            DeviceHubRules.CheckInvocation(method, proven: true, updateOnly: true).Should().Be(allowed ? null : SyncErrorCodes.UpdateRequired,
                "只更新的連線是白名單（交握方法與第 4 階段的方法也擋）：" + method);
        }
        DeviceHubRules.CheckInvocation(DeviceHubMethods.Notify, proven: true, updateOnly: true).Should().Be(SyncErrorCodes.Invalid, "清單外的照舊先判 invalid");
        DeviceHubRules.CheckInvocation(null, proven: true, updateOnly: true).Should().Be(SyncErrorCodes.Invalid);
        DeviceHubRules.CheckInvocation(DeviceHubMethods.Pull, proven: true).Should().BeNull("updateOnly 預設 false：既有兩個參數的呼叫不變");
    }

    [Fact]
    public void 連線啟用的功能_只更新的連線只留update1()
    {
        IReadOnlyList<string> negotiated = [DeviceFeatureRules.Cmd1, DeviceFeatureRules.Update1, DeviceFeatureRules.Conv1];
        DeviceHubRules.ConnectionFeatures(HelloGate.Full, negotiated).Should().Equal(negotiated);
        DeviceHubRules.ConnectionFeatures(HelloGate.UpdateOnly, negotiated).Should().Equal(new[] { DeviceFeatureRules.Update1 }, "第 4 階段的功能一律不啟用");
        DeviceHubRules.ConnectionFeatures(HelloGate.UpdateOnly, []).Should().BeEmpty("伺服器沒宣告 update1 時協商出的是空的");
    }

    private static HelloRequest ValidHello() => new(1, SyncIds.NewDeviceId(), "boot-1", "0.1.0", "PC", "User", HandshakeRules.NewNonce(),
        Base64UrlRules.Encode(new byte[32]), null, new OutboxPosition(1, null, null), new Dictionary<string, long>(), new DevicePolicy("Control", false));
}
