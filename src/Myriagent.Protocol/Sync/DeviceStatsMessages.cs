namespace Myriagent.Sync;

// 各電腦的即時狀態（stat1；docs/sync-protocol.md 14.8）：給人看的即時資料，不寫進任何持久事件、伺服器只放在記憶體，
// 可以合併、可以丟。時間一律不跨電腦比較：伺服器以自己收到的時間算「這筆樣本已經多舊」（ageMs）再送出

/// <summary>
/// 一台電腦這一刻的狀態（<see cref="DeviceStatsRules.Check"/> 檢查）。<c>cpu</c> 是全部核心的平均使用率（0～100）；記憶體、磁碟是位元組；
/// <c>netRx</c>／<c>netTx</c> 是所有實體與無線網卡合計的每秒位元組數；<c>rttMs</c> 是這台和伺服器上一次 <c>StatsPublish</c> 的往返時間
/// （沒量過是 null）。<c>cores</c>、<c>cpuName</c>、<c>uptimeSec</c> 只供顯示，拿不到是 null。
/// <c>gpus</c> 是各張顯示卡（拿不到、伺服器主機與舊版是 null），<c>cpuTemp</c> 是 CPU 溫度（攝氏、一位小數），<c>cpuTempSource</c> 是溫度從哪裡讀的
/// （<see cref="DeviceSensorRules"/> 的 <c>Source*</c>；讀不到時兩個都是 null）
/// </summary>
internal sealed record DeviceStats(
    double Cpu,
    long MemUsed,
    long MemTotal,
    IReadOnlyList<DiskStats> Disks,
    long NetRx,
    long NetTx,
    int? RttMs = null,
    int? Cores = null,
    string? CpuName = null,
    long? UptimeSec = null,
    IReadOnlyList<GpuStats>? Gpus = null,
    double? CpuTemp = null,
    string? CpuTempSource = null);

/// <summary>一個固定磁碟（<c>name</c> 是磁碟機代號，例如 <c>C:</c>）</summary>
internal sealed record DiskStats(string Name, long Used, long Total);

/// <summary>
/// 一張顯示卡：<c>util</c> 是使用率（0～100、一位小數；各引擎〔3D、複製、視訊解碼…〕中最忙的那個，同工作管理員），
/// <c>memUsed</c>／<c>memTotal</c> 是專用記憶體的已用與總量（位元組；拿不到時兩個都是 null）
/// </summary>
internal sealed record GpuStats(string Name, double Util, long? MemUsed = null, long? MemTotal = null);

/// <summary>
/// 裝置 → 伺服器（Invoke，<c>stat1</c>）：這台的狀態，<c>watch</c>＝這台現在有畫面要看其他電腦的狀態（要收 <see cref="StatsUpdate"/>）。
/// 回 <see cref="StatsPublishResult"/>；裝置以這次呼叫的往返時間當下一次的 <c>rttMs</c>
/// </summary>
internal sealed record StatsPublish(DeviceStats Stats, bool Watch);

/// <summary><see cref="StatsPublish"/> 的回應：<c>watched</c>＝現在有別人在看（其他電腦或瀏覽器），裝置照它決定多久送一次（<see cref="DeviceStatsRules.PublishInterval"/>）</summary>
internal sealed record StatsPublishResult(bool Watched);

/// <summary>
/// 看板上的一台：<c>seq</c> 是伺服器每收下一筆就遞增的編號（同一台的 seq 變了才是新樣本），<c>ageMs</c> 是伺服器送出時這筆樣本已經多舊
/// （伺服器收到的時間到送出的時間，單調時鐘）
/// </summary>
internal sealed record DeviceStatsEntry(string DeviceId, long Seq, long AgeMs, DeviceStats Stats);

/// <summary>
/// MyriagentHub 主機自己的負載：<c>name</c> 是主機的電腦名稱，<c>seq</c>／<c>ageMs</c> 同 <see cref="DeviceStatsEntry"/>，<c>stats</c> 的 <c>rttMs</c> 一律省略；
/// <c>devices</c> 是在線的電腦數、<c>browsers</c> 是連著的瀏覽器連線數（都是取樣當時的）
/// </summary>
internal sealed record HubStatsEntry(string Name, long Seq, long AgeMs, DeviceStats Stats, int Devices, int Browsers);

/// <summary>
/// 伺服器 → 裝置（Send，<c>stat1</c>）：在線的電腦的最新狀態，整份取代（含收件的這台自己，裝置自己略過）；<c>hub</c> 是 MyriagentHub 主機的負載
/// （還沒取樣是 null、線上省略）。只送給最後一次 <c>watch</c> 是 true 的連線
/// </summary>
internal sealed record StatsUpdate(IReadOnlyList<DeviceStatsEntry> Devices, HubStatsEntry? Hub = null);
