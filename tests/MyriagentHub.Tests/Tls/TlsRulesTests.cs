using System.Buffers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MyriagentHub.Tls;

namespace MyriagentHub.Tests.Tls;

/// <summary>ClientHello 解析與 ACME 續期規則</summary>
public sealed class TlsRulesTests
{
    [Fact]
    public async Task 解析真實的ClientHello_取得SNI與ALPN()
    {
        var hello = await CaptureClientHelloAsync("hub.example.com", [new SslApplicationProtocol("acme-tls/1")]);
        ClientHelloParser.TryParse(new ReadOnlySequence<byte>(hello), out var result).Should().BeTrue();
        result!.ServerName.Should().Be("hub.example.com");
        result.Alpn.Should().Equal("acme-tls/1");

        var normal = await CaptureClientHelloAsync("localhost", [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]);
        ClientHelloParser.TryParse(new ReadOnlySequence<byte>(normal), out var r2).Should().BeTrue();
        r2!.Alpn.Should().Equal("h2", "http/1.1");
    }

    [Fact]
    public void 亂七八糟的位元組不丟例外()
    {
        var random = new Random(42);
        for (var i = 0; i < 2_000; i++)
        {
            var bytes = new byte[random.Next(0, 300)];
            random.NextBytes(bytes);
            if (bytes.Length > 0 && random.Next(2) == 0)
                bytes[0] = 0x16;
            if (bytes.Length > 5 && random.Next(2) == 0)
                bytes[5] = 0x01;
            var act = () => ClientHelloParser.TryParse(bytes, out _);
            act.Should().NotThrow();
        }
        ClientHelloParser.TryParse([], out _).Should().BeFalse();
        ClientHelloParser.TryParse([0x16, 0x03, 0x01, 0x00, 0x04, 0x02, 0x00, 0x00, 0x00], out _).Should().BeFalse("不是 ClientHello");
    }

    [Fact]
    public void 退避_1_3_6_12小時()
    {
        AcmeRules.BackoffMs(1).Should().Be(1 * AcmeRules.HourMs);
        AcmeRules.BackoffMs(2).Should().Be(3 * AcmeRules.HourMs);
        AcmeRules.BackoffMs(3).Should().Be(6 * AcmeRules.HourMs);
        AcmeRules.BackoffMs(4).Should().Be(12 * AcmeRules.HourMs);
        AcmeRules.BackoffMs(30).Should().Be(12 * AcmeRules.HourMs);
        var failed = AcmeRules.Failed(new TlsState { ConsecutiveFailures = 1 }, 1_000, "boom");
        failed.ConsecutiveFailures.Should().Be(2);
        failed.NextAttemptAt.Should().Be(1_000 + 3 * AcmeRules.HourMs);
        failed.LastOk.Should().BeFalse();
    }

    [Fact]
    public void 續期時點_ARI視窗內固定一點_沒有ARI就三分之二效期()
    {
        var day = AcmeRules.DayMs;
        var noAri = new TlsState { CurrentThumbprint = "AA", NotBefore = 0, NotAfter = 90 * day };
        AcmeRules.RenewAt(noAri).Should().Be(60 * day);
        var shortLived = new TlsState { CurrentThumbprint = "AA", NotBefore = 0, NotAfter = 45 * day };
        AcmeRules.RenewAt(shortLived).Should().Be(30 * day, "2028 年起 45 天效期：不寫死 60 天");

        var ari = noAri with { AriWindowStart = 50 * day, AriWindowEnd = 52 * day };
        var renewAt = AcmeRules.RenewAt(ari);
        renewAt.Should().BeInRange(50 * day, 52 * day);
        AcmeRules.RenewAt(ari).Should().Be(renewAt, "同一張憑證每次算出來一樣");
        AcmeRules.RenewAt(ari with { CurrentThumbprint = "BB" }).Should().NotBe(renewAt, "不同憑證分散開");
    }

