using System.Diagnostics;
using System.Drawing;
using Myriagent.Remote;
using Myriagent.RemoteDesk;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;

namespace Myriagent.Tests.RemoteDesk;

/// <summary>假的螢幕：畫面由測試改（純色＋可以塗一塊），輸入記下來；不碰真的螢幕與滑鼠鍵盤</summary>
internal sealed class FakeDeskScreen : IDeskScreen
{
    private readonly object gate = new();
    private byte fill = 40;
    private DeskRect? painted;

    public List<DeskScreenInfo> ScreenList { get; } =
        [new("螢幕 1", new DeskRect(0, 0, 640, 480), true), new("螢幕 2", new DeskRect(640, 0, 320, 240), false)];

    /// <summary>這台鎖定中（擷取回 Locked）</summary>
    public volatile bool Locked;

    /// <summary>系統管理員的確認畫面（擷取回 Failed）</summary>
    public volatile bool Secure;

    /// <summary>擷取一直要重來（擷取回 Retry）</summary>
    public volatile bool Retrying;

    /// <summary>給了就讓擷取停在裡面，直到它被 Set（模擬擷取到一半）</summary>
    public ManualResetEventSlim? HoldCapture { get; set; }

    /// <summary>有擷取正停在 <see cref="HoldCapture"/> 裡</summary>
    public volatile bool Capturing;

    public Recorder<(IReadOnlyList<DeskInputEvent> Events, DeskRect Screen)> Injected { get; } = new();

    public int Released;

    public int Captures;

    /// <summary>收尾的順序（captureEnded、dispose）</summary>
    public Recorder<string> Lifecycle { get; } = new();

    public IReadOnlyList<DeskScreenInfo> Screens() => ScreenList;

    public DeskRect VirtualBounds() => new(0, 0, 960, 480);

    /// <summary>改整個畫面的顏色（全部格子都變）</summary>
    public void Fill(byte value)
    {
        lock (gate)
        {
            fill = value;
            painted = null;
        }
    }

    /// <summary>在左上塗一塊（只有那幾格變）</summary>
    public void Paint(DeskRect rect)
    {
        lock (gate)
            painted = rect;
    }

    public DeskCursorState? CursorState { get; set; } = new(10, 10, true, 1);

    public (int W, int H) OutputSize(DeskRect bounds, int? maxWidth) => DeskRules.SendSize(bounds.W, bounds.H, maxWidth);

    public DeskCursorState? Cursor() => CursorState;

    public DeskCursorImage? CursorImage() => new([1, 2, 3], 32, 32, 1, 2);

    public DeskCapture Capture(DeskRect bounds, int width, int height, byte[] bgra, bool force)
    {
        Interlocked.Increment(ref Captures);
        if (HoldCapture is { } hold)
        {
            Capturing = true;
            hold.Wait(TimeSpan.FromSeconds(10));
            Capturing = false;
        }

        if (Locked)
            return DeskCapture.Locked;
        if (Secure)
            return DeskCapture.Failed;
        if (Retrying)
            return DeskCapture.Retry;
        lock (gate)
        {
            Array.Fill(bgra, fill, 0, width * height * 4);
            if (painted is { } p)
            {
                for (var y = p.Y; y < Math.Min(height, p.Bottom); y++)
                    Array.Fill(bgra, (byte)200, (y * width + p.X) * 4, Math.Min(p.W, width - p.X) * 4);
            }
        }

        return DeskCapture.Ok;
    }

    public void Inject(IReadOnlyList<DeskInputEvent> events, DeskRect screen, DeskRect virtualScreen) => Injected.Add((events, screen));

    public void ReleaseAll() => Interlocked.Increment(ref Released);

    public void CaptureEnded() => Lifecycle.Add("captureEnded");

    public void Dispose() => Lifecycle.Add("dispose");
}

/// <summary>
/// 遠端桌面被看的一方的工作階段（RemoteDesk/DeskSession.cs；docs/sync-protocol.md 14.16）：影格、流量控制、確認逾時、輸入、設定、結束。
/// 以假的螢幕與真的擷取執行緒跑
/// </summary>
public sealed class DeskSessionTests
{
    private const string SessionId = "01JDESKSESSIONTEST0000000A";

