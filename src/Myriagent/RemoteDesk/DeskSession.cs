using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.RemoteDesk;

/// <summary>
/// 被看的電腦上的一個遠端桌面工作階段（docs/sync-protocol.md 14.16）：專用的擷取執行緒照 <see cref="DeskRules"/> 的流量控制送影格
/// （未確認的影格與位元組有上限、依往返時間調每秒格數與畫質、沒變化時每 <see cref="DeskRules.Heartbeat"/> 送一個空影格），
/// 看的一方的封包（輸入、確認、設定、結束）由 <see cref="OnPacket"/> 交進來。<see cref="DeskRules.AckTimeout"/> 沒有確認、看的一方結束、
/// <see cref="Stop"/> 都會停下，停下時放開還按著的鍵並呼叫 <c>ended</c>（只一次，擷取執行緒或呼叫 Stop 的執行緒）
/// </summary>
internal sealed class DeskSession : IDisposable
{
    private readonly IDeskScreen screen;
    private readonly Action<DeskPacket> send;
    private readonly Action<DeskSession, string> ended;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly SortedDictionary<long, (long SentMs, long Bytes)> inflight = [];
    private readonly DeskFrameEncoder encoder = new();
    private Thread? thread;
    private long seq;
    private double rttMs;
    private long lastAckMs;
    private string quality = "auto";
    private int? screenIndex;
    private int? maxWidth;
    private bool keyRequested = true;
    private bool stopped;
    private string? endReason;

    public DeskSession(string sessionId, string viewerLabel, IDeskScreen screen, Action<DeskPacket> send, Action<DeskSession, string> ended, TimeProvider? time = null)
    {
        SessionId = sessionId;
        ViewerLabel = viewerLabel;
        this.screen = screen;
        this.send = send;
        this.ended = ended;
        this.time = time ?? TimeProvider.System;
    }

    public string SessionId { get; }

    /// <summary>看的一方（給提示列：電腦「書房」、擁有者的瀏覽器）</summary>
    public string ViewerLabel { get; }

    /// <summary>平滑過的往返時間（毫秒；測試與記錄用）</summary>
    public double RttMs
    {
        get
        {
            lock (gate)
                return rttMs;
        }
    }

    public bool Stopped
    {
        get
        {
            lock (gate)
                return stopped;
        }
    }

    public void Start()
    {
        lock (gate)
            lastAckMs = NowMs();
        thread = new Thread(Loop) { IsBackground = true, Name = "遠端桌面 " + SessionId };
        thread.Start();
    }

    /// <summary>看的一方送來的封包（任何執行緒）</summary>
    public void OnPacket(DeskPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        switch (packet.Kind)
        {
            case DeskRules.Input:
                if (DeskRules.AckOf(packet.Body) is { } inputAck)
                    Ack(inputAck);
                if (DeskRules.ParseInput(packet.Body) is { Count: > 0 } events && !Stopped)
                    InjectSafely(events);
                break;
            case DeskRules.Ack:
                if (DeskRules.AckOf(packet.Body) is { } ack)
                    Ack(ack);
                break;
            case DeskRules.Config:
                if (DeskRules.ParseConfig(packet.Body) is { } config)
                {
                    lock (gate)
                    {
                        if (config.Quality is { } q)
                            quality = q;
                        if (config.Screen is { } s)
                            screenIndex = s;
                        if (config.MaxWidth is { } mw)
                            maxWidth = mw;
                        // 換螢幕、要求重送（看的一方漏了影格）：整個畫面重送，未確認的不再等
                        if (config.Key || config.Screen is not null || config.MaxWidth is not null)
                        {
                            keyRequested = true;
                            inflight.Clear();
                            lastAckMs = NowMs();
                        }

                        Monitor.PulseAll(gate);
                    }
                }

                break;
            case DeskRules.Close:
                Stop("viewerClosed", "看的一方結束了遠端桌面", notifyViewer: false);
                break;
        }
    }

    /// <summary>停下（任何執行緒；只有第一次有作用）。<paramref name="notifyViewer"/>：送 <c>end</c> 告訴看的一方</summary>
    public void Stop(string reason, string text, bool notifyViewer)
    {
        lock (gate)
        {
            if (stopped)
                return;
            stopped = true;
            endReason = reason;
            Monitor.PulseAll(gate);
        }

        if (notifyViewer)
            SendSafely(new DeskPacket(SessionId, DeskRules.End, 0, DeskRules.EndBody(reason, text)));
        try
        {
            screen.ReleaseAll();
        }
        catch (Exception ex)
        {
            Log.Warn($"遠端桌面 {SessionId} 放開按鍵失敗：{ex.Message}");
        }

        try
        {
            ended(this, reason);
        }
        catch (Exception ex)
        {
            Log.Error("遠端桌面結束的通知失敗", ex);
        }
    }

