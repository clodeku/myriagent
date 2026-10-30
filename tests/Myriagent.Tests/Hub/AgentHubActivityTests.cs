using System.Collections.Concurrent;
using Myriagent.Agents;
using Myriagent.Background;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 對話框的燈號（web-protocol 的 activity；AgentHub.Activity.cs）：沒掛取樣來源不送；看得到的對話第一次只取基準、之後送資料流
/// （管線位元組的增量）、硬碟（程序樹的增量扣掉管線）與整台電腦的網路；只量忙碌中的對話的程序樹；換程序重新取基準；
/// 只送給看得到那個對話的視窗、看不到的對話不放；什麼都沒有時不送。取樣來源是假的，自動排程關掉、以 ActivityTick 一次一次推
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubActivityTests
{
    private sealed class FakeSampler : IActivitySampler
    {
        public ConcurrentQueue<IReadOnlyList<ActivityRoot>> Calls { get; } = new();

        public ConcurrentDictionary<string, long> Trees { get; } = new();

        /// <summary>程序樹的 CPU 使用率（沒放的鍵是 null：只取了基準）</summary>
        public ConcurrentDictionary<string, double> Cpu { get; } = new();

        /// <summary>程序樹的記憶體（位元組；沒放的鍵是 null：一個程序都沒讀到）</summary>
        public ConcurrentDictionary<string, long> Mem { get; } = new();

        public (ulong Rx, ulong Tx)? Net { get; set; }

        public IReadOnlyDictionary<string, TreeActivity> SampleTrees(IReadOnlyList<ActivityRoot> roots)
        {
            Calls.Enqueue(roots);
            return roots.ToDictionary(r => r.Key,
                r => new TreeActivity(Trees.TryGetValue(r.Key, out var v) ? v : 0, Cpu.TryGetValue(r.Key, out var c) ? c : null,
                    Mem.TryGetValue(r.Key, out var m) ? m : null));
        }

        public (ulong Rx, ulong Tx)? ReadNetwork() => Net;
    }

    private static (HubHarness H, FakeSampler Sampler) Create(bool attach = true)
    {
        var h = new HubHarness();
        var sampler = new FakeSampler();
        h.Ui.Invoke(() =>
        {
            h.Hub.ActivityAutoSchedule = false;
            if (attach)
                h.Hub.AttachActivity(sampler);
        });
        return (h, sampler);
    }

    private static async Task TickAsync(HubHarness h)
    {
        h.Ui.Invoke(() => h.Hub.ActivityTick());
        await h.WaitUntilAsync(() => !h.Ui.Invoke(() => h.Hub.ActivityPending), "取樣做完");
        await h.Ui.DrainAsync();
    }

    private static (string Id, FakeSession Session) StartVisible(HubHarness h, long pipe)
    {
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        s.ActivitySource = new AgentActivitySource(4321, "inst-1", pipe);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        return (id, s);
    }

    [Fact]
    public async Task 沒掛取樣來源_不送activity()
    {
        var (h, sampler) = Create(attach: false);
        using var dispose = h;
        var (_, s) = StartVisible(h, 100);
        await TickAsync(h);
        s.ActivitySource = new AgentActivitySource(4321, "inst-1", 900);
        await TickAsync(h);
        h.Main.Messages("activity").Should().BeEmpty();
        sampler.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task 第一次只取基準_之後送資料流_硬碟扣掉管線_與整台電腦的網路()
    {
        var (h, sampler) = Create();
        using var dispose = h;
        var (id, s) = StartVisible(h, 1_000);
        s.IsRunning.Should().BeTrue("送出第一則之後這一輪在進行");
        sampler.Net = (10_000, 5_000);
        var mark = h.Main.Mark();
        await TickAsync(h);
        h.Main.Messages("activity", mark).Should().BeEmpty("第一次只取基準");
        sampler.Calls.Should().ContainSingle().Which.Should().Equal(new ActivityRoot(id, 4321, "inst-1"));

        s.ActivitySource = new AgentActivitySource(4321, "inst-1", 1_600);
        sampler.Trees[id] = 10_000;
        sampler.Net = (12_048, 5_512);
        await TickAsync(h);
        var m = h.Main.Messages("activity", mark).Should().ContainSingle().Subject;
        m.Value<int>("ms").Should().BePositive();
        var item = ((JArray)m["conversations"]!).Should().ContainSingle().Subject;
        item.Value<string>("id").Should().Be(id);
        item.Value<long>("data").Should().Be(600, "管線多了 600 位元組");
        item.Value<long>("disk").Should().Be(9_400, "程序樹 10,000 扣掉管線的 600");
        m["net"]!.Value<long>("rx").Should().Be(2_048);
        m["net"]!.Value<long>("tx").Should().Be(512);

        // 什麼都沒變：不送
        sampler.Trees[id] = 0;
        var mark2 = h.Main.Mark();
        await TickAsync(h);
        h.Main.Messages("activity", mark2).Should().BeEmpty();
    }

    [Fact]
    public async Task CPU_量了程序樹的對話帶cpu_0也送_閒置的不帶()
    {
        var (h, sampler) = Create();
        using var dispose = h;
        var (id, s) = StartVisible(h, 0);
        await TickAsync(h); // 基準

        sampler.Cpu[id] = 0;
        var mark = h.Main.Mark();
        await TickAsync(h);
        var item = h.Main.Messages("activity", mark).Should().ContainSingle("資料流、硬碟、網路都沒動靜，但執行中的對話量了 CPU（波形要畫平的）").Subject["conversations"]![0]!;
        item.Value<string>("id").Should().Be(id);
        item.Value<double>("cpu").Should().Be(0);

        sampler.Cpu[id] = 37.5;
        mark = h.Main.Mark();
        await TickAsync(h);
        h.Main.Messages("activity", mark).Single()["conversations"]![0]!.Value<double>("cpu").Should().Be(37.5);

        h.CompleteTurn(s);
        s.IsRunning = false;
        mark = h.Main.Mark();
        await TickAsync(h);
        h.Main.Messages("activity", mark).Should().BeEmpty("閒置的對話不量程序樹，沒有 cpu、也沒有別的動靜");
    }

    private sealed class FakeBackgroundSource : IBackgroundProgramSource
    {
        private Action<BackgroundScan>? callback;

        public void Start(Action<BackgroundScan> onScan) => callback = onScan;

        public void RequestScan()
        {
        }

        public Task<bool> StopAsync(BackgroundProgram program) => Task.FromResult(true);

        public async Task PublishAsync(HubHarness h, string conversationId)
        {
            await Task.Run(() => callback!(new BackgroundScan(new Dictionary<string, IReadOnlyList<BackgroundProgram>>
            {
                [conversationId] = [new BackgroundProgram(4242, "python.exe", "python train.py", 1)],
            })));
            await h.Ui.DrainAsync();
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task CPU_這一輪結束了但代理開的背景程式還在跑_照樣量_CLI沒在跑時只量那些程式()
    {
        var (h, sampler) = Create();
        using var dispose = h;
        var programs = new FakeBackgroundSource();
        h.Ui.Invoke(() => h.Hub.AttachBackgroundPrograms(programs));
        var (id, s) = StartVisible(h, 0);
        h.CompleteTurn(s);
        s.IsRunning = false;
        await TickAsync(h);
        sampler.Calls.Last().Should().BeEmpty("閒置、沒有背景程式：不量");

        await programs.PublishAsync(h, id);
        await TickAsync(h);
        sampler.Calls.Last().Should().Equal(new ActivityRoot(id, 4321, "inst-1"));

        // CLI 程序沒在跑（app 重開後還沒送過訊息）：只量它開的程式，PID 0
        s.ActivitySource = null;
        sampler.Cpu[id] = 12.5;
        sampler.Mem[id] = 734_003_200;
        var mark = h.Main.Mark();
        await TickAsync(h);
        sampler.Calls.Last().Should().Equal(new ActivityRoot(id, 0, string.Empty));
        var item = h.Main.Messages("activity", mark).Single()["conversations"]![0]!;
        item.Value<string>("id").Should().Be(id);
        item.Value<double>("cpu").Should().Be(12.5);
        item.Value<long>("mem").Should().Be(734_003_200, "記憶體和 CPU 同一組程序");
        item.Value<long>("data").Should().Be(0);
    }

    [Fact]
    public async Task 只送給看得到的視窗_看不到的對話不放()
    {
        var (h, sampler) = Create();
        using var dispose = h;
        var (id, s) = StartVisible(h, 0);
        var other = h.AddView("other");
        h.Command(other, "open", new { conversationId = id });
        var (hidden, hs) = h.StartConversation(h.Main, h.Claude);
        hs.ActivitySource = new AgentActivitySource(5555, "inst-2", 0);
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } }); // 第二個對話開著但不在看
        await TickAsync(h);

        s.ActivitySource = new AgentActivitySource(4321, "inst-1", 300);
        hs.ActivitySource = new AgentActivitySource(5555, "inst-2", 999);
        var mark = h.Main.Mark();
        var otherMark = other.Mark();
        await TickAsync(h);
        var ids = h.Main.Messages("activity", mark).SelectMany(m => (JArray)m["conversations"]!).Select(i => i.Value<string>("id")).ToList();
        ids.Should().Equal(id);
        ids.Should().NotContain(hidden);
        other.Messages("activity", otherMark).Should().BeEmpty("那個視窗開著但沒在看");
        sampler.Calls.Last().Select(r => r.Key).Should().Equal(id);
    }

    [Fact]
    public async Task 閒置的對話不量程序樹_資料流照算()
    {
        var (h, sampler) = Create();
        using var dispose = h;
        var (id, s) = StartVisible(h, 0);
        h.CompleteTurn(s);
        s.IsRunning = false;
        await TickAsync(h);
        sampler.Calls.Last().Should().BeEmpty("閒置、沒有背景工作：不走程序樹");

        s.ActivitySource = new AgentActivitySource(4321, "inst-1", 256);
        var mark = h.Main.Mark();
        await TickAsync(h);
        var item = h.Main.Messages("activity", mark).Single()["conversations"]![0]!;
        item.Value<long>("data").Should().Be(256);
        item.Value<long>("disk").Should().Be(0);

        s.HasLiveBackgroundWork = true;
        await TickAsync(h);
        sampler.Calls.Last().Select(r => r.Key).Should().Equal(new[] { id }, "有背景工作：量程序樹");
    }

    [Fact]
    public async Task 換程序_重新取基準()
    {
        var (h, _) = Create();
        using var dispose = h;
        var (_, s) = StartVisible(h, 5_000);
        await TickAsync(h);
        s.ActivitySource = new AgentActivitySource(7777, "inst-new", 40);
        var mark = h.Main.Mark();
        await TickAsync(h);
        h.Main.Messages("activity", mark).Should().BeEmpty("新程序的計數器從 0 開始：這一次只取基準");
        s.ActivitySource = new AgentActivitySource(7777, "inst-new", 90);
        await TickAsync(h);
        h.Main.Messages("activity", mark).Single()["conversations"]![0]!.Value<long>("data").Should().Be(50);
    }

    [Fact]
    public async Task 真的取樣來源_程序樹寫檔_送出的硬碟量涵蓋檔案大小()
    {
        var h = new HubHarness();
        using var dispose = h;
        h.Ui.Invoke(() =>
        {
            h.Hub.ActivityAutoSchedule = false;
            h.Hub.AttachActivity(new ActivitySampler());
        });
        var file = Path.Combine(h.Temp.Root, "activity-e2e.bin");
        const int size = 3_000_000;
        var script = $"Start-Sleep -Milliseconds 800; [IO.File]::WriteAllBytes('{file}', (New-Object byte[] {size})); Start-Sleep -Seconds 3";
        using var root = AgentProcess.Start(new AgentLauncher(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Array.Empty<string>()),
            new[] { "/c", "powershell", "-NoProfile", "-NonInteractive", "-Command", script }, h.Temp.Root, null);
        root.BeginReading();
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        s.ActivitySource = root.Activity;
        h.Command(h.Main, "visible", new { conversationIds = new[] { id } });
        await TickAsync(h); // 基準

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!(File.Exists(file) && new FileInfo(file).Length == size) && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        var mark = h.Main.Mark();
        s.ActivitySource = root.Activity;
        await TickAsync(h);
        var disk = h.Main.Messages("activity", mark).SelectMany(m => (JArray)m["conversations"]!)
            .Where(i => i.Value<string>("id") == id).Sum(i => i.Value<long>("disk"));
        disk.Should().BeGreaterThanOrEqualTo(size, "PowerShell 寫的檔算進這個對話的硬碟燈");
        await root.ExitTask.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task 沒有看得到的本機對話_不取樣()
    {
        var (h, sampler) = Create();
        using var dispose = h;
        var (_, s) = h.StartConversation(h.Main, h.Claude);
        s.ActivitySource = new AgentActivitySource(4321, "inst-1", 0);
        h.Command(h.Main, "visible", new { conversationIds = Array.Empty<string>() });
        await TickAsync(h);
        sampler.Calls.Should().BeEmpty();
        h.Ui.Invoke(() => h.Hub.ActivityPending).Should().BeFalse();
    }
}
