using System.Text.Json.Nodes;

namespace MyriagentHub.ProbeClient;

/// <summary>假裝置群裡一台的角色</summary>
internal enum FleetRole
{
    /// <summary>串流的對話（即時那一條：執行中的項目每 250 ms 一段、帶時間標記）</summary>
    Stream,

    /// <summary>持久那一條：每秒一段、每段各成一個完成的項目（completeEachSegment）</summary>
    Persistent,

    /// <summary>只連著（配對、心跳），不跑對話</summary>
    Idle,
}

/// <param name="Index">第幾台（0 起；資料根與名稱用它）</param>
/// <param name="Position">配對順序（0 起）</param>
/// <param name="Batch">第幾批配對（0 起）</param>
/// <param name="Title">對話的標題（＝送出的第一則訊息；瀏覽器以它認出要開的對話）；閒置的是 null</param>
internal sealed record FleetDevice(int Index, int Position, FleetRole Role, int Batch, string Name, string? Title);

/// <param name="Minutes">負載的分鐘數（串流與持久的對話要蓋滿：從全部配對好開始算）</param>
/// <param name="IdleMinutes">負載之後的閒置分鐘數（裝置照樣連著）</param>
/// <param name="DelayMs">串流每一段的間隔（桌面的節奏：每個對話每秒最多 4 則，設計:3187）</param>
/// <param name="SlackSecondsPerBatch">估計每一批配對要多久（較早配對的裝置多跑這麼久，才蓋得滿從全部配對好開始的負載期間）</param>
/// <param name="Batches">總批數</param>
/// <param name="SeedConversations">每台啟動前先寫進資料根的對話數（回補負載；0＝不寫）</param>
/// <param name="RelaySeconds">負載期間之後媒體轉送上限階段的秒數（WP4.9 步 12，<c>--relay-burst</c>；0＝不做）：串流與持久的對話多跑這麼久再加
/// <see cref="FleetRules.RelayMarginSeconds"/>，每台另外先跑一個只有媒體步驟的短對話</param>
internal sealed record FleetTiming(int Minutes, int IdleMinutes, int DelayMs, int SlackSecondsPerBatch, int Batches, int SeedConversations, int RelaySeconds = 0);

/// <summary>轉送階段的媒體檔（WP4.9 步 12）：完整路徑、長度、整份與前 <see cref="LoadRules.RelayAfterReleaseBytes"/> 位元組的 SHA-256（小寫十六進位）</summary>
internal sealed record RelayMediaFile(string Path, long Length, string Sha256, string PrefixSha256)
{
    public static RelayMediaFile Describe(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var prefix = Math.Min(content.Length, LoadRules.RelayAfterReleaseBytes);
        return new RelayMediaFile(path, content.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)),
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content.AsSpan(0, prefix))));
    }
}

/// <summary>
/// 假裝置群的分批、角色與劇本（WP4.9 計畫步 9、D6；純函式）：
/// <list type="bullet">
/// <item>配對一批最多 4 台、核准完再送下一批：伺服器的待核准上限是 5（Pairing.cs），10 台同時配對會撞上限。</item>
/// <item>配對順序：閒置的先、持久的其次、串流的最後（串流一開始就送，最後配對的最接近「全部配對好」那一刻）。</item>
/// <item>劇本（fake-device 的 session）：串流 <c>delayMs 250</c>、每步最多 1,000 段（ScriptedTurn.MaxSegments）、<c>stamp:true</c>，10 分鐘是三步；
/// 持久那一條另一個對話 <c>completeEachSegment:true</c>、每秒一段；跑完都再連著閒置一段時間才結束（session 接著等 outbox 清空、解除配對）。</item>
/// <item>媒體轉送上限階段（<see cref="FleetTiming.RelaySeconds"/> &gt; 0，WP4.9 步 12）：每台在 addAccount 之後先把遠端政策設成 View（原檔要 View 以上，
/// MediaServerRules.AllowsOriginals），再跑一個標題 <c>load-relay-NN</c>、只有一個 media 步驟（8 MB 的 .mp4）的短對話，之後照角色跑。
/// 媒體放在獨立的對話裡：串流與持久的項目不受影響，S7 量的是「其他對話」。</item>
/// </list>
/// </summary>
internal static class FleetRules
{
    /// <summary>一批最多幾台（D6）</summary>
    public const int MaxBatch = 4;

    /// <summary>一步最多幾段（fake-device 的 ScriptedTurn.MaxSegments）</summary>
    public const int MaxSegmentsPerStep = 1_000;

    /// <summary>劇本 wait 一步最多幾毫秒（FakeScript 的上限）</summary>
    public const int MaxWaitMs = 600_000;

