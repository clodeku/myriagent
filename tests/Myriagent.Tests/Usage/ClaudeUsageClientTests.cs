using System.Net;
using Myriagent.Tests.TestSupport;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Usage;

/// <summary>Claude 用量查詢：只讀權杖（絕不更新）、過期不查、各種 HTTP 結果的分類（假 HTTP，不連網）</summary>
public sealed class ClaudeUsageClientTests : IDisposable
{
    private readonly TempDirectory temp = new("claude-usage");

    public void Dispose() => temp.Dispose();

    private string WriteCredentials(DateTimeOffset? expiresAt, string token = "sk-ant-oat01-test-token")
    {
        var oauth = new JObject { ["accessToken"] = token, ["refreshToken"] = "sk-ant-ort01-不該被用到", ["subscriptionType"] = "max" };
        if (expiresAt.HasValue)
            oauth["expiresAt"] = expiresAt.Value.ToUnixTimeMilliseconds();
        return temp.WriteFile(".credentials.json", new JObject { ["claudeAiOauth"] = oauth }.ToString());
    }

    [Fact]
    public void ReadToken_實測憑證形狀_取權杖與到期時間()
    {
        var token = ClaudeUsageClient.ReadToken(RealSamples.ClaudeCredentials)!.Value;
        token.Token.Should().Be("sk-ant-oat01-假權杖");
        token.ExpiresAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1791600000000));

        ClaudeUsageClient.ReadToken("""{"claudeAiOauth":{"accessToken":"t"}}""")!.Value.ExpiresAt.Should().BeNull();
        ClaudeUsageClient.ReadToken("""{"claudeAiOauth":{"accessToken":""}}""").Should().BeNull();
        ClaudeUsageClient.ReadToken("""{"mcpOAuth":{}}""").Should().BeNull();
        ClaudeUsageClient.ReadToken("{壞掉").Should().BeNull();
        ClaudeUsageClient.ReadToken(null).Should().BeNull();
    }

    [Fact]
    public void IsExpired_到期前兩分鐘就當過期_沒有到期時間不算過期()
    {
        var now = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
        ClaudeUsageClient.IsExpired(null, now).Should().BeFalse();
        ClaudeUsageClient.IsExpired(now.AddMinutes(-1), now).Should().BeTrue();
        ClaudeUsageClient.IsExpired(now.AddMinutes(1), now).Should().BeTrue("距到期不到兩分鐘");
        ClaudeUsageClient.IsExpired(now.AddMinutes(2), now).Should().BeTrue("剛好兩分鐘");
        ClaudeUsageClient.IsExpired(now.AddMinutes(3), now).Should().BeFalse();
    }

    /// <summary>模擬 CLI 正在更新憑證檔：開著寫入與刪除（改名覆寫）權限的檔案代號</summary>
    private static FileStream HoldLikeCliWriting(string file)
        => new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);

    [Fact]
    public void ReadCredentials_CLI正在寫入或改名覆寫時也讀得到_不擋它()
    {
        var file = WriteCredentials(DateTimeOffset.UtcNow.AddHours(3));
        var content = File.ReadAllText(file);

        using (HoldLikeCliWriting(file))
        {
            FluentActions.Invoking(() => File.ReadAllText(file)).Should().Throw<IOException>(
                "預設的 FileShare.Read 不容許別人同時寫入或刪除，CLI 那邊也就會被擋下（EBUSY／EPERM）");
            ClaudeUsageClient.ReadCredentials(file).Should().Be(content, "以 ReadWrite｜Delete 共用模式開檔，與寫入、改名都相容");
        }

        ClaudeUsageClient.ReadCredentials(temp.Combine("沒有.json")).Should().BeNull();
        ClaudeUsageClient.ReadCredentials(temp.Combine("沒有的資料夾", ".credentials.json")).Should().BeNull();
    }

    [Fact]
    public async Task FetchAsync_CLI正開著憑證檔時照樣讀得到權杖()
    {
        var file = WriteCredentials(DateTimeOffset.UtcNow.AddHours(3));
        var handler = new StubHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, RealSamples.ClaudeUsage);

        using var cliHandle = HoldLikeCliWriting(file);
        var (outcome, _, error) = await new ClaudeUsageClient(new HttpClient(handler)).FetchAsync(file, CancellationToken.None);

        outcome.Should().Be(ClaudeUsageOutcome.Success, "原因：{0}", error);
        handler.Requests.Single().Headers.Authorization!.Parameter.Should().Be("sk-ant-oat01-test-token");
    }

    [Fact]
    public async Task FetchAsync_沒有憑證檔_不送請求()
    {
        var handler = new StubHttpHandler();
        var (outcome, json, error) = await new ClaudeUsageClient(new HttpClient(handler)).FetchAsync(temp.Combine("沒有.json"), CancellationToken.None);

        outcome.Should().Be(ClaudeUsageOutcome.NoCredentials);
        json.Should().BeNull();
        error.Should().BeNull();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_權杖過期_不送請求也不更新權杖()
    {
        var file = WriteCredentials(DateTimeOffset.UtcNow.AddMinutes(-5));
        var before = File.ReadAllText(file);
        var handler = new StubHttpHandler();

        var (outcome, _, error) = await new ClaudeUsageClient(new HttpClient(handler)).FetchAsync(file, CancellationToken.None);

        outcome.Should().Be(ClaudeUsageOutcome.TokenExpired);
        error.Should().Contain("過期");
        handler.Requests.Should().BeEmpty("本程式絕不自己 refresh 權杖");
        File.ReadAllText(file).Should().Be(before, "憑證檔不能被改動");
    }

    [Fact]
    public async Task FetchAsync_成功_帶OAuth標頭_日期維持字串()
    {
        var file = WriteCredentials(DateTimeOffset.UtcNow.AddHours(3));
        var handler = new StubHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, RealSamples.ClaudeUsage);

        var (outcome, json, error) = await new ClaudeUsageClient(new HttpClient(handler)).FetchAsync(file, CancellationToken.None);

        outcome.Should().Be(ClaudeUsageOutcome.Success);
        error.Should().BeNull();
        ((JArray)json!["limits"]!).Should().HaveCount(3);
        json["limits"]![0]!["resets_at"]!.Type.Should().Be(JTokenType.String, "不讓 Json.NET 把 ISO 時間轉成本地時間");
        UsageRules.ParseClaudeUsage(json).Windows.Should().HaveCount(3);

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.ToString().Should().Be(ClaudeUsageClient.UsageUrl);
        request.Headers.Authorization!.Scheme.Should().Be("Bearer");
        request.Headers.Authorization.Parameter.Should().Be("sk-ant-oat01-test-token");
        request.Headers.GetValues("anthropic-beta").Should().Equal("oauth-2025-04-20");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, nameof(ClaudeUsageOutcome.Unauthorized))]
    [InlineData(HttpStatusCode.Forbidden, nameof(ClaudeUsageOutcome.Unauthorized))]
    [InlineData(HttpStatusCode.TooManyRequests, nameof(ClaudeUsageOutcome.RateLimited))]
    [InlineData(HttpStatusCode.InternalServerError, nameof(ClaudeUsageOutcome.Failed))]
    public async Task FetchAsync_HTTP錯誤的分類(HttpStatusCode status, string expected)
    {
        var file = WriteCredentials(DateTimeOffset.UtcNow.AddHours(3));
        var handler = new StubHttpHandler();
        handler.Enqueue(status, """{"error":{"type":"x"}}""");

        var (outcome, json, error) = await new ClaudeUsageClient(new HttpClient(handler)).FetchAsync(file, CancellationToken.None);

        outcome.ToString().Should().Be(expected);
        json.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
        if (status == HttpStatusCode.InternalServerError)
            error.Should().Contain("HTTP 500");
    }

    [Fact]
    public async Task FetchAsync_回應不是JSON或連線失敗_回報失敗不丟例外()
    {
        var file = WriteCredentials(null);
        var handler = new StubHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, "<html>維護中</html>");
        var client = new ClaudeUsageClient(new HttpClient(handler));

        (await client.FetchAsync(file, CancellationToken.None)).Outcome.Should().Be(ClaudeUsageOutcome.Failed);

        var throwing = new ClaudeUsageClient(new HttpClient(new ThrowingHandler()));
        var (outcome, _, error) = await throwing.FetchAsync(file, CancellationToken.None);
        outcome.Should().Be(ClaudeUsageOutcome.Failed);
        error.Should().Contain("連不上");
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("連不上伺服器");
    }
}
