using Myriagent.Agents;
using Myriagent.Hub;

namespace Myriagent.Tests.Hub;

/// <summary>
/// API 伺服器暫時無法服務時自動重試的規則（OverloadRetryRules）：只有 Claude 過載（529）、Antigravity 暫時無法服務（503）而失敗的一輪重試、
/// 連續最多 MaxAttemptsOf 次（529、503 都是 3 次）、提示與接續訊息的說法
/// </summary>
public class OverloadRetryRulesTests
{
    private const AgentServerBusy Overloaded = AgentServerBusy.Overloaded;
    private const AgentServerBusy Unavailable = AgentServerBusy.Unavailable;
    private const AgentServerBusy NetworkError = AgentServerBusy.NetworkError;

    [Fact]
    public void 暫時無法服務而失敗才重試_次數接著算()
    {
        OverloadRetryRules.NextAttempt(failed: true, Overloaded, stopRequested: false, accountChanging: false, isReplica: false, previousAttempts: 0).Should().Be(1);
        OverloadRetryRules.NextAttempt(true, Overloaded, false, false, false, 1).Should().Be(2);
        OverloadRetryRules.NextAttempt(true, Overloaded, false, false, false, -1).Should().Be(1, "次數不會是負的");
        OverloadRetryRules.NextAttempt(true, Unavailable, false, false, false, 0).Should().Be(1, "Antigravity 的 503 一樣重試");
        OverloadRetryRules.NextAttempt(true, Unavailable, false, false, false, 2).Should().Be(3);
        OverloadRetryRules.NextAttempt(true, NetworkError, false, false, false, 0).Should().Be(1, "Antigravity 的 API 網路斷線一樣重試");
        OverloadRetryRules.NextAttempt(true, NetworkError, false, false, false, 2).Should().Be(3);

        OverloadRetryRules.NextAttempt(failed: false, Overloaded, false, false, false, 0).Should().BeNull("正常結束、被停止的不重試");
        OverloadRetryRules.NextAttempt(true, AgentServerBusy.None, false, false, false, 0).Should().BeNull("其他錯誤不重試");
        OverloadRetryRules.NextAttempt(true, Unavailable, stopRequested: true, false, false, 0).Should().BeNull("使用者按了停止");
        OverloadRetryRules.NextAttempt(true, Overloaded, false, accountChanging: true, false, 0).Should().BeNull("這一輪結束時要換帳戶");
        OverloadRetryRules.NextAttempt(true, Unavailable, false, false, isReplica: true, 0).Should().BeNull("其他電腦的對話由那台處理");
    }

    [Theory]
    [InlineData((int)AgentServerBusy.Overloaded, 3)]
    [InlineData((int)AgentServerBusy.Unavailable, 3)]
    [InlineData((int)AgentServerBusy.NetworkError, 3)]
    public void 連續到上限就不再重試_並給提示(int busyValue, int max)
    {
        var busy = (AgentServerBusy)busyValue;
        OverloadRetryRules.MaxAttemptsOf(busy).Should().Be(max, "使用者要求重試三次都失敗就停止");
        OverloadRetryRules.NextAttempt(true, busy, false, false, false, max - 1).Should().Be(max);
        OverloadRetryRules.NextAttempt(true, busy, false, false, false, max).Should().BeNull();
        OverloadRetryRules.GaveUp(true, busy, false, false, false, max).Should().BeTrue();

        OverloadRetryRules.GaveUp(true, busy, false, false, false, max - 1).Should().BeFalse("還能重試");
        OverloadRetryRules.GaveUp(true, busy, stopRequested: true, false, false, max).Should().BeFalse("使用者自己停的不提示");
        OverloadRetryRules.GaveUp(true, AgentServerBusy.None, false, false, false, max).Should().BeFalse();
        OverloadRetryRules.MaxAttemptsOf(AgentServerBusy.None).Should().Be(0);
    }

