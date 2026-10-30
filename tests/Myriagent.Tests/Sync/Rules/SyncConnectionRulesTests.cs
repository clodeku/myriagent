using System.Net.Security;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

public sealed class SyncConnectionRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void 退避_依序1到16秒_上限16()
    {
        var delays = Enumerable.Range(0, 9)
            .Select(attempt => SyncBackoffRules.Delay(attempt, 0.5, SyncBackoffRules.DefaultStepsSeconds).TotalSeconds)
            .ToList();

        delays.Should().Equal(1, 2, 4, 8, 16, 16, 16, 16, 16);
    }

    [Theory]
    [InlineData(0.0, 0.8)]
    [InlineData(0.25, 0.9)]
    [InlineData(1.0, 1.2)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(-5.0, 0.8)]
    [InlineData(7.0, 1.2)]
    public void 退避_抖動在正負百分之20內(double jitter, double factor)
    {
        foreach (var (attempt, step) in new[] { (0, 1.0), (2, 4.0), (4, 16.0) })
        {
            var delay = SyncBackoffRules.Delay(attempt, jitter, SyncBackoffRules.DefaultStepsSeconds).TotalSeconds;
            delay.Should().BeApproximately(step * factor, 1e-9);
            delay.Should().BeInRange(step * 0.8 - 1e-9, step * 1.2 + 1e-9);
        }
    }

    [Fact]
    public void 退避_可注入步階_負的attempt當成0()
    {
        double[] steps = [0.2, 0.5, 1, 2];

        SyncBackoffRules.Delay(-1, 0.5, steps).TotalSeconds.Should().BeApproximately(0.2, 1e-9);
        SyncBackoffRules.Delay(10, 0.5, steps).TotalSeconds.Should().BeApproximately(2, 1e-9);
        FluentActions.Invoking(() => SyncBackoffRules.Delay(0, 0.5, [])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Live滿30秒才歸零()
    {
        SyncBackoffRules.ShouldResetAttempts(TimeSpan.FromSeconds(29.9)).Should().BeFalse();
        SyncBackoffRules.ShouldResetAttempts(TimeSpan.FromSeconds(30)).Should().BeTrue();
        SyncBackoffRules.ShouldResetAttempts(TimeSpan.FromMinutes(5)).Should().BeTrue();
    }

    [Fact]
    public void NextDelay_不早於notBefore()
    {
        var steps = SyncBackoffRules.DefaultStepsSeconds;

        SyncBackoffRules.NextDelay(0, 0.5, steps, Now, Now.AddSeconds(45)).Should().Be(TimeSpan.FromSeconds(45), "伺服器說 45 秒後再來");
        SyncBackoffRules.NextDelay(4, 0.5, steps, Now, Now.AddSeconds(3)).Should().Be(TimeSpan.FromSeconds(16), "退避比較晚");
        SyncBackoffRules.NextDelay(1, 0.5, steps, Now, Now.AddSeconds(-10)).Should().Be(TimeSpan.FromSeconds(2), "過去的 notBefore 不影響");
        SyncBackoffRules.NextDelay(1, 0.5, steps, Now, null).Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void HubErrorCode_取最後一個HubException之後()
    {
        SyncFailureRules.HubErrorCode("An unexpected error occurred invoking 'Hello' on the server. HubException: x HubException: not_proven")
            .Should().Be("not_proven");
        SyncFailureRules.HubErrorCode("An unexpected error occurred invoking 'Pull' on the server. HubException: invalid").Should().Be("invalid");
        SyncFailureRules.HubErrorCode("HubException: protocol  ").Should().Be("protocol");
        SyncFailureRules.HubErrorCode("連線被關閉").Should().BeNull();
        SyncFailureRules.HubErrorCode("HubException: ").Should().BeNull();
        SyncFailureRules.HubErrorCode(null).Should().BeNull();
    }

    [Theory]
    [InlineData(401, null, false, false, "Unauthorized")]
    [InlineData(403, null, false, false, "Forbidden")]
    [InlineData(null, "protocol", false, false, "Protocol")]
    [InlineData(null, "draining", false, false, "Transient")]
    [InlineData(null, "update_required", false, false, "Transient")]
    [InlineData(null, "not_proven", false, false, "NotProven")]
    [InlineData(null, null, false, true, "NotProven")]
    [InlineData(null, "invalid", false, false, "ClientBug")]
    [InlineData(null, null, true, false, "Transient")]
    [InlineData(500, null, false, false, "Transient")]
    [InlineData(503, null, false, false, "Transient")]
    [InlineData(null, null, false, false, "Transient")]
    [InlineData(null, "brand_new_code", false, false, "Transient")]
    [InlineData(null, "brand_new_code", false, true, "Transient")]
    [InlineData(401, "not_proven", false, false, "Unauthorized")]
    public void 分類表(int? status, string? code, bool tls, bool closedDuringHandshake, string expected)
    {
        var failure = SyncFailureRules.Classify(status, code, tls, closedDuringHandshake);

        failure.Kind.Should().Be(Enum.Parse<SyncFailureKind>(expected));
        failure.Detail.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void 分類_說明文字()
    {
        SyncFailureRules.Classify(401, null, false).Detail.Should().Contain("重新配對");
        SyncFailureRules.Classify(null, "protocol", false).Detail.Should().Contain("伺服器的版本比這個 app 舊", "U3 起宣告了 update1：太舊會拿到只更新的連線");
        SyncFailureRules.Classify(null, "draining", false).Detail.Should().Be("伺服器正在更新");
        SyncFailureRules.Classify(null, "update_required", false).Detail.Should().Contain("只能下載更新");
        SyncFailureRules.Classify(null, "not_proven", false).Detail.Should().Contain("TLS 代理");
        SyncFailureRules.Classify(null, "brand_new_code", false).Code.Should().Be("brand_new_code");
    }

    [Fact]
    public void 建立連線失敗_逾時而且沒有HTTP狀態與TLS失敗才算連線逾時()
    {
        var timeout = TimeSpan.FromSeconds(20);

        var timedOut = SyncFailureRules.ClassifyConnect(null, false, timedOut: true, timeout);
        timedOut.Kind.Should().Be(SyncFailureKind.Transient, "照退避重連");
        timedOut.Code.Should().Be(SyncFailureRules.ConnectTimeoutCode);
        timedOut.Detail.Should().Be("連線伺服器 20 秒沒有完成（伺服器可能正在重新啟動）");

        SyncFailureRules.ClassifyConnect(null, false, timedOut: false, timeout).Should().Be(SyncFailureRules.Classify(null, null, false), "沒到時間就失敗的照原本的分類（伺服器連不上）");
        SyncFailureRules.ClassifyConnect(401, false, timedOut: true, timeout).Kind.Should().Be(SyncFailureKind.Unauthorized, "升級已經回了 401：照 HTTP 狀態，不是逾時");
        SyncFailureRules.ClassifyConnect(503, false, timedOut: true, timeout).Code.Should().Be("503");
        SyncFailureRules.ClassifyConnect(null, true, timedOut: true, timeout).Code.Should().Be("tls", "憑證驗不過的照 TLS 失敗");
    }

    [Fact]
    public void IsTerminal只有401_403_protocol改成每小時重試()
    {
        Enum.GetValues<SyncFailureKind>().Where(SyncFailureRules.IsTerminal)
            .Should().BeEquivalentTo([SyncFailureKind.Unauthorized, SyncFailureKind.Forbidden]);
        SyncFailureRules.StopReasonFor(SyncFailureKind.Unauthorized).Should().Be(SyncStopReason.Unauthorized);
        SyncFailureRules.StopReasonFor(SyncFailureKind.Forbidden).Should().Be(SyncStopReason.Forbidden);
        SyncFailureRules.StopReasonFor(SyncFailureKind.Protocol).Should().Be(SyncStopReason.Protocol);
        SyncFailureRules.StopReasonFor(SyncFailureKind.NotProven).Should().Be(SyncStopReason.None);
    }

    [Fact]
    public void Outbox判決_對應停止原因()
    {
        SyncFailureRules.StopReasonFor(OutboxVerdict.StopSeqRegression).Should().Be(SyncStopReason.SeqRegression);
        SyncFailureRules.StopReasonFor(OutboxVerdict.StopServerBehind).Should().Be(SyncStopReason.ServerBehind);
        SyncFailureRules.StopReasonFor(OutboxVerdict.StopInvalidBatch).Should().Be(SyncStopReason.InvalidBatch);
        SyncFailureRules.StopReasonFor(OutboxVerdict.Continue).Should().BeNull();
        SyncFailureRules.StopReasonFor(OutboxVerdict.Reconnect).Should().BeNull();
    }

    [Fact]
    public void 送出世代_舊世代gap丟棄_目前世代gap套用_seq_regression與invalid一律Stop_不認得的Reconnect()
    {
        static BatchAck Error(string code, long? expected = null) => new("b1", 0, [], new BatchError(code, expected));

        SendGenerationRules.OnAck(1, 2, Error(SyncErrorCodes.Gap, 5)).Should().Be(BatchAckAction.DropStale);
        SendGenerationRules.OnAck(2, 2, Error(SyncErrorCodes.Gap, 5)).Should().Be(BatchAckAction.Apply);
        SendGenerationRules.OnAck(2, 2, Error(SyncErrorCodes.Gap)).Should().Be(BatchAckAction.Apply, "沒帶 expected 交給 ApplyAck 判成 Reconnect");
        foreach (var code in new[] { SyncErrorCodes.SeqRegression, SyncErrorCodes.Invalid, SyncErrorCodes.TooLarge })
        {
            SendGenerationRules.OnAck(1, 2, Error(code)).Should().Be(BatchAckAction.Stop, $"{code} 舊世代也停");
            SendGenerationRules.OnAck(2, 2, Error(code)).Should().Be(BatchAckAction.Stop, code);
        }

        SendGenerationRules.OnAck(2, 2, Error("brand_new_code")).Should().Be(BatchAckAction.Reconnect);
        SendGenerationRules.OnAck(1, 2, new BatchAck("b1", 3, [new BatchEventResult(3, Seq: 10)])).Should().Be(BatchAckAction.Apply, "逐筆結果一律有效");
    }

    [Fact]
    public void 通知shutdown_notBefore夾在1到600秒_沒帶用30秒()
    {
        SyncTimingRules.NoticeNotBefore(Now, null).Should().Be(Now.AddSeconds(30));
        SyncTimingRules.NoticeNotBefore(Now, 45).Should().Be(Now.AddSeconds(45));
        SyncTimingRules.NoticeNotBefore(Now, 0).Should().Be(Now.AddSeconds(1));
        SyncTimingRules.NoticeNotBefore(Now, -20).Should().Be(Now.AddSeconds(1));
        SyncTimingRules.NoticeNotBefore(Now, 99999).Should().Be(Now.AddSeconds(600));
    }

    [Fact]
    public void 送出延遲_閒置後只等合併時間_距上一批不足間隔才等到滿()
    {
        var interval = TimeSpan.FromMilliseconds(100);
        var coalesce = TimeSpan.FromMilliseconds(10);
        SyncTimingRules.SendDelay(null, Now, interval, coalesce).Should().Be(coalesce, "這次連線還沒送過");
        SyncTimingRules.SendDelay(Now.AddSeconds(-5), Now, interval, coalesce).Should().Be(coalesce, "閒置很久了，不必再等滿 100 ms");
        SyncTimingRules.SendDelay(Now.AddMilliseconds(-30), Now, interval, coalesce).Should().Be(TimeSpan.FromMilliseconds(70), "批次之間至少隔 100 ms");
        SyncTimingRules.SendDelay(Now.AddMilliseconds(-95), Now, interval, coalesce).Should().Be(coalesce, "剩下的比合併時間短，照合併時間");
        SyncTimingRules.SendDelay(Now, Now, interval, coalesce).Should().Be(interval);
    }

    [Fact]
    public void 計時常數()
    {
        SyncTimingRules.HeartbeatInterval.Should().Be(TimeSpan.FromSeconds(15));
        SyncTimingRules.BatchInterval.Should().Be(TimeSpan.FromMilliseconds(100));
        SyncTimingRules.BatchCoalesce.Should().Be(TimeSpan.FromMilliseconds(10));
        SyncTimingRules.AckTimeout.Should().Be(TimeSpan.FromSeconds(60));
        SyncTimingRules.ConnectTimeout.Should().Be(TimeSpan.FromSeconds(20), "要比 SignalR 用戶端的交握逾時（15 秒）長");
        SyncTimingRules.StatusThrottle.Should().Be(TimeSpan.FromMilliseconds(500));
        SyncTimingRules.DevicesThrottle.Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void 開發信任_連到的與配對的都是迴路位址且開啟才接受()
    {
        const SslPolicyErrors bad = SslPolicyErrors.RemoteCertificateChainErrors;
        foreach (var local in new[] { "https://127.0.0.1:5443", "https://127.8.9.10", "https://[::1]:7001", "https://localhost:7001", "https://LOCALHOST" })
        {
            var uri = new Uri(local);
            DevTrustRules.IsLoopback(uri).Should().BeTrue(local);
            DevTrustRules.AcceptCertificate(uri, uri, bad, devTrustEnv: true).Should().BeTrue(local);
            DevTrustRules.AcceptCertificate(uri, uri, bad, devTrustEnv: false).Should().BeFalse($"{local}：沒開 MYRIAGENT_SYNC_DEV_TRUST");
        }

        var loopback = new Uri("https://127.0.0.1:5443");
        var production = new Uri(DevTrustRules.DefaultServer);
        DevTrustRules.IsLoopback(production).Should().BeFalse();
        DevTrustRules.IsLoopback(new Uri("https://10.0.0.1")).Should().BeFalse();
        DevTrustRules.IsLoopback(new Uri("https://localhost.evil.example")).Should().BeFalse();
        DevTrustRules.AcceptCertificate(loopback, production, bad, devTrustEnv: true).Should().BeFalse("配對的是正式伺服器，覆寫成迴路位址也不放寬");
        DevTrustRules.AcceptCertificate(production, loopback, bad, devTrustEnv: true).Should().BeFalse();
        DevTrustRules.AcceptCertificate(production, production, SslPolicyErrors.None, devTrustEnv: false).Should().BeTrue("驗得過的憑證照常接受");
    }

    [Theory]
    [InlineData("https://hub.example.com", "https://hub.example.com/")]
    [InlineData("https://hub.example.com/", "https://hub.example.com/")]
    [InlineData("  https://127.0.0.1:5443  ", "https://127.0.0.1:5443/")]
    [InlineData("https://[::1]:7001/", "https://[::1]:7001/")]
    [InlineData("http://hub.example.com", null)]
    [InlineData("wss://hub.example.com", null)]
    [InlineData("https://user:pass@hub.example.com", null)]
    [InlineData("https://user@hub.example.com", null)]
    [InlineData("https://hub.example.com/?x=1", null)]
    [InlineData("https://hub.example.com/?", null)]
    [InlineData("https://hub.example.com/#frag", null)]
    [InlineData("https://hub.example.com/#", null)]
    [InlineData("https://hub.example.com/api", null)]
    [InlineData("https://hub.example.com\\evil", null)]
    [InlineData("hub.example.com", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 伺服器網址_只收https_拒絕userinfo_query_fragment_路徑(string? input, string? expected)
    {
        DevTrustRules.ValidateServerUrl(input)?.ToString().Should().Be(expected);
        if (expected is null)
            DevTrustRules.ValidateServerUrl(input).Should().BeNull();
    }

    [Fact]
    public void 伺服器網址_覆寫合格才用_並標示()
    {
        DevTrustRules.ResolveServer("https://127.0.0.1:5443", DevTrustRules.DefaultServer)
            .Should().Be((new Uri("https://127.0.0.1:5443/"), true));
        DevTrustRules.ResolveServer("http://127.0.0.1:5443", DevTrustRules.DefaultServer)
            .Should().Be((new Uri(DevTrustRules.DefaultServer), false), "不合格的覆寫忽略");
        DevTrustRules.ResolveServer(null, DevTrustRules.DefaultServer).Overridden.Should().BeFalse();
    }

    [Fact]
    public void protocol之後一小時再試_有抖動_其他照退避()
    {
        var protocol = SyncFailureRules.Classify(null, "protocol", false);
        SyncBackoffRules.DelayAfter(protocol, 0, 0.5, [1, 2], Now, null).Should().Be(TimeSpan.FromHours(1));
        SyncBackoffRules.DelayAfter(protocol, 0, 0, [1, 2], Now, null).Should().Be(TimeSpan.FromMinutes(48));
        SyncBackoffRules.DelayAfter(protocol, 0, 1, [1, 2], Now, null).Should().Be(TimeSpan.FromMinutes(72));
        SyncBackoffRules.DelayAfter(protocol, 0, 0.5, [1, 2], Now, Now.AddHours(2)).Should().Be(TimeSpan.FromHours(2), "notBefore 較晚時取較晚的");
        var transient = SyncFailureRules.Classify(null, null, false);
        SyncBackoffRules.DelayAfter(transient, 1, 0.5, [1, 2], Now, null).Should().Be(TimeSpan.FromSeconds(2));
        SyncBackoffRules.DelayAfter(null, 0, 0.5, [1, 2], Now, null).Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void 停機通知的重連時間_乘上正負20百分比的抖動()
    {
        SyncTimingRules.NoticeNotBefore(Now, 30, 0.5).Should().Be(Now.AddSeconds(30));
        SyncTimingRules.NoticeNotBefore(Now, 30, 0).Should().Be(Now.AddSeconds(24));
        SyncTimingRules.NoticeNotBefore(Now, 30, 1).Should().Be(Now.AddSeconds(36));
        SyncTimingRules.NoticeNotBefore(Now, null, 0.5).Should().Be(Now.AddSeconds(30), "沒帶 retryAfterSec（Hello 的 draining）用 30 秒");
        SyncTimingRules.NoticeNotBefore(Now, 5000, 1).Should().Be(Now.AddSeconds(720), "先夾在 600 秒再乘抖動");
        SyncTimingRules.NoticeNotBefore(Now, 30, double.NaN).Should().Be(Now.AddSeconds(30));
    }
}
