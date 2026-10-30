using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.Sync.Fakes;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 引擎的第 4 階段登記點（WP4.1 步 10；SyncEngine.Contracts.cs，計畫 5.10、D45、D49）：沒有登記時 Hello、拉取、心跳與第 3 階段相同、
/// 第 4 階段的伺服器訊息一律略過；以測試接縫（ContractProbeForTest）代替後續工作包的實作，驗證每個登記點的位置、引數、執行緒與次數，
/// 以及功能協商、早到緩衝（依到達順序、溢出回 busy）
/// </summary>
public sealed class SyncEngineContractsTests
{
    private static readonly string ConversationId = new('c', 32);

    private static int nextCommand;

    private static CommandDelivery Command() =>
        new(SyncIds.NewUlid(1791580000000 + Interlocked.Increment(ref nextCommand)), new CommandEnvelope(CommandEnvelope.Version, "{}", "sig"));

    private static readonly string[] DispatchHooks = ["OnWatchChanged", "OnSnapshotRequest", "OnMediaRequest", "OnCommand"];

    /// <summary>記錄每次登記點的呼叫（名稱、引數、執行緒）；<see cref="Act"/> 代替後續工作包的實作</summary>
    private sealed class Probe
    {
        public ConcurrentQueue<(string Hook, object? Arg, int Thread)> Calls { get; } = new();

        public Action<string, object?>? Act { get; set; }

        public void Record(string hook, object? arg)
        {
            Calls.Enqueue((hook, arg, Environment.CurrentManagedThreadId));
            Act?.Invoke(hook, arg);
        }

        public IReadOnlyList<object?> Of(string hook) => Calls.Where(c => c.Hook == hook).Select(c => c.Arg).ToList();

        public IReadOnlyList<string> Hooks => Calls.Select(c => c.Hook).ToList();
    }

    private static Action<string, object?> Declare(params (string Hook, string Feature)[] features) => (hook, arg) =>
    {
        foreach (var (h, f) in features)
        {
            if (h == hook)
                ((List<string>)arg!).Add(f);
        }
    };

    private sealed record ConversationTestSignal(string Json) : SyncSignal;

    // ── 沒有登記 ──

    [Fact]
    public async Task 沒有登記時_Hello_拉取_心跳與第3階段相同()
    {
        await using var h = new EngineHarness();
        h.Host.PingSynchronously = true;
        h.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1];
        await h.StartAsync();

        var hello = h.Server.Hellos.Single();
        hello.Features.Should().BeNull("WP4.1 的桌面不宣告任何功能");
        JObject.Parse(SyncJson.Serialize(hello)).Properties().Select(p => p.Name).Should().Equal(
            "protocolVersion", "deviceId", "bootId", "appVersion", "machineName", "windowsUser", "nonceD", "observedSpkiSha256", "seenServer", "outbox", "cursors", "policy");
        hello.Cursors.Keys.Should().BeEquivalentTo([SyncClasses.Catalog, SyncClasses.Usage]);
        h.Server.Pulls.First().Cursors.Should().Equal(new PullCursor(0, SyncClasses.Catalog), new PullCursor(0, SyncClasses.Usage));
        (await h.Inspect(v => v.Features)).Should().BeEmpty("伺服器宣告了也沒有交集");
        (await h.Inspect(v => v.DeclaredFeatures)).Should().BeNull();