    /// <summary>chat 一步的時限上限（FakeScript：秒）</summary>
    public const int MaxChatSeconds = 3_600;

    /// <summary>持久那一條每一段的間隔</summary>
    public const int PersistentDelayMs = 1_000;

    public const string StreamTitlePrefix = "load-stream-";

    public const string PersistentTitlePrefix = "load-persist-";

    /// <summary>媒體轉送上限階段的短對話（WP4.9 步 12）：每台一個，只有一個 media 步驟</summary>
    public const string RelayTitlePrefix = "load-relay-";

    /// <summary>裝置名稱的前綴（load-NN；NN 和標題的 NN 相同）</summary>
    public const string DeviceNamePrefix = "load-";

    /// <summary>轉送階段的檔案大小：正好一段（MediaRelayRules.MaxSegmentBytes＝8 MiB），沒帶 Range 的 GET 是一次轉送</summary>
    public const int RelayMediaBytes = 8 * 1024 * 1024;

    /// <summary>轉送檔案內容的種子：2000＋第幾台（每台不同，讀回來的 SHA-256 也證明網址對到哪一台）</summary>
    public const int RelaySeedBase = 2_000;

    /// <summary>轉送階段之後串流與持久的對話再多跑的秒數（等標頭最多 30 秒、慢速讀取可能晚完成）</summary>
    public const int RelayMarginSeconds = 60;

    /// <summary>媒體短對話的時限（秒）</summary>
    public const int RelayChatSeconds = 120;

    /// <summary>媒體短對話的遠端政策（原檔要 View 以上：MediaServerRules.AllowsOriginals；Off 時桌面回 403 policy）</summary>
    public const string RelayPolicyLevel = "View";

    /// <summary>劇本裡的帳戶別名（每台各自一個 Claude 帳戶）</summary>
    public const string AccountAlias = "load";

    /// <summary>回補的種子對話的基準時間（固定：同一份參數重跑寫出同一份對話）</summary>
    public const string SeedBaseTimeUtc = "2026-10-01T00:00:00Z";

    /// <summary>每台的種子：1000＋第幾台</summary>
    public const int SeedBase = 1_000;

