using System.Globalization;
using MyriagentHub.Media;
using MyriagentHub.Ops;

namespace MyriagentHub.Tests.Media;

/// <summary>每日媒體清理的純函式規則（WP4.7 計畫 D18、D19）</summary>
public sealed class MediaMaintenanceRulesTests
{
    private static readonly TimeZoneInfo Taipei = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    private static readonly DateTimeOffset Now = new(2026, 10, 20, 3, 30, 0, TimeSpan.Zero);

    private static readonly long NowMs = Now.ToUnixTimeMilliseconds();

    private static readonly long HourMs = (long)TimeSpan.FromHours(1).TotalMilliseconds;

    [Fact]
    public void DeleteBefore_每日14份與每週到第70天時回第70天減1天()
    {
        // 保留規則（14 份每日＋8 份每週）實際留下的資料夾：第 1～14 天每天一份，第 15～70 天每週一份（第 1 天是今天）
        var today = new DateTime(2026, 10, 20);
        var names = Enumerable.Range(0, 14).Select(d => today.AddDays(-d))
            .Concat(Enumerable.Range(0, 8).Select(w => today.AddDays(-14 - (7 * w))))
            .Concat([today.AddDays(-69)])
            .Select(d => d.ToString(BackupRules.DayFormat, CultureInfo.InvariantCulture))
            .Distinct()
            .ToList();
        var times = names.Select(n => MediaMaintenanceRules.BackupTime(n, daily: true, DateTime.UtcNow, TimeZoneInfo.Utc));

        MediaMaintenanceRules.DeleteBefore(times).Should().Be(new DateTimeOffset(today.AddDays(-69).AddDays(-1), TimeSpan.Zero));
    }

    [Fact]
    public void DeleteBefore_沒有任何備份回null()
    {
        MediaMaintenanceRules.DeleteBefore([]).Should().BeNull();
    }

    [Fact]
    public void DeleteBefore_含predeploy與手動備份時取最舊的()
    {
        var daily = MediaMaintenanceRules.BackupTime("20261010", daily: true, DateTime.UtcNow, TimeZoneInfo.Utc);
        var manual = MediaMaintenanceRules.BackupTime("20261005-221500", daily: true, DateTime.UtcNow, TimeZoneInfo.Utc);
        var predeploy = MediaMaintenanceRules.BackupTime("predeploy-1.2.3-abcdef0", daily: false, new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), TimeZoneInfo.Utc);