    private sealed class Rig : IDisposable
    {
        public Rig(TimeProvider? time = null)
        {
            Session = new DeskSession(SessionId, "電腦「書房」", Screen, Sent.Add, (s, reason) => Ended.Add(reason), time);
        }

        public FakeDeskScreen Screen { get; } = new();

        public Recorder<DeskPacket> Sent { get; } = new();

        public Recorder<string> Ended { get; } = new();

        public DeskSession Session { get; }

        public List<DeskPacket> Frames => Sent.All.Where(p => p.Kind == DeskRules.Frame).ToList();

        public async Task<DeskPacket> FrameAsync(long seq)
        {
            await Until(() => Frames.Any(f => f.Seq == seq), $"影格 {seq}");
            return Frames.Last(f => f.Seq == seq);
        }

        public void Ack(long seq) => Session.OnPacket(new DeskPacket(SessionId, DeskRules.Ack, 0, new JObject { ["seq"] = seq }));

        public void Dispose() => Session.Stop("test", "測試結束", notifyViewer: false);
    }

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
    public async Task 第一個影格是整個畫面_帶螢幕清單_區塊是解得開的JPEG()
    {
        using var rig = new Rig();
        rig.Session.Start();

        var first = await rig.FrameAsync(1);

        first.Body!.Value<bool>("key").Should().BeTrue();
        first.Body.Value<int>("w").Should().Be(640);
        first.Body.Value<int>("h").Should().Be(480);
        first.Body["screens"]!.Should().HaveCount(2);
        var tiles = first.Body["tiles"]!.OfType<JObject>().ToList();
        tiles.Sum(t => t.Value<int>("w") * t.Value<int>("h")).Should().Be(640 * 480, "整個畫面");
        tiles.Should().OnlyContain(t => t.Value<int>("h") <= DeskRules.MaxBandRows * DeskRules.Tile, "太高的切成幾段");
        using var image = Image.FromStream(new MemoryStream(Convert.FromBase64String(tiles[0].Value<string>("d")!)));
        image.Width.Should().Be(tiles[0].Value<int>("w"));
    }

    [Fact]
    public async Task 未確認的影格最多兩個_確認之後只送變動的格子()
    {
        using var rig = new Rig();
        rig.Session.Start();
        await rig.FrameAsync(1);
        rig.Screen.Fill(80);
        await rig.FrameAsync(2);
        rig.Screen.Fill(120);
        await Task.Delay(500);
        rig.Frames.Should().HaveCount(2, "兩個都還沒確認，不送第三個");

        rig.Ack(2);
        var third = await rig.FrameAsync(3);
        third.Body!.Value<bool>("key").Should().BeFalse();
        rig.Ack(3);
        rig.Screen.Paint(new DeskRect(0, 0, 10, 10));
        var fourth = await rig.FrameAsync(4);
        var tiles = fourth.Body!["tiles"]!.OfType<JObject>().ToList();
        tiles.Should().ContainSingle().Which.Value<int>("w").Should().Be(DeskRules.Tile, "只有左上那一格變了");
    }

    [Fact]
    public async Task 輸入交給螢幕_座標以選的螢幕_確認可以併在輸入裡()
    {
        using var rig = new Rig();
        rig.Session.Start();
        await rig.FrameAsync(1);
        rig.Session.OnPacket(new DeskPacket(SessionId, DeskRules.Config, 0, new JObject { ["screen"] = 1 }));
        var keyFrame = await rig.FrameAsync(2);
        keyFrame.Body!.Value<bool>("key").Should().BeTrue("換螢幕整個重送");
        keyFrame.Body.Value<int>("w").Should().Be(320);

        rig.Session.OnPacket(new DeskPacket(SessionId, DeskRules.Input, 0, new JObject
        {
            ["ack"] = 2,
            ["events"] = new JArray(new JObject { ["t"] = "md", ["x"] = 0.5, ["y"] = 0.5, ["b"] = 0 }),
        }));

        var injected = rig.Screen.Injected.All.Should().ContainSingle().Which;
        injected.Screen.Should().Be(new DeskRect(640, 0, 320, 240));
        injected.Events.Single().Type.Should().Be("md");
        rig.Screen.Fill(99);
        await rig.FrameAsync(3);
    }

