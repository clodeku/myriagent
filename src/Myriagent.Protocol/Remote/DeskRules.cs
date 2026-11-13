using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Remote;

/// <summary>遠端桌面的一個輸入事件（<see cref="DeskRules.ParseInput"/> 檢查過的）。座標是 0～1 的比例</summary>
internal sealed record DeskInputEvent(string Type, double X = 0, double Y = 0, int Button = 0, int Dx = 0, int Dy = 0, int Key = 0, string? Text = null);

/// <summary>整數矩形（像素）</summary>
internal readonly record struct DeskRect(int X, int Y, int W, int H)
{
    public int Right => X + W;

    public int Bottom => Y + H;
}

/// <summary>
/// 遠端桌面（docs/sync-protocol.md 14.16，<c>desk1</c>；計畫 docs/sync-protocol.md 14.15 第 2 階段）的純規則：
/// 封包的種類與方向、輸入事件的格式、座標換算、影格的格子合併與拆包、流量控制與畫質。桌面、伺服器、網頁的規則（remotedeskrules.js）照同一份
/// </summary>
internal static class DeskRules
{
    // 被看的電腦 → 看的一方
    public const string Frame = "frame";
    public const string Status = "status";
    public const string End = "end";

    // 看的一方 → 被看的電腦（ack 的 body 是 {seq}；input 也可以帶 ack）
    public const string Input = "input";
    public const string Ack = "ack";
    public const string Config = "config";
    public const string Close = "close";

    /// <summary>一個封包序列化後的上限（裝置 hub 1 MB、瀏覽器佇列 2 MB 之內；影格超過就拆成幾個封包）</summary>
    public const int MaxPacketBytes = 600 * 1024;

    /// <summary>一包輸入最多幾個事件</summary>
    public const int MaxEvents = 64;

    /// <summary>一個文字事件最多幾個字</summary>
    public const int MaxText = 1000;

    /// <summary>伺服器同時最多幾個工作階段</summary>
    public const int MaxSessions = 16;

    /// <summary>傳送的畫面寬度上限（那台的螢幕比它寬就縮小）</summary>
    public const int MaxWidth = 1920;

    /// <summary>傳送的畫面高度上限</summary>
    public const int MaxHeight = 1200;

    /// <summary>
    /// 看的一方可以要求的傳送寬度上限（2026-11-10：放大看細節時要更清楚的畫面；沒有要求時照 <see cref="MaxWidth"/>）。
    /// 比 <see cref="MaxWidth"/> 寬時高度上限放寬成寬度的 3/4、JPEG 品質降一點（<see cref="QualityFor"/>），整個畫面重送才不會太大
    /// </summary>
    public const int MaxRequestWidth = 3840;

    /// <summary>比對變動的格子大小（像素）</summary>
    public const int Tile = 64;

    /// <summary>未確認的影格最多幾個</summary>
    public const int MaxInflightFrames = 2;

    /// <summary>未確認的位元組最多多少</summary>
    public const long MaxInflightBytes = 1_200_000;

    /// <summary>被看的電腦這麼久沒收到任何確認就結束（沒人在看）</summary>
    public static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>伺服器：工作階段這麼久沒有封包就忘掉</summary>
    public static readonly TimeSpan IdleForget = TimeSpan.FromMinutes(10);

    /// <summary>伺服器：看的電腦這麼久沒有連線就替它結束</summary>
    public static readonly TimeSpan OriginLostLimit = TimeSpan.FromSeconds(30);

    /// <summary>畫質的選擇（config 的 quality）</summary>
    public static readonly IReadOnlyList<string> Qualities = ["auto", "sharp", "smooth"];

    private const string SessionChars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._:-";

    /// <summary>工作階段 id：8～64 個英數字與 <c>._:-</c>（同型錄的 requestId，另要至少 8 個字）</summary>
    public static bool IsSessionId(string? id)
        => id is { Length: >= 8 and <= 64 } && id.AsSpan().IndexOfAnyExcept(SessionChars) < 0;

    /// <summary>被看的電腦送的種類</summary>
    public static bool IsTargetKind(string? kind) => kind is Frame or Status or End;

