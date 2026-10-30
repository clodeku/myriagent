using System.Security.Cryptography;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>裝置金鑰錯誤的分類與停止門檻（錯誤碼照 certutil -error 印出的值另外寫一份，規則裡打錯字就對不上）</summary>
public sealed class DeviceKeyErrorRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0x80090016u)]   // NTE_BAD_KEYSET
    [InlineData(0x8009000Du)]   // NTE_NO_KEY
    [InlineData(0x80090011u)]   // NTE_NOT_FOUND
    public void 金鑰不存在_Missing(uint code)
    {
        DeviceKeyErrorRules.Classify(unchecked((int)code)).Should().Be(DeviceKeyErrorKind.Missing);
    }

    [Theory]
    [InlineData(0x80090030u)]   // NTE_DEVICE_NOT_READY
    [InlineData(0x8009000Eu)]   // NTE_NO_MEMORY
    [InlineData(0x80090031u)]   // NTE_AUTHENTICATION_IGNORED：PCP 把字典攻擊鎖定轉成這個再回給 NCrypt
    [InlineData(0x80280800u)]   // TPM_E_RETRY
    [InlineData(0x80280801u)]   // TPM_E_NEEDS_SELFTEST
    [InlineData(0x80280802u)]   // TPM_E_DOING_SELFTEST
    [InlineData(0x80280803u)]   // TPM_E_DEFEND_LOCK_RUNNING
    [InlineData(0x80280015u)]   // TPM_E_RESOURCES
    [InlineData(0x80280901u)]   // TPM_20_E_CONTEXT_GAP
    [InlineData(0x80280902u)]   // TPM_20_E_OBJECT_MEMORY
    [InlineData(0x80280903u)]   // TPM_20_E_SESSION_MEMORY
    [InlineData(0x80280904u)]   // TPM_20_E_MEMORY
    [InlineData(0x80280905u)]   // TPM_20_E_SESSION_HANDLES
    [InlineData(0x80280908u)]   // TPM_20_E_YIELDED
    [InlineData(0x80280909u)]   // TPM_20_E_CANCELED
    [InlineData(0x8028090Au)]   // TPM_20_E_TESTING
    [InlineData(0x80280920u)]   // TPM_20_E_NV_RATE
    [InlineData(0x80280921u)]   // TPM_20_E_LOCKOUT
    [InlineData(0x80280922u)]   // TPM_20_E_RETRY
    [InlineData(0x80280923u)]   // TPM_20_E_NV_UNAVAILABLE
    [InlineData(0x80290401u)]   // TPM_E_PCP_DEVICE_NOT_READY
    [InlineData(0x80290409u)]   // TPM_E_PCP_AUTHENTICATION_IGNORED
    [InlineData(0x8029041Bu)]   // TPM_E_LOCKED_OUT
    [InlineData(0x80284008u)]   // TBS_E_SERVICE_NOT_RUNNING
    [InlineData(0x80284009u)]   // TBS_E_TOO_MANY_TBS_CONTEXTS
    [InlineData(0x8028400Au)]   // TBS_E_TOO_MANY_RESOURCES
    [InlineData(0x8028400Bu)]   // TBS_E_SERVICE_START_PENDING
    [InlineData(0x8028400Du)]   // TBS_E_COMMAND_CANCELED
    [InlineData(0x800705B4u)]   // HRESULT_FROM_WIN32(ERROR_TIMEOUT)
    [InlineData(0x80070102u)]   // HRESULT_FROM_WIN32(WAIT_TIMEOUT)
    [InlineData(0x800700AAu)]   // HRESULT_FROM_WIN32(ERROR_BUSY)
    public void TPM與TBS的暫時性錯誤_Transient(uint code)
    {
        DeviceKeyErrorRules.Classify(unchecked((int)code)).Should().Be(DeviceKeyErrorKind.Transient);
    }

    [Theory]
    [InlineData(0x80090035u)]   // NTE_DEVICE_NOT_FOUND：TPM 被停用或拿掉（恢復後金鑰還在）
    [InlineData(0x8028400Fu)]   // TBS_E_TPM_NOT_FOUND
    [InlineData(0x80284010u)]   // TBS_E_SERVICE_DISABLED
    [InlineData(0x80284006u)]   // TBS_E_IOERROR
    [InlineData(0x80284001u)]   // TBS_E_INTERNAL_ERROR
    [InlineData(0x80280906u)]   // TPM_20_E_OBJECT_HANDLES：要重新開機
    [InlineData(0x80280907u)]   // TPM_20_E_LOCALITY
    [InlineData(0x80090003u)]   // NTE_BAD_KEY
    [InlineData(0x80090010u)]   // NTE_PERM
    [InlineData(0x80090013u)]   // NTE_BAD_PROVIDER
    [InlineData(0x80290408u)]   // TPM_E_PCP_AUTHENTICATION_FAILED
    [InlineData(0x80131430u)]   // CORSEC_E_CRYPTO：沒帶錯誤碼的 CryptographicException
    [InlineData(0x80004005u)]   // E_FAIL
    [InlineData(0u)]
    [InlineData(1u)]
    public void 其他錯誤_Unknown(uint code)
    {
        DeviceKeyErrorRules.Classify(unchecked((int)code)).Should().Be(DeviceKeyErrorKind.Unknown);
    }

    [Fact]
    public void 例外_CryptographicException看HResult_其他例外當不認得()
    {
        DeviceKeyErrorRules.Classify(new CryptographicException(DeviceKeyErrorRules.NteBadKeyset)).Should().Be(DeviceKeyErrorKind.Missing);
        DeviceKeyErrorRules.Classify(new CryptographicException(DeviceKeyErrorRules.Tpm20Retry)).Should().Be(DeviceKeyErrorKind.Transient);
        DeviceKeyErrorRules.Classify(new CryptographicException("沒有錯誤碼")).Should().Be(DeviceKeyErrorKind.Unknown);
        DeviceKeyErrorRules.Classify(new ArgumentException("金鑰不是 ECDSA")).Should().Be(DeviceKeyErrorKind.Unknown);
        // 別的例外剛好帶了「不存在」的 HResult 也不算：只有 CNG 回的才可信
        DeviceKeyErrorRules.Classify(new HResultException(DeviceKeyErrorRules.NteBadKeyset)).Should().Be(DeviceKeyErrorKind.Unknown);
    }

    private sealed class HResultException : Exception
    {
        public HResultException(int hresult) => HResult = hresult;
    }

    [Fact]
    public void 不存在_立刻停止_紀錄不變()
    {
        var streak = new DeviceKeyStreak(3, 2, T0);

        var (next, action) = DeviceKeyErrorRules.OnFailure(streak, DeviceKeyErrorKind.Missing, T0.AddSeconds(5));

        action.Should().Be(DeviceKeyAction.Stop);
        next.Should().Be(streak);
    }

    [Fact]
    public void 暫時性_一直重試_不累計不認得的次數()
    {
        var streak = DeviceKeyStreak.None;
        var now = T0;
        for (var i = 0; i < 2000; i++)
        {
            (streak, var action) = DeviceKeyErrorRules.OnFailure(streak, DeviceKeyErrorKind.Transient, now);
            action.Should().Be(DeviceKeyAction.Retry, $"第 {i + 1} 次（TPM 鎖定可以持續好幾個小時）");
            now = now.AddSeconds(16);
        }

        streak.Should().Be(new DeviceKeyStreak(2000, 0, null));
    }

    [Fact]
    public void 不認得_次數與時間都到門檻才停()
    {
        var streak = DeviceKeyStreak.None;
        DeviceKeyAction action;

        // 10 次、但只過了 95 秒（退避 1, 2, 4, 8, 16, 16…）：不停
        var at = new[] { 0, 1, 3, 7, 15, 31, 47, 63, 79, 95 };
        foreach (var s in at)
        {
            (streak, action) = DeviceKeyErrorRules.OnFailure(streak, DeviceKeyErrorKind.Unknown, T0.AddSeconds(s));
            action.Should().Be(DeviceKeyAction.Retry, $"{s} 秒");
        }

        streak.Should().Be(new DeviceKeyStreak(10, 10, T0));

        // 時間到了的前一刻
        (streak, action) = DeviceKeyErrorRules.OnFailure(streak, DeviceKeyErrorKind.Unknown, T0 + DeviceKeyErrorRules.GiveUpAfter - TimeSpan.FromMilliseconds(1));
        action.Should().Be(DeviceKeyAction.Retry);

        // 剛好滿 10 分鐘
        (streak, action) = DeviceKeyErrorRules.OnFailure(streak, DeviceKeyErrorKind.Unknown, T0 + DeviceKeyErrorRules.GiveUpAfter);
        action.Should().Be(DeviceKeyAction.Stop);
        streak.Should().Be(new DeviceKeyStreak(12, 12, T0));
    }

    [Fact]
    public void 不認得_時間夠久但次數不夠_不停()
    {
        // 睡眠喚醒：第一次失敗之後隔了一整晚才第二次
        var (streak, action) = DeviceKeyErrorRules.OnFailure(DeviceKeyStreak.None, DeviceKeyErrorKind.Unknown, T0);
        action.Should().Be(DeviceKeyAction.Retry);
        for (var i = 2; i < DeviceKeyErrorRules.GiveUpAttempts; i++)
        {
            (streak, action) = DeviceKeyErrorRules.OnFailure(streak, DeviceKeyErrorKind.Unknown, T0.AddHours(10 + i));
            action.Should().Be(DeviceKeyAction.Retry, $"第 {i} 次");
        }

        (streak, action) = DeviceKeyErrorRules.OnFailure(streak, DeviceKeyErrorKind.Unknown, T0.AddHours(30));
        action.Should().Be(DeviceKeyAction.Stop, "第 10 次而且超過 10 分鐘");
        streak.Unknown.Should().Be(DeviceKeyErrorRules.GiveUpAttempts);
    }

    [Fact]
    public void 不認得_中間夾著暫時性_不歸零也不多算()
    {
        var streak = DeviceKeyStreak.None;
        for (var i = 0; i < 9; i++)
        {
            (streak, _) = DeviceKeyErrorRules.OnFailure(streak, DeviceKeyErrorKind.Unknown, T0.AddMinutes(i));
            (streak, _) = DeviceKeyErrorRules.OnFailure(streak, DeviceKeyErrorKind.Transient, T0.AddMinutes(i).AddSeconds(30));
        }

        streak.Should().Be(new DeviceKeyStreak(18, 9, T0));
        var (_, action) = DeviceKeyErrorRules.OnFailure(streak, DeviceKeyErrorKind.Unknown, T0.AddMinutes(10));
        action.Should().Be(DeviceKeyAction.Stop);
    }

    [Fact]
    public void 時鐘倒退_不算滿()
    {
        var streak = new DeviceKeyStreak(20, 20, T0);

        DeviceKeyErrorRules.ShouldGiveUp(streak, T0.AddMinutes(-30)).Should().BeFalse();
        DeviceKeyErrorRules.ShouldGiveUp(streak, T0.AddMinutes(10)).Should().BeTrue();
        DeviceKeyErrorRules.ShouldGiveUp(DeviceKeyStreak.None, T0.AddDays(1)).Should().BeFalse();
        DeviceKeyErrorRules.ShouldGiveUp(new DeviceKeyStreak(9, 9, T0), T0.AddDays(1)).Should().BeFalse();
    }

    [Fact]
    public void 不認得的種類值_丟例外()
    {
        FluentActions.Invoking(() => DeviceKeyErrorRules.OnFailure(DeviceKeyStreak.None, (DeviceKeyErrorKind)99, T0))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void 說明文字_退避不叫人重新配對_停下來照實際錯誤()
    {
        var unavailable = DeviceKeyErrorRules.Unavailable(DeviceKeyErrorRules.TpmRetry);
        unavailable.Kind.Should().Be(SyncFailureKind.Transient);
        unavailable.Code.Should().Be(DeviceKeyErrorRules.UnavailableCode);
        unavailable.Detail.Should().Be("裝置金鑰暫時無法使用（0x80280800）");
        DeviceKeyErrorRules.Hex(1).Should().Be("0x00000001");

        SyncStatusRules.Detail(SyncEngineState.Backoff, SyncStopReason.None, unavailable, TimeSpan.FromSeconds(15.2), false)
            .Should().Be("裝置金鑰暫時無法使用（0x80280800），16 秒後重試");

        var lost = DeviceKeyErrorRules.Lost(unchecked((int)0x80284006));
        lost.Code.Should().Be(DeviceKeyErrorRules.LostCode);
        SyncStatusRules.Detail(SyncEngineState.Stopped, SyncStopReason.KeyMissing, lost, null, false)
            .Should().Be("裝置金鑰一直無法使用（0x80284006），請解除配對後重新配對");
        SyncStatusRules.Detail(SyncEngineState.Stopped, SyncStopReason.KeyMissing, null, null, false)
            .Should().Be("找不到這台的裝置金鑰，請解除配對後重新配對");
        SyncStatusRules.Detail(SyncEngineState.Stopped, SyncStopReason.KeyMissing, unavailable, null, false)
            .Should().Be("找不到這台的裝置金鑰，請解除配對後重新配對", "之前暫時打不開、後來確定不存在的，照「找不到」說");
    }
}
