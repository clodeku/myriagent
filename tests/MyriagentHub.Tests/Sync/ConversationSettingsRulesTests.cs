using System.Reflection;
using MyriagentHub.Admin;
using MyriagentHub.Hosting;
using MyriagentHub.Sync;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Sync;

/// <summary><c>Hub:Conversations</c> 的上限設定（WP4.2 計畫步 1、D1：只有上限，沒有任何開關）</summary>
public sealed class ConversationSettingsRulesTests
{
    /// <summary>必須大於 0 的上限（逐一列出；<see cref="每個上限欄位都在清單裡_設定沒有開關"/> 確認沒有漏掉的屬性）</summary>
    private static readonly string[] PositiveLimits =
    [
        nameof(ConversationSettings.MaxLivePerDevicePerSecond),
        nameof(ConversationSettings.LiveBurstPerConversation),
        nameof(ConversationSettings.OverlayMaxBytes),
        nameof(ConversationSettings.OverlayMaxOrdsPerConversation),
        nameof(ConversationSettings.RenderedItemMaxBytes),
        nameof(ConversationSettings.PageMaxBytes),
        nameof(ConversationSettings.RenderCacheMaxBytes),
        nameof(ConversationSettings.ChangeFeedMaxEntries),
        nameof(ConversationSettings.ChangeFeedMaxBytes),
        nameof(ConversationSettings.ConversationPushIntervalMs),
        nameof(ConversationSettings.MaxOpensPerRound),
        nameof(ConversationSettings.SnapshotOpenCooldownMinutes),
        nameof(ConversationSettings.SnapshotRestoreGraceSeconds),
        nameof(ConversationSettings.SnapshotRestorePerSecond),
        nameof(ConversationSettings.DraftGraceSeconds),
        nameof(ConversationSettings.MaxDraftsPerSession),
        nameof(ConversationSettings.MaxWatchIds),
        nameof(ConversationSettings.RebuildChunkEvents),
        nameof(ConversationSettings.RejectAuditWindowSeconds),
        nameof(ConversationSettings.SyncDbWarnBytes),
        nameof(ConversationSettings.SyncDbCriticalBytes),
    ];

    public static TheoryData<string> PositiveLimitNames => [.. PositiveLimits];

    [Fact]
    public void 預設值合格()
    {
        var s = new ConversationSettings();
        ConversationSettingsRules.Validate(s).Should().BeNull();
        s.MaxLivePerDevicePerSecond.Should().Be(50);
        s.LiveBurstPerConversation.Should().Be(4);
        s.OverlayMaxBytes.Should().Be(8 * 1024 * 1024);
        s.OverlayMaxOrdsPerConversation.Should().Be(64);
        s.RenderedItemMaxBytes.Should().Be(256 * 1024);
        s.PageMaxBytes.Should().Be(1024 * 1024);
        s.RenderCacheMaxBytes.Should().Be(16 * 1024 * 1024);
        s.ChangeFeedMaxEntries.Should().Be(10_000);
        s.ChangeFeedMaxBytes.Should().Be(16 * 1024 * 1024);
        s.ConversationPushIntervalMs.Should().Be(50);
        s.MaxOpensPerRound.Should().Be(8);
        s.SnapshotOpenCooldownMinutes.Should().Be(10);
        s.SnapshotRestoreGraceSeconds.Should().Be(300);
        s.SnapshotRestorePerSecond.Should().Be(2);
        s.DraftGraceSeconds.Should().Be(120);
        s.MaxDraftsPerSession.Should().Be(8);
        s.MaxWatchIds.Should().Be(500);
        s.RebuildChunkEvents.Should().Be(500);
        s.RejectAuditWindowSeconds.Should().Be(60);
        s.RejectedRetentionDays.Should().Be(30);
        s.SupersededRetentionDays.Should().Be(7);
        s.SyncDbWarnBytes.Should().Be(2L * 1024 * 1024 * 1024);
        s.SyncDbCriticalBytes.Should().Be(8L * 1024 * 1024 * 1024);
        new HubOptions().Conversations.Should().NotBeNull();
        ConversationSettingsRules.Validate(null).Should().NotBeNull();
    }

    [Theory]
    [MemberData(nameof(PositiveLimitNames))]
    public void 上限小於等於0_不合格(string name)
    {
        foreach (var value in new long[] { 0, -1 })
        {
            var s = With(name, value);
            if (name == nameof(ConversationSettings.SyncDbWarnBytes))
                s.SyncDbCriticalBytes = 1;
            ConversationSettingsRules.Validate(s).Should().NotBeNull($"{name}={value}")
                .And.Contain("Hub:Conversations:" + name);
        }

        var positive = With(name, 2);
        if (name == nameof(ConversationSettings.SyncDbCriticalBytes))
            positive.SyncDbWarnBytes = 1;
        ConversationSettingsRules.Validate(positive).Should().BeNull("正數合格（{0}；嚴重的門檻是 2 時警告的門檻改成 1）", name);
    }