    [Fact]
    public void 計畫_先staging再正式_退避期間等待_到期續期()
    {
        var hour = AcmeRules.HourMs;
        var fresh = new TlsState();
        AcmeRules.Plan(fresh, 0, skipStaging: false, 12 * hour).Action.Should().Be(AcmeAction.IssueStaging);
        AcmeRules.Plan(fresh, 0, skipStaging: true, 12 * hour).Action.Should().Be(AcmeAction.IssueProduction);
        AcmeRules.Plan(fresh with { StagingVerifiedAt = 1 }, 0, false, 12 * hour).Action.Should().Be(AcmeAction.IssueProduction);
        var backoff = AcmeRules.Plan(fresh with { NextAttemptAt = 5 * hour }, 0, false, 12 * hour);
        backoff.Action.Should().Be(AcmeAction.Wait);
        backoff.NextCheckAt.Should().Be(5 * hour);

        var valid = new TlsState { CurrentThumbprint = "AA", NotBefore = 0, NotAfter = 90 * AcmeRules.DayMs };
        var wait = AcmeRules.Plan(valid, 10 * hour, false, 12 * hour);
        wait.Action.Should().Be(AcmeAction.Wait);
        wait.NextCheckAt.Should().Be(22 * hour, "每 12 小時檢查一次");
        AcmeRules.Plan(valid, 60 * AcmeRules.DayMs, false, 12 * hour).Action.Should().Be(AcmeAction.Renew);
    }

    [Fact]
    public void ARI查詢間隔_與到期告警_與舊憑證刪除時點()
    {
        var hour = AcmeRules.HourMs;
        var state = new TlsState { CurrentThumbprint = "AA", CurrentAriId = "x.y" };
        AcmeRules.ShouldCheckAri(state, 0, 12 * hour).Should().BeTrue("從沒查過");
        AcmeRules.ShouldCheckAri(state with { AriCheckedAt = 0 }, 11 * hour, 12 * hour).Should().BeFalse();
        AcmeRules.ShouldCheckAri(state with { AriCheckedAt = 0, AriRetryAfterMs = 20 * hour }, 13 * hour, 12 * hour).Should().BeFalse("伺服器要求的間隔比較長");
        AcmeRules.ShouldCheckAri(state with { CurrentAriId = null }, 0, 12 * hour).Should().BeFalse();

        var day = AcmeRules.DayMs;
        AcmeRules.Expiry(40 * day, 0).Should().Be(ExpirySeverity.None);
        AcmeRules.Expiry(29 * day, 0).Should().Be(ExpirySeverity.Warning);
        AcmeRules.Expiry(13 * day, 0).Should().Be(ExpirySeverity.Critical);
        AcmeRules.Expiry(0, 1).Should().Be(ExpirySeverity.Expired);

        var retired = new[] { new RetiredCertificate("OLD", 0), new RetiredCertificate("NEW", 20 * hour) };
        AcmeRules.DueForRemoval(retired, 24 * hour).Select(r => r.Thumbprint).Should().Equal("OLD");
        AcmeRules.SelfSignedNeedsRenewal(10 * day, 0).Should().BeFalse();
        AcmeRules.SelfSignedNeedsRenewal(6 * day, 0).Should().BeTrue();
    }

