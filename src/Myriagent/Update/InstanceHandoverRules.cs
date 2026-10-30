using Myriagent.Agents;

namespace Myriagent.Update;

/// <summary>
/// 正在執行的實例在 &lt;資料夾&gt;\update\instance.json 留的資訊：有這個檔（而且程序還在）＝對方懂交棒。
/// 比這個功能舊的版本不寫它（設計 app-self-update 第 16 節）
/// </summary>
internal sealed class RunningInstanceFile
{
    public int Schema { get; set; } = 1;

    public int Pid { get; set; }

    /// <summary>ReleaseIdentity.Release（「0.2.512-b247e78」；開發版的 build 是 dev）</summary>
    public string Release { get; set; } = string.Empty;

    public string Executable { get; set; } = string.Empty;

    /// <summary>Unix 毫秒（UTC）</summary>
    public long StartedAt { get; set; }
}

/// <summary>較新的版本請正在執行的舊版交棒：&lt;資料夾&gt;\update\handover-request.json</summary>
internal sealed class HandoverRequestFile
{
    public int Schema { get; set; } = 1;

    /// <summary>新版的執行檔（舊版排空後以 --handed-over 啟動它）</summary>
    public string Executable { get; set; } = string.Empty;

    /// <summary>新版的 ReleaseIdentity.Release</summary>
    public string Release { get; set; } = string.Empty;

    public int Pid { get; set; }

    /// <summary>Unix 毫秒（UTC）</summary>
    public long CreatedAt { get; set; }
}

/// <summary>新啟動的這一份遇到同一個資料夾已有實例時怎麼做</summary>
internal enum LaunchDecision
{
    /// <summary>叫醒它、自己結束（原本的行為：對方同版或較新）</summary>
    Activate,

    /// <summary>對方比自己舊而且懂交棒：請它排空對話、換成這一版</summary>
    RequestHandover,

    /// <summary>對方沒留 instance.json（還不懂交棒的舊版）：照樣叫醒它，再告訴使用者怎麼改用這一版</summary>
    ExplainOldInstance,
}

/// <summary>
/// 新版自動接手舊版（2026-10-22 擁有者：系統匣裡的舊版會讓新版的執行檔打開舊視窗）。純函式：
/// 版本只比「主.次.修」（開發版是 0.2.0，比任何發佈版舊；同版號不交棒，照舊叫醒對方）
/// </summary>
internal static class InstanceHandoverRules
{
    /// <summary>新版啟動時帶的參數：後面接 resume.json 的 token（同 --after-update，但沒有換版幫手在等健康訊號）</summary>
    public const string HandedOverArgument = "--handed-over";

    /// <summary>執行檔名稱（交棒請求只收這個名稱的檔）</summary>
    public const string ExecutableName = "Myriagent.exe";

    /// <summary>交棒請求多久之後作廢（新版寫完請求就等對方確認，最多幾秒）</summary>
    public static readonly TimeSpan RequestMaxAge = TimeSpan.FromSeconds(30);

    /// <summary>沒找到 instance.json 時再等一下（對方可能剛拿到 mutex、還沒寫）</summary>
    public static readonly TimeSpan RunningFileGrace = TimeSpan.FromMilliseconds(1500);

    /// <summary>新版送出請求後等對方確認多久；沒確認就照原本的方式叫醒它</summary>
    public static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(3);

    /// <summary>以 --handed-over 啟動的新版等舊版結束（放開 mutex）多久：舊版要先收尾代理程序</summary>
    public static readonly TimeSpan HandedOverTakeoverTimeout = TimeSpan.FromMinutes(2);

    /// <param name="self">這一份的版本</param>
    /// <param name="running">對方的 instance.json（已確認程序還在）；null＝沒有</param>
    public static LaunchDecision Decide(ReleaseVersion self, RunningInstanceFile? running)
    {
        if (running == null)
            return LaunchDecision.ExplainOldInstance;
        if (VersionOf(running.Release) is not { } other)
            return LaunchDecision.Activate;   // 看不懂對方的版本：不冒險交棒
        return self > other ? LaunchDecision.RequestHandover : LaunchDecision.Activate;
    }

    /// <summary>
    /// release（「0.2.512-b247e78」）的版本部分。開發版的 build 是 dev，ReleaseIdentity.TryParseRelease 不收（受管理的安裝不會有開發版），
    /// 所以這裡只看「-」前面的版本號；解析不了回 null
    /// </summary>
    public static ReleaseVersion? VersionOf(string? release)
    {
        if (string.IsNullOrWhiteSpace(release))
            return null;
        var dash = release.IndexOf('-');
        return ReleaseVersion.TryParse(dash > 0 ? release[..dash] : release, out var version) ? version : null;
    }

    /// <summary>
    /// 舊版收到的交棒請求能不能接（null＝可以；否則是記錄用的原因）：版本要比自己新、建立不到 30 秒、
    /// 執行檔是本機磁碟上的 Myriagent.exe 而且存在（UNC 一律不收）
    /// </summary>
    public static string? CheckRequest(HandoverRequestFile? request, ReleaseVersion self, DateTime nowUtc, Func<string, bool> fileExists)
    {
        if (request == null)
            return "沒有交棒請求";
        if (VersionOf(request.Release) is not { } target)
            return "版本不合法";
        if (target <= self)
            return "請求的版本沒有比較新";
        var created = DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(request.CreatedAt, 0, DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())).UtcDateTime;
        var age = nowUtc - created;
        if (age > RequestMaxAge || age < -RequestMaxAge)
            return "請求已過期";
        var exe = request.Executable;
        if (string.IsNullOrWhiteSpace(exe) || !AgentAttachmentRules.IsLocalDrivePath(exe)
            || !string.Equals(Path.GetFileName(exe), ExecutableName, StringComparison.OrdinalIgnoreCase))
            return "執行檔不合法";
        if (!fileExists(exe))
            return "執行檔不存在";
        return null;
    }

    /// <summary>舊版以這些參數啟動新版：--handed-over &lt;token&gt;；資料夾不是預設的時候加上 --data（同一個資料夾才是同一個實例）</summary>
    public static IReadOnlyList<string> RelaunchArguments(string token, string? dataRoot)
        => dataRoot == null ? [HandedOverArgument, token] : [HandedOverArgument, token, "--data", dataRoot];

    /// <summary>還不懂交棒的舊版在執行時給使用者的說明（舊版的視窗已叫到前面）</summary>
    public static string OldInstanceMessage(ReleaseVersion self)
        => $"已經有一個較舊的 Clode Myriagent 在背景執行，所以打開的是它，不是這一版（{self}）。\n\n"
           + "要改用這一版：在工作列右下角的系統匣找到 Clode Myriagent 圖示，按右鍵選「結束」，再重新開啟這一版。\n\n"
           + "之後的版本遇到這種情況會自動換成較新的那一份，不必再這樣做。";
}
