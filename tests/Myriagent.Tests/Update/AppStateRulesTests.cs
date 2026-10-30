using Myriagent.Sync;
using Myriagent.Update;

namespace Myriagent.Tests.Update;

/// <summary>app.state 的組成（app 自我更新 U3，設計 7.8）：階段的對應、lastError 整理成一行、開發版不帶 release、checkedAt 一定大於 0</summary>
public sealed class AppStateRulesTests
{
    private static readonly DateTime Checked = new(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);

    private static ReleaseIdentity Id(string release)
    {
        ReleaseIdentity.TryParseRelease(release, out var id).Should().BeTrue();
        return id!;
    }

    private static AppUpdateStatus Status(AppUpdatePhase phase, string? detail = null, string? staged = null, DateTime? checkedAt = null) =>
        new(phase, null, staged, 0, 0, detail, checkedAt, null, null);

    [Theory]
    [InlineData(null, nameof(AppUpdatePhase.Idle), false, AppStatePhases.Idle)]
    [InlineData(null, nameof(AppUpdatePhase.Checking), false, AppStatePhases.Idle)]
    [InlineData(null, nameof(AppUpdatePhase.Failed), false, AppStatePhases.Idle)]
    [InlineData(null, nameof(AppUpdatePhase.Downloading), false, AppStatePhases.Downloading)]
    [InlineData(null, nameof(AppUpdatePhase.Staged), false, AppStatePhases.Staged)]
    [InlineData(null, nameof(AppUpdatePhase.Staged), true, AppStatePhases.Waiting)]
    [InlineData(null, nameof(AppUpdatePhase.Blocked), false, AppStatePhases.Blocked)]
    [InlineData(null, nameof(AppUpdatePhase.Disabled), false, AppStatePhases.Blocked)]
    [InlineData("draining", nameof(AppUpdatePhase.Staged), true, AppStatePhases.Draining)]
    [InlineData("restarting", nameof(AppUpdatePhase.Staged), false, AppStatePhases.Applying)]
    [InlineData("installing", nameof(AppUpdatePhase.Idle), false, AppStatePhases.Idle)]
    public void 階段的對應(string? stage, string phase, bool blocked, string expected)
    {
        var payload = AppStateRules.Build(stage, Status(Enum.Parse<AppUpdatePhase>(phase)), blocked, Id("0.3.120-0123abc"), 5);

        payload.Phase.Should().Be(expected);
        AppStatePhases.IsKnown(payload.Phase).Should().BeTrue();
    }

    [Fact]
    public void 失敗與不能更新帶原因_其他不帶_已備妥帶release名稱()
    {
        var current = Id("0.3.120-0123abc");

        AppStateRules.Build(null, Status(AppUpdatePhase.Failed, "下載失敗：\r\n伺服器\t正在更新"), false, current, 5).LastError.Should().Be("下載失敗： 伺服器 正在更新");
        AppStateRules.Build(null, Status(AppUpdatePhase.Blocked, "磁碟空間不足"), false, current, 5).LastError.Should().Be("磁碟空間不足");
        AppStateRules.Build(null, Status(AppUpdatePhase.Idle, "已是最新版"), false, current, 5).LastError.Should().BeNull("idle 的說明不是錯誤");
        AppStateRules.Build(null, Status(AppUpdatePhase.Staged, staged: "0.3.121-0456def"), false, current, 5).Staged.Should().Be("0.3.121-0456def");
        AppStateRules.Build(null, Status(AppUpdatePhase.Staged, staged: "壞的"), false, current, 5).Staged.Should().BeNull("伺服器只收合法的 release 名稱");
    }

    [Fact]
    public void 版本與release_開發版不帶release_checkedAt沒檢查過用啟動時間()
    {
        var payload = AppStateRules.Build(null, Status(AppUpdatePhase.Idle, checkedAt: Checked), false, Id("0.3.120-0123abc"), 5);
        payload.Version.Should().Be("0.3.120");
        payload.Release.Should().Be("0.3.120-0123abc");
        payload.CheckedAt.Should().Be(new DateTimeOffset(Checked).ToUnixTimeMilliseconds());

        var dev = new ReleaseIdentity(Id("0.3.120-0123abc").Version, ReleaseIdentity.DevBuild);
        var devPayload = AppStateRules.Build(null, Status(AppUpdatePhase.Disabled, "開發版，不自動更新"), false, dev, 1_700_000_000_000);
        devPayload.Release.Should().BeNull();
        devPayload.CheckedAt.Should().Be(1_700_000_000_000);
        AppStateRules.Build(null, Status(AppUpdatePhase.Idle), false, dev, 0).CheckedAt.Should().Be(1, "伺服器要大於 0");
    }

    [Fact]
    public void lastError整理成一行_最多500字_不切開代理對()
    {
        AppStateRules.CleanLine(null).Should().BeNull();
        AppStateRules.CleanLine(" \r\n\t ").Should().BeNull();
        AppStateRules.CleanLine("  a​b \n\n c  ").Should().Be("a b c", "格式字元也當空白");
        AppStateRules.CleanLine(new string('字', 600))!.Length.Should().Be(AppStateRules.MaxLastError);
        var emoji = new string('a', AppStateRules.MaxLastError - 1) + "😀";
        AppStateRules.CleanLine(emoji).Should().Be(new string('a', AppStateRules.MaxLastError - 1), "截斷處正好是代理對的前半，整個拿掉");
    }
}
