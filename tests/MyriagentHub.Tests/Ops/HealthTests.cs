using System.Diagnostics;
using System.Net;
using System.Text.Json;
using MyriagentHub.Data;
using MyriagentHub.Hosting;
using MyriagentHub.Ops;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using static MyriagentHub.Tests.Sync.ConversationSamples;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Ops;

/// <summary>/healthz（匿名，只回 status 與 build）、/readyz 與 ops/status（已認證或本機）、status\running.json</summary>
public sealed class HealthTests
{
    [Fact]
    public async Task healthz匿名只回狀態與建置指紋()
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(cookies: false);
        var json = await client.GetJsonAsync("/healthz");
        json.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("status", "build");
        json.GetProperty("status").GetString().Should().Be("ok");
        json.GetProperty("build").GetString().Should().Be(BuildInfo.Current.Build);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task readyz本機迴路不必登入(string ip)
    {
        await using var host = await HubTestHost.StartAsync();
        using var client = host.CreateClient(ip: ip, cookies: false);
        using var response = await client.GetAsync("/readyz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task readyz與ops_status_外部匿名401_擁有者看得到完整狀態()
    {
        await using var owner = await OwnerHost.StartAsync();
        using var anonymous = owner.Host.CreateClient(cookies: false);
        using (var ready = await anonymous.GetAsync("/readyz"))
            ready.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using (var ops = await anonymous.GetAsync("/api/v1/ops/status"))
            ops.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var client = owner.Host.CreateClient();
        using (var login = await OwnerFlows.LoginAsync(client))
            login.StatusCode.Should().Be(HttpStatusCode.OK);
        var status = await client.GetJsonAsync("/api/v1/ops/status");
        status.GetProperty("status").GetString().Should().Be("ok");
        status.GetProperty("version").GetString().Should().Be(BuildInfo.Current.Version);
        status.GetProperty("process").GetProperty("serverGc").GetBoolean().Should().BeFalse("Workstation GC");
        status.GetProperty("tls").GetProperty("selfSigned").GetBoolean().Should().BeTrue();
        status.GetProperty("tls").GetProperty("storage").GetString().Should().Be("file");
        status.GetProperty("tls").GetProperty("name").GetString().Should().Be("localhost", "顯示名稱：自簽憑證有 CN");
        status.GetProperty("tls").GetProperty("dnsNames").EnumerateArray().Select(e => e.GetString()).Should().Equal("localhost");
        status.GetProperty("process").GetProperty("sampledAt").GetInt64().Should().Be(owner.Host.Clock.NowMs);
        status.GetProperty("database").GetProperty("hubDbBytes").GetInt64().Should().BeGreaterThan(0);
        status.TryGetProperty("setupRequired", out _).Should().BeFalse("帳號密碼登入沒有首次設定");
        status.GetProperty("audit").GetProperty("ok").GetBoolean().Should().BeTrue();
        var ready2 = await client.GetJsonAsync("/readyz");
        ready2.GetProperty("build").GetString().Should().Be(BuildInfo.Current.Build);
    }

    [Fact]
    public async Task readyz的記憶體與CPU是查詢當下取樣_不是保險絲的舊值()
    {
        // 測試主機的保險絲 3600 秒才取樣一次（只有啟動時那一次）：數字要是查詢當下的
        await using var host = await HubTestHost.StartAsync();
        using var local = host.CreateClient(ip: "127.0.0.1", cookies: false);
        var first = (await local.GetJsonAsync("/readyz")).GetProperty("process");
        first.GetProperty("sampledAt").GetInt64().Should().Be(host.Clock.NowMs);
        first.GetProperty("privateMb").GetDouble().Should().BeGreaterThan(0);
        first.GetProperty("workingSetMb").GetDouble().Should().BeGreaterThan(0);
        first.GetProperty("threads").GetInt32().Should().BeGreaterThan(0);

        host.Clock.Advance(TimeSpan.FromMinutes(5));
        var busy = Stopwatch.StartNew();
        while (busy.ElapsedMilliseconds < 300)
        {
            // 吃一點 CPU：下一次取樣的平均要看得到
        }
        var second = (await local.GetJsonAsync("/readyz")).GetProperty("process");
        second.GetProperty("sampledAt").GetInt64().Should().Be(host.Clock.NowMs, "查詢當下取樣");
        second.GetProperty("cpuWindowSeconds").GetDouble().Should().BeGreaterThanOrEqualTo(0.2);
        second.GetProperty("cpuPercent").GetDouble().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task running_json在開始服務後寫入_停止時改成stopped()
    {
        var dir = TempDir.Create();
        try
        {
            await using (var host = await HubTestHost.StartAsync(dataDir: dir))
            {
                var running = await WaitForAsync(host.Paths.RunningFile, json => json.GetProperty("state").GetString() == "running");
                running.GetProperty("version").GetString().Should().Be(BuildInfo.Current.Version);
                running.GetProperty("build").GetString().Should().Be(BuildInfo.Current.Build);
                running.GetProperty("pid").GetInt32().Should().Be(Environment.ProcessId);
            }
            var stopped = JsonDocument.Parse(await File.ReadAllTextAsync(new HubPaths(dir).RunningFile)).RootElement;
            stopped.GetProperty("state").GetString().Should().Be("stopped");
            stopped.GetProperty("stoppedAt").GetInt64().Should().BeGreaterThan(0);
            var wal = new HubPaths(dir).HubDb + "-wal";
            (File.Exists(wal) ? new FileInfo(wal).Length : 0).Should().Be(0, "結束時 WAL 併回主檔並截斷");
        }
        finally
        {
            TempDir.TryDelete(dir);
        }
    }

    [Fact]
    public async Task 同一個資料根不能同時跑兩個伺服器()
    {
        await using var host = await HubTestHost.StartAsync();
        var second = () => HubTestHost.StartAsync(dataDir: host.DataDir);
        await second.Should().ThrowAsync<InvalidOperationException>().WithMessage("*已有另一個 MyriagentHub*");
    }

    // ───── 伺服器對話的營運狀態（WP4.2 步 5、D31） ─────

    private static readonly string[] ConversationKeys =
    [
        "count", "items", "deleted", "recovering", "rebuilding", "rebuildPending", "unread", "overlayBytes", "liveDropped", "outboxOverflows", "rejected", "remasked",
        "conversationEvents", "conversationEventBytes", "conversationEventsBaselined", "syncDbBytes", "syncWalBytes", "syncDbLevel", "devices",
    ];

    [Fact]
    public async Task ops狀態有conversations區塊_沒有開關欄位_有remasked與rejected()
    {
        await using var host = await HubTestHost.StartAsync();
        using var local = host.CreateClient(ip: "127.0.0.1", cookies: false);

        var block = (await local.GetJsonAsync("/readyz")).GetProperty("conversations");

        block.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(ConversationKeys, "沒有功能開關，所以沒有開關的欄位（答 1）");
        block.GetProperty("rejected").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["notOwner", "fenced", "deleted", "invalid", "tooLarge"],
            "沒有 dlp：伺服器遮蔽後接受（答 5）");
        block.GetProperty("remasked").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["events", "live"]);
        block.GetProperty("liveDropped").EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["rate", "dlp", "notOwner", "policy", "final", "stale"]);
    }

