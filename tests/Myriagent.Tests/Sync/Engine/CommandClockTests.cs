using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Microsoft.Data.Sqlite;
using static Myriagent.Tests.Sync.Engine.CommandEngineTests;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 伺服器時鐘、修剪與遠端操作紀錄的 API（WP4.4 計畫步 4；D4、D11、D32）：協商出 cmd1 的連線滿 6 小時而且閒置時重新 Hello（有執行中的指令就延後）、
/// 時間判不過時重新 Hello（每分鐘最多一次）、睡眠恢復時重新 Hello；修剪在這個引擎實例第一次 Hello 時做、之後滿 24 小時才再做，失敗時這次不宣告 cmd1；
/// 遠端操作紀錄的讀取與匯出在引擎執行緒
/// </summary>
public sealed class CommandClockTests
{
    /// <summary>有指令卡在中樞時撥快（不能等引擎閒下來）：每觸發一個計時器就等引擎執行緒處理完排著的工作</summary>
    private static Task AdvanceBusyAsync(EngineHarness h, TimeSpan by) => h.Time.AdvanceAsync(by, () => h.Engine.InvokeAsync(_ => 0));

    /// <summary>已過期的信封（伺服器時間的上界晚於 exp：時間這一步回 expired）</summary>
    private static CommandEnvelopeBody Expired(CommandEnvelopeFactory f, string title)
    {
        var now = f.ServerNow;
        return f.Body(CommandEnvelopeFactory.Rename(title)) with { Created = now - 40_000, Iat = now - 40_000, Exp = now - 10_000 };
    }

    // ── 修剪（D11） ──

    [Fact]
    public async Task 修剪_第一次Hello做_24小時內重連不做_滿24小時再做()
    {
        // 心跳拉長到一天：撥快 24 小時的期間只有 6 小時的時鐘計時器會重連
        await using var h = new EngineHarness(heartbeat: TimeSpan.FromDays(1));
        UsePort(h);
        var prunes = new ConcurrentQueue<long>();
        await h.StartAsync(e => e.ContractProbeForTest = (hook, _) =>
        {
            if (hook == "Prune")
                prunes.Enqueue(h.Time.NowMs);
        });
        var startedAt = h.Time.NowMs;
        prunes.Should().ContainSingle("這個引擎實例第一次 Hello 一定修剪");
        h.Server.Hellos.Should().ContainSingle();

        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);
        h.Server.Hellos.Should().HaveCount(2);
        prunes.Should().ContainSingle("距上次不到 24 小時：重連不修剪");

        await h.AdvanceAsync(TimeSpan.FromHours(23));
        h.Server.Hellos.Should().HaveCount(5, "6 小時的時鐘計時器在 6、12、18 小時（從第二次 Hello 起算）各重連一次");
        prunes.Should().ContainSingle("還不滿 24 小時");

        await h.AdvanceAsync(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        h.Server.Hellos.Should().HaveCount(6, "第 24 小時的那次重連");
        prunes.Should().HaveCount(2, "距上次滿 24 小時的 Hello 再修剪");
        (prunes.Last() - startedAt).Should().BeGreaterThanOrEqualTo(SyncCommandRules.PruneIntervalMs);
        (await h.Inspect(v => v.Features)).Should().Equal(DeviceFeatureRules.Cmd1);
    }

    [Fact]
    public async Task 修剪丟例外_這次不宣告cmd1_連線照常()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        var fail = true;
        h.OpenStore = (path, deviceId) =>
        {
            var store = SyncStore.Open(path, deviceId);
            store.FaultInjection = point =>
            {
                if (Volatile.Read(ref fail) && point == "Prune:commit")
                    throw new SqliteException("模擬磁碟已滿", 13);
            };
            return store;
        };
        var prunes = new ConcurrentQueue<object?>();
        await h.StartAsync(e => e.ContractProbeForTest = (hook, arg) =>
        {
            if (hook == "Prune")
                prunes.Enqueue(arg);
        });