    [Fact]
    public async Task 很久沒有確認就結束並告訴看的一方_放開按鍵()
    {
        var clock = new ManualTimeProvider();
        using var rig = new Rig(clock);
        rig.Session.Start();
        await rig.FrameAsync(1);

        clock.Advance(DeskRules.AckTimeout + TimeSpan.FromSeconds(1));

        await Until(() => rig.Ended.Count == 1, "結束");
        rig.Ended.All.Single().Should().Be("noViewer");
        rig.Sent.All.Last().Kind.Should().Be(DeskRules.End);
        rig.Screen.Released.Should().Be(1);
    }

    [Fact]
    public async Task 看的一方結束時不再回end_只通知一次()
    {
        using var rig = new Rig();
        rig.Session.Start();
        await rig.FrameAsync(1);

        rig.Session.OnPacket(new DeskPacket(SessionId, DeskRules.Close, 0, DeskRules.EndBody("viewerClosed", "x")));
        rig.Session.Stop("again", "y", notifyViewer: true);

        rig.Ended.All.Should().Equal("viewerClosed");
        rig.Sent.All.Should().NotContain(p => p.Kind == DeskRules.End);
        rig.Session.Stopped.Should().BeTrue();
    }

    [Fact]
    public async Task 擷取不到時送一次狀態_恢復後清掉並整個重送()
    {
        using var rig = new Rig();
        rig.Screen.Locked = true;
        rig.Session.Start();
        await Until(() => rig.Sent.All.Any(p => p.Kind == DeskRules.Status), "狀態");
        rig.Sent.All.Single(p => p.Kind == DeskRules.Status).Body!.Value<string>("text").Should().Be(DeskScreenRules.LockedText);

        rig.Screen.Locked = false;
        var frame = await rig.FrameAsync(1);
        frame.Body!.Value<bool>("key").Should().BeTrue();
        rig.Sent.All.Where(p => p.Kind == DeskRules.Status).Should().HaveCount(2);
        rig.Sent.All.Last(p => p.Kind == DeskRules.Status).Body!["text"]!.Type.Should().Be(JTokenType.Null);
    }

    /// <summary>2026-11-12 使用者回報：那台鎖定中，看的一方只看到「連線到…」。鎖定時要說是鎖定、輸入不送進去（會白白叫醒那台的螢幕）</summary>
    [Fact]
    public async Task 鎖定中說明原因而且不注入輸入_換成確認畫面時換說明_恢復後輸入照常()
    {
        using var rig = new Rig();
        rig.Screen.Locked = true;
        rig.Session.Start();
        await Until(() => rig.Sent.All.Any(p => p.Kind == DeskRules.Status), "鎖定的說明");
        JObject Tap() => new() { ["events"] = new JArray(new JObject { ["t"] = "md", ["x"] = 0.5, ["y"] = 0.5, ["b"] = 0 }) };

        rig.Session.OnPacket(new DeskPacket(SessionId, DeskRules.Input, 0, Tap()));
        rig.Screen.Injected.All.Should().BeEmpty("鎖定中不注入");

        rig.Screen.Secure = true;
        rig.Screen.Locked = false;
        await Until(() => rig.Sent.All.Count(p => p.Kind == DeskRules.Status) == 2, "確認畫面的說明");
        rig.Sent.All.Last(p => p.Kind == DeskRules.Status).Body!.Value<string>("text").Should().Be(DeskScreenRules.SecureDesktopText);
        rig.Session.OnPacket(new DeskPacket(SessionId, DeskRules.Input, 0, Tap()));
        rig.Screen.Injected.All.Should().BeEmpty("確認畫面也不注入");

        rig.Screen.Secure = false;
        await rig.FrameAsync(1);
        rig.Session.OnPacket(new DeskPacket(SessionId, DeskRules.Input, 0, Tap()));
        rig.Screen.Injected.All.Should().ContainSingle("恢復之後照常注入");
    }