        MediaMaintenanceRules.DeleteBefore([daily, manual]).Should().Be(new DateTimeOffset(2026, 10, 4, 22, 15, 0, TimeSpan.Zero));
        MediaMaintenanceRules.DeleteBefore([daily, manual, predeploy]).Should().Be(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void BackupTime_每日的名稱是本機時間_其他用建立時間()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        MediaMaintenanceRules.BackupTime("20261011", daily: true, created, Taipei).Should().Be(new DateTimeOffset(2026, 10, 10, 16, 0, 0, TimeSpan.Zero));
        MediaMaintenanceRules.BackupTime("20261011-093000", daily: true, created, Taipei).Should().Be(new DateTimeOffset(2026, 10, 11, 1, 30, 0, TimeSpan.Zero));
        MediaMaintenanceRules.BackupTime("看不懂的資料夾", daily: true, created, Taipei).Should().Be(new DateTimeOffset(created), "名稱看不懂就看建立時間");
        MediaMaintenanceRules.BackupTime("20261011", daily: false, created, Taipei).Should().Be(new DateTimeOffset(created), "daily 以外的資料夾一律看建立時間");
        MediaMaintenanceRules.BackupTime("pre-restore-20261011", daily: false, DateTime.SpecifyKind(created, DateTimeKind.Unspecified), Taipei)
            .Should().Be(new DateTimeOffset(created), "建立時間一律當 UTC");
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void UploadExpired_滿24小時過期(long offsetMs, bool expired)
    {
        var at = NowMs - UploadRules.LifetimeMs + offsetMs;
        MediaMaintenanceRules.UploadExpired(UploadRules.RefId("up-1", at), NowMs).Should().Be(expired);
    }

    [Fact]
    public void UploadExpired_看不懂的ref_id當成過期()
    {
        MediaMaintenanceRules.UploadExpired("up-1", NowMs).Should().BeTrue();
        MediaMaintenanceRules.UploadExpired("up-1@abc", NowMs).Should().BeTrue();
        MediaMaintenanceRules.UploadExpired(string.Empty, NowMs).Should().BeTrue();
    }

    [Fact]
    public void Deletable_項目與縮圖在門檻前1毫秒才刪_存入不到1小時留著()
    {
        var before = NowMs - (40L * 24 * HourMs);
        var old = NowMs - (100L * 24 * HourMs);
        foreach (var kind in new[] { BlobKinds.ItemBody, BlobKinds.Thumb })
        {
            MediaMaintenanceRules.Deletable(new BlobRow("s", 1, kind, old, before - 1), NowMs, before).Should().BeTrue(kind);
            MediaMaintenanceRules.Deletable(new BlobRow("s", 1, kind, old, before), NowMs, before).Should().BeFalse(kind + "：等於門檻不刪");
            MediaMaintenanceRules.Deletable(new BlobRow("s", 1, kind, old, before + 1), NowMs, before).Should().BeFalse(kind);
            MediaMaintenanceRules.Deletable(new BlobRow("s", 1, kind, old, before - 1), NowMs, null).Should().BeFalse(kind + "：沒有備份不刪");
            MediaMaintenanceRules.Deletable(new BlobRow("s", 1, kind, NowMs - HourMs + 1, before - 1), NowMs, before).Should().BeFalse(kind + "：存入不到 1 小時");
        }
    }

    [Fact]
    public void Deletable_從沒被引用過的上傳不等保留窗_被引用過的照門檻()
    {
        var created = NowMs - HourMs;
        MediaMaintenanceRules.Deletable(new BlobRow("s", 1, BlobKinds.Upload, created, created), NowMs, null).Should().BeTrue("沒有備份也刪：不在任何備份裡");
        MediaMaintenanceRules.Deletable(new BlobRow("s", 1, BlobKinds.Upload, created + 1, created + 1), NowMs, null).Should().BeFalse("存入不到 1 小時");
        MediaMaintenanceRules.Deletable(new BlobRow("s", 1, BlobKinds.Upload, created, created + 5), NowMs, null).Should().BeFalse("曾被項目或縮圖引用過");
        MediaMaintenanceRules.Deletable(new BlobRow("s", 1, BlobKinds.Upload, created, created + 5), NowMs, created + 6).Should().BeTrue();
    }

    [Fact]
    public void TmpExpired_只看自己的前綴與修改時間()
    {
        var old = Now.UtcDateTime.AddHours(-1);
        var fresh = Now.UtcDateTime.AddMinutes(-59);

        MediaMaintenanceRules.TmpExpired(BlobStore.TmpPrefix + "abc", old, Now).Should().BeTrue();
        MediaMaintenanceRules.TmpExpired(ThumbCache.TmpPrefix + "abc", old, Now).Should().BeTrue();
        MediaMaintenanceRules.TmpExpired(BlobStore.TmpPrefix + "abc", fresh, Now).Should().BeFalse();
        MediaMaintenanceRules.TmpExpired("restore-staging", old.AddDays(-3), Now).Should().BeFalse("還原與部署的暫存不碰");
        MediaMaintenanceRules.TmpExpired("Blob-abc", old.AddDays(-3), Now).Should().BeFalse("前綴分大小寫");
    }

    [Fact]
    public void OldEnough_修改時間至少1小時前()
    {
        MediaMaintenanceRules.OldEnough(Now.UtcDateTime.AddHours(-1), Now).Should().BeTrue();
        MediaMaintenanceRules.OldEnough(Now.UtcDateTime.AddHours(-1).AddTicks(1), Now).Should().BeFalse();
        MediaMaintenanceRules.OldEnough(Now.UtcDateTime.AddDays(1), Now).Should().BeFalse("未來的修改時間不刪");
    }
}