        h.Server.Hellos.Single().Features.Should().BeNull("修剪失敗：這次不宣告 cmd1");
        port.Reports.Should().Equal(false);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live, "同步照常");
        prunes.Should().BeEmpty();

        Volatile.Write(ref fail, false);
        h.Server.Current!.Drop();
        await h.IdleAsync();
        await h.AdvanceAsync(1);

        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Server.Hellos.Last().Features.Should().Equal(DeviceFeatureRules.Cmd1, DeviceFeatureRules.Peer1, DeviceFeatureRules.Collab1);
        port.Reports.Should().Equal(false, true);
        prunes.Should().ContainSingle("上次沒修剪成功：下一次 Hello 再做");
    }

    // ── 伺服器時鐘（D4） ──

    [Fact]
    public async Task 時鐘滿6小時而且閒置_重新Hello一次_有執行中的指令就延後()
    {
        await using var h = new EngineHarness(heartbeat: TimeSpan.FromDays(1));
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var first = await h.Engine.CommandClockForTestAsync();
        first.Should().NotBeNull();

        await h.AdvanceAsync(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
        h.Server.Hellos.Should().ContainSingle("還不滿 6 小時");
        await h.AdvanceAsync(1);
        h.Server.Hellos.Should().HaveCount(2, "滿 6 小時而且閒置：重新 Hello 一次");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        var second = await h.Engine.CommandClockForTestAsync();
        second.Should().NotBeNull();
        second!.ServerTimeMs.Should().BeGreaterThan(first!.ServerTimeMs, "新的 Hello 記新的伺服器時鐘");

        var gate = new TaskCompletionSource<RemoteOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        port.Script = (_, _) => gate.Task;
        var body = f.Body();
        f.Send(f.Sign(body));
        await WaitUntilAsync(() => port.Runs.Count == 1, "進了閘門");

        await AdvanceBusyAsync(h, TimeSpan.FromHours(6));
        h.Server.Hellos.Should().HaveCount(2, "有執行中的指令：延後");

        gate.SetResult(FakeCommandPort.Done(body));
        await h.IdleAsync();
        h.Server.CommandResults.Should().ContainSingle().Which.State.Should().Be(RemoteResultStates.Done, "結果在原本的連線送出");
        h.Server.Hellos.Should().HaveCount(2, "指令做完不立刻重連，等下一次檢查");

        await h.AdvanceAsync(60);
        h.Server.Hellos.Should().HaveCount(3, "一分鐘後再看：閒置了，重新 Hello");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task 時間判不過_安排重新Hello_一分鐘內只一次()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();

        var first = f.Sign(Expired(f, "第一個"));
        f.Send(first);
        await h.IdleAsync();
        h.Server.Hellos.Should().HaveCount(2, "時間判不過：送出 expired 之後重新 Hello");
        h.Server.Calls.Should().Contain("1:CommandResult", "expired 的結果先在原本的連線送出才斷線");

        var second = f.Sign(Expired(f, "第二個"));
        f.Send(second);
        await h.IdleAsync();
        h.Server.Hellos.Should().HaveCount(2, "一分鐘內已經重新 Hello 過");

        await h.AdvanceAsync(60);
        var third = f.Sign(Expired(f, "第三個"));
        f.Send(third);
        await h.IdleAsync();
        h.Server.Hellos.Should().HaveCount(3, "滿一分鐘：可以再重新 Hello");

        h.Server.CommandResults.Select(r => (r.CommandId, r.State, r.Code)).Should().Equal(
            (first.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Expired),
            (second.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Expired),
            (third.CommandId, RemoteResultStates.Rejected, RemoteResultCodes.Expired));
        port.Calls.Should().BeEmpty();
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    [Fact]
    public async Task 時間判不過_一分鐘的限制看單調時鐘_牆上時鐘往回調也不會一直不重新Hello()
    {
        await using var h = new EngineHarness();
        UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        // 單調時鐘比牆上時鐘（ManualTimeProvider 的 GetUtcNow）多走的量：牆上時鐘被往回調時，兩者之差就是調回去的量
        long ahead = 0;
        await h.StartAsync(e => e.CommandTimestampForTest = () => h.Time.GetTimestamp() + Interlocked.Read(ref ahead));

        f.Send(f.Sign(Expired(f, "第一個")));
        await h.IdleAsync();
        h.Server.Hellos.Should().HaveCount(2, "時間判不過：重新 Hello");

        // 過了一分鐘，但牆上時鐘同時被往回調了一分鐘（GetUtcNow 停在原處）
        Interlocked.Add(ref ahead, TimeSpan.FromSeconds(60).Ticks);
        f.Send(f.Sign(Expired(f, "第二個")));
        await h.IdleAsync();

        h.Server.Hellos.Should().HaveCount(3, "單調時鐘已滿一分鐘就可以再重新 Hello，不看牆上時鐘（合併前審查 T3）");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
    }

    // ── 遠端操作紀錄的讀取不留住結果（合併前審查 T2） ──

    /// <summary>讀一頁紀錄、只留弱參考（不內嵌：測試方法的區域變數不會抓著那一頁）</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadPageWeakly(EngineHarness h)
    {
        var page = h.Engine.LoadRemoteAuditAsync(null, 10, CancellationToken.None).GetAwaiter().GetResult();
        page.Should().NotBeEmpty("前提：讀到的是一份新的清單");
        return new WeakReference(page);
    }

    [Fact]
    public async Task LoadRemoteAuditAsync_引擎還在執行_讀過的那一頁可以被回收()
    {
        await using var h = new EngineHarness();
        UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        f.Send(f.Sign(f.Body()));
        await h.IdleAsync();

        var page = LoadPageWeakly(h);
        // 引擎執行緒的工作佇列還記著上一個工作：再排一個，讓它換掉
        (await h.Engine.CommandClockForTestAsync()).Should().NotBeNull();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        page.IsAlive.Should().BeFalse("每次呼叫都不在 Exited 上留下抓著結果的接續：引擎連開幾週、翻幾千頁也不會累積");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live, "引擎還在執行");
    }

    // ── 停止時不留計時器（合併前審查 T1） ──

    [Fact]
    public async Task StopAsync_時鐘的重新Hello計時器一併取消_不留計時器()
    {
        await using var h = new EngineHarness();
        UsePort(h);
        await h.StartAsync();
        (await h.Engine.CommandClockForTestAsync()).Should().NotBeNull("前提：協商出 cmd1、有伺服器時鐘");
        h.Time.ActiveTimers.Should().Be(2, "陽性對照：心跳與時鐘滿 6 小時的重新 Hello");

        await h.Engine.StopAsync(TimeSpan.FromSeconds(5));

        h.Time.ActiveTimers.Should().Be(0, "回呼抓著整個舊引擎，最長 6 小時才觸發：停止時一律取消");
    }

    [Fact]
    public async Task Cancel_時鐘的重新Hello計時器一併取消_不留計時器()
    {
        await using var h = new EngineHarness();
        UsePort(h);
        await h.StartAsync();
        h.Time.ActiveTimers.Should().Be(2, "陽性對照：心跳與時鐘滿 6 小時的重新 Hello");

        h.Engine.Cancel();
        await h.Engine.Exited.WaitAsync(TimeSpan.FromSeconds(5));

        h.Time.ActiveTimers.Should().Be(0);
    }

    // ── 睡眠恢復（D32） ──

    [Fact]
    public async Task OnSystemResumed_連著時重連_舊連線的指令不執行_新Hello之後用新時鐘()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var old = h.Server.Current!;
        var oldConn = f.Conn;
        var oldClock = await h.Engine.CommandClockForTestAsync();
        oldClock.Should().NotBeNull();

        // 睡眠期間伺服器時間往前跳了一小時（單調時鐘沒走）：舊的時鐘會把新信封判成還沒生效
        h.Server.ClockOffsetMs = 3_600_000;
        CommandDelivery? early = null;
        h.Server.BeforeProveReturns = session =>
        {
            if (early is not null)
                return;
            early = f.Sign(f.Body(CommandEnvelopeFactory.Rename("交握中")));
            session.Command(early);
        };

        h.Engine.OnSystemResumed();
        await h.IdleAsync();

        h.Server.Hellos.Should().HaveCount(2, "連著時斷線重連");
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        var clock = await h.Engine.CommandClockForTestAsync();
        clock.Should().NotBeNull();
        (clock!.ServerTimeMs - oldClock!.ServerTimeMs).Should().BeGreaterThanOrEqualTo(3_600_000, "新的 Hello 記新的伺服器時鐘");
        early.Should().NotBeNull();
        port.Executed.Should().Equal(new[] { early!.CommandId }, "交握中收到的指令等交握完成、以新的時鐘驗");

        var stale = f.Sign(f.Body(CommandEnvelopeFactory.Rename("舊連線")) with { Conn = oldConn });
        old.Command(stale);
        await h.IdleAsync();

        port.Calls.Select(c => c.Body.Cid).Should().Equal(new[] { early.CommandId }, "舊連線送來的指令不處理");
        h.Server.CommandResults.Select(r => r.CommandId).Should().Equal(early.CommandId);
    }

    [Fact]
    public async Task OnSystemResumed_退避中立刻重試_notBefore之前不重連()
    {
        await using var h = new EngineHarness();
        UsePort(h);
        await h.StartAsync();

        h.Server.Current!.Drop();
        await h.IdleAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        h.Engine.OnSystemResumed();
        await h.IdleAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Live, "退避中：立刻重試（不等退避的 1 秒）");
        h.Server.Hellos.Should().HaveCount(2);

        h.Server.Current!.Notice("shutdown", 30);
        await h.IdleAsync();
        h.Server.Current.Drop();
        await h.IdleAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff);
        h.Engine.OnSystemResumed();
        await h.IdleAsync();
        (await h.StateAsync()).Should().Be(SyncEngineState.Backoff, "伺服器指定的時間之前不重連");
        h.Server.Hellos.Should().HaveCount(2);

        await h.AdvanceAsync(30);
        (await h.StateAsync()).Should().Be(SyncEngineState.Live);
        h.Server.Hellos.Should().HaveCount(3);
    }

    // ── 遠端操作紀錄（ISyncEngineCommands） ──

    [Fact]
    public async Task LoadRemoteAuditAsync與ExportRemoteAuditAsync在引擎執行緒()
    {
        await using var h = new EngineHarness();
        var port = UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        var threads = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        await h.StartAsync(e => e.ContractProbeForTest = (hook, _) =>
        {
            if (hook is nameof(SyncEngine.LoadRemoteAuditAsync) or nameof(SyncEngine.ExportRemoteAuditAsync))
                threads[hook] = Environment.CurrentManagedThreadId;
        });
        var delivery = f.Sign(f.Body());
        f.Send(delivery);
        await h.IdleAsync();
        var engineThread = port.Calls.Single().Thread;

        var page = await h.Engine.LoadRemoteAuditAsync(null, 10, CancellationToken.None);
        page.Select(e => (e.CommandId, e.State)).Should().Equal((delivery.CommandId, RemoteResultStates.Done));
        (await h.Engine.LoadRemoteAuditAsync(page[0].At, 10, CancellationToken.None, page[0].Seq + 1)).Select(e => e.Seq).Should().Equal([page[0].Seq],
            "beforeSeq 傳到 sync.db：同一個 at、seq 比較小的照樣在下一頁");
        (await h.Engine.LoadRemoteAuditAsync(page[0].At, 10, CancellationToken.None)).Should().BeEmpty("對照：只帶 at");
        var path = h.Dir.Combine("export", "audit.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        (await h.Engine.ExportRemoteAuditAsync(path, CancellationToken.None)).Should().Be(1);
        File.ReadAllLines(path).Should().ContainSingle().Which.Should().Contain(delivery.CommandId);

        threads.Should().HaveCount(2);
        threads.Values.Should().AllSatisfy(t => t.Should().Be(engineThread, "sync.db 只在引擎執行緒讀寫"));
        engineThread.Should().NotBe(Environment.CurrentManagedThreadId);

        await h.Engine.StopAsync(TimeSpan.FromSeconds(5));
        (await h.Engine.LoadRemoteAuditAsync(null, 10, CancellationToken.None)).Should().BeEmpty("引擎停了：回空清單");
        (await h.Engine.LoadRemoteAuditAsync(null, 10, new CancellationToken(canceled: true))).Should().BeEmpty("引擎停了：取消也回空清單");
        var after = h.Dir.Combine("export", "after.jsonl");
        (await h.Engine.ExportRemoteAuditAsync(after, CancellationToken.None)).Should().Be(0, "引擎停了：回 0");
        File.Exists(after).Should().BeFalse();
    }
}