    [Fact]
    public void 手動cert_renew一律重查ARI_force不看續期時點直接續期()
    {
        var hour = AcmeRules.HourMs;
        var day = AcmeRules.DayMs;
        var state = new TlsState { CurrentThumbprint = "AA", CurrentAriId = "x.y", NotBefore = 0, NotAfter = 90 * day, AriCheckedAt = 0 };
        AcmeRules.ShouldCheckAri(state, 1 * hour, 12 * hour).Should().BeFalse("排定的檢查：12 小時內不重查");
        AcmeRules.ShouldCheckAri(state, 1 * hour, 12 * hour, manual: true).Should().BeTrue("admin cert-renew 忽略 AriCheckedAt，一律重查");
        AcmeRules.ShouldCheckAri(state with { AriRetryAfterMs = AcmeRules.DayMs }, 1 * hour, 12 * hour, manual: true).Should().BeTrue("也忽略 Retry-After");
        AcmeRules.ShouldCheckAri(state with { CurrentAriId = null }, 1 * hour, 12 * hour, manual: true).Should().BeFalse("沒有 ARI 識別碼就沒得查");

        // CA 以 ARI 把視窗改成過去：重查後的續期時點已到 → 續期
        var moved = state with { AriWindowStart = 0, AriWindowEnd = 1 * hour, AriCheckedAt = 2 * hour };
        AcmeRules.Plan(moved, 2 * hour, skipStaging: true, 12 * hour).Action.Should().Be(AcmeAction.Renew);
        // 視窗還沒到：一般不續期，--force 直接續期
        AcmeRules.Plan(state, 1 * hour, skipStaging: true, 12 * hour).Action.Should().Be(AcmeAction.Wait);
        var forced = AcmeRules.Plan(state, 1 * hour, skipStaging: true, 12 * hour, forceRenew: true);
        forced.Action.Should().Be(AcmeAction.Renew);
        forced.Reason.Should().Be("forced");
        AcmeRules.Plan(new TlsState(), 0, skipStaging: false, 12 * hour, forceRenew: true).Action.Should().Be(AcmeAction.IssueStaging, "還沒有正式憑證時照常先打 staging");
        AcmeRules.Plan(state with { NextAttemptAt = 5 * hour }, 1 * hour, true, 12 * hour, forceRenew: true).Action.Should().Be(AcmeAction.Wait, "退避由呼叫端（手動時）先清掉");
    }

    [Fact]
    public void 憑證顯示名稱_有CN用CN_沒有CN用SAN的DNS名稱()
    {
        using var withCn = CreateCertificate("CN=hub.example.com, O=Myriagent", ["hub.example.com", "www.hub.example.com"]);
        TlsCertificates.DisplayName(withCn).Should().Be("hub.example.com");

        // Let's Encrypt 的短效憑證與 Pebble 簽的憑證主體是空的，名稱只在 SAN
        using var sanOnly = CreateCertificate("", ["hub.myriagent.test", "alt.myriagent.test"]);
        sanOnly.Subject.Should().BeEmpty();
        TlsCertificates.DisplayName(sanOnly).Should().Be("hub.myriagent.test, alt.myriagent.test");
        TlsCertificates.DnsNames(sanOnly).Should().Equal("hub.myriagent.test", "alt.myriagent.test");

        using var orgOnly = CreateCertificate("O=Myriagent", ["only.myriagent.test"]);
        TlsCertificates.DisplayName(orgOnly).Should().Be("only.myriagent.test", "主體沒有 CN 就用 SAN，不拿其他欄位充數");
        using var nothing = CreateCertificate("O=Myriagent", []);
        TlsCertificates.DisplayName(nothing).Should().Be("O=Myriagent", "什麼都沒有才用原樣的主體");

        // 從 PEM 重新載入（擴充欄位不一定是具名型別）也一樣
        using var reloaded = X509CertificateLoader.LoadCertificate(sanOnly.RawData);
        TlsCertificates.DisplayName(reloaded).Should().Be("hub.myriagent.test, alt.myriagent.test");
    }

    private static X509Certificate2 CreateCertificate(string subject, string[] dnsNames)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName(subject), key, HashAlgorithmName.SHA256);
        if (dnsNames.Length > 0)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var name in dnsNames)
                san.AddDnsName(name);
            request.CertificateExtensions.Add(san.Build());
        }
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>讓 SslStream 當用戶端把 ClientHello 寫進記憶體（對方永遠不回應），取得原始位元組</summary>
    private static async Task<byte[]> CaptureClientHelloAsync(string host, List<SslApplicationProtocol> alpn)
    {
        var capture = new CaptureStream();
        using var ssl = new SslStream(capture, leaveInnerStreamOpen: true);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handshake = ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            ApplicationProtocols = alpn,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        }, cts.Token);
        var bytes = await capture.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(10));
        capture.Complete();
        try
        {
            await handshake;
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException)
        {
        }
        return bytes;
    }

    private sealed class CaptureStream : Stream
    {
        private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<byte[]> FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Complete() => closed.TrySetResult();

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await closed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await closed.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override void Write(byte[] buffer, int offset, int count) => FirstWrite.TrySetResult(buffer.AsSpan(offset, count).ToArray());

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            FirstWrite.TrySetResult(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