    [Fact]
    public void 未知的種類丟例外()
        => FluentActions.Invoking(() => OverloadRetryRules.MaxAttemptsOf((AgentServerBusy)99)).Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void 提示與接續訊息_過載()
    {
        OverloadRetryRules.Delay.Should().Be(TimeSpan.FromSeconds(60), "使用者要求過 60 秒自動重試");
        OverloadRetryRules.ScheduledNotice(Overloaded, 1, OverloadRetryRules.Delay).Should()
            .Be("API 伺服器過載（529 Overloaded），60 秒後自動重試（第 1 次，最多 3 次）；在這之前送出訊息就改送你的訊息");
        OverloadRetryRules.RetryNotice(Overloaded, 2).Should().Be("API 過載後自動重試（第 2 次）");
        OverloadRetryRules.GaveUpNotice(Overloaded).Should().Contain("已連續自動重試 3 次都失敗");

        var text = OverloadRetryRules.Continuation(Overloaded, 3, OverloadRetryRules.Delay);
        text.Should().StartWith("（系統通知：").And.EndWith("）");
        text.Should().Contain("529 Overloaded").And.Contain("等了 60 秒後自動重試（第 3 次）").And.Contain("這不是使用者中斷")
            .And.Contain("使用者沒有傳送新訊息").And.Contain("從中斷處接著做");
    }

    [Fact]
    public void 提示與接續訊息_暫時無法服務()
    {
        OverloadRetryRules.ScheduledNotice(Unavailable, 1, OverloadRetryRules.Delay).Should()
            .Be("API 伺服器暫時無法服務（503 UNAVAILABLE），60 秒後自動重試（第 1 次，最多 3 次）；在這之前送出訊息就改送你的訊息");
        OverloadRetryRules.RetryNotice(Unavailable, 2).Should().Be("API 暫時無法服務後自動重試（第 2 次）");
        OverloadRetryRules.GaveUpNotice(Unavailable).Should().Contain("503 UNAVAILABLE").And.Contain("已連續自動重試 3 次都失敗").And.Contain("不再自動重試");

        var text = OverloadRetryRules.Continuation(Unavailable, 3, OverloadRetryRules.Delay);
        text.Should().StartWith("（系統通知：").And.EndWith("）");
        text.Should().Contain("503 UNAVAILABLE").And.Contain("等了 60 秒後自動重試（第 3 次）").And.Contain("這不是使用者中斷")
            .And.Contain("使用者沒有傳送新訊息").And.Contain("從中斷處接著做");
        text.Should().NotContain("529", "不是過載");
    }

    [Fact]
    public void 提示與接續訊息_網路連線中斷()
    {
        OverloadRetryRules.DelayOf(Overloaded).Should().Be(TimeSpan.FromSeconds(60));
        OverloadRetryRules.DelayOf(Unavailable).Should().Be(TimeSpan.FromSeconds(60));
        OverloadRetryRules.DelayOf(NetworkError).Should().Be(TimeSpan.FromSeconds(10), "網路瞬斷縮短等待 10 秒自動重試");
        OverloadRetryRules.DelayOf(AgentServerBusy.None).Should().Be(TimeSpan.Zero);

        OverloadRetryRules.ScheduledNotice(NetworkError, 1, OverloadRetryRules.NetworkErrorDelay).Should()
            .Be("API 網路連線中斷，10 秒後自動重試（第 1 次，最多 3 次）；在這之前送出訊息就改送你的訊息");
        OverloadRetryRules.RetryNotice(NetworkError, 2).Should().Be("API 連線中斷後自動重試（第 2 次）");
        OverloadRetryRules.GaveUpNotice(NetworkError).Should().Contain("網路連線持續中斷").And.Contain("已連續自動重試 3 次都失敗").And.Contain("不再自動重試");

        var text = OverloadRetryRules.Continuation(NetworkError, 1, OverloadRetryRules.NetworkErrorDelay);
        text.Should().StartWith("（系統通知：").And.EndWith("）");
        text.Should().Contain("網路連線中斷").And.Contain("等了 10 秒後自動重試（第 1 次）").And.Contain("這不是使用者中斷")
            .And.Contain("使用者沒有傳送新訊息").And.Contain("從中斷處接著做");
    }

    [Theory]
    [InlineData(60, "60 秒")]
    [InlineData(90, "90 秒")]
    [InlineData(120, "2 分鐘")]
    [InlineData(150, "150 秒")]
    [InlineData(0, "1 秒")]
    public void 等待時間的說法(int seconds, string expected)
        => OverloadRetryRules.FormatDelay(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
}
