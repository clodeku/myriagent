using System.IO.Pipes;
using System.Runtime.InteropServices;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using Myriagent.Update;
using MyriagentDesk;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>
/// 安全桌面上的提示列（<see cref="SecureDeskBanner"/>）：真的開視窗，但開在測試程序自己的桌面、放到畫面外（一般權限開不了安全桌面）——
/// 驗的是視窗開得起來、文字跟著換、一陣子沒被叫就收掉、不是安全桌面時不顯示，以及幫手在擷取到安全桌面時才叫它。
/// 在真的安全桌面上看不看得到（會不會被鎖定畫面蓋住）這裡驗不到
/// </summary>
public sealed class SecureDeskBannerTests
{
    private const int OffScreen = -30000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    private static bool Until(Func<bool> condition, int ms = 5000) => SpinWait.SpinUntil(condition, ms);

    [Fact]
    public void 顯示_換文字_收掉_視窗真的開了又關()
    {
        using var banner = new SecureDeskBanner(anyDesktop: true, left: OffScreen);
        banner.Visible.Should().BeFalse();

        const string first = "測試：這台電腦的畫面正被檢視 7f3a";
        banner.Show(first);
        Until(() => banner.Visible).Should().BeTrue("視窗開得起來");
        Until(() => FindWindowW("MyriagentDeskBanner", first) != IntPtr.Zero).Should().BeTrue("文字在視窗上");

        const string second = "測試：這台電腦正由「書房」操作 7f3a";
        banner.Show(second);
        Until(() => FindWindowW("MyriagentDeskBanner", second) != IntPtr.Zero).Should().BeTrue("文字跟著換");
        FindWindowW("MyriagentDeskBanner", first).Should().Be(IntPtr.Zero);

        banner.Hide();
        Until(() => !banner.Visible).Should().BeTrue();
        FindWindowW("MyriagentDeskBanner", second).Should().Be(IntPtr.Zero);

        // 收掉之後再叫照樣開得起來（同一個視窗類別）
        banner.Show(first);
        Until(() => banner.Visible).Should().BeTrue();
    }

    [Fact]
    public void 一陣子沒有再叫_自己收掉()
    {
        using var banner = new SecureDeskBanner(anyDesktop: true, left: OffScreen);
        banner.Show("測試：自己收掉 91c2");
        Until(() => banner.Visible).Should().BeTrue();
        Until(() => !banner.Visible, (int)SecureDeskBanner.KeepAlive.TotalMilliseconds + 3000).Should().BeTrue("沒有人看了就不留著");
    }

    [Fact]
    public void 不是安全桌面_不顯示()
    {
        // 測試程序在使用者的桌面：那裡的提示列是 app 的，幫手不畫
        using var banner = new SecureDeskBanner();
        banner.Show("測試：不該出現 55d0");
        Thread.Sleep(600);
        banner.Visible.Should().BeFalse();
        FindWindowW("MyriagentDeskBanner", "測試：不該出現 55d0").Should().Be(IntPtr.Zero);
    }

    [Fact]
    public void 兩個提示列先後用同一個視窗類別_前一個釋放了也不影響()
    {
        var first = new SecureDeskBanner(anyDesktop: true, left: OffScreen);
        first.Show("測試：第一個 a1");
        Until(() => first.Visible).Should().BeTrue();
        first.Dispose();
        GC.Collect();
        GC.WaitForPendingFinalizers();

        using var second = new SecureDeskBanner(anyDesktop: true, left: OffScreen);
        second.Show("測試：第二個 b2");
        Until(() => FindWindowW("MyriagentDeskBanner", "測試：第二個 b2") != IntPtr.Zero).Should().BeTrue();
    }

    [Fact]
    public void 給人看的那一句_知道是誰就寫出來_名稱裡的控制字元拿掉()
    {
        SecureDeskBanner.TextFor(null).Should().Be("這台電腦的畫面正由 Clode Myriagent 遠端桌面檢視");
        SecureDeskBanner.TextFor("  ").Should().Be(SecureDeskBanner.TextFor(null));
        SecureDeskBanner.TextFor("電腦「書房」").Should().Be("這台電腦正由「電腦「書房」」經 Clode Myriagent 遠端桌面操作");
        SecureDeskBanner.TextFor("a\nb\tc").Should().Contain("「abc」");
        SecureDeskBanner.TextFor(new string('x', 200)).Should().Contain("「" + new string('x', 40) + "」");
    }

    // ---- 幫手什麼時候叫它 ----

    private sealed class FakeBanner : IDeskBanner
    {
        public List<string> Shown { get; } = new();

        public void Show(string text)
        {
            lock (Shown)
                Shown.Add(text);
        }
    }

    private sealed class FakeIdentifier(DeskServiceClientInfo info) : IDeskClientIdentifier
    {
        public DeskServiceClientInfo Identify(NamedPipeServerStream pipe) => info;
    }

    private sealed class FakeCapturer : IDesktopCapturer
    {
        public string? Desktop { get; set; } = "Winlogon";

        public DeskServiceCaptureHeader Capture(DeskServiceCaptureRequest request, byte[] buffer)
        {
            var bytes = DeskServiceRules.CaptureBytes(request.OutW, request.OutH);
            return new DeskServiceCaptureHeader(DeskServiceRules.CaptureOk, bytes, Desktop, DeskServiceRules.IsSecureDesktop(Desktop), 0);
        }
    }

    [Fact]
    public async Task 幫手_擷取到安全桌面才顯示_驗過信封之後寫出是誰()
    {
        const string me = "S-1-5-21-1-2-3-1001";
        using var hub = new DeskOpenSamples();
        var banner = new FakeBanner();
        var capturer = new FakeCapturer { Desktop = "Default" };
        var pipe = DeskServiceRules.HelperPipeName(DeskServiceRules.TestServicePrefix + Guid.NewGuid().ToString("N")[..8], 1, Guid.NewGuid().ToString("N"));
        using var server = new DeskPipeServer(pipe, system: false, new FakeIdentifier(new DeskServiceClientInfo(1, me, me)),
            new AgentPipeHandler(new ReleaseIdentity(new ReleaseVersion(0, 2, 970), "abc1234"), system: false, 1, capturer, new DeskSecureInputTests.FakeInjector(),
                () => hub.Trust, banner));
        server.Start();
        await server.Listening.WaitAsync(TimeSpan.FromSeconds(10));
        using var client = DeskServiceClient.Connect(pipe, TimeSpan.FromSeconds(5));
        var request = new DeskServiceCaptureRequest(0, 0, 640, 480, 64, 48, true);
        var buffer = new byte[64 * 48 * 4];

        client.Capture(request, buffer);
        banner.Shown.Should().BeEmpty("使用者的桌面：不是幫手的事");

        capturer.Desktop = "Winlogon";
        client.Capture(request, buffer);
        banner.Shown.Single().Should().Be(SecureDeskBanner.TextFor(null), "還不知道是誰在看");

        client.SecureOpen(hub.Envelope(), DeskOpenSamples.Session);
        client.Capture(request, buffer);
        banner.Shown.Last().Should().Be(SecureDeskBanner.TextFor("擁有者的瀏覽器"), "信封是伺服器簽的，發起者可以相信");
        banner.Shown.Should().HaveCount(2, "每次擷取都叫一次（提示列靠它知道還有人在看）");
    }
}