    [Fact]
    public async Task 沒有對話時_conversations區塊全是0()
    {
        await using var host = await HubTestHost.StartAsync();
        await host.Services.GetRequiredService<ConversationMaintenance>().Baselined.WaitAsync(TimeSpan.FromSeconds(30));
        using var local = host.CreateClient(ip: "127.0.0.1", cookies: false);

        var block = (await local.GetJsonAsync("/readyz")).GetProperty("conversations");

        foreach (var key in new[] { "count", "items", "deleted", "recovering", "rebuilding", "rebuildPending", "unread", "overlayBytes", "outboxOverflows", "conversationEvents", "conversationEventBytes" })
            block.GetProperty(key).GetInt64().Should().Be(0, key);
        foreach (var group in new[] { "liveDropped", "rejected", "remasked" })
            block.GetProperty(group).EnumerateObject().Should().OnlyContain(p => p.Value.GetInt64() == 0, group);
        block.GetProperty("devices").GetArrayLength().Should().Be(0);
        block.GetProperty("conversationEventsBaselined").GetBoolean().Should().BeTrue();
        block.GetProperty("syncDbLevel").GetString().Should().Be("ok");
        block.GetProperty("syncDbBytes").GetInt64().Should().BeGreaterThan(0, "sync.db 照 hubDbBytes 的作法量主檔");
    }

