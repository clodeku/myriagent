using System.Collections.Concurrent;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using HubOptions = MyriagentHub.Hosting.HubOptions;

namespace MyriagentHub.Tests.TestSupport;

/// <summary>
/// 假的快照來源（閘道的單元測試）：回傳 <see cref="Current"/>（沒要求的部分沿用 previous）、可設定丟例外、記下每次讀的部分與時間
/// </summary>
internal sealed class FakeSnapshotSource : IBrowserSnapshotSource
{
    private readonly object gate = new();
    private readonly List<(BrowserParts Parts, long AtMs)> reads = [];

    public BrowserSnapshotData Current { get; set; } = new("[]", "[]", "{}", """{"theme":"dark","fontSize":14,"enterToSend":true,"notifications":true}""");

    /// <summary>true 時 Read 丟 InvalidOperationException（照樣記一次呼叫）</summary>
    public bool Throw { get; set; }

    public int Calls
    {
        get
        {
            lock (gate)
                return reads.Count;
        }
    }

    public IReadOnlyList<(BrowserParts Parts, long AtMs)> Reads
    {
        get
        {
            lock (gate)
                return reads.ToList();
        }
    }

    public BrowserSnapshotData Read(BrowserParts parts, BrowserSnapshotData? previous, long nowMs)
    {
        lock (gate)
        {
            reads.Add((parts, nowMs));
            if (Throw)
                throw new InvalidOperationException("假的讀取失敗");
            var c = Current;
            if (previous is null)
                return c;
            return new BrowserSnapshotData(
                parts.HasFlag(BrowserParts.Accounts) ? c.AccountsJson : previous.AccountsJson,
                parts.HasFlag(BrowserParts.Devices) ? c.DevicesJson : previous.DevicesJson,
                parts.HasFlag(BrowserParts.DevicesCli) ? c.DevicesCliJson : previous.DevicesCliJson,
                parts.HasFlag(BrowserParts.Settings) ? c.SettingsJson : previous.SettingsJson);
        }
    }
}

/// <summary>一個已登入的擁有者瀏覽器：HTTP 用戶端（cookie）＋它的瀏覽器 hub 連線</summary>
internal sealed class BrowserOwner(HubClient http, BrowserHubClient hub) : IAsyncDisposable
{
    public HubClient Http { get; } = http;

    public BrowserHubClient Hub { get; } = hub;

    public async ValueTask DisposeAsync()
    {
        await Hub.DisposeAsync();
        Http.Dispose();
    }
}

/// <summary>瀏覽器閘道測試的共用輔助（EventLogTests 的輔助照舊以 using static 使用，不搬動）</summary>
internal static class BrowserFixtures
{
    /// <summary>登入一個新的擁有者用戶端（自己的 cookie、指定的 IP）並連上瀏覽器 hub</summary>
    public static async Task<BrowserOwner> LoginAndConnectAsync(HubTestHost host, string ip = TestIpStartupFilter.DefaultIp)
    {
        var http = host.CreateClient(ip);
        try
        {
            await OwnerFlows.LoginOwnerAsync(http);
            return new BrowserOwner(http, await BrowserHubClient.ConnectAsync(host, http));
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    /// <summary>測試主機的設定（HubTestHost.StartAsync 的 settings）</summary>
    public static Dictionary<string, string?> Settings(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 依序建立已配對的裝置，每台之間把時鐘撥快 1 ms：TestDevice.AddAsync 以 host.Clock.NowMs 當 CreatedAt，
    /// 不推進時鐘時裝置清單的順序（CreatedAt、Id）會落到隨機的 id 上
    /// </summary>
    public static async Task<List<TestDevice>> AddDevicesInOrderAsync(HubTestHost host, params string[] names)
    {
        var devices = new List<TestDevice>();
        foreach (var name in names)
        {
            devices.Add(await TestDevice.AddAsync(host, name));
            host.Clock.Advance(TimeSpan.FromMilliseconds(1));
        }
        return devices;
    }

    /// <summary>輪詢等條件成立（伺服器端的非同步收尾，例如連線斷了之後的移除）；逾時就讓測試失敗</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000, string because = "等候逾時")
    {
        for (var waited = 0; waited < timeoutMs; waited += 20)
        {
            if (condition())
                return;
            await Task.Delay(20);
        }
        condition().Should().BeTrue(because);
    }
}

/// <summary>
/// 可撥快的單調時鐘（GetTimestamp／TimestampFrequency）與牆上時鐘（GetUtcNow），單元測試驅動節流與逾時用。
/// 主機測試的 <see cref="ManualTimeProvider"/> 只覆寫 GetUtcNow（單調時鐘是真實時間）；這個兩個一起撥
/// </summary>
internal sealed class FakeTimestampProvider(DateTimeOffset start) : TimeProvider
{
    /// <summary>單調時鐘的起點（不是 0：程式不能假設 GetTimestamp 從 0 開始）</summary>
    private const long TimestampBase = 1_000_000_000;

    private long elapsedTicks;
    private int failures;

    public FakeTimestampProvider()
        : this(new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero))
    {
    }

    public override long TimestampFrequency { get; } = 1_000_000;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>不是 null 時 GetTimestamp 丟這個例外（注入「一輪在讀快照之外失敗」：閘道每一輪最外層的攔截）</summary>
    public Exception? FailWith { get; set; }

    /// <summary>GetTimestamp 因 <see cref="FailWith"/> 丟出的次數</summary>
    public int Failures => Volatile.Read(ref failures);

    public override long GetTimestamp()
    {
        if (FailWith is { } failure)
        {
            Interlocked.Increment(ref failures);
            throw failure;
        }
        return TimestampBase + Interlocked.Read(ref elapsedTicks) / (TimeSpan.TicksPerMillisecond / 1000);
    }

    public override DateTimeOffset GetUtcNow() => start.AddTicks(Interlocked.Read(ref elapsedTicks));

    public long NowMs => GetUtcNow().ToUnixTimeMilliseconds();

    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(by), "單調時鐘不能倒退");
        Interlocked.Add(ref elapsedTicks, by.Ticks);
    }
}