    [Fact]
    public void 每個上限欄位都在清單裡_設定沒有開關()
    {
        var properties = typeof(ConversationSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        properties.Should().OnlyContain(p => p.PropertyType == typeof(int) || p.PropertyType == typeof(long),
            "只有上限參數，沒有 bool 開關（答 1）");
        properties.Select(p => p.Name).Except([nameof(ConversationSettings.RejectedRetentionDays), nameof(ConversationSettings.SupersededRetentionDays)])
            .Should().BeEquivalentTo(PositiveLimits);
        ConversationSettingsRules.Limits(new ConversationSettings()).Select(l => l.Name).Should().BeEquivalentTo(PositiveLimits);
    }

    [Fact]
    public void 保留天數0合格_負數不合格()
    {
        ConversationSettingsRules.Validate(new ConversationSettings { RejectedRetentionDays = 0 }).Should().BeNull("0＝不刪");
        ConversationSettingsRules.Validate(new ConversationSettings { RejectedRetentionDays = -1 }).Should()
            .Contain("Hub:Conversations:RejectedRetentionDays").And.Contain("-1");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(2, true)]
    [InlineData(30, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void 被取代的項目事件保留天數_0或至少2(int days, bool ok)
    {
        var problem = ConversationSettingsRules.Validate(new ConversationSettings { SupersededRetentionDays = days });
        if (ok)
            problem.Should().BeNull("{0} 天合格（0＝不刪；至少 2 天才遠大於桌面 outbox 保留的 24 小時）", days);
        else
            problem.Should().Contain("Hub:Conversations:SupersededRetentionDays");
    }

    [Theory]
    [InlineData(8L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024)]
    [InlineData(9L * 1024 * 1024 * 1024, 8L * 1024 * 1024 * 1024)]
    public void 警告不小於嚴重_不合格(long warn, long critical)
    {
        var problem = ConversationSettingsRules.Validate(new ConversationSettings { SyncDbWarnBytes = warn, SyncDbCriticalBytes = critical });
        problem.Should().Contain("Hub:Conversations:SyncDbWarnBytes").And.Contain("Hub:Conversations:SyncDbCriticalBytes");
        ConversationSettingsRules.Validate(new ConversationSettings { SyncDbWarnBytes = critical - 1, SyncDbCriticalBytes = critical }).Should().BeNull();
    }

    [Fact]
    public async Task 啟動時不合格_AddHubRealtime丟例外()
    {
        var start = () => HubTestHost.StartAsync(settings: new Dictionary<string, string?> { ["Hub:Conversations:RejectedRetentionDays"] = "-1" });
        (await start.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Hub:Conversations:RejectedRetentionDays*-1*");

        var startOther = () => HubTestHost.StartAsync(settings: new Dictionary<string, string?> { ["Hub:Conversations:MaxWatchIds"] = "0" });
        (await startOther.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Hub:Conversations:MaxWatchIds*");

        await using var ok = await HubTestHost.StartAsync(settings: new Dictionary<string, string?> { ["Hub:Conversations:RejectedRetentionDays"] = "0" });
        ok.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HubOptions>>().Value.Conversations.RejectedRetentionDays
            .Should().Be(0, "設定綁得進 Hub:Conversations");
    }

    [Fact]
    public async Task preflight列出conversations檢查()
    {
        var dir = TempDir.Create();
        var work = TempDir.Create("myriagenthub-preflight-temp");
        try
        {
            var paths = new HubPaths(dir);
            Directory.CreateDirectory(paths.ConfigDir);
            var config = Path.Combine(paths.ConfigDir, "appsettings.Production.json");

            await File.WriteAllTextAsync(config, "{\"Hub\":{\"ServerDomain\":\"hub.example.com\"}}");
            var good = await Preflight.RunAsync(paths, work, CancellationToken.None);
            var goodCheck = good.Checks.Single(c => c.Name == "config.conversations");
            goodCheck.Ok.Should().BeTrue();
            goodCheck.Detail.Should().BeNull();

            await File.WriteAllTextAsync(config, "{\"Hub\":{\"ServerDomain\":\"hub.example.com\",\"Conversations\":{\"SyncDbWarnBytes\":9000000000,\"SyncDbCriticalBytes\":8000000000}}}");
            var bad = await Preflight.RunAsync(paths, work, CancellationToken.None);
            bad.Ok.Should().BeFalse("部署前就擋下：新版讀到不合格的對話上限");
            var badCheck = bad.Checks.Single(c => c.Name == "config.conversations");
            badCheck.Ok.Should().BeFalse();
            badCheck.Detail.Should().Contain("Hub:Conversations:SyncDbWarnBytes");
        }
        finally
        {
            TempDir.TryDelete(work);
            TempDir.TryDelete(dir);
        }
    }

    private static ConversationSettings With(string name, long value)
    {
        var s = new ConversationSettings();
        var property = typeof(ConversationSettings).GetProperty(name) ?? throw new InvalidOperationException("沒有這個屬性：" + name);
        property.SetValue(s, property.PropertyType == typeof(long) ? value : (object)checked((int)value));
        return s;
    }
}
