using Myriagent.Agents;

namespace Myriagent.Cli;

/// <summary>
/// CLI 執行檔的身分：原地換版時路徑不變，所以不能只比路徑。NativeExecutable 是實際執行的原生檔
/// （npm 版 Codex 是 vendor 底下的 codex.exe，不是啟動器的 node.exe；npm 版 Claude 是套件裡的 bin\claude.exe）。
/// 相等＝同一個安裝（Provider、Kind、NativeExecutable 不分大小寫）的同一份檔案（VolumeSerial、FileId、Size、LastWriteUtc 都相同）。
/// Version 是由檔案推得的顯示資訊，不參與比較：版本來源（FileVersionInfo、package.json、--version）不同時，同一份檔案不能被當成換版。
/// 不用建立時間：NTFS tunneling 讓換檔後的新檔沿用舊檔的建立時間；FileId 每次換檔一定會變。
/// </summary>
/// <param name="NativeExecutable">原生執行檔的完整路徑（CliInstallation.NativeExecutable）</param>
/// <param name="Version">CLI 版本，一律是 CliVersionRules.ParseVersion 解析後的 x.y.z[-pre]（Claude 讀 claude.exe 的 FileVersionInfo；Codex 是 package.json、vendor\codex-package.json、codex --version 三者一致時的值）</param>
/// <param name="VolumeSerial">檔案所在磁碟區的序號（GetFileInformationByHandleEx 的 FILE_ID_INFO.VolumeSerialNumber）</param>
/// <param name="FileId">128 位元檔案識別碼（FILE_ID_INFO.FileId 的 16 個位元組依 little-endian 解讀）</param>
/// <param name="Size">檔案大小（位元組）</param>
/// <param name="LastWriteUtc">最後寫入時間（UTC；傳入本機時間會轉成 UTC，未指定種類的視為 UTC）</param>
internal sealed record CliBinaryIdentity(
    AgentProvider Provider,
    CliInstallKind Kind,
    string NativeExecutable,
    string Version,
    ulong VolumeSerial,
    UInt128 FileId,
    long Size,
    DateTime LastWriteUtc)
{
    private readonly DateTime lastWriteUtc = ToUtc(LastWriteUtc);

    /// <summary>最後寫入時間（UTC）：建立或以 with 改值時都換算成 UTC</summary>
    public DateTime LastWriteUtc
    {
        get => lastWriteUtc;
        init => lastWriteUtc = ToUtc(value);
    }

    /// <summary>同一個安裝的同一份檔案（Version 不參與比較）</summary>
    public bool Equals(CliBinaryIdentity? other)
        => other is not null
           && Provider == other.Provider
           && Kind == other.Kind
           && string.Equals(NativeExecutable, other.NativeExecutable, StringComparison.OrdinalIgnoreCase)
           && VolumeSerial == other.VolumeSerial
           && FileId == other.FileId
           && Size == other.Size
           && LastWriteUtc.Ticks == other.LastWriteUtc.Ticks;

    public override int GetHashCode()
        => HashCode.Combine(Provider, Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(NativeExecutable), VolumeSerial, FileId, Size, LastWriteUtc.Ticks);

    /// <summary>
    /// 兩個身分是不是同一份執行檔；任一為 null（身分不明：還沒偵測、讀取失敗）時視為相同——身分不明不構成重開程序的理由。
    /// 會話判斷要不要重開程序（AgentSessionSettings.Cli 與目前程序啟動時的身分）、中樞判斷對話是不是還在跑舊版，都用這一個規則
    /// </summary>
    public static bool SameOrUnknown(CliBinaryIdentity? a, CliBinaryIdentity? b) => a is null || b is null || a.Equals(b);

    public override string ToString()
        => $"{Provider} {Version}（{Kind}，{NativeExecutable}，FileId {FileId:x32}，{Size} 位元組，寫入 {LastWriteUtc:O}）";

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

/// <summary>誰觸發了 CLI 換版（只影響介面文字；兩種都照同一套重開政策處理）</summary>
internal enum CliChangeOrigin
{
    /// <summary>app 自己（CliManager 的更新與安裝：claude update、npm、下載受管 Codex）</summary>
    App,

    /// <summary>外部（使用者在終端機更新、互動式 claude 每 30 分鐘的背景更新、其他工具）</summary>
    External,
}

/// <summary>
/// CLI 安裝的變化（CliManager.VersionChanged）。依旗標分成四種：
/// <list type="bullet">
/// <item>Stable=false：偵測到安裝正在變動（Claude 換檔、npm retire 改名到解壓完成之間），New 為 null——暫緩這種 CLI 的新啟動，等下一個 Stable=true</item>
/// <item>Stable=true、InstallBroken=true：安裝損壞（版本來源互相矛盾，例如 npm rollback 留下的混版），New 為 null——不重開任何對話，Detail 是給使用者看的說明</item>
/// <item>Stable=true、沒有損壞、New 不為 null：已穩定、可用的身分——還在跑其他身分的對話依重開政策處理（IsSettledBinary）</item>
/// <item>Stable=true、沒有損壞、New 為 null：這種 CLI 已不在這台電腦</item>
/// </list>
/// </summary>
/// <param name="Old">變化前已穩定的身分（不明為 null）</param>
/// <param name="New">變化後已穩定的身分（見上）</param>
/// <param name="Detail">給使用者看的一句說明（「你在終端機更新了 Claude Code」「Codex 安裝不一致（package.json 0.162.0、執行檔 0.160.1），請修復」）</param>
internal sealed record CliVersionChange(
    AgentProvider Provider,
    CliBinaryIdentity? Old,
    CliBinaryIdentity? New,
    CliChangeOrigin Origin,
    bool Stable,
    bool InstallBroken,
    string? Detail)
{
    /// <summary>偵測到的時間（UTC；介面的「最近一次換版」）</summary>
    public DateTime DetectedAtUtc { get; init; } = DateTime.UtcNow;

    public string? FromVersion => Old?.Version;

    public string? ToVersion => New?.Version;

    /// <summary>安裝已穩定、沒有損壞、有可用的新身分：重開還在跑其他身分的對話的依據</summary>
    public bool IsSettledBinary => Stable && !InstallBroken && New != null;

    /// <summary>已穩定的新身分與變化前的不同（Old 不明時不算換版）</summary>
    public bool IsVersionSwitch => IsSettledBinary && !CliBinaryIdentity.SameOrUnknown(Old, New);
}