    /// <summary>
    /// <c>status</c> 的內容：<paramref name="text"/> 是擷取不到的說明（蓋在畫面中間；null＝收掉），<paramref name="note"/> 是有畫面時的附註
    /// （例：安全桌面目前只能看；放在工具列旁，不擋畫面。沒有就不帶這個鍵——舊的網頁本來就不認得它）
    /// </summary>
    public static JObject StatusBody(string? text, string? note) => StatusBody(text, note, secure: false, servicePublic: null);

    /// <summary>
    /// 同上，另帶安全桌面的狀態（鎖定畫面支援）：<paramref name="secure"/>＝現在的畫面是安全桌面（鎖定畫面、UAC）、
    /// 由服務的幫手擷取；<paramref name="servicePublic"/>＝服務這個工作階段的公鑰（<c>svc {v, pub}</c>）——有它，看的一方在安全桌面時
    /// 把輸入以服務的金鑰加密（<see cref="DeskSealRules.SecureInfo"/>），由服務解開、注入；沒有就只能看。兩個都是有才帶
    /// </summary>
    public static JObject StatusBody(string? text, string? note, bool secure, string? servicePublic)
    {
        var body = new JObject { ["text"] = text is null ? JValue.CreateNull() : text };
        if (!string.IsNullOrEmpty(note))
            body["note"] = note;
        if (secure)
            body["secure"] = true;
        if (!string.IsNullOrEmpty(servicePublic))
            body["svc"] = DeskSealRules.KeyExchange(servicePublic);
        return body;
    }

    /// <summary>
    /// 被看的一方的第一個封包：<c>status {text: null, kx}</c>——接受了看的一方在 <c>deskOpen</c> 給的公鑰、回自己的（<see cref="DeskSealRules"/>）。
    /// 只在看的一方給了公鑰時送（那是認得 <c>kx</c> 的網頁）；看的一方看到它就開始加密輸入，第一個封包沒有它就照舊送明文
    /// </summary>
    public static JObject KeyExchangeBody(string targetPublic) => new() { ["text"] = JValue.CreateNull(), ["kx"] = DeskSealRules.KeyExchange(targetPublic) };

    /// <summary>看的一方送的種類</summary>
    public static bool IsViewerKind(string? kind) => kind is Input or Ack or Config or Close;

    /// <summary>
    /// 封包的格式檢查（伺服器轉送前、兩邊收到時）：工作階段 id、方向、seq、大小，輸入另驗事件。合格回 null，否則回原因
    /// </summary>
    public static string? Check(DeskPacket? packet, bool fromTarget)
    {
        if (packet is null)
            return "沒有封包";
        if (!IsSessionId(packet.SessionId))
            return "工作階段 id 不合格";
        if (fromTarget ? !IsTargetKind(packet.Kind) : !IsViewerKind(packet.Kind))
            return "種類不對";
        if (packet.Seq < 0)
            return "seq 不合格";
        if (packet.Body is { } body && System.Text.Encoding.UTF8.GetByteCount(body.ToString(Formatting.None)) > MaxPacketBytes)
            return "封包太大";
        if (packet.Kind == Input && ParseInput(packet.Body) is null)
            return "輸入的格式不對";
        if (packet.Kind == Ack && AckOf(packet.Body) is null)
            return "確認的格式不對";
        if (packet.Kind == Config && ParseConfig(packet.Body) is null)
            return "設定的格式不對";
        return null;
    }

