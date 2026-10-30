using Myriagent.Security;
using Newtonsoft.Json.Linq;

namespace Myriagent.Sync;

/// <summary>GetSystemTimes 的三個累計值（100 ns 為單位；kernel 含 idle）</summary>
internal readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

/// <summary>
/// 各電腦即時狀態的規則（stat1；docs/sync-protocol.md 14.8）：格式與上限（伺服器收件、裝置收 <see cref="StatsUpdate"/> 共用）、
/// 送出的頻率、CPU 使用率與網路速率的計算（桌面的取樣器用）
/// </summary>
internal static class DeviceStatsRules
{
    public const int MaxDisks = 8;

    public const int MaxDiskNameLength = 16;

    public const int MaxCpuNameLength = 80;

    public const int MaxCores = 4096;

    public const int MaxGpus = 8;

    public const int MaxGpuNameLength = 80;

    /// <summary>CPU 溫度的範圍（攝氏）：防呆，超出的一律當成讀錯</summary>
    public const double MinCpuTemp = -50;

    public const double MaxCpuTemp = 150;

    public const int MaxTempSourceLength = 16;

    /// <summary>往返時間的上限（10 分鐘；呼叫逾時之前就會斷線）</summary>
    public const int MaxRttMs = 600_000;

    /// <summary>容量的上限（2^60 位元組，約 1 EiB）：防呆，真的電腦遠小於此</summary>
    public const long MaxBytes = 1L << 60;

    /// <summary>速率的上限（2^50 位元組／秒）</summary>
    public const long MaxRate = 1L << 50;

    /// <summary>開機時間的上限（100 年）</summary>
    public const long MaxUptimeSec = 100L * 365 * 24 * 3600;

    /// <summary><see cref="StatsUpdate"/> 最多幾台（同裝置名冊的上限）</summary>
    public const int MaxDevices = 256;

    /// <summary>MyriagentHub 主機名稱的長度上限</summary>
    public const int MaxHubNameLength = 64;

    /// <summary>主機卡片的連線數上限（防呆）</summary>
    public const int MaxHubConnections = 1_000_000;

    /// <summary>有人在看時多久送一次</summary>
    public static readonly TimeSpan WatchedInterval = TimeSpan.FromSeconds(2);

    /// <summary>沒人在看時多久送一次（延遲照樣量得到；有人開始看之後最慢這麼久就改回 2 秒）</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(10);

    /// <summary>伺服器：同一條連線兩次至少隔多久（比 2 秒短，容許計時誤差與網路抖動）；更密的不採用、照樣回 watched</summary>
    public const long MinPublishGapMs = 1_000;

    /// <summary>伺服器：一台多久沒送就從看板拿掉（沒人看時 10 秒一次，留兩次的餘裕）</summary>
    public const long StaleAfterMs = 35_000;

    /// <summary>伺服器送 <see cref="StatsUpdate"/>、瀏覽器的 <c>deviceStats</c>，以及桌面送網頁的 <c>deviceStats</c> 的最小間隔</summary>
    public const long FanoutIntervalMs = 1_000;

    /// <summary>這次送完之後多久再送（伺服器上一次回 watched 才用短的）</summary>
    public static TimeSpan PublishInterval(bool watched) => watched ? WatchedInterval : IdleInterval;