        await h.AdvanceAsync(15);
        var beat = h.Server.Heartbeats.Should().ContainSingle().Subject;
        beat.Running.Should().BeEmpty();
        beat.Waiting.Should().BeEmpty();
        SyncJson.Serialize(beat).Should().Be($$"""{"uiAliveAt":{{beat.UiAliveAt}},"running":[],"waiting":[]}""");
    }

    [Fact]
    public async Task 沒有協商功能_第4階段的伺服器訊息一律略過_不回任何東西_連線照常()
    {
        await using var h = new EngineHarness();
        var probe = new Probe();
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);
        var callsBefore = h.Server.Calls.Count;

        var session = h.Server.Current!;
        session.Watch(ConversationId);
        session.Snapshot(ConversationId);
        session.Media(new MediaRequest("relay-1", "0123456789ABCDEFGHJKMNPQRS", MediaPurposes.Media));
        session.Command(Command());
        session.Command(Command());
        await h.IdleAsync();

        probe.Hooks.Should().NotContain(DispatchHooks, "沒協商的訊息不交給登記點");
        h.Server.CommandResults.Should().BeEmpty();
        h.Server.Calls.Skip(callsBefore).Should().BeEmpty("略過的訊息不呼叫任何 hub 方法");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Server.Sessions.Should().ContainSingle();
    }

    // ── 功能宣告與協商 ──

    [Fact]
    public async Task 宣告的功能_協商取交集_訊息只分派協商出的_在引擎執行緒()
    {
        await using var h = new EngineHarness();
        var probe = new Probe
        {
            Act = Declare(("ContributeConversationFeatures", DeviceFeatureRules.Conv1), ("ContributeConversationFeatures", DeviceFeatureRules.Live1),
                ("ContributeCommandFeatures", DeviceFeatureRules.Cmd1)),
        };
        h.Server.Features = [DeviceFeatureRules.Cmd1, DeviceFeatureRules.Conv1, DeviceFeatureRules.Media1];
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        h.Server.Hellos.Single().Features.Should().Equal(DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Cmd1);
        probe.Hooks.Where(hook => hook.EndsWith("Features", StringComparison.Ordinal))
            .Should().Equal("ContributeConversationFeatures", "ContributeCommandFeatures", "ContributeMediaFeatures", "ContributeLogFeatures", "ContributeHandoffFeatures");
        (await h.Inspect(v => v.Features)).Should().Equal(DeviceFeatureRules.Conv1, DeviceFeatureRules.Cmd1);

        var watch = new WatchChanged([ConversationId]);
        var snapshot = new SnapshotRequest(ConversationId, SnapshotReasons.Open);
        var media = new MediaRequest("relay-1", "0123456789ABCDEFGHJKMNPQRS", MediaPurposes.Thumb);
        var command = Command();
        var session = h.Server.Current!;
        session.Events.OnWatchChanged(watch);
        session.Events.OnSnapshotRequest(snapshot);
        session.Events.OnMediaRequest(media);
        session.Events.OnCommandDelivery(command);
        session.Events.OnCommandDelivery(null!);
        await h.IdleAsync();

        probe.Of("OnWatchChanged").Should().BeEmpty("live1 只有裝置宣告");
        probe.Of("OnMediaRequest").Should().BeEmpty("media1 只有伺服器宣告");
        probe.Of("OnSnapshotRequest").Should().ContainSingle().Which.Should().BeSameAs(snapshot);
        probe.Of("OnCommand").Should().ContainSingle().Which.Should().BeSameAs(command, "null 不交出去");
        probe.Calls.Where(c => DispatchHooks.Contains(c.Hook)).Should().OnlyContain(c => c.Thread == h.Engine.ThreadId);
    }

    // ── 第 5 階段的登記點（WP5.1） ──

    private static readonly string[] Phase5Hooks = ["OnHandoffRequest", "OnHandoffUpdate", "OnLogRepair"];

    private static HandoffRequest Request() => new(SyncIds.NewUlid(1791580000000), ConversationId, 1, HandoffModes.AfterTurn, "d_8P2Q0000000000000000000000", "HOME-PC",
        1791588800000, new HandoffInitiator(HandoffInitiatorKinds.Browser, Label: "瀏覽器"));

    private static HandoffUpdate Update() => new("01J9QZ3K8M5R7T2V4W6X8Y0A1B", ConversationId, HandoffKinds.Transfer, HandoffStates.Draining,
        "d_7K3M0000000000000000000000", "d_8P2Q0000000000000000000000");

    private static LogRepair Repair() => new(LogIdRules.LogKey("cl:a"), "cl:a", LogRepairReasons.Damaged);

    [Fact]
    public async Task 第5階段_開關預設關_沒有登記時Hello不宣告log1與hand1_訊息一律略過()
    {
        SyncPhase5Switches.Default.Should().Be(new SyncPhase5Switches(Log1: false, Hand1: false), "做到一半的第 5 階段在正式的桌面休眠（計畫 4.1）");
        SyncEngineOptions.Default.Phase5.Should().BeNull("沒給＝正式的預設");
        new SyncPhase5Switches(Log1: false, Hand1: true).DeclaresHand1.Should().BeFalse("hand1 要有 log1");
        SyncPhase5Switches.All.DeclaresHand1.Should().BeTrue();

        await using var h = new EngineHarness();
        var probe = new Probe { Act = Declare(("ContributeConversationFeatures", DeviceFeatureRules.Conv1)) };
        h.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Log1, DeviceFeatureRules.Hand1];
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);
        var callsBefore = h.Server.Calls.Count;

        h.Server.Hellos.Single().Features.Should().Equal(DeviceFeatureRules.Conv1);
        var session = h.Server.Current!;
        session.Events.OnHandoffRequest(Request());
        session.Events.OnHandoffUpdate(Update());
        session.Events.OnLogRepair(Repair());
        await h.IdleAsync();

        probe.Hooks.Should().NotContain(Phase5Hooks, "沒協商 hand1／log1 的訊息不交給登記點");
        h.Server.Calls.Skip(callsBefore).Should().BeEmpty();
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task 第5階段_協商出log1與hand1_訊息分派給各自的登記點_在引擎執行緒()
    {
        await using var h = new EngineHarness();
        var probe = new Probe
        {
            Act = Declare(("ContributeConversationFeatures", DeviceFeatureRules.Conv1), ("ContributeLogFeatures", DeviceFeatureRules.Log1),
                ("ContributeHandoffFeatures", DeviceFeatureRules.Hand1)),
        };
        h.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Log1, DeviceFeatureRules.Hand1];
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        h.Server.Hellos.Single().Features.Should().Equal(DeviceFeatureRules.Conv1, DeviceFeatureRules.Log1, DeviceFeatureRules.Hand1);
        (await h.Inspect(v => v.Features)).Should().Equal(DeviceFeatureRules.Conv1, DeviceFeatureRules.Log1, DeviceFeatureRules.Hand1);
        var request = Request();
        var update = Update();
        var repair = Repair();
        var session = h.Server.Current!;
        session.Events.OnHandoffRequest(request);
        session.Events.OnHandoffUpdate(update);
        session.Events.OnLogRepair(repair);
        await h.IdleAsync();

        probe.Of("OnHandoffRequest").Should().ContainSingle().Which.Should().BeSameAs(request);
        probe.Of("OnHandoffUpdate").Should().ContainSingle().Which.Should().BeSameAs(update);
        probe.Of("OnLogRepair").Should().ContainSingle().Which.Should().BeSameAs(repair);
        probe.Calls.Where(c => Phase5Hooks.Contains(c.Hook)).Should().OnlyContain(c => c.Thread == h.Engine.ThreadId);
    }

    [Fact]
    public async Task 第5階段_只協商出log1時hand1的訊息略過_LogRepair照收()
    {
        await using var h = new EngineHarness();
        var probe = new Probe
        {
            Act = Declare(("ContributeConversationFeatures", DeviceFeatureRules.Conv1), ("ContributeLogFeatures", DeviceFeatureRules.Log1),
                ("ContributeHandoffFeatures", DeviceFeatureRules.Hand1)),
        };
        h.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Log1];
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        (await h.Inspect(v => v.Features)).Should().Equal(DeviceFeatureRules.Conv1, DeviceFeatureRules.Log1);
        var session = h.Server.Current!;
        session.Events.OnHandoffRequest(Request());
        session.Events.OnLogRepair(Repair());
        await h.IdleAsync();

        probe.Of("OnHandoffRequest").Should().BeEmpty();
        probe.Of("OnLogRepair").Should().ContainSingle();
    }

    [Fact]
    public async Task 第5階段_登記了hand1卻沒有log1_Hello不宣告hand1()
    {
        await using var h = new EngineHarness();
        var probe = new Probe
        {
            Act = Declare(("ContributeConversationFeatures", DeviceFeatureRules.Conv1), ("ContributeHandoffFeatures", DeviceFeatureRules.Hand1)),
        };
        h.Server.Features = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Log1, DeviceFeatureRules.Hand1];
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        h.Server.Hellos.Single().Features.Should().Equal(new[] { DeviceFeatureRules.Conv1 }, "hand1 要靠日誌驗證逐字稿（D1）");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task 第5階段_交握完成前到的訊息_暫存_交握完成後依到達順序分派()
    {
        await using var h = new EngineHarness();
        var probe = new Probe
        {
            Act = Declare(("ContributeConversationFeatures", DeviceFeatureRules.Live1), ("ContributeLogFeatures", DeviceFeatureRules.Log1),
                ("ContributeHandoffFeatures", DeviceFeatureRules.Hand1)),
        };
        h.Server.Features = [DeviceFeatureRules.Live1, DeviceFeatureRules.Log1, DeviceFeatureRules.Hand1];
        var request = Request();
        var watch = new WatchChanged([ConversationId]);
        var repair = Repair();
        h.Server.BeforeProveReturns = s =>
        {
            s.Events.OnHandoffRequest(request);
            s.Events.OnWatchChanged(watch);
            s.Events.OnLogRepair(repair);
        };

        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        probe.Calls.Where(c => Phase5Hooks.Contains(c.Hook) || c.Hook == "OnWatchChanged").Select(c => c.Arg).Should().Equal(request, watch, repair);
    }

    [Fact]
    public async Task 登記的功能宣告不合格_整份不送_Hello照常()
    {
        await using var h = new EngineHarness();
        var probe = new Probe { Act = Declare(("ContributeMediaFeatures", "Media 1")) };
        h.Server.Features = [DeviceFeatureRules.Media1];
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        h.Server.Hellos.Single().Features.Should().BeNull("伺服器會整個拒絕不合格的 Hello，所以不送");
        (await h.Inspect(v => v.Features)).Should().BeEmpty();
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    // ── 早到緩衝（D49） ──

    [Fact]
    public async Task 交握完成前的訊息_暫存_交握完成後依到達順序處理_溢出的指令回busy()
    {
        await using var h = new EngineHarness();
        var probe = new Probe
        {
            Act = Declare(("ContributeConversationFeatures", DeviceFeatureRules.Live1), ("ContributeCommandFeatures", DeviceFeatureRules.Cmd1)),
        };
        h.Server.Features = [DeviceFeatureRules.Cmd1, DeviceFeatureRules.Live1];
        var commands = Enumerable.Range(0, SyncEarlyMessages.MaxCommands + 3).Select(_ => Command()).ToList();
        var watch1 = new WatchChanged([ConversationId]);
        var watch2 = new WatchChanged([]);
        h.Server.BeforeProveReturns = s =>
        {
            s.Events.OnWatchChanged(watch1);
            foreach (var c in commands.Take(SyncEarlyMessages.MaxCommands + 2))
                s.Events.OnCommandDelivery(c);
            s.Events.OnWatchChanged(watch2);
            s.Events.OnCommandDelivery(commands[^1]);
        };

        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        var calls = h.Server.Calls.ToList();
        var prove = calls.IndexOf("1:Prove");
        h.Server.Sessions.Single().CallsAtProveReturn.Should().Be(prove + 1, "ProveResponse 回來之前沒有呼叫任何 hub 方法");
        var dispatched = probe.Calls.Where(c => DispatchHooks.Contains(c.Hook)).Select(c => c.Arg).ToList();
        dispatched.Should().Equal(new object[] { watch1 }.Concat(commands.Take(SyncEarlyMessages.MaxCommands)).Append(watch2), "依到達順序；第 17 則起的指令不留");
        var proven = probe.Hooks.ToList().IndexOf("OnProvenForCommands");
        proven.Should().BeGreaterThanOrEqualTo(0).And.BeLessThan(probe.Hooks.ToList().IndexOf("OnWatchChanged"), "指令的接續在暫存的訊息之前");
        h.Server.CommandResults.Should().HaveCount(3);
        h.Server.CommandResults.Select(r => r.CommandId).Should().Equal(commands.Skip(SyncEarlyMessages.MaxCommands).Select(c => c.CommandId));
        h.Server.CommandResults.Should().OnlyContain(r => r.State == RemoteResultStates.Rejected && r.Code == RemoteResultCodes.Busy);
        h.Server.CommandResults.Should().OnlyContain(r => RemoteResultRules.Check(r) == null, "伺服器的格式檢查收得下");
        calls.IndexOf("1:CommandResult").Should().BeGreaterThan(prove);

        // 交握完成之後到的照常立刻處理
        var later = Command();
        h.Server.Current!.Command(later);
        await h.IdleAsync();
        probe.Of("OnCommand").Last().Should().BeSameAs(later);
    }

    [Fact]
    public async Task 交握完成前的指令_沒協商cmd1_不分派也不回busy()
    {
        await using var h = new EngineHarness();
        var probe = new Probe();
        h.Server.Features = [DeviceFeatureRules.Cmd1];
        h.Server.BeforeProveReturns = s =>
        {
            for (var i = 0; i < SyncEarlyMessages.MaxCommands + 4; i++)
                s.Command(Command());
        };

        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        probe.Hooks.Should().NotContain(DispatchHooks);
        h.Server.CommandResults.Should().BeEmpty("沒協商的伺服器不送它新訊息");
        h.Server.Calls.Should().NotContain(c => c.EndsWith(":CommandResult", StringComparison.Ordinal));
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task 伺服器身分驗證失敗_Hello前收到的訊息隨連線丟掉()
    {
        await using var h = new EngineHarness();
        var probe = new Probe { Act = Declare(("ContributeCommandFeatures", DeviceFeatureRules.Cmd1)) };
        h.Server.Features = [DeviceFeatureRules.Cmd1];
        h.Server.ForgeHubSignature = true;
        var forged = Command();
        h.Server.BeforeHelloReturns = s =>
        {
            s.Command(forged);
            for (var i = 0; i < SyncEarlyMessages.MaxCommands + 2; i++)
                s.Command(Command());
        };

        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);
        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        h.Server.ForgeHubSignature = false;
        h.Server.BeforeHelloReturns = null;
        await h.AdvanceAsync(1);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        probe.Of("OnCommand").Should().BeEmpty("伺服器身分沒驗過的連線，暫存的訊息一則都不處理");
        h.Server.CommandResults.Should().BeEmpty("也不回 busy");
        probe.Of("OnProvenForCommands").Should().Equal(2);
    }

    [Fact]
    public async Task OnProvenForMedia排在暫存的MediaRequest之前_權杖存好才回覆()
    {
        await using var h = new EngineHarness();
        var probe = new Probe { Act = Declare(("ContributeMediaFeatures", DeviceFeatureRules.Media1)) };
        h.Server.Features = [DeviceFeatureRules.Media1];
        var relay = new FakeRelayHandler();
        var early = new MediaRequest(MediaTestFiles.NewId(), MediaTestFiles.NewId(), MediaPurposes.Thumb);
        h.Server.BeforeProveReturns = s => s.Media(early);

        await h.StartAsync(e =>
        {
            e.ContractProbeForTest = probe.Record;
            e.MediaHttpHandlerForTest = relay;
        });

        var hooks = probe.Hooks.ToList();
        hooks.IndexOf("OnProvenForMedia").Should().BeGreaterThanOrEqualTo(0).And.BeLessThan(hooks.IndexOf("OnProvenForCommands"));
        hooks.IndexOf("OnProvenForCommands").Should().BeLessThan(hooks.IndexOf("OnMediaRequest"), "暫存的訊息在所有 Prove 的接續之後才處理");
        probe.Of("OnMediaRequest").Should().ContainSingle().Which.Should().BeSameAs(early);
        var put = relay.Puts.Should().ContainSingle().Subject;
        put.RelayId.Should().Be(early.RelayId);
        put.Status.Should().Be(404, "沒有對話的盒子：參照一律不認得");
        put.Authorization.Should().Be("Bearer cha_test", "Prove 回的權杖已經存好");
    }

    [Fact]
    public async Task 媒體的登記點_交握完成_Live_丟掉_各通知一次_在引擎執行緒()
    {
        await using var h = new EngineHarness();
        var probe = new Probe();
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        probe.Of("OnProvenForMedia").Should().Equal(1);
        probe.Of("OnOnlineForMedia").Should().Equal(1);
        probe.Of("OnConnectionClosedForMedia").Should().BeEmpty();
        var hooks = probe.Hooks.ToList();
        hooks.IndexOf("OnOnlineForMedia").Should().BeGreaterThan(hooks.IndexOf("OnOnlineForConversations"));

        h.Server.Current!.Drop();
        await h.IdleAsync();
        probe.Of("OnConnectionClosedForMedia").Should().Equal(1);

        await h.AdvanceAsync(1);
        probe.Of("OnProvenForMedia").Should().Equal(1, 2);
        probe.Of("OnOnlineForMedia").Should().Equal(1, 2);
        probe.Calls.Where(c => c.Hook.EndsWith("ForMedia", StringComparison.Ordinal)).Should().OnlyContain(c => c.Thread == h.Engine.ThreadId);
    }

    // ── 游標、拉取與對話事件 ──

    [Fact]
    public async Task 登記的游標_Hello只加新鍵_拉取接在第3階段的兩個之後()
    {
        await using var h = new EngineHarness();
        var stream = new PullCursor(0, Stream: SyncStreams.Conversation(ConversationId));
        var probe = new Probe
        {
            Act = (hook, arg) =>
            {
                if (hook == "ContributeHelloCursors")
                {
                    var into = (Dictionary<string, long>)arg!;
                    into[SyncClasses.Meta] = 5;
                    into[SyncClasses.Catalog] = 999;
                }
                else if (hook == "ContributePullCursors")
                {
                    ((List<PullCursor>)arg!).Add(stream);
                }
            },
        };

        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        var cursors = h.Server.Hellos.Single().Cursors;
        cursors.Should().BeEquivalentTo(new Dictionary<string, long> { [SyncClasses.Catalog] = 0, [SyncClasses.Usage] = 0, [SyncClasses.Meta] = 5 },
            "第 3 階段的游標不讓登記點改");
        h.Server.Pulls.First().Cursors.Should().Equal(new PullCursor(0, SyncClasses.Catalog), new PullCursor(0, SyncClasses.Usage), stream);
    }

    [Fact]
    public async Task 拉到的對話事件_UI的部分與目錄同一次套用_提交之後才做其餘_不警告()
    {
        await using var h = new EngineHarness();
        var owner = SyncIds.NewDeviceId();
        var seq = h.Server.PublishConversation(ConversationEventKinds.Created, ConversationId, "{}", owner);
        var onUi = new ConcurrentQueue<(bool IsUi, IReadOnlyDictionary<string, long> Cursors)>();
        var afterCommit = new ConcurrentQueue<(int Thread, IReadOnlyDictionary<string, long> Cursors)>();
        IReadOnlyList<PulledEvent>? events = null;
        var probe = new Probe
        {
            Act = (hook, arg) =>
            {
                if (hook == "ContributePullCursors")
                    ((List<PullCursor>)arg!).Add(new PullCursor(0, Stream: SyncStreams.Conversation(ConversationId)));
                if (hook != "PlanConversationApply")
                    return;
                var (pulled, ui, after) = ((IReadOnlyList<PulledEvent>, List<Action>, List<Action>))arg!;
                events = pulled;
                ui.Add(() => onUi.Enqueue((h.Ui.IsCurrent, h.RawCursors())));
                after.Add(() => afterCommit.Enqueue((Environment.CurrentManagedThreadId, h.RawCursors())));
            },
        };

        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        events.Should().ContainSingle().Which.Seq.Should().Be(seq);
        events![0].Kind.Should().Be(ConversationEventKinds.Created);
        events[0].ConversationId.Should().Be(ConversationId);
        var (isUi, cursorsOnUi) = onUi.Should().ContainSingle().Subject;
        isUi.Should().BeTrue();
        cursorsOnUi.Should().NotContainKey(SyncClasses.Meta, "套用完才提交游標");
        var (thread, cursorsAfter) = afterCommit.Should().ContainSingle().Subject;
        thread.Should().Be(h.Engine.ThreadId);
        cursorsAfter.Should().ContainKey(SyncClasses.Meta).WhoseValue.Should().Be(seq);
        h.Host.LastStatus!.Detail.Should().BeNull("對話事件不再當成不認得的種類");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task 對話事件在UI上套用失敗_游標不前進_下次重拉()
    {
        await using var h = new EngineHarness();
        h.Server.PublishConversation(ConversationEventKinds.Item, ConversationId, "{}", SyncIds.NewDeviceId());
        var fail = true;
        var applied = 0;
        var probe = new Probe
        {
            Act = (hook, arg) =>
            {
                if (hook == "ContributePullCursors")
                    ((List<PullCursor>)arg!).Add(new PullCursor(0, Stream: SyncStreams.Conversation(ConversationId)));
                if (hook == "PlanConversationApply")
                {
                    var (_, ui, _) = ((IReadOnlyList<PulledEvent>, List<Action>, List<Action>))arg!;
                    ui.Add(() =>
                    {
                        if (fail)
                            throw new InvalidOperationException("套用失敗");
                        applied++;
                    });
                }
            },
        };

        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        h.RawCursors().Should().NotContainKey(SyncClasses.Item);
        h.Host.LastStatus!.Detail.Should().Contain("套用伺服器的資料失敗");
        fail = false;
        h.Server.Current!.Notify(SyncClasses.Catalog);
        await h.IdleAsync();
        applied.Should().Be(1);
        h.RawCursors().Should().ContainKey(SyncClasses.Item);
    }

    [Fact]
    public async Task 沒有登記時拉到對話事件_不呼叫UI_不警告_游標照樣前進()
    {
        await using var h = new EngineHarness();
        // 沒有登記就不會要對話的串流：由假伺服器硬塞一筆，確認第 3 階段的路徑不會把它當成不認得的種類
        var (enc, text) = PayloadRules.Encode("{}");
        const long seq = 7;
        var pulled = new PulledEvent(seq, SyncStreams.Conversation(ConversationId), SyncClasses.Meta, SyncIds.NewDeviceId(), ConversationEventKinds.Meta, enc, text,
            1791580000000, ConversationId, 1);
        var served = 0;
        h.Server.PullOverride = _ => Interlocked.Increment(ref served) == 1 ? new PullResponse([pulled], false, seq) : null;

        await h.StartAsync();

        h.Applier.Catalogs.Should().ContainSingle("只有啟動時以快取組的那一次");
        h.Host.LastStatus!.Detail.Should().BeNull();
        (await h.Inspect(v => v.Cursors)).Should().ContainKey(SyncClasses.Meta).WhoseValue.Should().Be(seq);
    }

    [Fact]
    public async Task Notify_對話的類別由登記點決定要不要拉()
    {
        await using var h = new EngineHarness();
        var wantMeta = false;
        var probe = new Probe
        {
            Act = (hook, arg) =>
            {
                if (hook != "OnNotifyForConversations")
                    return;
                var (notify, pull) = ((Notify, StrongBox<bool>))arg!;
                if (wantMeta && notify.Cls.Contains(SyncClasses.Meta))
                    pull.Value = true;
            },
        };
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);
        var pulls = h.Server.Pulls.Count;

        h.Server.Current!.Notify(SyncClasses.Meta);
        await h.IdleAsync();
        h.Server.Pulls.Count.Should().Be(pulls, "第 3 階段不拉 meta");
        probe.Of("OnNotifyForConversations").Should().ContainSingle();

        wantMeta = true;
        h.Server.Current.Notify(SyncClasses.Meta);
        await h.IdleAsync();
        h.Server.Pulls.Count.Should().Be(pulls + 1);

        h.Server.Current.Notify(SyncClasses.Catalog);
        await h.IdleAsync();
        h.Server.Pulls.Count.Should().Be(pulls + 2, "第 3 階段的類別照拉");
        var (_, box) = ((Notify, StrongBox<bool>))probe.Of("OnNotifyForConversations").Last()!;
        box.Value.Should().BeTrue("登記點看得到第 3 階段已經算好的結果");
    }

    [Fact]
    public async Task Notify_交握完成前收到的不問登記點()
    {
        await using var h = new EngineHarness();
        var probe = new Probe
        {
            Act = (hook, arg) =>
            {
                if (hook == "OnNotifyForConversations")
                    (((Notify, StrongBox<bool>))arg!).Item2.Value = true;
            },
        };
        h.Server.BeforeProveReturns = s => s.Notify(SyncClasses.Meta);

        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        probe.Of("OnNotifyForConversations").Should().BeEmpty("交握完成後本來就會先拉一次（登記的游標也在裡面）");
        h.Server.Pulls.Should().ContainSingle();
    }

    // ── 收件匣、通知、批次 ──

    [Fact]
    public async Task 不認得的訊號_登記點處理了就寫進outbox_同一個交易()
    {
        await using var h = new EngineHarness();
        var probe = new Probe
        {
            Act = (hook, arg) =>
            {
                if (hook != "HandleConversationSignal")
                    return;
                var (signal, appends, handled) = ((SyncSignal, List<OutboxAppend>, StrongBox<bool>))arg!;
                if (signal is ConversationTestSignal test)
                {
                    appends.Add(new OutboxAppend(SyncStreams.Catalog, SyncEventKinds.CliState, test.Json, null));
                    handled.Value = true;
                }
            },
        };
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        h.Engine.Post(new ConversationTestSignal("""{"providers":{},"from":"conversation"}"""));
        await h.AdvanceAsync(TimeSpan.FromMilliseconds(100));

        h.Server.Accepted.Should().ContainSingle(e => e.Kind == SyncEventKinds.CliState);
        probe.Of("HandleConversationSignal").Should().ContainSingle("認得的訊號（這裡是 Append）不經登記點");

        h.Append();
        await h.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        probe.Of("HandleConversationSignal").Should().ContainSingle();
        h.Server.Accepted.Should().HaveCount(2);
    }

    [Fact]
    public async Task 不認得的伺服器通知_交給登記點_認得的不經過()
    {
        await using var h = new EngineHarness();
        var probe = new Probe();
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        h.Server.Current!.Notice("ownerLogin", text: "Chrome・Windows");
        h.Server.Current.Notice("alert", text: "伺服器公告");
        await h.IdleAsync();

        var (notice, _) = ((ServerNotice, StrongBox<bool>))probe.Of("HandleServerNoticeExtension").Should().ContainSingle().Subject!;
        notice.Kind.Should().Be("ownerLogin");
        h.Host.LastStatus!.Detail.Should().Be("伺服器公告");
    }

    [Fact]
    public async Task BatchAck寫回outbox之後_通知登記點()
    {
        await using var h = new EngineHarness();
        var probe = new Probe();
        await h.StartAsync(e =>
        {
            e.ContractProbeForTest = probe.Record;
            h.Append(2);
        });

        var acks = probe.Of("OnBatchResultsForConversations").Cast<BatchAck>().ToList();
        acks.Should().NotBeEmpty();
        acks.SelectMany(a => a.Results).Select(r => r.DeviceSeq).Should().Equal(1, 2);
        (await h.Inspect(v => v.Outbox)).Should().OnlyContain(e => e.State == OutboxState.Acked);
    }

    // ── 連線的生命週期、心跳、即時更新 ──

    [Fact]
    public async Task 連線的生命週期_交握完成_Live_丟掉_各通知一次()
    {
        await using var h = new EngineHarness();
        var probe = new Probe();
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        probe.Of("OnProvenForCommands").Should().Equal(1);
        probe.Of("OnOnlineForConversations").Should().Equal(1);
        probe.Of("OnConnectionClosedForConversations").Should().BeEmpty();

        h.Server.Current!.Drop();
        await h.IdleAsync();
        probe.Of("OnConnectionClosedForConversations").Should().Equal(1);
        probe.Of("OnConnectionClosedForCommands").Should().Equal(1);

        await h.AdvanceAsync(1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        probe.Of("OnProvenForCommands").Should().Equal(1, 2);
        probe.Of("OnOnlineForConversations").Should().Equal(1, 2);

        h.Engine.SetPaused(true);
        await h.IdleAsync();
        probe.Of("OnConnectionClosedForConversations").Should().Equal(new object[] { 1, 2 }, "暫停也是丟掉連線");
        probe.Of("OnConnectionClosedForCommands").Should().Equal(1, 2);
        probe.Calls.Where(c => c.Hook.StartsWith("OnConnectionClosed", StringComparison.Ordinal)).Should().OnlyContain(c => c.Thread == h.Engine.ThreadId);
    }

    [Fact]
    public async Task 心跳_登記點填執行中與等待回應的對話()
    {
        await using var h = new EngineHarness();
        var running = new string('a', 32);
        var waiting = new string('b', 32);
        var probe = new Probe
        {
            Act = (hook, arg) =>
            {
                if (hook != "ContributeHeartbeat")
                    return;
                var (r, w) = ((List<string>, List<string>))arg!;
                r.Add(running);
                w.Add(waiting);
            },
        };
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);

        await h.AdvanceAsync(15);

        var beat = h.Server.Heartbeats.Should().ContainSingle().Subject;
        beat.Running.Should().Equal(running);
        beat.Waiting.Should().Equal(waiting);
    }

    [Fact]
    public async Task PostLive_排進引擎執行緒交給登記點_沒有實作時不送()
    {
        await using var h = new EngineHarness();
        var probe = new Probe();
        await h.StartAsync(e => e.ContractProbeForTest = probe.Record);
        var relay = new LiveRelay(ConversationId, 1, 3, 2, "{}");

        await Task.Run(() => h.Engine.PostLive(relay));
        await h.IdleAsync();

        var call = probe.Calls.Where(c => c.Hook == "OnLiveRelay").Should().ContainSingle().Subject;
        call.Arg.Should().BeSameAs(relay);
        call.Thread.Should().Be(h.Engine.ThreadId);
        h.Server.LiveRelays.Should().BeEmpty("WP4.1 沒有實作，不送");
        FluentActions.Invoking(() => h.Engine.PostLive(null!)).Should().Throw<ArgumentNullException>();

        // 停止中：排在停止之後的不交給登記點（停止的第一步就設了旗標，之後才關連線與 sync.db）
        var stop = h.Engine.StopAsync(TimeSpan.FromSeconds(5));
        h.Engine.PostLive(new LiveRelay(ConversationId, 1, 4, 3, "{}"));
        await stop;
        probe.Of("OnLiveRelay").Should().ContainSingle();
    }

    // ── 信封的 conn 綁定（合併前審查 seams/F1）──

    [Fact]
    public async Task 信封的conn是這條連線Hello回應的nonceS_驗得過_重連後舊的conn步驟不符且不算竄改()
    {
        const string aud = "https://hub.example";
        const long created = 1_791_580_000_000;
        await using var h = new EngineHarness();
        await h.StartAsync();
        var first = h.Server.Current!.HelloState!.Value.NonceS;

        (await h.Inspect(v => v.EnvelopeConn)).Should().Be(first, "裝置略過 negotiate、拿不到 SignalR 的連線 id，綁定改用 Hello 的 nonceS");
        var bounds = new ServerTimeBounds(created + 1_000, created + 2_000);
        var body = new CommandEnvelopeBody(aud, "01J9QZ3K8M5R7T2V4W6X8Y0A1B", h.DeviceId, first, RemoteSamples.ConversationId, 1,
            new JObject
            {
                ["cmd"] = "approve",
                ["conversationId"] = RemoteSamples.ConversationId,
                ["itemId"] = "a1b2c3",
                ["choice"] = "allow",
                ["requestDigest"] = RemoteSamples.Digest,
            },
            1, false, false, created, created + 500, created + 30_500, new CommandIssuer(CommandIssuerKinds.Browser, "Chrome・Windows"),
            new CommandAuth(created - 60_000, false));
        h.Server.TrySignEnvelope(body, out var envelope).Should().BeTrue();

        var conn = await h.Inspect(v => v.EnvelopeConn!);
        var verdict = CommandEnvelope.Verify(envelope, body.Cid, new CommandEnvelopeTarget(h.Server.HubSpki, aud, h.DeviceId, conn), bounds);
        verdict.Ok.Should().BeTrue(verdict.Detail);

        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        var second = h.Server.Current!.HelloState!.Value.NonceS;
        second.Should().NotBe(first);
        (await h.Inspect(v => v.EnvelopeConn)).Should().Be(second, "每條連線各自的 nonceS");

        var stale = CommandEnvelope.Verify(envelope, body.Cid, new CommandEnvelopeTarget(h.Server.HubSpki, aud, h.DeviceId, second), bounds);
        stale.Step.Should().Be(CommandEnvelopeSteps.Connection, "簽給舊連線的信封在新連線上不收");
        stale.CountsAsTampering.Should().BeFalse("重連的競態不算竄改");
    }
}