    /// <summary>
    /// 輸入事件（<c>body.events</c>，0～<see cref="MaxEvents"/> 個；可以順便帶 <c>ack</c>：瀏覽器每秒的指令數有上限，網頁把確認併進輸入一起送）。
    /// 格式不對、或事件與 ack 都沒有，整包回 null。事件端對端加密時（<c>body.e2e</c>，<see cref="DeskSealRules"/>）這裡只檢查形狀、回空的清單——
    /// 內容只有被看的那台解得開（伺服器照樣以這個函式檢查封包）；加密的和明文的事件不能放在同一包
    /// </summary>
    public static IReadOnlyList<DeskInputEvent>? ParseInput(JObject? body)
    {
        if (body is null)
            return null;
        var hasAck = AckOf(body) is not null;
        var sealedInput = DeskSealRules.SealedOf(body, out var malformed);
        if (malformed || sealedInput is not null && body["events"] is not (null or { Type: JTokenType.Null }))
            return null;
        if (sealedInput is not null)
            return [];
        if (body["events"] is null or { Type: JTokenType.Null })
            return hasAck ? [] : null;
        if (body["events"] is not JArray { Count: <= MaxEvents } events || events.Count == 0 && !hasAck)
            return null;
        var list = new List<DeskInputEvent>(events.Count);
        foreach (var token in events)
        {
            if (token is not JObject e || e["t"] is not JValue { Type: JTokenType.String, Value: string type })
                return null;
            DeskInputEvent? parsed = type switch
            {
                "mm" => Point(e, out var x, out var y) ? new DeskInputEvent(type, x, y) : null,
                "md" or "mu" => Point(e, out var x, out var y) && Int(e["b"], 0, 2, out var b) ? new DeskInputEvent(type, x, y, Button: b) : null,
                "wh" => Int(e["dy"], -2400, 2400, out var dy) && OptionalInt(e["dx"], -2400, 2400, out var dx) ? new DeskInputEvent(type, Dx: dx, Dy: dy) : null,
                "kd" or "ku" => Int(e["k"], 1, 254, out var k) ? new DeskInputEvent(type, Key: k) : null,
                "tx" => e["s"] is JValue { Type: JTokenType.String, Value: string s } && s.Length is > 0 and <= MaxText && !s.Any(c => c < ' ' && c is not '\n' and not '\t')
                    ? new DeskInputEvent(type, Text: s) : null,
                _ => null,
            };
            if (parsed is null)
                return null;
            list.Add(parsed);
        }

        return list;
    }

    /// <summary>確認到哪一個影格（<c>ack</c> 封包的 <c>body.seq</c>，或 <c>input</c> 的 <c>body.ack</c>）；沒有回 null</summary>
    public static long? AckOf(JObject? body)
    {
        var token = body?["ack"] ?? body?["seq"];
        return token is JValue { Type: JTokenType.Integer } v && Convert.ToInt64(v.Value, System.Globalization.CultureInfo.InvariantCulture) is >= 0 and var seq ? seq : null;
    }

    /// <summary>設定（<c>body</c>：<c>quality</c>、<c>screen</c>、<c>maxWidth</c>〔傳送寬度的上限〕、<c>key</c>〔要求整個畫面重送〕都是選用的）；格式不對回 null</summary>
    public static DeskConfig? ParseConfig(JObject? body)
    {
        if (body is null)
            return null;
        string? quality = null;
        if (body["quality"] is { Type: not JTokenType.Null } q)
        {
            if (q is not JValue { Type: JTokenType.String, Value: string qs } || !Qualities.Contains(qs))
                return null;
            quality = qs;
        }

        int? screen = null;
        if (body["screen"] is { Type: not JTokenType.Null } sc)
        {
            if (!Int(sc, 0, 15, out var si))
                return null;
            screen = si;
        }

        int? maxWidth = null;
        if (body["maxWidth"] is { Type: not JTokenType.Null } mw)
        {
            if (!Int(mw, MinSendWidth, MaxRequestWidth, out var mwi))
                return null;
            maxWidth = mwi;
        }

        var key = body["key"] is JValue { Type: JTokenType.Boolean, Value: true };
        return new DeskConfig(quality, screen, key, maxWidth);
    }

    /// <summary>
    /// 比例座標（0～1，相對於擷取的那個螢幕 <paramref name="screen"/>）換成 SendInput 的虛擬桌面絕對座標（0～65535，相對於 <paramref name="virtualScreen"/>）
    /// </summary>
    public static (int X, int Y) ToAbsolute(double nx, double ny, DeskRect screen, DeskRect virtualScreen)
    {
        var px = screen.X + Math.Clamp(nx, 0, 1) * Math.Max(0, screen.W - 1);
        var py = screen.Y + Math.Clamp(ny, 0, 1) * Math.Max(0, screen.H - 1);
        var ax = virtualScreen.W <= 1 ? 0 : (px - virtualScreen.X) * 65535.0 / (virtualScreen.W - 1);
        var ay = virtualScreen.H <= 1 ? 0 : (py - virtualScreen.Y) * 65535.0 / (virtualScreen.H - 1);
        return ((int)Math.Round(Math.Clamp(ax, 0, 65535)), (int)Math.Round(Math.Clamp(ay, 0, 65535)));
    }

    /// <summary>看的一方可以要求的最小傳送寬度（手機的小螢幕要求小一點，省頻寬、畫面更快）</summary>
    public const int MinSendWidth = 640;