    public void Dispose() => Stop("disposed", "遠端桌面已經結束", notifyViewer: true);

    private void Ack(long ackSeq)
    {
        lock (gate)
        {
            var now = NowMs();
            if (inflight.TryGetValue(ackSeq, out var sent))
                rttMs = DeskRules.SmoothRtt(rttMs, now - sent.SentMs);
            foreach (var key in inflight.Keys.Where(k => k <= ackSeq).ToList())
                inflight.Remove(key);
            lastAckMs = now;
            Monitor.PulseAll(gate);
        }
    }

    private void InjectSafely(IReadOnlyList<DeskInputEvent> events)
    {
        try
        {
            var screens = screen.Screens();
            int index;
            lock (gate)
                index = ScreenIndex(screens, screenIndex);
            screen.Inject(events, screens[index].Bounds, screen.VirtualBounds());
        }
        catch (Exception ex)
        {
            Log.Warn($"遠端桌面 {SessionId} 的輸入送不進去：{ex.Message}");
        }
    }

    /// <summary>選的螢幕（沒選或超出範圍：主螢幕）</summary>
    internal static int ScreenIndex(IReadOnlyList<DeskScreenInfo> screens, int? chosen)
    {
        if (chosen is { } c && c >= 0 && c < screens.Count)
            return c;
        var primary = screens.ToList().FindIndex(s => s.Primary);
        return primary < 0 ? 0 : primary;
    }

    private void Loop()
    {
        byte[]? previous = null;
        byte[]? current = null;
        var lastSize = (W: 0, H: 0);
        var lastScreen = -1;
        var lastSentMs = 0L;
        string? statusShown = null;
        var needKey = false;
        DeskCursorState? lastCursor = null;
        var retries = 0;
        try
        {
            while (true)
            {
                bool key;
                string q;
                int? chosen;
                int? widthLimit;
                double rtt;
                var timedOut = false;
                lock (gate)
                {
                    while (!stopped && !DeskRules.CanSend(inflight.Count, inflight.Values.Sum(v => v.Bytes)))
                    {
                        if (NowMs() - lastAckMs >= (long)DeskRules.AckTimeout.TotalMilliseconds)
                        {
                            timedOut = true;
                            break;
                        }

                        Monitor.Wait(gate, 200);
                    }

                    if (stopped)
                        return;
                    if (!timedOut && inflight.Count > 0 && NowMs() - lastAckMs >= (long)DeskRules.AckTimeout.TotalMilliseconds)
                        timedOut = true;
                    key = keyRequested || needKey;
                    keyRequested = false;
                    q = quality;
                    chosen = screenIndex;
                    widthLimit = maxWidth;
                    rtt = rttMs;
                }

                if (timedOut)
                {
                    Stop("noViewer", "看的一方很久沒有回應，遠端桌面已經結束", notifyViewer: true);
                    return;
                }

                needKey = false;

                var screens = screen.Screens();
                if (screens.Count == 0)
                {
                    Wait(1000);
                    continue;
                }

                var index = ScreenIndex(screens, chosen);
                var bounds = screens[index].Bounds;
                var (w, h) = screen.OutputSize(bounds, widthLimit);
                if ((w, h) != lastSize || index != lastScreen)
                {
                    key = true;
                    previous = null;
                    current = new byte[w * h * 4];
                    lastSize = (w, h);
                    lastScreen = index;
                }

                current ??= new byte[w * h * 4];
                var captured = screen.Capture(bounds, w, h, current, force: key);
                if (captured == DeskCapture.Retry)
                {
                    // 擷取的方式換了（大小可能不同）：馬上重來，連續太多次就等一下
                    needKey = true;
                    lastSize = (0, 0);
                    if (++retries > 3)
                        Wait(500);
                    continue;
                }

                retries = 0;
                if (captured == DeskCapture.Failed)
                {
                    if (statusShown is null)
                    {
                        statusShown = "這台目前是鎖定畫面或系統管理員的確認畫面，遠端看不到也操作不了；那台的人解除之後就會恢復";
                        SendSafely(new DeskPacket(SessionId, DeskRules.Status, 0, new JObject { ["text"] = statusShown }));
                    }

                    needKey = true;
                    Wait(1000);
                    continue;
                }

                if (statusShown is not null)
                {
                    statusShown = null;
                    SendSafely(new DeskPacket(SessionId, DeskRules.Status, 0, new JObject { ["text"] = JValue.CreateNull() }));
                }

                IReadOnlyList<DeskTile> tiles = captured == DeskCapture.Unchanged
                    ? []
                    : encoder.Encode(previous, current, w, h, DeskRules.Quality(q, rtt), key);
                // 游標另外送（看的一方疊在畫面上）：位置換成這個螢幕的比例，形狀變了才帶圖
                var cursor = screen.Cursor();
                var cursorChanged = !Equals(cursor, lastCursor) || key;
                var now = NowMs();
                if (tiles.Count == 0 && !cursorChanged && now - lastSentMs < (long)DeskRules.Heartbeat.TotalMilliseconds)
                {
                    Wait(DeskRules.IntervalMs(q, rtt));
                    continue;
                }

                var shape = cursor is { Visible: true } && (key || cursor.ShapeId != lastCursor?.ShapeId) ? screen.CursorImage() : null;
                SendFrame(tiles, w, h, key, index, screens, q, CursorBody(cursor, bounds, shape));
                lastCursor = cursor;
                lastSentMs = now;
                if (captured == DeskCapture.Ok)
                    (previous, current) = (current, previous ?? new byte[w * h * 4]);
                Wait(DeskRules.IntervalMs(q, rtt));
            }
        }
        catch (Exception ex)
        {
            Log.Error($"遠端桌面 {SessionId} 的擷取停了", ex);
            Stop("error", "那台擷取畫面時發生錯誤，遠端桌面已經結束", notifyViewer: true);
        }
    }

