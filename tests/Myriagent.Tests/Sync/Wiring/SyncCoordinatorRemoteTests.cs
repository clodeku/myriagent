using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.Sync.Pairing;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>
/// 協調者的遠端指令部分（WP4.4 計畫步 5；D2、D3、D31～D33）：同一代的 EnginePort 在 UI 執行緒進中樞閘門、回當時的政策與暫停；
/// 舊一代與還沒開始就取消的確定不執行（busy、Started=false）；sync 的 remoteSupported；睡眠恢復交給引擎；解除配對前先匯出遠端操作紀錄
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class SyncCoordinatorRemoteTests
{
    private sealed class Rig : IDisposable
    {
        public Rig(DeviceFileData? device = null)
        {
            H = new HubHarness();
            Paths = new SyncPaths(H.Temp.Combine("sync"));
            Directory.CreateDirectory(Paths.Directory);
            Device = device ?? PairingTestData.Device();
            DeviceFile.Save(Paths.DeviceFile, Device);
            Coordinator = H.OnUi(() => new SyncCoordinator(H.Hub, H.Accounts, H.Host, H.Settings, Paths,
                new SyncCoordinatorOptions(false, null, false, "0.5.0", "OFFICE-PC", "user"), Time,
                args =>
                {
                    Created.Add(args);
                    return Engine;
                }));
            H.Ui.Invoke(() => H.Hub.AttachSync(Coordinator));
        }

        public HubHarness H { get; }

        public FakeView Main => H.Main;

        public ManualTimeProvider Time { get; } = new();

        public SyncPaths Paths { get; }

        public DeviceFileData Device { get; }

        public RecordingSyncEngine Engine { get; } = new();

        public List<SyncEngineCreateArgs> Created { get; } = [];

        public SyncCoordinator Coordinator { get; }

        /// <summary>這一代引擎拿到的遠端指令入口（EnginePort）</summary>
        public IRemoteCommandPort Port => (IRemoteCommandPort)Created.Single().Host;

        public async Task StartAsync()
        {
            H.Ui.Invoke(() => Coordinator.Start());
            await Coordinator.WhenStarted.WaitAsync(TimeSpan.FromSeconds(10));
            await H.Ui.DrainAsync();
        }

        public void Command(string cmd, object? args = null) => H.Command(Main, cmd, args);

        public async Task UnpairAsync()
        {
            var mark = Main.Mark();
            Command("syncUnpair");
            await Main.WaitForAsync("sync", IsNullSync, mark, timeoutMs: 10000);
        }

        public void Dispose()
        {
            H.Ui.Invoke(() => Coordinator.Dispose());
            H.Dispose();
        }
    }

    /// <summary>閘門 5j 的觀察者：記下每次在不在 UI 執行緒；<see cref="Hold"/> 時接下 rename 但不回結果（之後由測試 Complete）</summary>
    private sealed class WatchHandler(UiThread ui) : IRemoteCommandHandler
    {
        public Recorder<(string Cmd, bool OnUi)> Calls { get; } = new();

        public bool Hold { get; set; }

        public RemoteViewContext? Held { get; private set; }

        public bool TryHandle(RemoteViewContext context, IAgentView view, string cmd, JObject command)
        {
            Calls.Add((cmd, ui.IsCurrent));
            if (!Hold || cmd != "rename")
                return false;
            Held = context;
            return true;
        }
    }

    private static bool IsNullSync(JObject m) => m["sync"]?.Type == JTokenType.Null;

    private static bool? RemoteSupportedOf(JObject message) => message["sync"] is JObject sync ? sync.Value<bool?>("remoteSupported") : null;

    private static async Task WaitUntilAsync(Func<bool> condition, string because, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new XunitException($"{timeoutMs} ms 內沒有等到：{because}");
            await Task.Delay(10);
        }
    }

    /// <summary>rename 的信封內容（每次新的 cid）</summary>
    private static CommandEnvelopeBody Rename(string id, string title) => RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("rename", id, new { title }));

    private static async Task<(Rig Rig, string Id, WatchHandler Handler)> PairedWithConversationAsync()
    {
        var rig = new Rig();
        await rig.StartAsync();
        RemoteEntrySamples.AllowRemote(rig.H);
        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Claude);
        rig.H.CompleteTurn(s);
        var handler = new WatchHandler(rig.H.Ui);
        rig.H.Ui.Invoke(() => rig.H.Hub.AddRemoteHandlerForTest(handler));
        return (rig, id, handler);
    }

    [Fact]
    public async Task port_同一代在UI執行緒進閘門_回結果與當時的政策和暫停()
    {
        var (rig, id, handler) = await PairedWithConversationAsync();
        using var _ = rig;

        var run = await Task.Run(() => rig.Port.ExecuteRemoteAsync(Rename(id, "遠端改的"), _ => { }, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));

        run.Started.Should().BeTrue();
        run.Outcome.State.Should().Be(RemoteResultStates.Done, run.Outcome.Message);
        (run.Policy, run.Paused).Should().Be(("Control", (bool?)false));
        handler.Calls.All.Should().Equal(("rename", true));
        rig.H.Conversation(id)!.Title.Should().Be("遠端改的", "授權者放行，退路的 rename 照 HandleCommand 執行");

        // 遠端暫停：進閘門時讀到的是 Off＋暫停，閘門 5e 拒絕
        rig.H.Ui.Invoke(() => rig.H.Settings.Sync.RemotePaused = true);
        var paused = await Task.Run(() => rig.Port.ExecuteRemoteAsync(Rename(id, "不會改"), _ => { }, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));

        paused.Started.Should().BeTrue();
        RemoteEntrySamples.ShouldBeRejected(paused.Outcome, RemoteResultCodes.Policy, RemoteGateSteps.Policy);
        (paused.Policy, paused.Paused).Should().Be(("Off", (bool?)true));
        rig.H.Conversation(id)!.Title.Should().Be("遠端改的");
    }

    [Fact]
    public async Task port_解除配對後的舊一代_回busy_Started是false_中樞沒有被呼叫()
    {
        var (rig, id, handler) = await PairedWithConversationAsync();
        using var _ = rig;
        var port = rig.Port;
        await rig.UnpairAsync();

        var run = await Task.Run(() => port.ExecuteRemoteAsync(Rename(id, "舊一代"), _ => { }, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));

        run.Started.Should().BeFalse();
        RemoteEntrySamples.ShouldBeRejected(run.Outcome, RemoteResultCodes.Busy, RemoteGateSteps.Route);
        (run.Policy, run.Paused).Should().Be(((string?)null, (bool?)null), "沒進閘門、沒讀政策");
        handler.Calls.Count.Should().Be(0);
        rig.H.Conversation(id)!.Title.Should().Be("第一則");

        // 舊一代的回報也丟掉：未配對時不送 sync
        var mark = rig.Main.Mark();
        port.ReportCommandsSupported(false);
        await rig.H.Ui.DrainAsync();
        rig.Main.Messages("sync", mark).Should().BeEmpty();
    }

    [Fact]
    public async Task port_取消時還沒開始回busy_已經開始的等結果()
    {
        var (rig, id, handler) = await PairedWithConversationAsync();
        using var _ = rig;

        // 還沒開始：UI 執行緒被占住，排進去的工作還沒跑就取消 → 確定沒有執行
        using (var release = new ManualResetEventSlim())
        using (var cts = new CancellationTokenSource())
        {
            rig.H.Ui.Post(() => release.Wait(TimeSpan.FromSeconds(10)));
            var pending = rig.Port.ExecuteRemoteAsync(Rename(id, "取消的"), _ => { }, cts.Token);
            cts.Cancel();
            var run = await pending.WaitAsync(TimeSpan.FromSeconds(10));
            release.Set();
            await rig.H.Ui.DrainAsync();

            run.Started.Should().BeFalse();
            RemoteEntrySamples.ShouldBeRejected(run.Outcome, RemoteResultCodes.Busy, RemoteGateSteps.Route);
            handler.Calls.Count.Should().Be(0, "取消之後排進去的工作不進閘門");
            rig.H.Conversation(id)!.Title.Should().Be("第一則");
        }

        // 已經開始：不理會取消，照樣等閘門的結果
        handler.Hold = true;
        using (var cts = new CancellationTokenSource())
        {
            var started = rig.Port.ExecuteRemoteAsync(Rename(id, "開始了"), _ => { }, cts.Token);
            await WaitUntilAsync(() => handler.Held is not null, "處理者接下 rename");
            cts.Cancel();
            await Task.Delay(100);
            started.IsCompleted.Should().BeFalse("已經進了閘門：等處理者的結果");

            rig.H.Ui.Invoke(() => handler.Held!.Complete());
            var run = await started.WaitAsync(TimeSpan.FromSeconds(10));

            run.Started.Should().BeTrue();
            run.Outcome.State.Should().Be(RemoteResultStates.Done);
        }
    }

    [Fact]
    public async Task sync與init的remote帶remoteSupported()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        rig.Command("ready");
        RemoteSupportedOf((JObject)rig.Main.Last("init")!["remote"]!).Should().BeTrue("還沒回報：配對網址的 aud 合格");

        var mark = rig.Main.Mark();
        rig.Port.ReportCommandsSupported(false);
        await rig.Main.WaitForAsync("sync", m => RemoteSupportedOf(m) == false, mark);
        rig.Command("ready");
        RemoteSupportedOf((JObject)rig.Main.Last("init")!["remote"]!).Should().BeFalse("引擎回報過就照它");

        var again = rig.Main.Mark();
        rig.Port.ReportCommandsSupported(true);
        await rig.Main.WaitForAsync("sync", m => RemoteSupportedOf(m) == true, again);

        // 值沒變不重送
        var same = rig.Main.Mark();
        rig.Port.ReportCommandsSupported(true);
        await rig.H.Ui.DrainAsync();
        rig.Main.Messages("sync", same).Should().BeEmpty();

        // 其他的 sync（例如改政策）也帶著
        var policy = rig.Main.Mark();
        rig.Command("syncSetPolicy", new { remoteControlLevel = "View" });
        var changed = await rig.Main.WaitForAsync("sync", since: policy);
        RemoteSupportedOf(changed).Should().BeTrue();
    }

    [Fact]
    public async Task remoteSupported_配對網址不合格且還沒回報是false_未配對不送()
    {
        using (var rig = new Rig(PairingTestData.Device() with { ServerUrl = PairingTestData.Server + "/x" }))
        {
            await rig.StartAsync();
            rig.Command("ready");
            RemoteSupportedOf((JObject)rig.Main.Last("init")!["remote"]!).Should().BeFalse("配對網址帶路徑：aud 不合格，這台不執行遠端指令");
        }

        using var unpaired = new HubHarness();
        var paths = new SyncPaths(unpaired.Temp.Combine("sync"));
        var coordinator = unpaired.OnUi(() => new SyncCoordinator(unpaired.Hub, unpaired.Accounts, unpaired.Host, unpaired.Settings, paths,
            new SyncCoordinatorOptions(false, null, false, "0.5.0", "OFFICE-PC", "user"), TimeProvider.System,
            engineFactory: _ => throw new InvalidOperationException("未配對不該建引擎")));
        unpaired.Ui.Invoke(() =>
        {
            unpaired.Hub.AttachSync(coordinator);
            coordinator.Start();
        });
        await coordinator.WhenStarted.WaitAsync(TimeSpan.FromSeconds(10));
        await unpaired.Ui.DrainAsync();
        unpaired.Command(unpaired.Main, "ready");
        unpaired.Main.Last("init")!["remote"]!.Type.Should().Be(JTokenType.Null);
        unpaired.Ui.Invoke(() => coordinator.Dispose());
    }

    [Fact]
    public async Task 睡眠恢復_呼叫引擎的OnSystemResumed()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var uiThread = rig.H.OnUi(() => Environment.CurrentManagedThreadId);

        await Task.Run(() => rig.Coordinator.OnSystemResumed());
        await rig.H.Ui.DrainAsync();

        rig.Engine.Resumes.Should().Be(1);
        rig.Engine.ResumeThreads.Should().Equal(uiThread);
        rig.Engine.Retries.Should().Be(0, "恢復交給引擎：退避中它自己照 RetryNow，連著時重新 Hello");

        // 解除配對之後沒有引擎：什麼都不做
        await rig.UnpairAsync();
        await Task.Run(() => rig.Coordinator.OnSystemResumed());
        await rig.H.Ui.DrainAsync();
        rig.Engine.Resumes.Should().Be(1);
    }

    [Fact]
    public async Task 解除配對_先匯出再停引擎_匯出逾時或失敗照樣解除()
    {
        // 正常：在資料根的 remote-audit 底下、以 deviceId 命名，匯出時引擎還沒停
        using (var rig = new Rig())
        {
            await rig.StartAsync();
            await rig.UnpairAsync();

            var export = rig.Engine.Exports.Should().ContainSingle().Subject;
            export.StoppedBefore.Should().BeFalse("先匯出才停引擎（之後 sync.db 會被刪掉）");
            export.Path.Should().Be(Path.Combine(rig.H.Temp.Root, "remote-audit", rig.Device.DeviceId + ".jsonl"));
            export.Path.Should().StartWith(rig.H.Temp.Root, "不碰真的 %LOCALAPPDATA%");
            rig.Engine.Stopped.Should().BeTrue();
            File.Exists(rig.Paths.DeviceFile).Should().BeFalse();
        }

        // 匯出丟例外：照樣解除
        using (var rig = new Rig())
        {
            rig.Engine.Export = (_, _) => Task.FromException<int>(new IOException("磁碟滿了"));
            await rig.StartAsync();
            await rig.UnpairAsync();

            rig.Engine.Exports.Should().ContainSingle();
            rig.Engine.Stopped.Should().BeTrue();
            File.Exists(rig.Paths.DeviceFile).Should().BeFalse("匯出失敗只記錄，device.json 照樣刪掉");
        }

        // 匯出卡住：10 秒逾時之後照樣解除
        using (var rig = new Rig())
        {
            var hang = new TaskCompletionSource<int>();
            rig.Engine.Export = (_, _) => hang.Task;
            await rig.StartAsync();
            var mark = rig.Main.Mark();
            rig.Command("syncUnpair");
            await WaitUntilAsync(() => rig.Engine.Exports.Count == 1, "開始匯出");

            rig.Time.Advance(SyncCoordinator.RemoteAuditExportTimeout - TimeSpan.FromSeconds(1));
            await Task.Delay(100);
            rig.Engine.Stopped.Should().BeFalse("還在等匯出");

            rig.Time.Advance(TimeSpan.FromSeconds(1));
            await rig.Main.WaitForAsync("sync", IsNullSync, mark, timeoutMs: 10000);
            rig.Engine.Stopped.Should().BeTrue();
            File.Exists(rig.Paths.DeviceFile).Should().BeFalse();
        }
    }

    // ───────────────────────────── 啟動提醒（步 10、D30；契約 D26） ─────────────────────────────

    [Theory]
    [InlineData(SyncSettings.LevelControl, false, false, true)]
    [InlineData(SyncSettings.LevelControl, true, false, true)]
    [InlineData(SyncSettings.LevelView, false, false, true)]
    [InlineData(SyncSettings.LevelOff, true, false, false)]
    [InlineData(SyncSettings.LevelControl, true, true, false)]
    public async Task 啟動時政策不是Off提醒一次_Off不提醒_暫停中不提醒(string level, bool allowFullAccess, bool paused, bool reminds)
    {
        using var rig = new Rig();
        rig.H.Ui.Invoke(() =>
        {
            rig.H.Settings.Sync.RemoteControlLevel = level;
            rig.H.Settings.Sync.AllowRemoteFullAccess = allowFullAccess;
            rig.H.Settings.Sync.RemotePaused = paused;
            // 啟動提醒不看「通知遠端操作」：讓被別的程式改了的設定看得見
            rig.H.Settings.Sync.NotifyRemoteActions = false;
        });
        var before = rig.H.Host.Notifications.Count;

        await rig.StartAsync();
        rig.H.Ui.Invoke(() => rig.Coordinator.Start());
        await Task.Delay(100);
        await rig.H.Ui.DrainAsync();

        rig.Created.Should().ContainSingle("提醒在建好引擎之後；再呼叫 Start 不會重新載入");
        var reminders = rig.H.Host.Notifications.All.Skip(before).ToList();
        if (reminds)
            reminders.Should().Equal((RemoteNotifyRules.StartupReminderTitle, RemoteNotifyRules.StartupReminder(level, allowFullAccess)!, (string?)null));
        else
            reminders.Should().BeEmpty();
    }

    [Fact]
    public async Task 啟動提醒_未配對不提醒()
    {
        using var rig = new Rig();
        File.Delete(rig.Paths.DeviceFile);
        rig.H.Ui.Invoke(() => rig.H.Settings.Sync.RemoteControlLevel = SyncSettings.LevelControl);
        var before = rig.H.Host.Notifications.Count;

        await rig.StartAsync();

        rig.Created.Should().BeEmpty();
        rig.H.Host.Notifications.Count.Should().Be(before);
    }
}
