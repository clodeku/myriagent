using System.Diagnostics;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>
/// 平常的擷取加上安全桌面（<see cref="SecureDeskScreen"/>，鎖定畫面支援）：平常的那一個說鎖定或受保護的畫面時改向幫手要；
/// 幫手給得出來就照常有畫面（只能看：不注入、不送游標、給看的一方一句附註），給不出來就和以前一樣回報原因；換來源的那一次一律整張。
/// 平常的擷取與幫手都是假的
/// </summary>
public sealed class SecureDeskScreenTests
{
    private const string SessionId = "01JDESKSECURETEST00000000A";

    private static readonly DeskRect Bounds = new(0, 0, 640, 480);

    /// <summary>假的幫手：記下每次要不要整張；<see cref="Result"/> 是 null 時當成用不了</summary>
    private sealed class FakeSecure : ISecureDeskCapture
    {
        public DeskCapture? Result { get; set; } = DeskCapture.Ok;

        public byte Fill { get; set; } = 200;

        public List<bool> Forced { get; } = new();

        public bool Disposed { get; private set; }

        public DeskCapture? Capture(DeskRect bounds, int width, int height, byte[] bgra, bool force)
        {
            lock (Forced)
                Forced.Add(force);
            if (Result == DeskCapture.Ok)
                bgra.AsSpan(0, width * height * 4).Fill(Fill);
            return Result;
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>記下平常的擷取每次收到的 force</summary>
    private sealed class ForceRecorder(FakeDeskScreen inner) : IDeskScreen
    {
        public List<bool> Forced { get; } = new();

        public IReadOnlyList<DeskScreenInfo> Screens() => inner.Screens();

        public DeskRect VirtualBounds() => inner.VirtualBounds();

        public (int W, int H) OutputSize(DeskRect bounds, int? maxWidth) => inner.OutputSize(bounds, maxWidth);

        public DeskCapture Capture(DeskRect bounds, int width, int height, byte[] bgra, bool force)
        {
            Forced.Add(force);
            return inner.Capture(bounds, width, height, bgra, force);
        }

        public DeskCursorState? Cursor() => inner.Cursor();

        public DeskCursorImage? CursorImage() => inner.CursorImage();

        public void Inject(IReadOnlyList<DeskInputEvent> events, DeskRect screen, DeskRect virtualScreen) => inner.Inject(events, screen, virtualScreen);

        public void ReleaseAll() => inner.ReleaseAll();

        public void CaptureEnded() => inner.CaptureEnded();

        public void Dispose() => inner.Dispose();
    }

    private static readonly IReadOnlyList<DeskInputEvent> NoEvents = [];

    [Fact]
    public void 平常擷取得到_不問幫手_照常注入()
    {
        var inner = new FakeDeskScreen();
        var secure = new FakeSecure();
        using var screen = new SecureDeskScreen(inner, secure);
        var buffer = new byte[320 * 240 * 4];

        screen.Capture(Bounds, 320, 240, buffer, force: true).Should().Be(DeskCapture.Ok);
        secure.Forced.Should().BeEmpty();
        screen.ViewOnlyNote.Should().BeNull();
        screen.Cursor().Should().NotBeNull();
        screen.Inject(NoEvents, Bounds, Bounds);
        inner.Injected.All.Should().ContainSingle();
    }

    [Fact]
    public void 鎖定_幫手給得出畫面_有畫面但只能看_不注入也不送游標()
    {
        var inner = new FakeDeskScreen { Locked = true };
        var secure = new FakeSecure();
        using var screen = new SecureDeskScreen(inner, secure);
        var buffer = new byte[320 * 240 * 4];

        screen.Capture(Bounds, 320, 240, buffer, force: false).Should().Be(DeskCapture.Ok);
        buffer.Should().OnlyContain(b => b == 200, "畫面是幫手給的");
        screen.ViewOnlyNote.Should().Be(SecureDeskScreen.ViewOnlyText);
        screen.Cursor().Should().BeNull();
        screen.CursorImage().Should().BeNull();
        screen.Inject(NoEvents, Bounds, Bounds);
        inner.Injected.All.Should().BeEmpty("只能看的時候不注入");

        secure.Result = DeskCapture.Unchanged;
        screen.Capture(Bounds, 320, 240, buffer, force: false).Should().Be(DeskCapture.Unchanged);
        screen.ViewOnlyNote.Should().NotBeNull();
    }

    [Fact]
    public void 系統管理員的確認畫面_同樣改向幫手要()
    {
        var inner = new FakeDeskScreen { Secure = true };
        using var screen = new SecureDeskScreen(inner, new FakeSecure());
        screen.Capture(Bounds, 320, 240, new byte[320 * 240 * 4], force: false).Should().Be(DeskCapture.Ok);
        screen.ViewOnlyNote.Should().NotBeNull();
    }

    [Fact]
    public void 幫手用不了_和以前一樣回報擷取不到的原因()
    {
        var inner = new FakeDeskScreen { Locked = true };
        var secure = new FakeSecure { Result = null };
        using var screen = new SecureDeskScreen(inner, secure);
        var buffer = new byte[320 * 240 * 4];

        screen.Capture(Bounds, 320, 240, buffer, force: false).Should().Be(DeskCapture.Locked);
        screen.ViewOnlyNote.Should().BeNull();
        inner.Locked = false;
        inner.Secure = true;
        screen.Capture(Bounds, 320, 240, buffer, force: false).Should().Be(DeskCapture.Failed);
    }

    [Fact]
    public void 換來源的那一次一律整張_兩邊各自的沒變才不會對到另一邊的畫面()
    {
        var fake = new FakeDeskScreen();
        var inner = new ForceRecorder(fake);
        var secure = new FakeSecure();
        using var screen = new SecureDeskScreen(inner, secure);
        var buffer = new byte[320 * 240 * 4];

        screen.Capture(Bounds, 320, 240, buffer, force: false);
        fake.Locked = true;
        screen.Capture(Bounds, 320, 240, buffer, force: false);
        screen.Capture(Bounds, 320, 240, buffer, force: false);
        secure.Forced.Should().Equal([true, false], "剛換到幫手的那一次要整張，之後照要求");

        // 幫手中途用不了又恢復：恢復的那一次也要整張
        secure.Result = null;
        screen.Capture(Bounds, 320, 240, buffer, force: false);
        secure.Result = DeskCapture.Ok;
        screen.Capture(Bounds, 320, 240, buffer, force: false);
        secure.Forced.Should().Equal(true, false, false, true);

        // 解鎖：換回平常的擷取，那一次也要整張
        inner.Forced.Clear();
        fake.Locked = false;
        screen.Capture(Bounds, 320, 240, buffer, force: false).Should().Be(DeskCapture.Ok);
        screen.Capture(Bounds, 320, 240, buffer, force: false);
        inner.Forced.Should().Equal(true, false);
        screen.ViewOnlyNote.Should().BeNull();
    }

    [Fact]
    public void 釋放_兩邊都釋放()
    {
        var inner = new FakeDeskScreen();
        var secure = new FakeSecure();
        new SecureDeskScreen(inner, secure).Dispose();
        secure.Disposed.Should().BeTrue();
        inner.Lifecycle.All.Should().Contain("dispose");
    }

    // ---- 接上工作階段 ----

    private static async Task Until(Func<bool> condition, string what, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > ms)
                throw new TimeoutException("等不到：" + what);
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task 工作階段_鎖定時照常送影格_附註說明只能看_不注入_解鎖後收掉附註照常注入()
    {
        var inner = new FakeDeskScreen { Locked = true };
        var secure = new FakeSecure();
        var screen = new SecureDeskScreen(inner, secure);
        var sent = new Recorder<DeskPacket>();
        var session = new DeskSession(SessionId, "電腦「書房」", screen, sent.Add, (_, _) => { });
        try
        {
            session.Start();
            await Until(() => sent.All.Any(p => p.Kind == DeskRules.Frame), "鎖定時的第一個影格");
            await Until(() => sent.All.Any(p => p.Kind == DeskRules.Status), "只能看的附註");
            var status = sent.All.Last(p => p.Kind == DeskRules.Status).Body!;
            status.Value<string>("text").Should().BeNull("有畫面：中間不蓋說明");
            status.Value<string>("note").Should().Be(SecureDeskScreen.ViewOnlyText);
            sent.All.First(p => p.Kind == DeskRules.Frame).Body!["cursor"].Should().BeNull("安全桌面不送游標");

            JObject Tap() => new() { ["events"] = new JArray(new JObject { ["t"] = "md", ["x"] = 0.5, ["y"] = 0.5, ["b"] = 0 }) };
            session.OnPacket(new DeskPacket(SessionId, DeskRules.Input, 0, Tap()));
            inner.Injected.All.Should().BeEmpty("只能看的時候不注入");

            // 解鎖：附註收掉（一則沒有 note 的 status），輸入照常
            var statuses = sent.All.Count(p => p.Kind == DeskRules.Status);
            session.OnPacket(new DeskPacket(SessionId, DeskRules.Ack, 0, new JObject { ["seq"] = sent.All.Last(p => p.Kind == DeskRules.Frame).Seq }));
            inner.Locked = false;
            await Until(() => sent.All.Count(p => p.Kind == DeskRules.Status) > statuses, "收掉附註");
            var cleared = sent.All.Last(p => p.Kind == DeskRules.Status).Body!;
            cleared.Value<string>("text").Should().BeNull();
            cleared.ContainsKey("note").Should().BeFalse();
            await Until(() =>
            {
                session.OnPacket(new DeskPacket(SessionId, DeskRules.Input, 0, Tap()));
                return inner.Injected.All.Count > 0;
            }, "解鎖後照常注入");
        }
        finally
        {
            session.Stop("test", "測試結束", notifyViewer: false);
        }
    }

    [Fact]
    public void 狀態的內容_附註沒有就不帶那個鍵()
    {
        DeskRules.StatusBody(null, null).ToString(Newtonsoft.Json.Formatting.None).Should().Be("{\"text\":null}");
        DeskRules.StatusBody("那台電腦目前鎖定中", null).Value<string>("text").Should().Be("那台電腦目前鎖定中");
        var withNote = DeskRules.StatusBody(null, "只能看");
        withNote.Value<string>("note").Should().Be("只能看");
        DeskRules.StatusBody(null, "").ContainsKey("note").Should().BeFalse();
    }
}