    [Fact]
    public async Task ops狀態不含對話id與標題()
    {
        await using var owner = await OwnerHost.StartAsync();
        var host = owner.Host;
        var a = await EventLogTests.AddDeviceAsync(host);
        const string secretA = "機密標題甲";
        const string secretB = "機密標題乙";
        await ConversationEventLogTests.Append(host, a,
            ConversationEventLogTests.Ev(1, K.Created, Meta(secretA)), ConversationEventLogTests.Ev(2, K.Item, Item("內容甲")),
            ConversationEventLogTests.Ev(3, K.Created, Meta(secretB), conversationId: OtherConv), ConversationEventLogTests.Ev(4, K.Item, Item("內容乙", id: "x"), conversationId: OtherConv),
            ConversationEventLogTests.Ev(5, K.Item, Item(), epoch: 2));
        using var local = host.CreateClient(ip: "127.0.0.1", cookies: false);
        using var client = host.CreateClient();
        using (var login = await OwnerFlows.LoginAsync(client))
            login.StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var (who, path) in new[] { (local, "/readyz"), (client, "/api/v1/ops/status") })
        {
            using var response = await who.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var text = await response.Content.ReadAsStringAsync();
            text.Should().NotContain(Conv).And.NotContain(OtherConv, path).And.NotContain(secretA).And.NotContain(secretB).And.NotContain("內容甲").And.NotContain("內容乙");
            var block = JsonDocument.Parse(text).RootElement.GetProperty("conversations");
            (block.GetProperty("count").GetInt64(), block.GetProperty("items").GetInt64(), block.GetProperty("conversationEvents").GetInt64())
                .Should().Be((2L, 2L, 4L), path);
            block.GetProperty("rejected").GetProperty("fenced").GetInt64().Should().Be(1);
        }
    }

    [Fact]
    public async Task 每台裝置的owned與recovering計數()
    {
        await using var host = await HubTestHost.StartAsync();
        var a = await EventLogTests.AddDeviceAsync(host);
        var b = await EventLogTests.AddDeviceAsync(host);
        await ConversationEventLogTests.Append(host, a, ConversationEventLogTests.Ev(1, K.Created, Meta()), ConversationEventLogTests.Ev(2, K.Item, Item()),
            ConversationEventLogTests.Ev(3, K.Created, Meta(), conversationId: OtherConv));
        var third = new string('7', 32);
        var fromB = await ConversationEventLogTests.Append(host, b, ConversationEventLogTests.Ev(1, K.Created, Meta(), conversationId: third));
        // 還原的標記（直接改表），再送一筆讓目錄快取換上新的列
        await host.Services.GetRequiredService<SyncWriter>().WriteAsync(c => c.Execute($"UPDATE conversations SET lease_state = 'recovering' WHERE conversation_id = '{Conv}'"));
        var last = await ConversationEventLogTests.Append(host, a, ConversationEventLogTests.Ev(4, K.Item, Item("第二則", id: "item2", ord: 2)));
        using var local = host.CreateClient(ip: "127.0.0.1", cookies: false);

        var block = (await local.GetJsonAsync("/readyz")).GetProperty("conversations");

        (block.GetProperty("count").GetInt64(), block.GetProperty("recovering").GetInt64(), block.GetProperty("items").GetInt64()).Should().Be((3L, 1L, 2L));
        var devices = block.GetProperty("devices").EnumerateArray().ToDictionary(d => d.GetProperty("deviceId").GetString()!);
        devices.Keys.Should().BeEquivalentTo([a, b]);
        Device(devices[a]).Should().Be((2L, 1L, 2L, last.Ack.Results[0].Seq!.Value, host.Clock.NowMs));
        Device(devices[b]).Should().Be((1L, 0L, 0L, fromB.Ack.Results[0].Seq!.Value, host.Clock.NowMs));

        static (long, long, long, long, long) Device(JsonElement d) =>
            (d.GetProperty("owned").GetInt64(), d.GetProperty("recovering").GetInt64(), d.GetProperty("items").GetInt64(), d.GetProperty("maxSeq").GetInt64(),
                d.GetProperty("lastEventAt").GetInt64());
    }

    [Theory]
    [InlineData(0L, "ok")]
    [InlineData(2L * 1024 * 1024 * 1024 - 1, "ok")]
    [InlineData(2L * 1024 * 1024 * 1024, "warn")]
    [InlineData(8L * 1024 * 1024 * 1024 - 1, "warn")]
    [InlineData(8L * 1024 * 1024 * 1024, "critical")]
    [InlineData(long.MaxValue, "critical")]
    public void syncDbLevel依門檻(long bytes, string expected)
    {
        var defaults = new ConversationSettings();
        ConversationOpsRules.SyncDbLevel(bytes, defaults.SyncDbWarnBytes, defaults.SyncDbCriticalBytes).Should().Be(expected);
    }

    [Theory]
    [InlineData("1", "2", "critical")]
    [InlineData("1", "1000000000000", "warn")]
    public async Task syncDbLevel照設定的門檻(string warn, string critical, string expected)
    {
        await using var host = await HubTestHost.StartAsync(settings: new Dictionary<string, string?>
        {
            ["Hub:Conversations:SyncDbWarnBytes"] = warn,
            ["Hub:Conversations:SyncDbCriticalBytes"] = critical,
        });
        using var local = host.CreateClient(ip: "127.0.0.1", cookies: false);

        (await local.GetJsonAsync("/readyz")).GetProperty("conversations").GetProperty("syncDbLevel").GetString().Should().Be(expected);
    }

    private static async Task<JsonElement> WaitForAsync(string path, Func<JsonElement, bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
                    if (condition(json))
                        return json;
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("等不到 " + path);
    }
}