    /// <summary>鎖定期間兩邊都沒有別的封包：伺服器太久沒有封包會忘掉工作階段，所以同一句說明每隔一陣子重送</summary>
    [Fact]
    public async Task 鎖定很久時每隔一陣子重送說明_不會因為沒有確認就結束()
    {
        var clock = new ManualTimeProvider();
        using var rig = new Rig(clock);
        rig.Screen.Locked = true;
        rig.Session.Start();
        await Until(() => rig.Sent.All.Count(p => p.Kind == DeskRules.Status) == 1, "鎖定的說明");
        await Task.Delay(1300);
        rig.Sent.All.Count(p => p.Kind == DeskRules.Status).Should().Be(1, "原因沒變、還沒到重送的時間，不重送");

        clock.Advance(TimeSpan.FromMilliseconds(DeskScreenRules.StatusRepeatMs + 1000));
        await Until(() => rig.Sent.All.Count(p => p.Kind == DeskRules.Status) == 2, "重送說明");
        rig.Sent.All.Last(p => p.Kind == DeskRules.Status).Body!.Value<string>("text").Should().Be(DeskScreenRules.LockedText);

        clock.Advance(DeskRules.AckTimeout + DeskRules.AckTimeout);
        await Task.Delay(1300);
        rig.Session.Stopped.Should().BeFalse("沒有送過影格，沒有確認不算看的一方不見了");
        (DeskScreenRules.StatusRepeatMs * 2L).Should().BeLessThan((long)DeskRules.IdleForget.TotalMilliseconds, "要比伺服器忘掉工作階段的時間短得多");
    }

    [Fact]
    public async Task 擷取一直要重來時也告訴看的一方_輸入照常_拿到畫面就清掉()
    {
        using var rig = new Rig();
        rig.Screen.Retrying = true;
        rig.Session.Start();
        await Until(() => rig.Sent.All.Any(p => p.Kind == DeskRules.Status), "重試的說明", 15000);
        rig.Sent.All.Single(p => p.Kind == DeskRules.Status).Body!.Value<string>("text").Should().Be(DeskScreenRules.RetryText);
        rig.Screen.Captures.Should().BeGreaterThanOrEqualTo(DeskScreenRules.RetryStatusAfter);

        rig.Session.OnPacket(new DeskPacket(SessionId, DeskRules.Input, 0, new JObject { ["events"] = new JArray(new JObject { ["t"] = "kd", ["k"] = 65 }) }));
        rig.Screen.Injected.All.Should().ContainSingle("重試期間輸入照常：按鍵可以叫醒螢幕");

        rig.Screen.Retrying = false;
        await rig.FrameAsync(1);
        rig.Sent.All.Last(p => p.Kind == DeskRules.Status).Body!["text"]!.Type.Should().Be(JTokenType.Null);
    }

    /// <summary>擷取到一半（正在讀回顯示卡的材質）時釋放畫面會讀到已經釋放的記憶體：等擷取執行緒結束才釋放</summary>
    [Fact]
    public async Task 擷取途中停下_畫面等擷取執行緒結束才釋放()
    {
        using var rig = new Rig();
        rig.Session.Start();
        await rig.FrameAsync(1);
        rig.Ack(1);
        using var hold = new ManualResetEventSlim();
        rig.Screen.HoldCapture = hold;
        rig.Screen.Fill(90);
        await Until(() => rig.Screen.Capturing, "擷取到一半");

        rig.Session.Stop("viewerClosed", "x", notifyViewer: false);
        rig.Session.ReleaseScreen();
        await Task.Delay(200);
        rig.Screen.Lifecycle.All.Should().BeEmpty("擷取還沒結束，不能釋放");

        hold.Set();
        await Until(() => rig.Screen.Lifecycle.Count == 2, "釋放");
        rig.Screen.Lifecycle.All.Should().Equal("captureEnded", "dispose");
        rig.Session.ReleaseScreen();
        rig.Screen.Lifecycle.All.Should().HaveCount(2, "只釋放一次");
    }

