using Myriagent.Core;
using Myriagent.Remote;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.RemoteDesk;

/// <summary>
/// 被看的電腦上的一個遠端桌面工作階段（docs/sync-protocol.md 14.16）：專用的擷取執行緒照 <see cref="DeskRules"/> 的流量控制送影格
/// （未確認的影格與位元組有上限、依往返時間調每秒格數與畫質、沒變化時每 <see cref="DeskRules.Heartbeat"/> 送一個空影格），
/// 看的一方的封包（輸入、確認、設定、結束）由 <see cref="OnPacket"/> 交進來。<see cref="DeskRules.AckTimeout"/> 沒有確認、看的一方結束、
/// <see cref="Stop"/> 都會停下，停下時放開還按著的鍵並呼叫 <c>ended</c>（只一次，擷取執行緒或呼叫 Stop 的執行緒）。
/// 擷取不到（鎖定、安全桌面、一直要重來）時送 <c>status</c> 說明原因，不讓看的一方對著「連線到…」空等；鎖定與安全桌面期間不注入輸入。
/// 畫面（<see cref="IDeskScreen"/>）由 <see cref="ReleaseScreen"/> 釋放：擷取執行緒還在用的時候等它結束才釋放
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
    private bool loopEnded;
    private bool releaseRequested;
    private bool screenReleased;

    /// <summary>鎖定或安全桌面：輸入送不進去（鎖定時還會白白叫醒那台的螢幕），先不注入</summary>
    private volatile bool inputBlocked;

    /// <summary>看的一方給了公鑰時這個工作階段的輸入解密（沒有就是 null：收明文）</summary>
    private readonly DeskInputCipher? cipher;

    private bool sealedInputWarned;

    public DeskSession(string sessionId, string viewerLabel, IDeskScreen screen, Action<DeskPacket> send, Action<DeskSession, string> ended, TimeProvider? time = null,
        DeskInputCipher? cipher = null)
    {
        SessionId = sessionId;
        ViewerLabel = viewerLabel;
        this.screen = screen;
        this.send = send;
        this.ended = ended;
        this.time = time ?? TimeProvider.System;
        this.cipher = cipher;
    }

    /// <summary>這個工作階段的輸入是端對端加密的（看的一方給了合格的公鑰）</summary>
    public bool InputSealed => cipher is not null;

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
        // 輸入的加密：自己的公鑰一定是第一個封包（看的一方看第一個封包決定之後的輸入要不要加密）
        if (cipher is not null)
            SendSafely(new DeskPacket(SessionId, DeskRules.Status, 0, DeskRules.KeyExchangeBody(cipher.PublicKey)));
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
                if (Stopped)
                    break;
                if (DeskSealRules.SealedOf(packet.Body, out _) is { } sealedInput)
                {
                    // 加密的輸入：只有這個工作階段的金鑰解得開；解不開（沒有金鑰、被動過、重送）就丟掉
                    if (cipher?.Open(sealedInput.N, sealedInput.Ct) is { } opened)
                    {
                        if (opened.Count > 0)
                            InjectSafely(opened);
                    }
                    else if (!sealedInputWarned)
                    {
                        sealedInputWarned = true;
                        Log.Warn($"遠端桌面 {SessionId} 收到解不開的輸入（第 {sealedInput.N} 包），已丟掉");
                    }
                }
                else if (cipher is { Established: true })
                {
                    // 看的一方已經在加密了：之後明文的事件一律不收（不讓中途被換回明文）
                }
                else if (DeskRules.ParseInput(packet.Body) is { Count: > 0 } events)
                {
                    InjectSafely(events);
                }

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

    /// <summary>
    /// 釋放畫面（停下之後呼叫，任何執行緒）：擷取執行緒可能還在擷取的途中（讀回顯示卡的材質），這時釋放會讓它讀到已經釋放的記憶體——
    /// 還在跑就記下來，由擷取執行緒結束時釋放
    /// </summary>
    public void ReleaseScreen()
    {
        lock (gate)
        {
            if (screenReleased)
                return;
            if (thread is not null && !loopEnded)
            {
                releaseRequested = true;
                return;
            }

            screenReleased = true;
        }

        DisposeScreen();
    }

    private void DisposeScreen()
    {
        try
        {
            screen.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"遠端桌面 {SessionId} 的畫面釋放失敗：{ex.Message}");
        }
    }

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
        if (inputBlocked)
            return;
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
        string? noteShown = null;
        var statusSentMs = 0L;
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
                if (captured is DeskCapture.Retry or DeskCapture.Failed or DeskCapture.Locked)
                {
                    // 擷取不到。Retry 是擷取的方式換了（大小可能不同）：馬上重來，連續太多次就等一下、也告訴看的一方；
                    // 鎖定、安全桌面：說明原因，每秒再看一次
                    retries = captured == DeskCapture.Retry ? retries + 1 : 0;
                    inputBlocked = captured != DeskCapture.Retry;
                    // 同一個原因每隔一陣子重送：這段期間兩邊都沒有別的封包，伺服器太久沒有封包會忘掉工作階段（等別人解除鎖定可能很久）
                    if (DeskScreenRules.StatusText(captured, retries) is { } text
                        && (text != statusShown || NowMs() - statusSentMs >= DeskScreenRules.StatusRepeatMs))
                    {
                        statusShown = text;
                        noteShown = null;
                        statusSentMs = NowMs();
                        SendSafely(new DeskPacket(SessionId, DeskRules.Status, 0, new JObject { ["text"] = text }));
                    }

                    needKey = true;
                    if (captured == DeskCapture.Retry)
                    {
                        lastSize = (0, 0);
                        if (retries > 3)
                            Wait(500);
                    }
                    else
                    {
                        Wait(1000);
                    }

                    continue;
                }

                retries = 0;
                // 有畫面但只能看（安全桌面的畫面是鎖定畫面支援的幫手給的，SecureDeskScreen）：不注入輸入，說明放在 note（畫面照常顯示）
                var note = (screen as IDeskViewOnly)?.ViewOnlyNote;
                inputBlocked = note is not null;
                if (statusShown is not null || note != noteShown)
                {
                    statusShown = null;
                    noteShown = note;
                    SendSafely(new DeskPacket(SessionId, DeskRules.Status, 0, DeskRules.StatusBody(null, note)));
                }

                IReadOnlyList<DeskTile> tiles = captured == DeskCapture.Unchanged
                    ? []
                    : encoder.Encode(previous, current, w, h, DeskRules.QualityFor(q, rtt, w), key);
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
        finally
        {
            try
            {
                screen.CaptureEnded();
            }
            catch (Exception ex)
            {
                Log.Warn($"遠端桌面 {SessionId} 的擷取收尾失敗：{ex.Message}");
            }

            bool release;
            lock (gate)
            {
                loopEnded = true;
                release = releaseRequested && !screenReleased;
                screenReleased |= release;
            }

            if (release)
                DisposeScreen();
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
        // 停下之後擷取執行緒手上剛好壓好的影格不再送（end 之後才到的影格看的一方用不到）
        if (packet.Kind != DeskRules.End && Stopped)
            return;
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
