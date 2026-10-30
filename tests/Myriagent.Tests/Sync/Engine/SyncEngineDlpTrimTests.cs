using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Sync;
using Myriagent.Tests.Sync.Fakes;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 引擎閒置時釋放 DLP 樣式、有活動時預熱（WP4.9 步 5、計畫 D3、D5）：靜置期之後釋放；有串流、有回合、有人看、重整中都不釋放；
/// 別處用過樣式時再等一個靜置期；有人看時先預熱；釋放之後送出的投影和釋放之前相同；沒打開時什麼都不做。
/// 樣式是全程序共用的，和 <see cref="CredentialLeakRulesReleaseTests"/> 同一個循序執行的集合；時間一律以假時鐘撥
/// </summary>
[Collection(DlpPatternCollection.Name)]
public sealed class SyncEngineDlpTrimTests
{
    private static readonly DateTime T0 = new(2026, 10, 21, 9, 0, 0, DateTimeKind.Local);

    private static readonly string[] BothFeatures = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];

    private static readonly TimeSpan Quiet = DlpCacheTrimRules.QuietPeriod;

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(bool trim = true)
        {
            Box = new ConversationChangeBox();
            H = new EngineHarness(conversations: Box) { TrimDlpCache = trim };
            Ui = new FakeConversationUi(Box, () => H.Engine);
            Box.UiPort = Ui;
            H.Server.Features = BothFeatures;
            _ = H.Server.Conversations;
        }

        public EngineHarness H { get; }

        public ConversationChangeBox Box { get; }

        public FakeConversationUi Ui { get; }

        /// <summary>假的 GC（null＝這個程序真的積極回收）</summary>
        public FakeHeap? Heap { get; init; }

        public Task StartAsync() => H.StartAsync(e =>
        {
            if (Heap is { } heap)
                e.DlpHeap = heap;
        });

        public Task IdleAsync() => H.IdleAsync();

        public FakeConversation New()
            => Ui.Add(new FakeConversation(new AgentConversation
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "釋放 DLP 樣式的對話",
                AccountId = EngineHarness.LocalAccount,
                Provider = AgentProvider.Claude,
                CreatedAt = T0,
                UpdatedAt = T0,
            }));

        public void Meta(FakeConversation c) => Ui.Put(new MetaChanged(c.Id, c.Snapshot()));

        /// <summary>新的對話：送出 created、確認</summary>
        public async Task<FakeConversation> KnownAsync()
        {
            var c = New();
            Meta(c);
            await IdleAsync();
            await H.AdvanceAsync(1);
            (await H.Inspect(v => v.Store!.LoadPublishMeta()))[c.Id].CreatedAcked.Should().BeTrue("前提：created 已確認");
            return c;
        }

        /// <summary>發布過的對話改成 localOnly：伺服器刪除的旁檔、conv.deleted、墓碑＋封鎖都寫好（之後的 MetaChanged 不再更新 lastMeta）</summary>
        public async Task MakeLocalOnlyAsync(FakeConversation c)
        {
            c.Meta.SyncPolicy = ConversationSyncPolicies.LocalOnly;
            Meta(c);
            await IdleAsync();
            await H.AdvanceAsync(1);
            var row = (await H.Inspect(v => v.Store!.LoadPublishMeta()))[c.Id];
            row.DeletedAt.Should().NotBeNull("前提：墓碑寫好");
            row.Blocked.Should().Be(SyncEngine.BlockedLocalOnly, "前提：localOnly 的封鎖");
        }

        public AgentChatItem Item(FakeConversation c, string text, AgentItemStatus status)
        {
            var item = new AgentChatItem { Kind = AgentItemKind.Assistant, Status = status, Text = text, CreatedAt = T0, UpdatedAt = T0, Ord = c.NextOrd++ };
            c.Items.Add(item);
            Ui.Put(new ItemChanged(c.Id, ChatItemCopyRules.Copy(item)));
            return item;
        }

        public async Task AdvanceAsync(TimeSpan by)
        {
            await H.AdvanceAsync(by);
            await IdleAsync();
        }

        /// <summary>伺服器收到的這個項目的最後一筆 conv.item 的 text</summary>
        public string ServerText(FakeConversation c, AgentChatItem item)
            => H.Server.Batches.SelectMany(b => b.Events)
                .Where(e => e.ConversationId == c.Id && e.Kind == ConversationEventKinds.Item)
                .Select(Obj)
                .Where(o => o["id"]!.Value<string>() == item.Id)
                .Select(o => o["text"]!.Value<string>()!)
                .Last();

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    [Fact]
    public async Task 靜置期之後釋放_少一秒還在()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        rig.Item(c, "完成的回覆", AgentItemStatus.Done);
        await rig.IdleAsync();
        CredentialLeakRules.IsCompiled.Should().BeTrue("剛遮蔽過");

        await rig.AdvanceAsync(Quiet - TimeSpan.FromSeconds(1));
        CredentialLeakRules.IsCompiled.Should().BeTrue("還沒滿靜置期");
        rig.H.Engine.DlpTrimCount.Should().Be(0);

        await rig.AdvanceAsync(TimeSpan.FromSeconds(1));
        CredentialLeakRules.IsCompiled.Should().BeFalse("靜置滿 20 秒、沒有回合、沒有人看、重整做完");
        rig.H.Engine.DlpTrimCount.Should().Be(1);

        await rig.AdvanceAsync(Quiet * 3);
        rig.H.Engine.DlpTrimCount.Should().Be(1, "已經釋放了：沒有新的活動就不再釋放（下一次只是一小時後的再壓縮檢查）");
    }

    [Fact]
    public async Task 有串流時一直留著_串流結束之後才釋放()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        var item = rig.Item(c, "串流中", AgentItemStatus.Running);
        await rig.IdleAsync();

        for (var i = 0; i < 4; i++)
            await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeTrue("有執行中的項目（串流中）不釋放");
        rig.H.Engine.DlpTrimCount.Should().Be(0);

        item.Status = AgentItemStatus.Done;
        rig.Ui.Put(new ItemChanged(c.Id, ChatItemCopyRules.Copy(item)));
        await rig.IdleAsync();
        await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeFalse("串流結束、靜置期之後釋放");
    }

    [Fact]
    public async Task 有執行中的回合時不釋放_回合開始的那一批已經編譯好()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeFalse("前提：已釋放");

        c.Running = true;
        rig.Ui.Put(new TurnStartedChange(c.Id, false));
        rig.Meta(c);
        await rig.IdleAsync();
        CredentialLeakRules.IsCompiled.Should().BeTrue("回合開始的那一批（遮蔽中繼資料、或預熱）之後樣式在");

        for (var i = 0; i < 3; i++)
            await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeTrue("回合還在執行：不釋放");

        c.Running = false;
        rig.Ui.Put(new TurnCompletedChange(c.Id, AgentTurnOutcome.Completed, null));
        rig.Meta(c);
        await rig.IdleAsync();
        await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeFalse("回合結束、靜置期之後釋放");
    }

    [Fact]
    public async Task 有人看時預熱而且不釋放()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeFalse("前提：已釋放");

        rig.H.Server.Current!.Watch([c.Id]);
        await rig.IdleAsync();
        CredentialLeakRules.IsCompiled.Should().BeTrue("開始有人看：先在引擎執行緒預熱（沒有任何遮蔽）");

        rig.Item(c, "有人在看", AgentItemStatus.Done);
        await rig.IdleAsync();
        for (var i = 0; i < 3; i++)
            await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeTrue("有人在看：不釋放");

        rig.H.Server.Current!.Watch([]);
        await rig.IdleAsync();
        await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeFalse("沒人看了、靜置期之後釋放");
    }

    [Fact]
    public async Task 只有觀看沒有項目_觀看結束之後仍釋放()
    {
        // 只看不改的已同步對話不經 HandleConversationSignal：觀看清單本身要算活動、排計時器（合併前審查 DLP-2、THR-1 (a)）
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeFalse("前提：已釋放、計時器鏈已結束");
        var trims = rig.H.Engine.DlpTrimCount;

        rig.H.Server.Current!.Watch([c.Id]);
        await rig.IdleAsync();
        CredentialLeakRules.IsCompiled.Should().BeTrue("開始有人看：預熱");
        for (var i = 0; i < 2; i++)
            await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeTrue("有人在看：不釋放");
        rig.H.Engine.DlpTrimCount.Should().Be(trims);

        rig.H.Server.Current!.Watch([]);
        await rig.IdleAsync();
        await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeFalse("沒人看了：靜置期之後釋放預熱建的那一份");
        rig.H.Engine.DlpTrimCount.Should().Be(trims + 1, "那一次積極回收只做一次");
    }

    [Fact]
    public async Task 遠端指令重建的樣式_靜置期之後照樣釋放()
    {
        // 稽核遮蔽指令名稱（AuditFacts）在釋放之後重建樣式，指令本身不改對話（合併前審查 THR-1 (b)）
        await using var h = new EngineHarness { TrimDlpCache = true };
        CommandEngineTests.UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        CredentialLeakRules.ReleaseCompiledPatterns();

        f.Send(f.Sign(f.Body(ListFiles())));
        await h.IdleAsync();
        h.Server.CommandResults.Should().ContainSingle("前提：指令做完了");
        CredentialLeakRules.IsCompiled.Should().BeTrue("稽核的事實遮蔽指令名稱時重建");
        var trims = h.Engine.DlpTrimCount;

        await h.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeFalse("指令算一次活動：靜置期之後釋放");
        h.Engine.DlpTrimCount.Should().Be(trims + 1);
    }

    [Fact]
    public async Task 長時間的遠端指令期間釋放_背景重建的那一份在結果回來之後再釋放()
    {
        // fileFetch 的檔案 DLP（MediaServerRules.HasLeak）在背景、可能在引擎釋放之後才重建：結果回來時再記一次活動（THR-1 (b) 的 FinishCommand）
        await using var h = new EngineHarness { TrimDlpCache = true };
        var port = CommandEngineTests.UsePort(h);
        using var f = new CommandEnvelopeFactory(h);
        await h.StartAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        port.Script = async (body, _) =>
        {
            await gate.Task;
            CredentialLeakRules.HasLeak("背景的檔案 DLP 掃的內容");
            return FakeCommandPort.Done(body);
        };

        f.Send(f.Sign(f.Body(ListFiles())));
        await CommandEngineTests.WaitUntilAsync(() => port.Runs.Count == 1, "進了閘門、卡在腳本");
        // 指令卡著時 IdleAsync 等不到（進行中的工作）：只撥時鐘、每個計時器之後在引擎執行緒繞一圈
        await h.Time.AdvanceAsync(Quiet, () => h.Engine.InvokeAsync(_ => 0));
        await CommandEngineTests.WaitUntilAsync(() => h.Engine.DlpTrimCount == 1, "指令執行中滿靜置期：釋放");
        CredentialLeakRules.IsCompiled.Should().BeFalse();

        gate.SetResult();
        await h.IdleAsync();
        h.Server.CommandResults.Should().ContainSingle();
        CredentialLeakRules.IsCompiled.Should().BeTrue("背景的檔案 DLP 重建了");

        await h.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeFalse("結果回來算一次活動：靜置期之後再釋放");
        h.Engine.DlpTrimCount.Should().Be(2);
    }

    [Fact]
    public async Task 靜置期內又有一批變化_從那一批重新計時_釋放之後的下一批再排()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        rig.Item(c, "完成的回覆", AgentItemStatus.Done);
        await rig.IdleAsync();

        await rig.AdvanceAsync(Quiet - TimeSpan.FromSeconds(5));
        rig.Item(c, "又一則", AgentItemStatus.Done);
        await rig.IdleAsync();
        await rig.AdvanceAsync(TimeSpan.FromSeconds(5));
        CredentialLeakRules.IsCompiled.Should().BeTrue("第二批之後還沒滿靜置期");

        await rig.AdvanceAsync(Quiet - TimeSpan.FromSeconds(5));
        CredentialLeakRules.IsCompiled.Should().BeFalse("從第二批算滿靜置期");
        rig.H.Engine.DlpTrimCount.Should().Be(1);

        rig.Item(c, "釋放之後的一則", AgentItemStatus.Done);
        await rig.IdleAsync();
        CredentialLeakRules.IsCompiled.Should().BeTrue("遮蔽時重建");
        await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeFalse("下一批活動之後再釋放一次");
        rig.H.Engine.DlpTrimCount.Should().Be(2);
    }

    [Fact]
    public async Task 釋放之後送出的投影和釋放之前相同()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        var text = "部署的權杖是 " + "gh" + "p_" + new string('a', 18) + new string('7', 18) + "，請勿外流";
        var before = rig.Item(c, text, AgentItemStatus.Done);
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(1);
        var maskedBefore = rig.ServerText(c, before);

        await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeFalse("前提：已釋放");

        var after = rig.Item(c, text, AgentItemStatus.Done);
        await rig.IdleAsync();
        await rig.H.AdvanceAsync(1);
        var maskedAfter = rig.ServerText(c, after);

        maskedBefore.Should().Contain(CredentialLeakRules.Replacement).And.NotContain("gh" + "p_");
        maskedAfter.Should().Be(maskedBefore, "釋放之後重建的樣式遮出同樣的文字");
    }

    [Fact]
    public async Task 沒打開時不釋放()
    {
        await using var rig = new Rig(trim: false);
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        rig.Item(c, "完成的回覆", AgentItemStatus.Done);
        await rig.IdleAsync();

        await rig.AdvanceAsync(Quiet * 3);
        CredentialLeakRules.IsCompiled.Should().BeTrue();
        rig.H.Engine.DlpTrimCount.Should().Be(0);
    }

    // ── 步 6b：連不上時的那一次積極回收、閒置期間的再壓縮（假的 GC：不真的積極回收，已提交的空間由測試設定） ──

    private static readonly TimeSpan Hour = DlpCacheTrimRules.RecompactInterval;

    private static readonly TimeSpan CheckPeriod = DlpCacheTrimRules.RecompactCheckPeriod;

    private const long Growth = DlpCacheTrimRules.RecompactGrowthBytes;

    [Fact]
    public async Task 已配對但連不上_啟動之後靜置滿做一次積極回收_樣式從沒建過()
    {
        // 步 6 的發現：離線、吞封包的桌面不進 Live、不重整、不遮蔽，ShouldTrim 永遠不成立，啟動留下的已提交空間一直不還
        var heap = new FakeHeap();
        await using var rig = new Rig { Heap = heap };
        rig.H.Server.ConnectFailure = _ => EngineHarness.Transient();
        CredentialLeakRules.ReleaseCompiledPatterns();
        await rig.StartAsync();
        (await rig.H.StateAsync()).Should().NotBe(SyncEngineState.Live, "前提：連不上");

        await rig.AdvanceAsync(Quiet - TimeSpan.FromSeconds(1));
        heap.Compactions.Should().Be(0, "啟動之後還沒滿靜置期");
        await rig.AdvanceAsync(TimeSpan.FromSeconds(1));
        heap.Compactions.Should().Be(1, "啟動之後靜置滿 20 秒、沒有回合、沒有人看：做一次");
        rig.H.Engine.DlpCompactCount.Should().Be(1);
        rig.H.Engine.DlpTrimCount.Should().Be(0, "沒有樣式可釋放");
        CredentialLeakRules.IsCompiled.Should().BeFalse("積極回收不建樣式");
        (await rig.H.StateAsync()).Should().NotBe(SyncEngineState.Live, "整段都連不上");

        await rig.AdvanceAsync(Hour * 2);
        heap.Compactions.Should().Be(1, "只做一次：之後已提交的空間沒長就不再做（重連的嘗試不算活動）");
    }

    [Fact]
    public async Task 連不上時有一批變化_從那一批重新計時_做的是釋放那一次()
    {
        var heap = new FakeHeap();
        await using var rig = new Rig { Heap = heap };
        rig.H.Server.ConnectFailure = _ => EngineHarness.Transient();
        await rig.StartAsync();

        await rig.AdvanceAsync(Quiet - TimeSpan.FromSeconds(5));
        var c = rig.New();
        rig.Meta(c);
        await rig.IdleAsync();
        CredentialLeakRules.IsCompiled.Should().BeTrue("離線時新建的對話照樣投影、遮蔽中繼資料");
        await rig.AdvanceAsync(TimeSpan.FromSeconds(5));
        heap.Compactions.Should().Be(0, "啟動的那一次計時被這一批重排");

        await rig.AdvanceAsync(Quiet - TimeSpan.FromSeconds(5));
        heap.Compactions.Should().Be(1, "從那一批算滿靜置期");
        rig.H.Engine.DlpTrimCount.Should().Be(1, "樣式在：釋放並積極回收（一次，不另做啟動的那一次）");
        CredentialLeakRules.IsCompiled.Should().BeFalse();
    }

    [Fact]
    public async Task 閒置期間已提交的空間長回來_滿一小時而且多了16MB才再做一次()
    {
        var heap = new FakeHeap();
        await using var rig = new Rig { Heap = heap };
        await rig.StartAsync();
        await rig.KnownAsync();
        await rig.AdvanceAsync(Quiet);
        rig.H.Engine.DlpTrimCount.Should().Be(1, "前提：釋放樣式的那一次（之後再壓縮從它算）");
        heap.Compactions.Should().Be(1);
        rig.H.Time.DueIn().Should().Contain(d => d > Hour - TimeSpan.FromSeconds(2) && d <= Hour, "做完之後排一小時後的檢查");

        // 心跳與 keepalive 的配置讓已提交的空間長回來：一小時內長得再多也不做（中途有一批不遮蔽的活動、靜置期之後照樣不做）
        heap.Committed = heap.AfterCompaction + Growth * 3;
        await rig.AdvanceAsync(TimeSpan.FromMinutes(30));
        rig.Ui.Put(new ReplicaClosed(null));
        await rig.IdleAsync();
        await rig.AdvanceAsync(Quiet);
        CredentialLeakRules.IsCompiled.Should().BeFalse("前提：那一批沒有遮蔽（關掉複本）");
        heap.Compactions.Should().Be(1, "距上一次才 30 分鐘");
        await rig.AdvanceAsync(Hour - TimeSpan.FromMinutes(30) - Quiet - TimeSpan.FromSeconds(2));
        heap.Compactions.Should().Be(1, "距上一次還不到一小時");
        await rig.AdvanceAsync(TimeSpan.FromSeconds(2));
        heap.Compactions.Should().Be(2, "滿一小時、多了 16 MB 以上、閒置：再做一次");
        rig.H.Engine.DlpTrimCount.Should().Be(1, "樣式本來就釋放了");

        // 只長到門檻少 1 位元組：滿一小時之後每 10 分鐘看一次，都不做
        heap.Committed = heap.AfterCompaction + Growth - 1;
        await rig.AdvanceAsync(Hour * 3);
        heap.Compactions.Should().Be(2, "沒長到 16 MB");
        heap.Committed = heap.AfterCompaction + Growth;
        await rig.AdvanceAsync(CheckPeriod);
        heap.Compactions.Should().Be(3, "長到門檻：下一次檢查（10 分鐘內）就做");

        // 做完之後又是一小時內最多一次
        heap.Committed = heap.AfterCompaction + Growth * 4;
        await rig.AdvanceAsync(Hour - CheckPeriod - TimeSpan.FromSeconds(1));
        heap.Compactions.Should().Be(3, "第三次之後還不到一小時");
        await rig.AdvanceAsync(CheckPeriod + TimeSpan.FromSeconds(1));
        heap.Compactions.Should().Be(4);
        CredentialLeakRules.IsCompiled.Should().BeFalse("再壓縮不建樣式");
    }

    [Fact]
    public async Task 有人看時不再壓縮_沒人看了才做釋放的那一次()
    {
        var heap = new FakeHeap();
        await using var rig = new Rig { Heap = heap };
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        await rig.AdvanceAsync(Quiet);
        heap.Compactions.Should().Be(1, "前提：釋放樣式的那一次");
        heap.Committed = heap.AfterCompaction + Growth * 2;

        rig.H.Server.Current!.Watch([c.Id]);
        await rig.IdleAsync();
        await rig.AdvanceAsync(Hour + CheckPeriod * 2);
        heap.Compactions.Should().Be(1, "有人在看：滿一小時、長了 32 MB 也不做");

        rig.H.Server.Current!.Watch([]);
        await rig.IdleAsync();
        await rig.AdvanceAsync(Quiet);
        heap.Compactions.Should().Be(2, "沒人看了、靜置期之後：釋放預熱的樣式並積極回收");
        rig.H.Engine.DlpTrimCount.Should().Be(2);
    }

    // ── 合併前審查 PROD-1：執行中的回合不看發布狀態（發布過、後來改成 localOnly 的對話有墓碑，lastMeta 看不到它之後的中繼資料） ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 回合執行中不積極回收_發布過後來改成localOnly的對話也一樣(bool localOnly)
    {
        var heap = new FakeHeap();
        await using var rig = new Rig { Heap = heap };
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        if (localOnly)
            await rig.MakeLocalOnlyAsync(c);
        await rig.AdvanceAsync(Quiet);
        heap.Compactions.Should().Be(1, "前提：釋放樣式的那一次");

        c.Running = true;
        rig.Ui.Put(new TurnStartedChange(c.Id, false));
        rig.Meta(c);
        await rig.IdleAsync();
        heap.Committed = heap.AfterCompaction + Growth * 2;

        await rig.AdvanceAsync(Hour + TimeSpan.FromMinutes(20));
        heap.Compactions.Should().Be(1, "回合還在執行：滿一小時、已提交的空間多了 32 MB 也不做阻塞的積極回收");
        rig.H.Engine.DlpTrimCount.Should().Be(1, "回合還在執行：不釋放");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 重整時正在跑之後結束的回合_靜置期之後照樣釋放_發布過後來改成localOnly的對話也一樣(bool localOnly)
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.KnownAsync();
        if (localOnly)
            await rig.MakeLocalOnlyAsync(c);

        // 回合執行中換一個引擎（app 離線啟動、回合中途才第一次進 Live；或自我更新之後新版接續）：重整那一刻記下 Running
        c.Running = true;
        await rig.H.RestartAsync();
        await rig.IdleAsync();
        (await rig.H.StateAsync()).Should().Be(SyncEngineState.Live, "前提：進了 Live、做了重整");

        c.Running = false;
        rig.Ui.Put(new TurnCompletedChange(c.Id, AgentTurnOutcome.Completed, null));
        rig.Meta(c);
        await rig.IdleAsync();

        var other = await rig.KnownAsync();
        rig.Item(other, "另一個對話遮蔽一次", AgentItemStatus.Done);
        await rig.IdleAsync();
        CredentialLeakRules.IsCompiled.Should().BeTrue("前提：剛遮蔽過");

        await rig.AdvanceAsync(Quiet * 3);
        CredentialLeakRules.IsCompiled.Should().BeFalse("回合已經結束、沒有人看：靜置期之後釋放（不留著重整時的 Running）");
    }

    [Fact]
    public async Task 停止時取消一小時後的計時器()
    {
        // 回呼抓著整個舊引擎（同合併前審查 T4）：SyncEngine.cs 的 CancelTimers 一併取消
        var heap = new FakeHeap();
        await using var rig = new Rig { Heap = heap };
        rig.H.Server.ConnectFailure = _ => EngineHarness.Transient();
        await rig.StartAsync();
        await rig.AdvanceAsync(Quiet);
        heap.Compactions.Should().Be(1, "前提：啟動之後的那一次");
        rig.H.Time.DueIn().Should().Contain(d => d > TimeSpan.FromMinutes(30), "前提：排著一小時後的檢查");

        await rig.H.Engine.StopAsync(TimeSpan.FromSeconds(5));
        rig.H.Time.DueIn().Should().NotContain(d => d > TimeSpan.FromMinutes(30), "停止之後不留著");
    }

    /// <summary>假的 GC：數積極回收的次數；已提交的空間由測試設定，積極回收之後回到 <see cref="AfterCompaction"/></summary>
    private sealed class FakeHeap : IDlpHeapPort
    {
        private long committed = 40L << 20;
        private int compactions;

        public long AfterCompaction { get; } = 10L << 20;

        public long Committed
        {
            get => Interlocked.Read(ref committed);
            set => Interlocked.Exchange(ref committed, value);
        }

        public int Compactions => Volatile.Read(ref compactions);

        public long CommittedBytes() => Committed;

        public void Compact()
        {
            Interlocked.Increment(ref compactions);
            Committed = AfterCompaction;
        }
    }

    /// <summary>不改對話的遠端指令（假的中樞閘門一律 done，不看參數）</summary>
    private static JObject ListFiles() => new() { ["cmd"] = "listFiles", ["conversationId"] = CommandEnvelopeFactory.ConversationId };

    private static JObject Obj(SyncEvent e) => JObject.Parse(PayloadRules.TryDecode(e.Enc, e.Payload, 16 << 20, out var json) ? json : throw new InvalidOperationException("解不開"));
}