    [Fact]
    public async Task 擷取執行緒已經結束或沒開始過_釋放畫面馬上做()
    {
        using var never = new Rig();
        never.Session.ReleaseScreen();
        never.Screen.Lifecycle.All.Should().Equal("dispose");

        using var rig = new Rig();
        rig.Session.Start();
        await rig.FrameAsync(1);
        rig.Session.Stop("viewerClosed", "x", notifyViewer: false);
        await Until(() => rig.Screen.Lifecycle.Count == 1, "擷取執行緒結束");
        rig.Session.ReleaseScreen();
        rig.Screen.Lifecycle.All.Should().Equal("captureEnded", "dispose");
    }

    [Fact]
    public void 鎖定要系統回報鎖定而且登入畫面的程序在_沒回報鎖定就不去列程序()
    {
        DeskScreenRules.Locked(true, () => true).Should().BeTrue();
        DeskScreenRules.Locked(true, () => false).Should().BeFalse("只有一個來源這麼說不算：判斷錯那台就永遠是一句鎖定中");
        DeskScreenRules.Locked(false, () => throw new InvalidOperationException("不該問")).Should().BeFalse();
    }

    [Fact]
    public void 一直沒有第一張畫面_先叫醒螢幕_再沒有就改用GDI()
    {
        DeskScreenRules.OnNoFirstFrame(1).Should().Be((true, false));
        DeskScreenRules.OnNoFirstFrame(2).Should().Be((false, false));
        DeskScreenRules.OnNoFirstFrame(3).Should().Be((false, true));
        DeskScreenRules.OnNoFirstFrame(4).Should().Be((false, true));
    }

    [Fact]
    public void 擷取不到的說明_依原因_重來要連續幾次才說()
    {
        DeskScreenRules.StatusText(DeskCapture.Locked, 0).Should().Be(DeskScreenRules.LockedText).And.Contain("鎖定");
        DeskScreenRules.StatusText(DeskCapture.Failed, 0).Should().Be(DeskScreenRules.SecureDesktopText);
        DeskScreenRules.StatusText(DeskCapture.Retry, DeskScreenRules.RetryStatusAfter - 1).Should().BeNull();
        DeskScreenRules.StatusText(DeskCapture.Retry, DeskScreenRules.RetryStatusAfter).Should().Be(DeskScreenRules.RetryText);
        DeskScreenRules.StatusText(DeskCapture.Ok, 99).Should().BeNull();
        DeskScreenRules.StatusText(DeskCapture.Unchanged, 99).Should().BeNull();
    }

    [Fact]
    public void 選的螢幕超出範圍時用主螢幕()
    {
        List<DeskScreenInfo> screens = [new("a", new DeskRect(0, 0, 1, 1), false), new("b", new DeskRect(1, 0, 1, 1), true)];
        DeskSession.ScreenIndex(screens, null).Should().Be(1);
        DeskSession.ScreenIndex(screens, 0).Should().Be(0);
        DeskSession.ScreenIndex(screens, 5).Should().Be(1);
    }
}

/// <summary>影格編碼（RemoteDesk/DeskFrameEncoder.cs）</summary>
public sealed class DeskFrameEncoderTests
{
    [Fact]
    public void 變動的格子_沒有上一張全部算_同樣的畫面一格都不算()
    {
        var a = new byte[130 * 70 * 4];
        var b = (byte[])a.Clone();
        DeskFrameEncoder.ChangedTiles(null, a, 130, 70, key: false, out var cols, out var rows).Should().OnlyContain(c => c);
        (cols, rows).Should().Be((3, 2));
        DeskFrameEncoder.ChangedTiles(a, b, 130, 70, key: false, out _, out _).Should().OnlyContain(c => !c);
        b[(69 * 130 + 129) * 4] = 1;
        DeskFrameEncoder.ChangedTiles(a, b, 130, 70, key: false, out _, out _).Should().Equal(false, false, false, false, false, true);
        DeskFrameEncoder.ChangedTiles(a, a, 130, 70, key: true, out _, out _).Should().OnlyContain(c => c);
    }
}

