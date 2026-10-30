using System.Net;
using System.Text;
using System.Text.Json;
using FakeDevice;

namespace MyriagentHub.ProbeClient.Tests;

/// <summary>
/// ProbeClient 的正式站守門（WP4.9 計畫 D18）：共用測試表（和 fake-device、hub-e2e 同一份）經連結編譯的 LocalHubTargetRules 照樣成立；
/// 網路這一半（換掉 HttpMessageHandler）：Windows 服務、/readyz 的 tls 名稱是正式站、讀不到或沒有 environment 一律拒絕；
/// 網址不合格時連 /readyz 都不讀。admin 包裝沒有資料根就拒絕
/// </summary>
public sealed class ProbeGuardTests
{
    private static readonly Lazy<JsonDocument> Cases = new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "guard-cases.json"))));

    [Fact]
    public void 共用測試表_網址與連上之後()
    {
        var root = Cases.Value.RootElement;
        foreach (var row in root.GetProperty("allowedServerUrl").EnumerateArray())
        {
            var url = row.GetProperty("url").GetString();
            LocalHubTargetRules.IsAllowedServerUrl(url).Should().Be(row.GetProperty("allowed").GetBoolean(), $"{url}（{row.GetProperty("why").GetString()}）");
        }

        foreach (var row in root.GetProperty("productionTarget").EnumerateArray())
        {
            var facts = LocalHubTargetRules.FactsFromJson(Raw(row.GetProperty("readyz")), Raw(row.GetProperty("tls")));
            LocalHubTargetRules.IsProductionTarget(facts).Should().Be(row.GetProperty("production").GetBoolean(), row.GetProperty("why").GetString());
        }
    }

    [Fact]
    public async Task 本機主控台的MyriagentHub可以連()
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"status":"ok","environment":"console","tls":{"name":"localhost","issuer":"CN=localhost"}}""");
        var facts = await ProbeGuard.VerifyAsync("https://127.0.0.1:5443", TimeSpan.FromSeconds(5), handler);
        facts.Environment.Should().Be("console");
        handler.Requests.Should().Equal("https://127.0.0.1:5443/readyz");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"status":"ok","environment":"service"}""", "正式服務")]
    [InlineData(HttpStatusCode.ServiceUnavailable, """{"status":"draining","environment":"service"}""", "正式服務（排空中）")]
    [InlineData(HttpStatusCode.OK, """{"environment":"console","tls":{"name":"hub.example.com"}}""", "ssh 轉送：/readyz 的 tls 名稱是正式站")]
    [InlineData(HttpStatusCode.OK, """{"environment":"console","tls":{"issuer":"CN=R11, O=Let's Encrypt, C=US"}}""", "公開 CA")]
    [InlineData(HttpStatusCode.OK, """{"status":"ok"}""", "沒有 environment")]
    [InlineData(HttpStatusCode.NotFound, "", "不是 MyriagentHub")]
    [InlineData(HttpStatusCode.OK, "<html>not json</html>", "不是 JSON")]
    public async Task 不確定或是正式站一律拒絕(HttpStatusCode status, string body, string why)
    {
        var handler = new StubHandler(status, body);
        await FluentActions.Invoking(() => ProbeGuard.VerifyAsync("https://localhost:5443", TimeSpan.FromSeconds(5), handler))
            .Should().ThrowAsync<ProbeGuardException>(why);
    }

    [Fact]
    public async Task 讀不到readyz就拒絕()
    {
        var handler = new StubHandler(new HttpRequestException("連線被拒"));
        (await FluentActions.Invoking(() => ProbeGuard.VerifyAsync("https://127.0.0.1:1", TimeSpan.FromSeconds(5), handler))
            .Should().ThrowAsync<ProbeGuardException>()).Which.Message.Should().Contain("讀不到");
    }

    [Theory]
    [InlineData("https://hub.example.com")]
    [InlineData("https://localhost.hub.example.com:5443")]
    [InlineData("http://127.0.0.1:5443")]
    [InlineData("https://10.0.0.5:5443")]
    public async Task 網址不合格時連readyz都不讀(string url)
    {
        var handler = new StubHandler(HttpStatusCode.OK, """{"environment":"console"}""");
        await FluentActions.Invoking(() => ProbeGuard.VerifyAsync(url, TimeSpan.FromSeconds(5), handler)).Should().ThrowAsync<ProbeGuardException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public void admin一律帶資料根()
    {
        AdminWrapper.Arguments(@"C:\tmp\hub-data", ["stop", "--wait", "20"]).Should().Equal("admin", "stop", "--wait", "20", "--data", @"C:\tmp\hub-data");
        FluentActions.Invoking(() => AdminWrapper.Arguments(null, ["stop"])).Should().Throw<ArgumentException>().WithMessage("*ProgramData*");
        FluentActions.Invoking(() => AdminWrapper.Arguments(" ", ["stop"])).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AdminWrapper.Arguments(@"C:\tmp\a", ["stop", "--data", @"C:\ProgramData\MyriagentHub"])).Should().Throw<ArgumentException>("--data 只能由包裝加");
        FluentActions.Invoking(() => AdminWrapper.Arguments(@"C:\tmp\a", ["stop", "--DATA=x"])).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void 金鑰檔以UTF16LE的位元組比對_任何位移()
    {
        var dir = Directory.CreateTempSubdirectory("probe-keys-").FullName;
        try
        {
            var needle = @"probe-hub-1-1\MyriagentHub.exe";
            File.WriteAllBytes(Path.Combine(dir, "even"), [.. new byte[8], .. Encoding.Unicode.GetBytes(@"C:\x\" + needle), .. new byte[4]]);
            File.WriteAllBytes(Path.Combine(dir, "odd"), [.. new byte[7], .. Encoding.Unicode.GetBytes(needle), 0]);
            File.WriteAllBytes(Path.Combine(dir, "other"), Encoding.Unicode.GetBytes(@"probe-hub-1-2\MyriagentHub.exe"));
            File.WriteAllBytes(Path.Combine(dir, "ascii"), Encoding.ASCII.GetBytes(needle));
            LocalHub.KeyFilesNaming(needle, dir).Order().Should().Equal("even", "odd");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string? Raw(JsonElement element) => element.ValueKind == JsonValueKind.Null ? null : element.GetRawText();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode status;
        private readonly string body;
        private readonly Exception? error;

        public StubHandler(HttpStatusCode status, string body)
        {
            this.status = status;
            this.body = body;
        }

        public StubHandler(Exception error)
        {
            this.error = error;
            body = "";
        }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            if (error is not null)
                throw error;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