    /// <summary>格式與範圍；null＝合格，否則是說明（伺服器回 <c>invalid</c>、裝置略過整則）</summary>
    public static string? Check(DeviceStats? stats)
    {
        if (stats is null)
            return "沒有內容";
        if (!double.IsFinite(stats.Cpu) || stats.Cpu < 0 || stats.Cpu > 100)
            return "cpu 不在 0～100";
        if (stats.MemTotal <= 0 || stats.MemTotal > MaxBytes || stats.MemUsed < 0 || stats.MemUsed > stats.MemTotal)
            return "記憶體不正確";
        if (stats.Disks is null || stats.Disks.Count > MaxDisks)
            return "disks 不正確";
        foreach (var disk in stats.Disks)
        {
            if (disk is null || !LabelRules.IsClean(disk.Name, MaxDiskNameLength))
                return "磁碟名稱不正確";
            if (disk.Total <= 0 || disk.Total > MaxBytes || disk.Used < 0 || disk.Used > disk.Total)
                return "磁碟容量不正確";
        }
        if (stats.NetRx is < 0 or > MaxRate || stats.NetTx is < 0 or > MaxRate)
            return "網路速率不正確";
        if (stats.RttMs is < 0 or > MaxRttMs)
            return "rttMs 不正確";
        if (stats.Cores is < 1 or > MaxCores)
            return "cores 不正確";
        if (stats.CpuName is not null && !LabelRules.IsClean(stats.CpuName, MaxCpuNameLength))
            return "cpuName 不正確";
        if (stats.UptimeSec is < 0 or > MaxUptimeSec)
            return "uptimeSec 不正確";
        if (stats.Gpus is { } gpus)
        {
            if (gpus.Count > MaxGpus)
                return "gpus 不正確";
            foreach (var gpu in gpus)
            {
                if (CheckGpu(gpu) is { } problem)
                    return problem;
            }
        }
        if (stats.CpuTemp is { } temp && (!double.IsFinite(temp) || temp < MinCpuTemp || temp > MaxCpuTemp))
            return "cpuTemp 不正確";
        if (stats.CpuTempSource is not null && !LabelRules.IsClean(stats.CpuTempSource, MaxTempSourceLength))
            return "cpuTempSource 不正確";
        return null;
    }

    /// <summary>一張顯示卡：名稱乾淨、使用率 0～100、記憶體兩個都沒有或都合格；null＝合格</summary>
    public static string? CheckGpu(GpuStats? gpu)
    {
        if (gpu is null || !LabelRules.IsClean(gpu.Name, MaxGpuNameLength))
            return "GPU 名稱不正確";
        if (!double.IsFinite(gpu.Util) || gpu.Util < 0 || gpu.Util > 100)
            return "GPU 使用率不在 0～100";
        if (gpu.MemUsed is null != gpu.MemTotal is null
            || gpu.MemTotal is <= 0 or > MaxBytes || gpu.MemUsed < 0 || gpu.MemUsed > gpu.MemTotal)
            return "GPU 記憶體不正確";
        return null;
    }

