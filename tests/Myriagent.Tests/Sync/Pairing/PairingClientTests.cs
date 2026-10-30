using System.Net;
using System.Net.Http.Headers;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Pairing;

/// <summary>配對端點的 HTTP 用戶端（假傳輸 <see cref="ScriptedHttpHandler"/>：沒有預備的回應就丟例外）</summary>
public sealed class PairingClientTests : IDisposable
{
    private static readonly Uri Server = new(PairingTestData.Server);

    private readonly ScriptedHttpHandler http = new();
    private readonly PairingClient client;

    public PairingClientTests() => client = new PairingClient(Server, Server, devTrustEnv: false, http);

    public void Dispose() => client.Dispose();

    private static PairStartRequest Request() =>
        new("OFFICE-PC", "OFFICE-PC", "User", PairingTestData.NewSpki(), PairingRules.KeyStorageSoftware, "0.1.0", SyncProtocol.Version);

    private static string Error(string code, int? interval = null) => SyncJson.Serialize(new PairError(code, interval));

    [Fact]
    public async Task Start_送SyncJson_不帶Authorization與Origin_路徑正確()
    {
        var start = PairingTestData.Start();
        http.Respond(HttpStatusCode.OK, SyncJson.Serialize(start));
        var request = Request();

        var response = await client.StartAsync(request, CancellationToken.None);

        response.Should().Be(start);
        var sent = http.Requests.Should().ContainSingle().Subject;
        sent.Method.Should().Be(HttpMethod.Post);
        sent.Uri.Should().Be(new Uri("https://hub.test/api/v1/pair/start"));
        sent.Headers.Keys.Should().NotContain(k => k.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || k.Equals("Origin", StringComparison.OrdinalIgnoreCase)
            || k.Equals("Cookie", StringComparison.OrdinalIgnoreCase));
        sent.Headers["Content-Type"].Should().StartWith("application/json");
        var body = JObject.Parse(sent.Body);
        body.Properties().Select(p => p.Name).Should().Equal("deviceName", "machineName", "windowsUser", "devicePubKey", "keyStorage", "appVersion", "protocolVersion");
        body["devicePubKey"]!.Value<string>().Should().Be(request.DevicePubKey);
        SyncJson.Deserialize<PairStartRequest>(sent.Body).Should().Be(request);
    }

    [Fact]
    public async Task Start_429_帶RetryAfter()
    {
        http.Respond(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1800)));

        var error = await client.Invoking(c => c.StartAsync(Request(), CancellationToken.None)).Should().ThrowAsync<PairingException>();

        error.Which.Code.Should().Be(PairingCodes.RateLimited);
        error.Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(1800));
    }

    [Fact]
    public async Task Start_400_伺服器的代碼_格式不對的回應是bad_response()
    {
        http.Respond(HttpStatusCode.BadRequest, Error(PairingErrors.TooManyPending));
        http.Respond(HttpStatusCode.OK, """{"deviceCode":"短","userCode":"x"}""");
        http.Respond(HttpStatusCode.BadRequest, "<html>");

        (await client.Invoking(c => c.StartAsync(Request(), CancellationToken.None)).Should().ThrowAsync<PairingException>()).Which.Code.Should().Be(PairingErrors.TooManyPending);
        (await client.Invoking(c => c.StartAsync(Request(), CancellationToken.None)).Should().ThrowAsync<PairingException>()).Which.Code.Should().Be(PairingCodes.BadResponse);
        (await client.Invoking(c => c.StartAsync(Request(), CancellationToken.None)).Should().ThrowAsync<PairingException>()).Which.Code.Should().Be(PairingCodes.BadResponse);
    }

    [Fact]
    public async Task Poll_authorization_pending()
    {
        http.Respond(HttpStatusCode.BadRequest, Error(PairingErrors.AuthorizationPending));
        var code = PairingRules.NewDeviceCode();

        var result = await client.PollAsync(code, CancellationToken.None);

        result.Should().Be(new PairPollResult.Error(new PairError(PairingErrors.AuthorizationPending)));
        var sent = http.Requests.Single();
        sent.Uri.AbsolutePath.Should().Be("/api/v1/pair/token");
        JObject.Parse(sent.Body).Should().BeEquivalentTo(new JObject { ["deviceCode"] = code });
    }

    [Fact]
    public async Task Poll_slow_down帶interval()
    {
        http.Respond(HttpStatusCode.BadRequest, Error(PairingErrors.SlowDown, 7));

        var result = await client.PollAsync(PairingRules.NewDeviceCode(), CancellationToken.None);

        result.Should().Be(new PairPollResult.Error(new PairError(PairingErrors.SlowDown, 7)));
    }

    [Fact]
    public async Task Poll_access_denied()
    {
        http.Respond(HttpStatusCode.BadRequest, Error(PairingErrors.AccessDenied));

        (await client.PollAsync(PairingRules.NewDeviceCode(), CancellationToken.None))
            .Should().Be(new PairPollResult.Error(new PairError(PairingErrors.AccessDenied)));
    }

    [Fact]
    public async Task Poll_expired_token()
    {
        http.Respond(HttpStatusCode.BadRequest, Error(PairingErrors.ExpiredToken));

        (await client.PollAsync(PairingRules.NewDeviceCode(), CancellationToken.None))
            .Should().Be(new PairPollResult.Error(new PairError(PairingErrors.ExpiredToken)));
    }

    [Fact]
    public async Task Poll_網路錯誤_NetworkError()
    {
        http.Throw(new HttpRequestException("連線被拒"));
        http.Respond(HttpStatusCode.ServiceUnavailable, "");
        http.Respond(HttpStatusCode.TooManyRequests, "", r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)));

        (await client.PollAsync(PairingRules.NewDeviceCode(), CancellationToken.None)).Should().BeOfType<PairPollResult.NetworkError>()
            .Which.Detail.Should().Contain("連線被拒");
        (await client.PollAsync(PairingRules.NewDeviceCode(), CancellationToken.None)).Should().Be(new PairPollResult.NetworkError("HTTP 503"));
        (await client.PollAsync(PairingRules.NewDeviceCode(), CancellationToken.None)).Should().Be(new PairPollResult.RateLimited(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Poll_核准_回應不進ToString()
    {
        var approval = PairingTestData.Approval(SyncIds.NewDeviceId(), PairingTestData.NewSpki());
        http.Respond(HttpStatusCode.OK, SyncJson.Serialize(approval));

        var result = await client.PollAsync(PairingRules.NewDeviceCode(), CancellationToken.None);

        var approved = result.Should().BeOfType<PairPollResult.Approved>().Subject;
        approved.Response.Should().BeEquivalentTo(approval);
        approved.ToString().Should().NotContain(approval.DeviceToken);
        new PairStep.Approved(approval).ToString().Should().NotContain(approval.DeviceToken);
    }

    [Fact]
    public async Task 取消_丟OperationCanceledException()
    {
        var gate = http.Gate(HttpStatusCode.BadRequest, Error(PairingErrors.AuthorizationPending));
        using var cts = new CancellationTokenSource();

        var poll = client.PollAsync(PairingRules.NewDeviceCode(), cts.Token);
        await http.WaitForRequestsAsync(1);
        cts.Cancel();

        await poll.Awaiting(p => p).Should().ThrowAsync<OperationCanceledException>();
        gate.TrySetResult();
    }
}
