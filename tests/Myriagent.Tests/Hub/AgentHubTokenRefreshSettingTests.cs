using Myriagent.Tests.TestSupport;
using Myriagent.Usage;

namespace Myriagent.Tests.Hub;

/// <summary>設定頁「閒置的 Claude 帳戶自動換發權杖」（claudeIdleTokenRefresh）：預設開、送給網頁、切換後寫入設定，打開時資料過時的 Claude 帳戶馬上再查</summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubTokenRefreshSettingTests
{
    [Fact]
    public async Task 預設開_關掉後設定值送給網頁()
    {
        using var h = new HubHarness();
        h.OnUi(() => h.Settings.ClaudeIdleTokenRefresh).Should().BeTrue("預設開");
        var mark = h.Main.Mark();

        h.Command(h.Main, "setting", new { key = "claudeIdleTokenRefresh", value = false });

        h.OnUi(() => h.Settings.ClaudeIdleTokenRefresh).Should().BeFalse();
        var settings = await h.Main.WaitForAsync("settings", since: mark);
        settings["settings"]!.Value<bool>("claudeIdleTokenRefresh").Should().BeFalse();
    }

    [Fact]
    public void 打開時_資料過時的Claude帳戶馬上再查_其他帳戶不動()
    {
        using var h = new HubHarness();
        h.Command(h.Main, "setting", new { key = "claudeIdleTokenRefresh", value = false });
        var later = DateTimeOffset.UtcNow.AddMinutes(10);
        var stale = new UsageSnapshot(DateTimeOffset.UtcNow.AddHours(-20), "cache", new[] { new UsageWindow("weekly", "每週", 99, null) }, null, null, "登入權杖已過期", true);
        var fresh = stale with { Stale = false, Source = "api" };
        h.Ui.Invoke(() =>
        {
            h.Accounts.SeedUsage(h.Claude.Id, stale);
            h.Accounts.SeedUsage(h.Claude2.Id, fresh);
            h.Accounts.SeedUsage(h.Codex.Id, stale);
            foreach (var id in new[] { h.Claude.Id, h.Claude2.Id, h.Codex.Id })
                h.Accounts.SeedNextPoll(id, later);
        });

        h.Command(h.Main, "setting", new { key = "claudeIdleTokenRefresh", value = true });

        h.OnUi(() => h.Accounts.GetNextPoll(h.Claude.Id)).Should().BeOnOrBefore(DateTimeOffset.UtcNow, "資料過時的 Claude 帳戶下次輪詢就查");
        h.OnUi(() => h.Accounts.GetNextPoll(h.Claude2.Id)).Should().Be(later, "資料是新的不必提早");
        h.OnUi(() => h.Accounts.GetNextPoll(h.Codex.Id)).Should().Be(later, "Codex 沒有這個問題");
    }
}