    /// <summary>
    /// 那台的螢幕縮成傳送的大小（等比例、寬 ≤ <paramref name="maxWidth"/>〔看的一方要求的，沒有是 <see cref="MaxWidth"/>〕、高 ≤ <see cref="MaxHeight"/>、
    /// 寬高取偶數）
    /// </summary>
    public static (int W, int H) SendSize(int width, int height, int? maxWidth = null)
    {
        if (width <= 0 || height <= 0)
            return (2, 2);
        var limit = Math.Clamp(maxWidth ?? MaxWidth, MinSendWidth, MaxRequestWidth);
        var heightLimit = limit > MaxWidth ? limit * 3 / 4 : MaxHeight;
        var scale = Math.Min(1.0, Math.Min((double)limit / width, (double)heightLimit / height));
        var w = Math.Max(2, (int)Math.Round(width * scale) & ~1);
        var h = Math.Max(2, (int)Math.Round(height * scale) & ~1);
        return (w, h);
    }

    /// <summary>
    /// GPU 縮小（mipmap，每一層寬高減半）要用第幾層：寬度 ≤ 上限×1.25 的第一層（上限是看的一方要求的、沒有是 <see cref="MaxWidth"/>）。
    /// 例：3072 寬的螢幕、上限 1920 → 第 1 層（1536）；3840 → 第 1 層（1920）；1920 → 第 0 層（原尺寸）
    /// </summary>
    public static int MipLevel(int width, int? maxWidth)
    {
        var limit = Math.Clamp(maxWidth ?? MaxWidth, MinSendWidth, MaxRequestWidth) * 5 / 4;
        var level = 0;
        while ((width >> level) > limit && (width >> (level + 1)) >= MinSendWidth / 2)
            level++;
        return level;
    }

    /// <summary>第 <paramref name="level"/> 層的大小（每層減半、至少 1）</summary>
    public static (int W, int H) MipSize(int width, int height, int level) => (Math.Max(1, width >> level), Math.Max(1, height >> level));

    /// <summary>
    /// 變動的格子（<paramref name="changed"/> 以列為主，<paramref name="cols"/>×<paramref name="rows"/>）合併成矩形：同一列相連的併成一段，
    /// 上下相鄰而且起訖相同的段再往下併。回的是格子座標（乘上 <see cref="Tile"/>、裁到畫面大小由呼叫端做）
    /// </summary>
    public static List<DeskRect> MergeTiles(bool[] changed, int cols, int rows)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (changed.Length < cols * rows)
            throw new ArgumentException("格子數不對", nameof(changed));
        var open = new Dictionary<(int Start, int Len), int>();
        var result = new List<DeskRect>();
        for (var r = 0; r < rows; r++)
        {
            var runs = new List<(int Start, int Len)>();
            for (var c = 0; c < cols;)
            {
                if (!changed[r * cols + c])
                {
                    c++;
                    continue;
                }

                var start = c;
                while (c < cols && changed[r * cols + c])
                    c++;
                runs.Add((start, c - start));
            }

            var next = new Dictionary<(int Start, int Len), int>();
            foreach (var run in runs)
            {
                if (open.TryGetValue(run, out var index))
                {
                    var rect = result[index];
                    result[index] = rect with { H = rect.H + 1 };
                    next[run] = index;
                }
                else
                {
                    result.Add(new DeskRect(run.Start, r, run.Len, 1));
                    next[run] = result.Count - 1;
                }
            }

            open = next;
        }