    /// <summary>送一個影格（太大就拆成幾包，每包帶 part／parts）</summary>
    /// <summary>游標（<c>cur</c>）：<c>x</c>／<c>y</c> 是這個螢幕的比例、<c>v</c> 看不看得到；形狀變了另帶 <c>img</c>（PNG）、<c>hx</c>／<c>hy</c> 熱點、<c>sw</c> 螢幕寬（看的一方以它換算圖的大小）</summary>
    internal static JObject? CursorBody(DeskCursorState? cursor, DeskRect bounds, DeskCursorImage? shape)
    {
        if (cursor is null)
            return null;
        var body = new JObject
        {
            ["x"] = Math.Round((cursor.X - bounds.X) / (double)Math.Max(1, bounds.W), 5),
            ["y"] = Math.Round((cursor.Y - bounds.Y) / (double)Math.Max(1, bounds.H), 5),
            ["v"] = cursor.Visible && cursor.X >= bounds.X && cursor.Y >= bounds.Y && cursor.X < bounds.Right && cursor.Y < bounds.Bottom,
        };
        if (shape is not null)
        {
            body["img"] = Convert.ToBase64String(shape.Png);
            body["hx"] = shape.HotX;
            body["hy"] = shape.HotY;
            body["sw"] = bounds.W;
        }

        return body;
    }

    private void SendFrame(IReadOnlyList<DeskTile> tiles, int w, int h, bool key, int index, IReadOnlyList<DeskScreenInfo> screens, string q, JObject? cursor)
    {
        long frameSeq;
        lock (gate)
            frameSeq = ++seq;
        var sizes = tiles.Select(t => DeskRules.EncodedSize(t.Jpeg.Length)).ToList();
        var packs = DeskRules.SplitBySize(sizes, DeskRules.MaxPacketBytes - 8 * 1024);
        if (packs.Count == 0)
            packs.Add([]);
        // 先記成未確認再送：確認可能比這裡記下來還早到
        lock (gate)
            inflight[frameSeq] = (NowMs(), sizes.Sum() + 256L * packs.Count);
        for (var p = 0; p < packs.Count; p++)
        {
            var body = new JObject
            {
                ["w"] = w,
                ["h"] = h,
                ["key"] = key,
                ["part"] = p,
                ["parts"] = packs.Count,
                ["screen"] = index,
                ["q"] = q,
                ["tiles"] = new JArray(packs[p].Select(i => new JObject
                {
                    ["x"] = tiles[i].Rect.X, ["y"] = tiles[i].Rect.Y, ["w"] = tiles[i].Rect.W, ["h"] = tiles[i].Rect.H,
                    ["d"] = Convert.ToBase64String(tiles[i].Jpeg),
                })),
            };
            if (p == packs.Count - 1 && cursor is not null)
                body["cur"] = cursor;
            if (key && p == 0)
                body["screens"] = new JArray(screens.Select((s, i) => new JObject { ["i"] = i, ["name"] = s.Name, ["w"] = s.Bounds.W, ["h"] = s.Bounds.H, ["primary"] = s.Primary }));
            SendSafely(new DeskPacket(SessionId, DeskRules.Frame, frameSeq, body));
        }

    }

    private void SendSafely(DeskPacket packet)
    {
        try
        {
            send(packet);
        }
        catch (Exception ex)
        {
            Log.Warn($"遠端桌面 {SessionId} 的封包送不出去：{ex.Message}");
        }
    }

    /// <summary>等一段時間（停下或設定改了就提早醒來）</summary>
    private void Wait(int ms)
    {
        lock (gate)
        {
            if (!stopped && !keyRequested)
                Monitor.Wait(gate, ms);
        }
    }

    private long NowMs() => time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>結束的原因（測試用）</summary>
    internal string? EndReason
    {
        get
        {
            lock (gate)
                return endReason;
        }
    }
}