/// <summary>閘道的單元測試環境：假的快照來源與時鐘、真的連線登記（送出委派記下收到的字串）；不經主機，<see cref="RunAsync"/> 直接驅動一輪</summary>
internal sealed class GatewayHarness
{
    /// <param name="environment">主機的環境名稱（開發用的調整，BrowserTuningRules）；null＝沒有環境（不送任何調整）</param>
    public GatewayHarness(Action<HubOptions>? configure = null, string? environment = null)
    {
        var options = new HubOptions();
        configure?.Invoke(options);
        Options = Microsoft.Extensions.Options.Options.Create(options);
        Connections = new BrowserConnections(Options, NullLogger<BrowserConnections>.Instance);
        var host = environment is null ? null : new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = environment };
        Gateway = new BrowserGateway(Source, Connections, Options, Time, NullLogger<BrowserGateway>.Instance, environment: host);
    }

    public IOptions<HubOptions> Options { get; }

    public FakeTimestampProvider Time { get; } = new();

    public FakeSnapshotSource Source { get; } = new();

    public BrowserConnections Connections { get; }

    public BrowserGateway Gateway { get; }

    public SendRecorder Add(string connectionId, string sessionId = "s1")
    {
        var recorder = new SendRecorder();
        recorder.Connection = Connections.TryAdd(connectionId, sessionId, null, recorder.OnAbort, recorder.SendAsync)
            ?? throw new InvalidOperationException("連線被拒");
        return recorder;
    }

    /// <summary>新連線送 ready 並收下 init</summary>
    public async Task<SendRecorder> ReadyAsync(string connectionId, string sessionId = "s1")
    {
        var recorder = Add(connectionId, sessionId);
        Gateway.RequestInit(recorder.Connection, "main");
        await RunAsync();
        (await recorder.NextAsync())["type"]!.Value<string>().Should().Be("init");
        return recorder;
    }

    public Task<TimeSpan> RunAsync() => Gateway.RunOnceAsync(CancellationToken.None);
}

/// <summary>
/// 一條假連線的送出端：依序記下收到的字串。<see cref="Block"/> 之後送出卡住直到 <see cref="Release"/>（取消會結束等待）；
/// <see cref="Hang"/> 之後送出永不完成、也不理會取消（只有送出幫浦的逾時救得回來）
/// </summary>
internal sealed class SendRecorder
{
    private readonly ConcurrentQueue<string> sent = new();
    private TaskCompletionSource? gate;
    private volatile bool hang;
    private int aborts;
    private int inFlight;

    public BrowserConnection Connection { get; set; } = null!;

    public int Aborts => Volatile.Read(ref aborts);

    public void OnAbort() => Interlocked.Increment(ref aborts);

    public void Block() => gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release() => gate?.TrySetResult();

    public void Hang() => hang = true;

    /// <summary>送出卡住了（Hang，或 Block 還沒 Release）</summary>
    private bool Stalled => hang || gate is { Task.IsCompleted: false };

    public async Task SendAsync(string json, CancellationToken cancellationToken)
    {
        if (hang)
        {
            await new TaskCompletionSource().Task;
            return;
        }
        Interlocked.Increment(ref inFlight);
        try
        {
            await SendCoreAsync(json, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref inFlight);
        }
    }

    public async Task<string> NextRawAsync(int timeoutMs = 5000)
    {
        for (var waited = 0; waited < timeoutMs; waited += 10)
        {
            if (sent.TryDequeue(out var json))
                return json;
            await Task.Delay(10);
        }
        throw new TimeoutException("沒有收到訊息");
    }

    public async Task<JObject> NextAsync() => JObject.Parse(await NextRawAsync());

    /// <summary>
    /// 取 <paramref name="count"/> 則（0＝已經入列的都送完之後收到的：送出幫浦是非同步的，機器忙的時候 50 ms 不一定送得完，
    /// 所以再等送出佇列清空、沒有送到一半的，連續兩次都成立才取，最多 5 秒；送出卡住〔Block、Hang〕時不等）
    /// </summary>
    public async Task<List<JObject>> TakeAsync(int count)
    {
        var list = new List<JObject>();
        if (count == 0)
        {
            await Task.Delay(50);
            for (var (waited, idle) = (0, 0); waited < 5000 && idle < 2 && !Stalled; waited += 10)
            {
                idle = Connection.Outbox.Count == 0 && Volatile.Read(ref inFlight) == 0 ? idle + 1 : 0;
                if (idle < 2)
                    await Task.Delay(10);
            }
            while (sent.TryDequeue(out var json))
                list.Add(JObject.Parse(json));
            return list;
        }
        for (var i = 0; i < count; i++)
            list.Add(await NextAsync());
        return list;
    }

    public async Task AssertNothingAsync(string because = "")
    {
        await Task.Delay(50);
        Connection.Outbox.Count.Should().Be(0, because);
        sent.Should().BeEmpty(because);
    }

    private async Task SendCoreAsync(string json, CancellationToken cancellationToken)
    {
        if (gate is { } blocked)
            await blocked.Task.WaitAsync(cancellationToken);
        sent.Enqueue(json);
    }
}