        return result;
    }

    /// <summary>合併後的矩形最多幾列格子高（整個畫面重送時才不會壓成一張太大的 JPEG）</summary>
    public const int MaxBandRows = 4;

    /// <summary>太高的矩形（格子座標）切成每段最多 <paramref name="maxRows"/> 列</summary>
    public static IEnumerable<DeskRect> SplitTall(DeskRect tiles, int maxRows = MaxBandRows)
    {
        for (var y = 0; y < tiles.H; y += maxRows)
            yield return tiles with { Y = tiles.Y + y, H = Math.Min(maxRows, tiles.H - y) };
    }

    /// <summary>被看的電腦畫面沒有變化時，隔多久送一個空的影格（讓看的一方確認還在、伺服器看得出看的電腦還在不在）</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(5);

    /// <summary>JPEG 轉成 base64 之後大約多大（拆包的估計：4/3 再加上欄位）</summary>
    public static int EncodedSize(int jpegBytes) => (jpegBytes + 2) / 3 * 4 + 64;

    /// <summary>格子座標的矩形換成像素（裁到 <paramref name="width"/>×<paramref name="height"/>）</summary>
    public static DeskRect ToPixels(DeskRect tiles, int width, int height)
    {
        var x = tiles.X * Tile;
        var y = tiles.Y * Tile;
        return new DeskRect(x, y, Math.Min(tiles.W * Tile, width - x), Math.Min(tiles.H * Tile, height - y));
    }

    /// <summary>依大小把區塊分成幾包（每包的總大小 ≤ <paramref name="budget"/>；單一區塊比預算大的自己一包）；回每包的索引</summary>
    public static List<List<int>> SplitBySize(IReadOnlyList<int> sizes, int budget)
    {
        var packs = new List<List<int>>();
        var current = new List<int>();
        long total = 0;
        for (var i = 0; i < sizes.Count; i++)
        {
            if (current.Count > 0 && total + sizes[i] > budget)
            {
                packs.Add(current);
                current = [];
                total = 0;
            }

            current.Add(i);
            total += sizes[i];
        }

        if (current.Count > 0)
            packs.Add(current);
        return packs;
    }

    /// <summary>還能不能送下一個影格（未確認的影格數與位元組都在上限內）</summary>
    public static bool CanSend(int inflightFrames, long inflightBytes)
        => inflightFrames < MaxInflightFrames && inflightBytes < MaxInflightBytes;

    /// <summary>JPEG 品質：清晰固定 80、流暢固定 45，自動依往返時間（&lt;150 ms 70、&lt;400 ms 55、其他 40）</summary>
    /// <summary>依傳送寬度調整的品質：比 2560 寬的（放大看細節時要求的）降 15、至少 35，整個畫面重送才不會太大</summary>
    public static int QualityFor(string? mode, double rttMs, int width)
        => width > 2560 ? Math.Max(35, Quality(mode, rttMs) - 15) : Quality(mode, rttMs);

    public static int Quality(string? mode, double rttMs) => mode switch
    {
        "sharp" => 80,
        "smooth" => 45,
        _ => rttMs < 150 ? 70 : rttMs < 400 ? 55 : 40,
    };

    /// <summary>兩次擷取之間至少隔多久（毫秒）：流暢 66、清晰 150，自動依往返時間 100／200／400</summary>
    public static int IntervalMs(string? mode, double rttMs) => mode switch
    {
        "smooth" => 66,
        "sharp" => 150,
        _ => rttMs < 150 ? 100 : rttMs < 400 ? 200 : 400,
    };

    /// <summary>往返時間的平滑（指數移動平均，新的一筆佔 1/4；還沒有值時直接用新的）</summary>
    public static double SmoothRtt(double previous, double sample)
        => previous <= 0 ? sample : previous * 0.75 + sample * 0.25;

    /// <summary>結束封包的 body</summary>
    public static JObject EndBody(string reason, string text) => new() { ["reason"] = reason, ["text"] = text };

    private static bool Point(JObject e, out double x, out double y)
    {
        x = y = 0;
        return Number(e["x"], out x) && Number(e["y"], out y) && x is >= 0 and <= 1 && y is >= 0 and <= 1;
    }

    private static bool Number(JToken? token, out double value)
    {
        value = 0;
        if (token is not JValue { Type: JTokenType.Float or JTokenType.Integer } v)
            return false;
        value = Convert.ToDouble(v.Value, System.Globalization.CultureInfo.InvariantCulture);
        return double.IsFinite(value);
    }

    private static bool Int(JToken? token, int min, int max, out int value)
    {
        value = 0;
        if (token is not JValue { Type: JTokenType.Integer } v)
            return false;
        var l = Convert.ToInt64(v.Value, System.Globalization.CultureInfo.InvariantCulture);
        if (l < min || l > max)
            return false;
        value = (int)l;
        return true;
    }

    private static bool OptionalInt(JToken? token, int min, int max, out int value)
    {
        value = 0;
        return token is null || token.Type == JTokenType.Null || Int(token, min, max, out value);
    }
}

/// <summary>看的一方改的設定（null＝不改）；<c>Key</c>＝要求整個畫面重送</summary>
internal sealed record DeskConfig(string? Quality, int? Screen, bool Key, int? MaxWidth = null);