/// <summary>
/// 這台真的螢幕的擷取與編碼耗時（只量、不注入輸入）：<c>MYRIAGENT_DESK_IT=1</c> 才跑，給效能調整參考。DXGI（GPU 縮小）與 GDI 備援各量一次
/// </summary>
public sealed class GdiDeskScreenBenchmark(ITestOutputHelper output)
{
    /// <summary>
    /// 這台現在擷取得到什麼（只回報、不判定；RemoteDesk/DeskDiagnostics.cs）：鎖定、螢幕關閉時各種擷取方式回什麼、畫面是不是全黑。
    /// 查「遠端桌面看不到畫面」時在那台跑（別台沒有原始碼時用 tools/desk-probe）。<c>MYRIAGENT_DESK_WAKE=1</c> 另外以會叫醒螢幕的方式擷取一次
    /// </summary>
    [Fact]
    public void 回報這台現在擷取得到什麼()
    {
        if (Environment.GetEnvironmentVariable("MYRIAGENT_DESK_IT") != "1")
            return;
        DeskDiagnostics.Run(output.WriteLine, wake: Environment.GetEnvironmentVariable("MYRIAGENT_DESK_WAKE") == "1", allScreens: true);
    }

    [Fact]
    public void 量擷取與編碼的耗時()
    {
        if (Environment.GetEnvironmentVariable("MYRIAGENT_DESK_IT") != "1")
            return;
        foreach (var dxgi in new[] { true, false })
        {
            using IDeskScreen screen = dxgi ? new DxgiDeskScreen() : new GdiDeskScreen();
            var primary = screen.Screens().First(s => s.Primary);
            foreach (var limit in new int?[] { null, 1280 })
            {
                var (w, h) = screen.OutputSize(primary.Bounds, limit);
                var buffer = new byte[w * h * 4];
                var encoder = new DeskFrameEncoder();
                var first = screen.Capture(primary.Bounds, w, h, buffer, force: true);
                if (first == DeskCapture.Retry)
                    first = screen.Capture(primary.Bounds, w, h, buffer, force: true);
                first.Should().Be(DeskCapture.Ok);
                var capture = Stopwatch.StartNew();
                var unchanged = 0;
                for (var i = 0; i < 20; i++)
                {
                    if (screen.Capture(primary.Bounds, w, h, buffer, force: false) == DeskCapture.Unchanged)
                        unchanged++;
                }

                capture.Stop();
                // 3 秒內真的有新畫面的那幾次（這台的畫面本來就在動：app 的動畫、主控台）
                double changedMs = 0;
                var changedCount = 0;
                var window = Stopwatch.StartNew();
                while (window.ElapsedMilliseconds < 3000)
                {
                    var one = Stopwatch.StartNew();
                    if (screen.Capture(primary.Bounds, w, h, buffer, force: false) == DeskCapture.Ok)
                    {
                        changedMs += one.Elapsed.TotalMilliseconds;
                        changedCount++;
                    }

                    Thread.Sleep(15);
                }

                output.WriteLine($"  有新畫面 {changedCount} 次，平均 {(changedCount == 0 ? 0 : changedMs / changedCount):0.0} ms");
                var forced = Stopwatch.StartNew();
                for (var i = 0; i < 10; i++)
                    screen.Capture(primary.Bounds, w, h, buffer, force: true);
                forced.Stop();
                var encode = Stopwatch.StartNew();
                var tiles = encoder.Encode(null, buffer, w, h, 70, key: true);
                encode.Stop();
                output.WriteLine($"{(dxgi ? "DXGI" : "GDI")} 螢幕 {primary.Bounds.W}x{primary.Bounds.H} → {w}x{h}：擷取 {capture.ElapsedMilliseconds / 20.0} ms／次（{unchanged}/20 次沒變化）、"
                                 + $"強制讀回 {forced.ElapsedMilliseconds / 10.0} ms、整張編碼 {encode.ElapsedMilliseconds} ms（{tiles.Count} 塊、{tiles.Sum(t => t.Jpeg.Length) / 1024} KB）");
            }
        }
    }
}