    /// <summary>裝置收到的 <see cref="StatsUpdate"/>：最多 <see cref="MaxDevices"/> 台、deviceId 格式正確不重複、seq 與 ageMs 不是負的、每台的狀態合格；null＝合格</summary>
    public static string? CheckUpdate(StatsUpdate? update)
    {
        if (update?.Devices is not { } devices || devices.Count > MaxDevices)
            return "devices 不正確";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in devices)
        {
            if (entry is null || !SyncIds.IsDeviceId(entry.DeviceId) || !seen.Add(entry.DeviceId))
                return "deviceId 不正確或重複";
            if (entry.Seq < 0 || entry.AgeMs < 0)
                return "seq 或 ageMs 不正確";
            if (Check(entry.Stats) is { } problem)
                return problem;
        }
        if (update.Hub is { } hub)
        {
            if (!LabelRules.IsClean(hub.Name, MaxHubNameLength) || hub.Seq < 0 || hub.AgeMs < 0
                || hub.Devices is < 0 or > MaxHubConnections || hub.Browsers is < 0 or > MaxHubConnections)
                return "hub 不正確";
            if (Check(hub.Stats) is { } problem)
                return "hub：" + problem;
        }
        return null;
    }

    /// <summary>
    /// 兩次 GetSystemTimes 之間的 CPU 使用率（0～100，一位小數）：忙碌＝(kernel＋user)−idle（kernel 含 idle）。
    /// 計數倒退（不該發生）或兩次之間沒有經過時間回 null
    /// </summary>
    public static double? CpuPercent(CpuTimes previous, CpuTimes current)
    {
        if (current.Idle < previous.Idle || current.Kernel < previous.Kernel || current.User < previous.User)
            return null;
        var idle = current.Idle - previous.Idle;
        var total = current.Kernel - previous.Kernel + (current.User - previous.User);
        if (total == 0)
            return null;
        var busy = total > idle ? total - idle : 0;
        return Math.Round(Math.Clamp(busy * 100.0 / total, 0, 100), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 累計位元組 → 每秒位元組（四捨五入）。計數倒退（網卡重設、換了網卡）、經過時間不是正的回 0；夾在 <see cref="MaxRate"/> 以內
    /// </summary>
    public static long Rate(long previousBytes, long currentBytes, double elapsedMs)
    {
        if (elapsedMs <= 0 || currentBytes < previousBytes)
            return 0;
        var rate = Math.Round((currentBytes - previousBytes) * 1000.0 / elapsedMs, MidpointRounding.AwayFromZero);
        return rate >= MaxRate ? MaxRate : (long)rate;
    }

    /// <summary>往返時間（毫秒，無條件進位、至少 0）夾在 <see cref="MaxRttMs"/> 以內</summary>
    public static int RttMs(TimeSpan elapsed) => elapsed <= TimeSpan.Zero ? 0 : (int)Math.Min(MaxRttMs, Math.Ceiling(elapsed.TotalMilliseconds));

    /// <summary>
    /// 網頁的 DeviceStatsView（web-protocol 的 <c>deviceStats</c>；桌面的中樞與伺服器的瀏覽器閘道共用）：<paramref name="id"/> 是 deviceId
    /// （桌面未配對時這台是 null），<paramref name="self"/>＝這台（只有桌面），<paramref name="name"/> 只有這台帶（電腦名稱；其他電腦由網頁照 id 查）
    /// </summary>
    public static JObject WebView(string? id, bool self, string? name, long seq, long ageMs, DeviceStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new JObject
        {
            ["id"] = Text(id),
            ["self"] = self,
            ["name"] = Text(name),
            ["seq"] = seq,
            ["ageMs"] = Math.Max(0, ageMs),
            ["cpu"] = stats.Cpu,
            ["cores"] = stats.Cores,
            ["cpuName"] = Text(stats.CpuName),
            ["memUsed"] = stats.MemUsed,
            ["memTotal"] = stats.MemTotal,
            ["disks"] = new JArray((stats.Disks ?? []).Select(d => new JObject { ["name"] = d.Name, ["used"] = d.Used, ["total"] = d.Total })),
            ["netRx"] = stats.NetRx,
            ["netTx"] = stats.NetTx,
            ["rttMs"] = stats.RttMs,
            ["uptimeSec"] = stats.UptimeSec,
            ["gpus"] = new JArray((stats.Gpus ?? []).Select(g => new JObject
            {
                ["name"] = g.Name,
                ["util"] = g.Util,
                ["memUsed"] = g.MemUsed,
                ["memTotal"] = g.MemTotal,
            })),
            ["cpuTemp"] = stats.CpuTemp,
            ["cpuTempSource"] = Text(stats.CpuTempSource),
        };
    }

    /// <summary>字串欄位：null 是 JSON null（JValue 的字串建構子會把 null 記成字串型別）</summary>
    private static JToken Text(string? value) => value is null ? JValue.CreateNull() : new JValue(value);

    /// <summary>網頁的 HubStatsView（<c>deviceStats</c> 的 <c>hub</c>）：DeviceStatsView 的數值欄位（沒有 id、self、rttMs）加 <c>devices</c>、<c>browsers</c></summary>
    public static JObject HubWebView(HubStatsEntry hub, long ageMs)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var view = WebView(null, self: false, hub.Name, hub.Seq, ageMs, hub.Stats);
        view.Remove("id");
        view.Remove("self");
        view.Remove("rttMs");
        view["devices"] = hub.Devices;
        view["browsers"] = hub.Browsers;
        return view;
    }
}