    /// <summary>每一批的台數：10 台是 4、4、2</summary>
    public static IReadOnlyList<int> Batches(int devices, int maxPerBatch = MaxBatch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(devices);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPerBatch, 1);
        var result = new List<int>();
        for (var left = devices; left > 0; left -= maxPerBatch)
            result.Add(Math.Min(maxPerBatch, left));
        return result;
    }

    /// <summary>每台的角色、名稱與批次（依配對順序）；串流＋持久的台數超過總台數丟例外</summary>
    public static IReadOnlyList<FleetDevice> Plan(int devices, int streams, int persistent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(devices, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(streams);
        ArgumentOutOfRangeException.ThrowIfNegative(persistent);
        if (streams + persistent > devices)
            throw new ArgumentException($"串流 {streams} 台＋持久 {persistent} 台超過裝置數 {devices}（每台同時只跑一個對話）");
        var roles = Enumerable.Repeat(FleetRole.Idle, devices - streams - persistent)
            .Concat(Enumerable.Repeat(FleetRole.Persistent, persistent))
            .Concat(Enumerable.Repeat(FleetRole.Stream, streams))
            .ToList();
        var result = new List<FleetDevice>(devices);
        for (var position = 0; position < roles.Count; position++)
        {
            var role = roles[position];
            var number = (position + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
            var title = role switch
            {
                FleetRole.Stream => StreamTitlePrefix + number,
                FleetRole.Persistent => PersistentTitlePrefix + number,
                FleetRole.Idle => null,
                _ => throw new ArgumentOutOfRangeException(nameof(role), role, "未知的角色"),
            };
            result.Add(new FleetDevice(position, position, role, position / MaxBatch, "load-" + number, title));
        }

        return result;
    }

    /// <summary>
    /// 標題認得出的角色（瀏覽器開哪些對話）；不是 load 的對話回 null。媒體短對話（<see cref="RelayTitlePrefix"/>）回 <see cref="FleetRole.Idle"/>：
    /// 瀏覽器照樣開（才拿得到項目裡的 <c>/m/</c> 網址），但裡面沒有時間標記、不算延遲
    /// </summary>
    public static FleetRole? RoleOfTitle(string? title)
    {
        if (title is null)
            return null;
        if (title.StartsWith(StreamTitlePrefix, StringComparison.Ordinal))
            return FleetRole.Stream;
        if (title.StartsWith(PersistentTitlePrefix, StringComparison.Ordinal))
            return FleetRole.Persistent;
        if (title.StartsWith(RelayTitlePrefix, StringComparison.Ordinal))
            return FleetRole.Idle;
        return null;
    }

    /// <summary>這台的媒體短對話的標題（load-relay-NN，NN 同裝置名稱）</summary>
    public static string RelayTitle(FleetDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return RelayTitlePrefix + device.Name[DeviceNamePrefix.Length..];
    }

    /// <summary>媒體短對話的標題 → 裝置名稱（load-relay-03 → load-03）；不是媒體短對話回 null</summary>
    public static string? DeviceOfRelayTitle(string? title) =>
        title is not null && title.StartsWith(RelayTitlePrefix, StringComparison.Ordinal) && title.Length > RelayTitlePrefix.Length
            ? DeviceNamePrefix + title[RelayTitlePrefix.Length..]
            : null;

    /// <summary>
    /// 轉送階段的檔案內容（純函式、固定種子）：開頭是一個 ISO BMFF 的 <c>ftyp</c> 方塊（看起來像 .mp4；桌面與伺服器都只看副檔名決定型別），
    /// 其餘是 <c>new Random(seed)</c> 的位元組，總長 <paramref name="length"/>
    /// </summary>
    public static byte[] RelayMediaContent(int seed, int length = RelayMediaBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 24);
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        byte[] ftyp = [0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0,
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'m', (byte)'p', (byte)'4', (byte)'1'];
        ftyp.CopyTo(data, 0);
        return data;
    }

    /// <summary>劇本裡最後一個 chat 步驟是第幾步（1 起，同 FakeScript 進度行的「第 N 步」）；沒有 chat 回 null。角色的對話一定是最後一個 chat</summary>
    public static int? RoleChatStep(JsonObject script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (script["steps"] is not JsonArray steps)
            return null;
        int? last = null;
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i]?["op"]?.GetValue<string>() == "chat")
                last = i + 1;
        }

        return last;
    }

    /// <summary>這台比最後一批早配對多少秒（估計；較早配對的對話多跑這麼久）</summary>
    public static int LeadSeconds(FleetDevice device, FleetTiming timing) => timing.SlackSecondsPerBatch * Math.Max(0, timing.Batches - 1 - device.Batch);

    /// <summary>
    /// 對話（串流或持久）要跑幾秒：領先的秒數＋負載期間＋10 秒；有轉送階段時再加它的秒數與 <see cref="RelayMarginSeconds"/>（串流照跑，WP4.9 步 12）
    /// </summary>
    public static int ActiveSeconds(FleetDevice device, FleetTiming timing) =>
        LeadSeconds(device, timing) + timing.Minutes * 60 + 10 + (timing.RelaySeconds > 0 ? timing.RelaySeconds + RelayMarginSeconds : 0);

    /// <summary>
    /// 對話跑完之後再連著幾秒：閒置期間＋所有批次的估計差距＋30 秒（閒置期間從最後一個對話跑完才開始算，最早跑完的那台要撐到它結束）
    /// </summary>
    public static int TailSeconds(FleetTiming timing) => timing.IdleMinutes * 60 + timing.SlackSecondsPerBatch * timing.Batches + 30;

    /// <summary>這台的 session 估計要跑多久（不含配對與解除配對）：給呼叫端算等待的上限</summary>
    public static int TotalSeconds(FleetDevice device, FleetTiming timing) => ActiveSeconds(device, timing) + TailSeconds(timing);

    /// <summary>總段數切成一步一步（每步最多 1,000 段）：2,400 段是 1,000、1,000、400</summary>
    public static IReadOnlyList<int> SegmentSteps(int totalSegments)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSegments);
        var result = new List<int>();
        for (var left = totalSegments; left > 0; left -= MaxSegmentsPerStep)
            result.Add(Math.Min(MaxSegmentsPerStep, left));
        return result;
    }

    /// <summary>毫秒數切成一步一步的 wait（每步最多 10 分鐘）</summary>
    public static IReadOnlyList<int> WaitSteps(long totalMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalMs);
        var result = new List<int>();
        for (var left = totalMs; left > 0; left -= MaxWaitMs)
            result.Add((int)Math.Min(MaxWaitMs, left));
        return result;
    }

    /// <summary>fake-device session 的劇本（<c>{"steps":[…]}</c>）</summary>
    /// <param name="relayMedia">轉送階段的媒體檔（完整的本機路徑）；<see cref="FleetTiming.RelaySeconds"/> &gt; 0 時一定要有，否則一定是 null</param>
    public static JsonObject Script(FleetDevice device, FleetTiming timing, string? relayMedia = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timing);
        if (timing.DelayMs is < 1 or > 60_000)
            throw new ArgumentOutOfRangeException(nameof(timing), timing.DelayMs, "delayMs 要在 1～60000 之間");
        ArgumentOutOfRangeException.ThrowIfNegative(timing.RelaySeconds);
        if ((timing.RelaySeconds > 0) != (relayMedia is not null))
            throw new ArgumentException("有轉送階段（RelaySeconds > 0）時才要、而且一定要有媒體檔", nameof(relayMedia));
        if (relayMedia is not null && !Path.IsPathFullyQualified(relayMedia))
            throw new ArgumentException("媒體檔要是完整的本機路徑（桌面只收磁碟代號開頭的路徑）：" + relayMedia, nameof(relayMedia));
        var steps = new JsonArray();
        if (timing.SeedConversations > 0)
        {
            // seedConversations 一定要在最前面（中樞啟動之前寫進資料根）
            steps.Add(new JsonObject
            {
                ["op"] = "seedConversations",
                ["count"] = timing.SeedConversations,
                ["seed"] = SeedBase + device.Index,
                ["baseTimeUtc"] = SeedBaseTimeUtc,
                ["account"] = "seed",
            });
        }

        // 第一個在中樞啟動之後執行的步驟：它的進度行（「第 N 步 addAccount：」）代表這台已經配對、連上
        steps.Add(new JsonObject { ["op"] = "addAccount", ["alias"] = AccountAlias, ["provider"] = "Claude" });
        if (relayMedia is not null)
        {
            // 轉送階段：原檔要遠端政策 View 以上；媒體放在自己的短對話（不動串流與持久的項目）
            steps.Add(new JsonObject { ["op"] = "policy", ["level"] = RelayPolicyLevel });
            steps.Add(new JsonObject
            {
                ["op"] = "chat",
                ["conversation"] = "relay",
                ["account"] = AccountAlias,
                ["message"] = RelayTitle(device),
                ["seconds"] = RelayChatSeconds,
                ["script"] = new JsonObject { ["steps"] = new JsonArray(new JsonObject { ["media"] = relayMedia }) },
            });
        }

        var active = ActiveSeconds(device, timing);
        switch (device.Role)
        {
            case FleetRole.Stream:
            {
                var segments = (int)Math.Ceiling(active * 1000.0 / timing.DelayMs);
                steps.Add(Chat("stream", device.Title!, active, SegmentSteps(segments).Select(n => (JsonNode)new JsonObject
                {
                    ["text"] = Repeat("負載串流", n),
                    ["segments"] = n,
                    ["delayMs"] = timing.DelayMs,
                    ["stamp"] = true,
                })));
                AddWaits(steps, TailSeconds(timing) * 1000L);
                break;
            }
            case FleetRole.Persistent:
            {
                var segments = (int)Math.Ceiling(active * 1000.0 / PersistentDelayMs);
                steps.Add(Chat("persist", device.Title!, active, SegmentSteps(segments).Select(n => (JsonNode)new JsonObject
                {
                    ["text"] = Repeat("持久項目", n),
                    ["segments"] = n,
                    ["delayMs"] = PersistentDelayMs,
                    ["stamp"] = true,
                    ["completeEachSegment"] = true,
                })));
                AddWaits(steps, TailSeconds(timing) * 1000L);
                break;
            }
            case FleetRole.Idle:
                AddWaits(steps, TotalSeconds(device, timing) * 1000L);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(device), device.Role, "未知的角色");
        }

        return new JsonObject { ["steps"] = steps };
    }

    private static JsonObject Chat(string conversation, string message, int activeSeconds, IEnumerable<JsonNode> textSteps)
    {
        var seconds = activeSeconds + 120;
        if (seconds > MaxChatSeconds)
            throw new ArgumentOutOfRangeException(nameof(activeSeconds), activeSeconds, $"對話要跑 {activeSeconds} 秒，超過 chat 的時限上限（{MaxChatSeconds} 秒）");
        return new JsonObject
        {
            ["op"] = "chat",
            ["conversation"] = conversation,
            ["account"] = AccountAlias,
            ["message"] = message,
            ["seconds"] = seconds,
            ["script"] = new JsonObject { ["steps"] = new JsonArray(textSteps.ToArray()) },
        };
    }

    private static void AddWaits(JsonArray steps, long totalMs)
    {
        foreach (var ms in WaitSteps(totalMs))
            steps.Add(new JsonObject { ["op"] = "wait", ["ms"] = ms });
    }

    /// <summary>每一段 4 個字（全形中文：不是權杖字元，DLP 不會扣住或遮掉）</summary>
    private static string Repeat(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));
}
