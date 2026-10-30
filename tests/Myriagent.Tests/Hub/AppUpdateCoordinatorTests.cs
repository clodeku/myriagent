using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Hub;
using Myriagent.Tests.TestSupport;
using Myriagent.UI;
using Myriagent.Update;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>協調器（設計 9.1～9.5、8.5）：以真的中樞（HubHarness）、真的 AppUpdateManager（從 updater-state.json 讀已備妥的版本）與假的 shell</summary>
[Collection(AppPathsCollection.Name)]
public sealed class AppUpdateCoordinatorTests
{
    private const string Current = "0.2.1-aaaaaa1";
    private const string Staged = "0.2.2-bbbbbb2";
    private static readonly CliBinaryIdentity ClaudeOld = CliIdentities.Claude("2.1.295", fileId: 1);
    private static readonly CliBinaryIdentity CodexOld = CliIdentities.Codex("0.160.1", fileId: 1);

    private sealed class FakeShell : IAppUpdateShell
    {
        public bool AnyWindowForeground { get; set; }

        public bool MainWindowHidden { get; set; }

        public TimeSpan SinceLastInput { get; set; } = TimeSpan.FromMinutes(10);

        public bool LoginInProgress { get; set; }

        public bool CliUpdating { get; set; }

        public List<ResumePopout> Popouts { get; } = new();

        public List<ResumePopout> Opened { get; } = new();

        public List<(string Title, string Message)> Notices { get; } = new();

        public List<(string Exe, IReadOnlyList<string> Args)> Helpers { get; } = new();

        public List<int> Stopped { get; } = new();

        public List<string> RestartedInto { get; } = new();

        /// <summary>啟動幫手時要不要寫 .armed（模擬幫手正常啟動）</summary>
        public InstallLayout? ArmWith { get; set; }

        public IReadOnlySet<string>? ExitedWith { get; private set; }

        public IReadOnlyList<ResumePopout> CapturePopouts() => Popouts;

        public void OpenPopouts(IReadOnlyList<ResumePopout> popouts) => Opened.AddRange(popouts);

        public void Notify(string title, string message) => Notices.Add((title, message));

        public int? StartHelper(string executable, IReadOnlyList<string> arguments)
        {
            Helpers.Add((executable, arguments));
            if (ArmWith != null)
            {
                var token = arguments[arguments.ToList().IndexOf("--token") + 1];
                Directory.CreateDirectory(ArmWith.HealthDirectory);
                File.WriteAllText(ArmWith.ArmedFile(token), "1");
            }
            return 4242;
        }

        public void StopHelper(int pid) => Stopped.Add(pid);

        public void ExitForUpdate(IReadOnlySet<string> keepDrafts) => ExitedWith = keepDrafts;

        public void RestartInto(string executable) => RestartedInto.Add(executable);

        public int ShowMainCount { get; private set; }

        public void ShowMain() => ShowMainCount++;

        /// <summary>交棒時啟動新版成不成功</summary>
        public bool HandOverSucceeds { get; set; } = true;

        public List<(string Exe, IReadOnlyList<string> Args, IReadOnlySet<string> Keep)> HandedOver { get; } = new();

        public bool HandOver(string executable, IReadOnlyList<string> arguments, IReadOnlySet<string> keepDrafts)
        {
            HandedOver.Add((executable, arguments, keepDrafts));
            return HandOverSucceeds;
        }
    }

    /// <summary>檢查停在「取得最新版」的來源（模擬檢查或下載較新版進行中）</summary>
    private sealed class PendingSource : IAppReleaseSource
    {
        public TaskCompletionSource<ReleaseOffer?> Latest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "測試來源";

        public Task<ReleaseOffer?> GetLatestAsync(CancellationToken ct) => Latest.Task;

        public Task DownloadZipAsync(ReleaseOffer offer, string zipName, string destination, IProgress<long>? progress, CancellationToken ct)
            => throw new InvalidOperationException("這個測試不下載");
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(HubHarness h, IAppReleaseSource? source, bool managed, bool usesDefaultData, TimeSpan armedTimeout, UpdateBlockReason block)
        {
            H = h;
            Block = block;
            Layout = new InstallLayout(H.Temp.Combine("Programs", "Myriagent"));
            if (managed)
            {
                foreach (var r in new[] { Current, Staged })
                    H.Temp.WriteFile(Path.Combine("Programs", "Myriagent", "versions", r, "Myriagent.exe"), r);
                Junction.Create(Layout.Current, Layout.VersionDirectory(Current));
                UpdaterStateStore.Update(Layout, s =>
                {
                    s.Current = Current;
                    s.Staged = Staged;
                    return s;
                });
            }
            ReleaseIdentity.TryParseRelease(Current, out var id);
            Manager = new AppUpdateManager(Layout, id!, source, () => Block, Array.Empty<ReleaseKey>(), Runtime);
            Coordinator = H.OnUi(() => new AppUpdateCoordinator(H.Hub, Manager, Shell, Layout, H.Temp.Root, managed ? Current : null, usesDefaultData,
                AppUpdateTimings.Normal with { ArmedTimeout = armedTimeout }, () => Now));
        }

        /// <param name="managed">從受管理的安裝執行（current 指向 versions\Current、updater-state 記著已備妥的 Staged）；false＝還沒安裝的開發版</param>
        /// <param name="armedTimeout">等幫手寫 .armed 的時限（預設 300 毫秒，測試不必等 10 秒）</param>
        /// <param name="block">manager 的啟用條件（之後可改 Block）</param>
        public static async Task<Fixture> CreateAsync(IAppReleaseSource? source = null, bool managed = true, bool usesDefaultData = true, TimeSpan? armedTimeout = null,
            UpdateBlockReason block = UpdateBlockReason.None)
        {
            var h = new HubHarness();
            try
            {
                await h.SwitchCliAsync(AgentProvider.Claude, null, ClaudeOld);
                await h.SwitchCliAsync(AgentProvider.Codex, null, CodexOld);
                return new Fixture(h, source, managed, usesDefaultData, armedTimeout ?? TimeSpan.FromMilliseconds(300), block);
            }
            catch
            {
                h.Dispose();
                throw;
            }
        }

        public HubHarness H { get; }

        public InstallLayout Layout { get; }

        public AppUpdateManager Manager { get; }

        /// <summary>manager 每次評估啟用條件時讀的值（關掉自動更新、--data 等）</summary>
        public UpdateBlockReason Block { get; set; }

        public FakeShell Shell { get; } = new();

        /// <summary>這個程序的 .NET 有沒有被移除（Removed 不是 null＝已被移除）</summary>
        public AlwaysRuntime Runtime { get; } = new();

        public AppUpdateCoordinator Coordinator { get; }

        public DateTime Now { get; set; } = DateTime.Now;

        public void Tick() => H.Ui.Invoke(() => Coordinator.Tick());

        /// <summary>最近一次送給主視窗的 AppUpdateView（init.appUpdate 或 appUpdate 訊息，取比較晚的）</summary>
        public JToken? LastState()
            => H.OnUi(() => H.Main.All().LastOrDefault(m => m.Value<string>("type") is "appUpdate" or "init") is { } m
                ? m.Value<string>("type") == "init" ? m["appUpdate"] : m["state"]
                : null);

        public void Dispose()
        {
            try
            {
                H.Ui.Invoke(() => Coordinator.Dispose());
                Manager.Dispose();
            }
            finally
            {
                H.Dispose();
            }
        }

        public sealed class AlwaysRuntime : IRuntimeProbe
        {
            public string? Removed { get; set; }

            public bool HasAtLeast(string name, ReleaseVersion version) => true;

            public string? RemovedRuntime => Removed;
        }
    }

    /// <summary>
    /// 2026-10-27：.NET 更新刪掉了這個程序啟動時用的修補版，之後的新版都解壓不了、要等人手動重開 app。沒有已備妥的版本時，
    /// 閒置就照交棒的流程重新啟動成同一版（--handed-over、目前這一版的執行檔），網頁不寫「切換到新開的」；取消就 6 小時內不再自動重新啟動
    /// </summary>
    [Fact]
    public async Task 這個程序的NET已被移除_沒有已備妥的版本_閒置時交棒給同一版自己_取消就暫停()
    {
        using var f = await Fixture.CreateAsync();
        UpdaterStateStore.Update(f.Layout, s =>
        {
            s.Staged = null;
            return s;
        });
        f.H.Ui.Invoke(() => f.Manager.RefreshFromState());
        f.Tick();
        f.Shell.HandedOver.Should().BeEmpty(".NET 還在：沒有要重新啟動");

        f.Runtime.Removed = "10.0.13";
        await TickAndAnswerSnapshotAsync(f);
        await f.H.WaitUntilAsync(() => f.Shell.HandedOver.Count > 0, "交棒給同一版自己");

        var (exe, args, _) = f.Shell.HandedOver.Should().ContainSingle().Subject;
        exe.Should().Be(f.Layout.Executable(Current), "目前這一版自己");
        args[0].Should().Be(InstanceHandoverRules.HandedOverArgument);
        f.Shell.Helpers.Should().BeEmpty("不經換版幫手");
        var resume = ResumeFileStore.Load(f.H.Temp.Root)!;
        (resume.From, resume.To).Should().Be((Current, Current));
        f.Shell.Notices.Should().BeEmpty();
    }

    [Fact]
    public async Task 這個程序的NET已被移除_重新啟動時啟動不了執行檔_就地接續_1小時內不再排空()
    {
        using var f = await Fixture.CreateAsync();
        UpdaterStateStore.Update(f.Layout, s =>
        {
            s.Staged = null;
            return s;
        });
        f.H.Ui.Invoke(() => f.Manager.RefreshFromState());
        f.Shell.HandOverSucceeds = false;
        f.Runtime.Removed = "10.0.13";

        await TickAndAnswerSnapshotAsync(f);
        await f.H.WaitUntilAsync(() => f.Shell.HandedOver.Count > 0, "試著交棒給同一版自己");
        await f.H.WaitUntilAsync(() => f.LastState()?.Value<string>("phase") != "draining", "放棄之後回到等待");
        f.Tick();
        f.Tick();
        await f.H.Ui.DrainAsync();

        f.Shell.HandedOver.Should().ContainSingle("啟動不了就等 1 小時再試，不是每次 Tick 都再排空一次");
        f.LastState()!.Value<string>("blocker").Should().Be(AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.RetryLater));
        f.Now += AppUpdateRestartRules.RetryAfterAbort;
        await TickAndAnswerSnapshotAsync(f);
        await f.H.WaitUntilAsync(() => f.Shell.HandedOver.Count == 2, "1 小時後再試");
    }

    [Fact]
    public async Task 這個程序的NET已被移除_有已備妥的版本_照常以換版幫手換版()
    {
        using var f = await Fixture.CreateAsync();
        f.Shell.ArmWith = f.Layout;
        f.Runtime.Removed = "10.0.13";

        await TickAndAnswerSnapshotAsync(f);
        await f.H.WaitUntilAsync(() => f.Shell.ExitedWith != null, "交棒給換版幫手後結束");

        f.Shell.HandedOver.Should().BeEmpty("換版幫手是新的程序，照常換版就好");
        f.Shell.Helpers.Should().ContainSingle();
    }

    [Fact]
    public async Task 這個程序的NET已被移除_排空中取消_6小時內不再自動重新啟動()
    {
        using var f = await Fixture.CreateAsync();
        UpdaterStateStore.Update(f.Layout, s =>
        {
            s.Staged = null;
            return s;
        });
        f.H.Ui.Invoke(() => f.Manager.RefreshFromState());
        StartBusyCodex(f);
        f.Runtime.Removed = "10.0.13";
        f.Tick();
        await f.H.Ui.DrainAsync();
        f.LastState()!.Value<string>("phase").Should().Be("draining");
        f.LastState()!.Value<string>("handoverTo").Should().BeNull("重新啟動成同一版不寫「切換到新開的」");

        f.H.Ui.Invoke(() => f.Coordinator.CancelRestart());
        await f.H.Ui.DrainAsync();
        f.Tick();
        await f.H.Ui.DrainAsync();

        f.Shell.HandedOver.Should().BeEmpty();
        f.LastState()!.Value<string>("phase").Should().NotBe("draining");
        f.LastState()!.Value<string>("blocker").Should().Be(AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.Snoozed));
    }

    /// <summary>Tick 開始排空，回應交棒前的草稿快照請求（沒有草稿）</summary>
    private static async Task TickAndAnswerSnapshotAsync(Fixture f)
    {
        var mark = f.H.Main.Mark();
        f.Tick();
        var request = await f.H.Main.WaitForAsync("draftSnapshotRequest", since: mark, timeoutMs: 5000);
        f.H.Command(f.H.Main, "draftSnapshot", new { requestId = request.Value<string>("requestId"), drafts = Array.Empty<object>() });
    }

    /// <summary>Codex 對話正在執行工具：排空要等它，不會馬上停下</summary>
    private static FakeSession StartBusyCodex(Fixture f)
    {
        var (_, s) = f.H.StartConversation(f.H.Main, f.H.Codex, "做事");
        f.H.ProcessStarted(s, "p1", CodexOld);
        s.SucceedRestarts("p");
        f.H.Raise(s, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm test", "npm test"));
        f.H.Raise(s, new AgentSafePointChangedEvent(ToolRunning: true, false, false));
        return s;
    }

    [Fact]
    public async Task 閒置時開始排空_全部停下後寫resume_以舊版exe啟動幫手_看到armed才結束()
    {
        using var f = await Fixture.CreateAsync();
        f.Manager.Status.Phase.Should().Be(AppUpdatePhase.Staged);
        var (id, s) = f.H.StartConversation(f.H.Main, f.H.Claude, "做事");
        f.H.ProcessStarted(s, "p1", ClaudeOld);
        s.SafePoint = true;
        s.SucceedRestarts("p");
        f.Shell.ArmWith = f.Layout;
        f.Shell.Popouts.Add(new ResumePopout(id, new[] { 10, 20, 800, 600 }));
        var draftId = f.H.NewConversation(f.H.Main, f.H.Codex);   // 還沒送出的新對話：結束時不能刪它的資料夾（新版要還原它的附件）
        var mark = f.H.Main.Mark();

        f.Tick();
        var request = await f.H.Main.WaitForAsync("draftSnapshotRequest", since: mark, timeoutMs: 5000);
        f.H.Command(f.H.Main, "draftSnapshot", new { requestId = request.Value<string>("requestId"), drafts = new[] { new { conversationId = draftId, text = "還沒送出的第一則" } } });
        await f.H.WaitUntilAsync(() => f.Shell.ExitedWith != null, "交棒後結束");

        f.Shell.ExitedWith.Should().BeEquivalentTo(new[] { draftId }, "resume.json 列出的草稿對話要原樣傳給結束流程（Hub.ShutdownAsync 的 keepDrafts）");
        var helper = f.Shell.Helpers.Should().ContainSingle().Subject;
        helper.Exe.Should().Be(f.Layout.Executable(Current), "幫手用舊版（已證明能跑）的 exe，不經 current");
        helper.Args.Take(5).Should().Equal("--apply-update", "--to", Staged, "--wait-pid", Environment.ProcessId.ToString());
        var token = helper.Args[^1];
        var file = ResumeFileStore.Load(f.H.Temp.Root)!;
        file.Token.Should().Be(token);
        file.From.Should().Be(Current);
        file.To.Should().Be(Staged);
        file.Conversations.Should().ContainSingle().Which.Id.Should().Be(id);
        file.Ui.Popouts.Should().ContainSingle().Which.Bounds.Should().Equal(10, 20, 800, 600);
        var draft = file.Ui.NewConversations.Should().ContainSingle().Subject;
        draft.DraftId.Should().Be(draftId);
        draft.Text.Should().Be("還沒送出的第一則");
        file.Ui.MainHidden.Should().BeFalse("主視窗開著");
        f.Shell.Stopped.Should().BeEmpty("幫手寫了 .armed：交給它換版");
    }

    [Fact]
    public async Task 主視窗藏在系統匣時換版_resume記下主視窗藏著()
    {
        using var f = await Fixture.CreateAsync();
        f.Shell.ArmWith = f.Layout;
        f.Shell.MainWindowHidden = true;   // 關到系統匣或最小化：9.1 最常見的套用時機

        f.Tick();
        await f.H.WaitUntilAsync(() => f.Shell.ExitedWith != null, "交棒後結束");

        ResumeFileStore.Load(f.H.Temp.Root)!.Ui.MainHidden.Should().BeTrue("新版要照樣藏著主視窗，不能自己跳出來");
    }

    [Fact]
    public async Task resume寫不進去_不啟動幫手_就地接續()
    {
        using var f = await Fixture.CreateAsync();
        var (_, s) = f.H.StartConversation(f.H.Main, f.H.Claude, "做事");
        f.H.ProcessStarted(s, "p1", ClaudeOld);
        s.SafePoint = true;
        s.SucceedRestarts("p");
        Directory.CreateDirectory(ResumeFileStore.PathFor(f.H.Temp.Root));   // 同名資料夾擋住：寫檔一定失敗

        f.Tick();
        await f.H.WaitUntilAsync(() => !f.Coordinator.Draining && s.Restarts.All.Count == 2, "放棄並就地接續");

        f.Shell.Helpers.Should().BeEmpty("新版不知道要接續哪些對話，不換版");
        f.Shell.ExitedWith.Should().BeNull();
        f.H.OnUi(() => f.H.Hub.AppUpdateDraining).Should().BeFalse();
        s.Restarts.All.Should().HaveCount(2, "停下、再以目前的 CLI 就地重開接續");
    }

    [Fact]
    public async Task 幫手沒有回應_放棄並就地接續_刪掉resume_六小時後再試()
    {
        using var f = await Fixture.CreateAsync();
        var (_, s) = f.H.StartConversation(f.H.Main, f.H.Claude, "做事");
        f.H.ProcessStarted(s, "p1", ClaudeOld);
        s.SafePoint = true;
        s.SucceedRestarts("p");

        f.Tick();
        await f.H.WaitUntilAsync(() => f.Shell.Helpers.Count == 1 && !f.Coordinator.Draining, "等 .armed 逾時後放棄");

        f.Shell.Stopped.Should().Equal(new[] { 4242 }, "晚到的幫手要先結束，不然它之後會強制結束 app、以「新版啟動失敗」重開舊版");
        f.Shell.ExitedWith.Should().BeNull();
        f.H.OnUi(() => f.H.Hub.AppUpdateDraining).Should().BeFalse();
        s.Restarts.All.Should().HaveCount(2, "停下、再就地重開接續");
        File.Exists(ResumeFileStore.PathFor(f.H.Temp.Root)).Should().BeFalse();

        f.Now = f.Now.AddHours(5).AddMinutes(59);
        f.Tick();
        f.Shell.Helpers.Should().HaveCount(1, "幫手沒回應：6 小時內不再自動開始");
        f.Now = f.Now.AddMinutes(2);
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Shell.Helpers.Count == 2, "6 小時後再試");
    }

    [Fact]
    public async Task 使用者在用就不開始_按立即重啟就開始()
    {
        using var f = await Fixture.CreateAsync();
        f.Shell.AnyWindowForeground = true;
        f.Shell.SinceLastInput = TimeSpan.FromSeconds(3);
        f.Shell.ArmWith = f.Layout;

        f.Tick();
        f.Shell.Helpers.Should().BeEmpty();

        f.H.Command(f.H.Main, "appUpdateRestartNow");
        await f.H.WaitUntilAsync(() => f.Shell.ExitedWith != null, "立即重啟");
    }

    /// <summary>開著 Remote Control 的 Claude 對話：等回應（核准卡沒人回）或工具在執行（遠端開始的一輪、或不會結束的前景指令）</summary>
    private static (string Id, FakeSession Session) StartRemoteControl(Fixture f, bool toolRunning)
    {
        var (id, remote) = f.H.StartConversation(f.H.Main, f.H.Claude, "從手機接手");
        f.H.ProcessStarted(remote, "r1", ClaudeOld);
        remote.HasRemoteControl = true;
        remote.SucceedRestarts("r");
        if (toolRunning)
        {
            f.H.Raise(remote, new AgentToolStartedEvent("t1", "執行指令", "bash", "npm run dev", "npm run dev"));
            f.H.Raise(remote, new AgentSafePointChangedEvent(ToolRunning: true, false, false));
        }
        else
        {
            f.H.Raise(remote, new AgentApprovalEvent("q1", "執行指令", "bash", "git push", null, "t1", CanAllowForSession: true));
            f.H.Raise(remote, new AgentSafePointChangedEvent(false, WaitingForUser: true, false));
        }
        return (id, remote);
    }

    [Fact]
    public async Task RemoteControl的對話剛有動靜_桌面閒置也不開始_橫幅說明原因_10分鐘沒有動靜才開始()
    {
        using var f = await Fixture.CreateAsync();
        var (_, remote) = f.H.StartConversation(f.H.Main, f.H.Claude, "從手機接手");
        f.H.ProcessStarted(remote, "r1", ClaudeOld);
        f.H.CompleteTurn(remote);
        remote.HasRemoteControl = true;
        StartBusyCodex(f);   // 開始排空後不會馬上結束

        f.Tick();
        f.Coordinator.Draining.Should().BeFalse("桌面沒人時使用者可能正在手機或 claude.ai 上用 Remote Control（設計 9.1 條件 1）");
        f.H.OnUi(() => f.H.Hub.AppUpdateDraining).Should().BeFalse();
        var state = f.LastState();
        state.Should().NotBeNull("阻擋原因變了就送狀態");
        state!.Value<string>("phase").Should().Be("staged");
        state.Value<string>("blocker").Should().Be(AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.RemoteInUse),
            "橫幅與設定頁說明為什麼還沒重啟，不再說「閒置時自動重啟套用」");

        f.Now = DateTime.Now + AppUpdateRestartRules.RemoteIdleThreshold;
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "Remote Control 的對話 10 分鐘沒有動靜：開始排空");
    }

    [Fact]
    public async Task RemoteControl的對話在等回應_1小時內不自動開始_按立即重啟就開始()
    {
        using var f = await Fixture.CreateAsync();
        var (id, remote) = StartRemoteControl(f, toolRunning: false);
        StartBusyCodex(f);
        f.Now = DateTime.Now + AppUpdateRestartRules.RemoteWaitThreshold - TimeSpan.FromMinutes(1);

        f.Tick();
        f.Coordinator.Draining.Should().BeFalse("手機上可能正要核准");
        f.LastState().Should().NotBeNull("阻擋原因變了就送狀態");
        f.LastState()!.Value<string>("blocker").Should().Be(AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.RemoteInUse));

        f.H.Command(f.H.Main, "appUpdateRestartNow");
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "按立即重啟等於條件 1 成立");
        f.H.OnUi(() => f.H.Hub.AppUpdateDraining).Should().BeTrue();
        remote.Restarts.Count.Should().Be(0, "排空中開著 Remote Control 的等回應照 CLI 更新等回應（最多 10 分鐘）");
        f.H.OnUi(() => f.H.Hub.RenderHeaderRestartForTest(id)).Value<string>("state").Should().Be("waitingUser");
    }

    [Theory]
    [InlineData(false)]   // 核准卡沒人回（下班前跳出的「執行指令：git push」）
    [InlineData(true)]    // 前景指令一直不結束（誤把開發伺服器開在前景）
    public async Task RemoteControl的對話1小時沒有動靜_等回應或工具在執行也自動開始排空_交給排空的既有規則(bool toolRunning)
    {
        using var f = await Fixture.CreateAsync();
        var (id, remote) = StartRemoteControl(f, toolRunning);
        StartBusyCodex(f);
        f.Now = DateTime.Now + AppUpdateRestartRules.RemoteWaitThreshold - TimeSpan.FromMinutes(1);
        f.Tick();
        f.Coordinator.Draining.Should().BeFalse("1 小時內照樣算在用");

        f.Now = DateTime.Now + AppUpdateRestartRules.RemoteWaitThreshold;
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "1 小時沒有動靜：不能讓自動更新永遠不開始");
        f.H.OnUi(() => f.H.Hub.AppUpdateDraining).Should().BeTrue();
        remote.Restarts.Count.Should().Be(0, toolRunning
            ? "工具照排空的既有規則等它跑完（排空上限 30 分鐘）"
            : "等回應照排空的既有規則：開著 Remote Control 的照 CLI 更新等回應，排空滿 10 分鐘才照立即套用停下");
        f.H.OnUi(() => f.H.Hub.RenderHeaderRestartForTest(id)).Value<string>("state").Should().Be(toolRunning ? "waitingTool" : "waitingUser");
    }

    [Fact]
    public async Task 啟用條件不成立時_RemoteControl的對話在用也不列成阻擋原因()
    {
        using var f = await Fixture.CreateAsync(block: UpdateBlockReason.Disabled);
        var (_, remote) = f.H.StartConversation(f.H.Main, f.H.Claude, "從手機接手");
        f.H.ProcessStarted(remote, "r1", ClaudeOld);
        f.H.CompleteTurn(remote);
        remote.HasRemoteControl = true;

        f.H.Ui.Invoke(() => f.Coordinator.Start());
        f.Tick();
        await f.H.Ui.DrainAsync();

        f.LastState().Should().NotBeNull("Start 送過狀態");
        f.LastState()!.Value<string>("blocker").Should().BeNull("沒有要套用的版本：Remote Control 的對話在用和更新無關，也不必一直重送狀態");
    }

    [Fact]
    public async Task 排空中要求結束_改成結束時套用_不就地接續_resume留給下次啟動()
    {
        using var f = await Fixture.CreateAsync();
        var s = StartBusyCodex(f);
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "開始排空");

        var action = f.H.OnUi(() => f.Coordinator.PrepareExit(windowsShuttingDown: false));
        await f.H.OnUi(() => f.H.Hub.ShutdownAsync());
        await f.H.Ui.DrainAsync();
        f.H.Ui.Invoke(() => f.Coordinator.LaunchExitHelper());

        action.Should().Be(AppExitAction.ApplyOnExit);
        var helper = f.Shell.Helpers.Should().ContainSingle().Subject;
        helper.Args.Should().Contain("--no-relaunch");
        helper.Args.Should().Contain(Staged);
        s.Restarts.Count.Should().Be(0, "結束時不就地接續");
        File.Exists(ResumeFileStore.PathFor(f.H.Temp.Root)).Should().BeTrue("排空開始就寫了，下次啟動依它給「繼續」");
    }

    [Fact]
    public async Task 交棒中要求結束_停掉還沒寫armed的幫手_改由結束時換版的幫手切換_不重新啟動()
    {
        using var f = await Fixture.CreateAsync(armedTimeout: TimeSpan.FromSeconds(10));
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Shell.Helpers.Count == 1, "交棒：啟動幫手、等它寫 .armed");

        var action = f.H.OnUi(() => f.Coordinator.PrepareExit(windowsShuttingDown: false));
        f.H.Ui.Invoke(() => f.Coordinator.LaunchExitHelper());
        await f.H.Ui.DrainAsync();

        action.Should().Be(AppExitAction.ApplyOnExit);
        f.Shell.Stopped.Should().Equal(new[] { 4242 }, "兩個幫手同時切換 current 會互相干擾，先啟動的那個還會在使用者要結束時把 app 重新開起來");
        f.Shell.Helpers.Should().HaveCount(2);
        f.Shell.Helpers[1].Args.Should().Contain("--no-relaunch");
        f.Shell.ExitedWith.Should().BeNull("交棒中止：由使用者的結束流程收尾");
    }

    [Fact]
    public async Task 排空中按取消_就地接續_六小時內不自動套用()
    {
        using var f = await Fixture.CreateAsync();
        StartBusyCodex(f);
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "開始排空");

        f.H.Command(f.H.Main, "appUpdateCancel");

        f.Coordinator.Draining.Should().BeFalse();
        f.H.OnUi(() => f.H.Hub.AppUpdateDraining).Should().BeFalse("還沒停下的照常跑");
        File.Exists(ResumeFileStore.PathFor(f.H.Temp.Root)).Should().BeFalse();
        var state = f.LastState()!;
        state.Value<string>("phase").Should().Be("staged");
        state.Value<string>("blocker").Should().Be(AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.Snoozed));

        f.Now = f.Now.AddHours(5).AddMinutes(59);
        f.Tick();
        f.Coordinator.Draining.Should().BeFalse("取消後 6 小時內不自動套用");
        f.Now = f.Now.AddMinutes(2);
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "6 小時後再自動開始");
    }

    /// <summary>
    /// 2026-10-28 使用者要求桌面版換版一直等到背景工作都完成（以前排空最多等 30 分鐘就停掉；00:14 三個執行中的對話被停下、
    /// 空等另外五個對話的背景工作，使用者只好取消）：有對話的背景工作還在跑就不開始，按「立即重啟」也等，結束就馬上開始（不必等閒置）
    /// </summary>
    [Fact]
    public async Task 有對話的背景工作在跑_不開始排空_按立即重啟也等_背景工作結束就馬上開始()
    {
        using var f = await Fixture.CreateAsync();
        var (_, scraper) = f.H.StartConversation(f.H.Main, f.H.Claude, "背景擷取");
        f.H.ProcessStarted(scraper, "s1", ClaudeOld);
        f.H.CompleteTurn(scraper);
        scraper.HasLiveBackgroundWork = true;
        StartBusyCodex(f);   // 開始排空後不會馬上結束

        f.Tick();
        f.Coordinator.Draining.Should().BeFalse("重啟會停掉背景工作");
        f.H.OnUi(() => f.H.Hub.AppUpdateDraining).Should().BeFalse("沒有任何對話被停下");
        f.LastState()!.Value<string>("blocker").Should().Be(AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.BackgroundWork));

        f.H.Command(f.H.Main, "appUpdateRestartNow");
        await f.H.Ui.DrainAsync();
        f.Tick();
        f.Coordinator.Draining.Should().BeFalse("立即重啟也等背景工作都結束");
        scraper.Restarts.Count.Should().Be(0);

        f.Now = f.Now.AddHours(3);   // 不論等多久都不停掉背景工作
        f.Tick();
        f.Coordinator.Draining.Should().BeFalse();

        f.Shell.AnyWindowForeground = true;   // 使用者在用：按過立即重啟的照樣開始
        f.Shell.SinceLastInput = TimeSpan.FromSeconds(3);
        scraper.HasLiveBackgroundWork = false;
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "背景工作都結束：按過立即重啟的馬上開始");
    }

    /// <summary>
    /// 排空開始時沒有背景工作（阻擋原因擋著），排空中才有對話開始背景工作：放棄這次、已停下的就地接續，背景工作不停掉；
    /// 背景工作都結束、5 分鐘的冷卻過了再開始（不然同一個對話又開背景工作時，執行中的對話每次都被停下又接續）
    /// </summary>
    [Fact]
    public async Task 排空中有對話開始背景工作_放棄這次_已停下的就地接續_背景工作結束且冷卻過後再開始()
    {
        using var f = await Fixture.CreateAsync();
        var (_, held) = f.H.StartConversation(f.H.Main, f.H.Claude, "寫程式");
        f.H.ProcessStarted(held, "a1", ClaudeOld);
        held.SafePoint = true;
        held.SucceedRestarts("a");
        var busy = StartBusyCodex(f);
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "開始排空");
        await f.H.WaitUntilAsync(() => held.Restarts.Count == 1, "執行中的對話到安全點停下");

        busy.HasLiveBackgroundWork = true;   // 排空中把指令讓出成背景工作
        f.Tick();
        f.Coordinator.Draining.Should().BeFalse("放棄這次：不停掉背景工作，也不讓先停下的對話一直等");
        f.H.OnUi(() => f.H.Hub.AppUpdateDraining).Should().BeFalse();
        await f.H.WaitUntilAsync(() => held.Restarts.Count == 2, "已停下的就地接續");
        held.Restarts.All[1].AutoContinue.Should().BeTrue();
        busy.Restarts.Count.Should().Be(0, "背景工作沒有被停掉");
        File.Exists(ResumeFileStore.PathFor(f.H.Temp.Root)).Should().BeFalse();
        f.LastState()!.Value<string>("blocker").Should().Be(AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.BackgroundWork),
            "說明在等背景工作，不是「稍後再試」");

        busy.HasLiveBackgroundWork = false;
        f.Tick();
        f.Coordinator.Draining.Should().BeFalse("放棄後 5 分鐘內不再自動開始");
        f.LastState()!.Value<string>("blocker").Should().Be(AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.RetryLater));

        f.Now += AppUpdateRestartRules.RetryAfterBackground;
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "背景工作都結束、冷卻過後再開始");
    }

    /// <summary>
    /// 「強制更新」（2026-10-28 使用者要求）：有背景工作也馬上開始排空（不等閒置），排空中再開始背景工作也不放棄；
    /// 網頁的狀態帶 forced 與幾點起強制停下；取消就回到一般（之後的自動套用照樣等背景工作）
    /// </summary>
    [Fact]
    public async Task 強制更新_有背景工作也馬上開始排空_排空中出現背景工作不放棄_取消後回到一般()
    {
        using var f = await Fixture.CreateAsync();
        var (_, scraper) = f.H.StartConversation(f.H.Main, f.H.Claude, "背景擷取");
        f.H.ProcessStarted(scraper, "s1", ClaudeOld);
        f.H.CompleteTurn(scraper);
        scraper.HasLiveBackgroundWork = true;
        var busy = StartBusyCodex(f);
        f.Shell.AnyWindowForeground = true;   // 使用者在用：強制更新照樣開始
        f.Shell.SinceLastInput = TimeSpan.FromSeconds(3);

        f.Tick();
        f.Coordinator.Draining.Should().BeFalse("背景工作還在跑");
        f.H.Command(f.H.Main, "appUpdateForce");
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "強制更新：不等背景工作就開始排空");
        await f.H.Ui.DrainAsync();
        var state = f.LastState()!;
        state.Value<string>("phase").Should().Be("draining");
        state.Value<bool?>("forced").Should().BeTrue();
        state.Value<long?>("forceAt").Should().NotBeNull("網頁說明幾點起強制停下");
        scraper.Restarts.Count.Should().Be(0, "背景工作照常跑，滿 10 分鐘才停（中樞的規則）");

        busy.HasLiveBackgroundWork = true;   // 排空中又有對話開始背景工作
        f.Tick();
        f.Coordinator.Draining.Should().BeTrue("強制更新不因背景工作放棄");

        f.H.Command(f.H.Main, "appUpdateForce");   // 再按一次不重設
        await f.H.Ui.DrainAsync();
        f.Coordinator.Draining.Should().BeTrue();

        f.H.Command(f.H.Main, "appUpdateCancel");
        f.Coordinator.Draining.Should().BeFalse();
        f.LastState()!.Value<bool?>("forced").Should().BeNull();
        f.Now = f.Now + AppUpdateRestartRules.SnoozeAfterCancel;
        f.Shell.AnyWindowForeground = false;
        f.Tick();
        f.Coordinator.Draining.Should().BeFalse("取消後回到一般：照樣等背景工作都結束");
        f.LastState()!.Value<string>("blocker").Should().Be(AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.BackgroundWork));
    }

    [Fact]
    public async Task 一般排空中按強制更新_改成強制_不因背景工作放棄()
    {
        using var f = await Fixture.CreateAsync();
        var busy = StartBusyCodex(f);
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "開始排空");
        f.LastState()!.Value<bool?>("forced").Should().BeNull();

        f.H.Command(f.H.Main, "appUpdateForce");
        await f.H.Ui.DrainAsync();
        f.LastState()!.Value<bool?>("forced").Should().BeTrue();
        f.H.OnUi(() => f.H.Hub.AppUpdateForceAt).Should().NotBeNull();

        busy.HasLiveBackgroundWork = true;
        f.Tick();
        f.Coordinator.Draining.Should().BeTrue("已經強制：不放棄");
    }

    [Fact]
    public async Task 取消前那次排空的接續晚到_不放棄緊接著開始的新排空()
    {
        using var f = await Fixture.CreateAsync();
        StartBusyCodex(f);
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "開始排空");

        // 取消與冷卻到期後的 Tick 在同一次 UI 執行緒呼叫裡：舊排空等的屏障以 RunContinuationsAsynchronously 完成，
        // 它的接續一定排在新排空開始之後（慢的機器上真的會這樣：GitHub 的 4 核機器上「6 小時後再自動開始」等不到）
        f.H.Ui.Invoke(() =>
        {
            f.Coordinator.CancelRestart();
            f.Now += AppUpdateRestartRules.SnoozeAfterCancel + TimeSpan.FromMinutes(1);
            f.Coordinator.Tick();
        });
        f.Coordinator.Draining.Should().BeTrue("冷卻到期後再自動開始");

        await Task.Delay(200);
        await f.H.Ui.DrainAsync();
        f.Coordinator.Draining.Should().BeTrue("取消前那次排空的接續不能把這一次放棄掉");
        f.H.OnUi(() => f.H.Hub.AppUpdateDraining).Should().BeTrue();
    }

    [Fact]
    public async Task 排空超過35分鐘_放棄_一小時後再試_冷卻到期時橫幅跟著更新()
    {
        using var f = await Fixture.CreateAsync();
        StartBusyCodex(f);
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "開始排空");

        f.Now = f.Now.AddMinutes(34);
        f.Tick();
        f.Coordinator.Draining.Should().BeTrue("排空上限 35 分鐘（背景工作最多等 30 分鐘，留 5 分鐘讓它停下）");
        f.Now = f.Now.AddMinutes(1);
        f.Tick();
        f.Coordinator.Draining.Should().BeFalse("排空上限 35 分鐘");
        f.H.OnUi(() => f.H.Hub.AppUpdateDraining).Should().BeFalse();
        f.LastState()!.Value<string>("blocker").Should().Be(AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.RetryLater));

        f.Shell.AnyWindowForeground = true;   // 使用者回來了：冷卻到期也不開始，但橫幅不能停在「稍後再試」
        f.Shell.SinceLastInput = TimeSpan.FromSeconds(3);
        f.Now = f.Now.AddMinutes(61);
        f.Tick();
        f.Coordinator.Draining.Should().BeFalse();
        f.LastState()!.Value<string>("blocker").Should().BeNull("冷卻到期");

        f.Shell.AnyWindowForeground = false;
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "1 小時後、使用者沒在用時再試");
    }

    // ───────────────────────────── 新版自動接手舊版（交棒） ─────────────────────────────

    private const string NewerRelease = "0.2.9-ccccccc";
    private const string NewerExe = @"C:\Downloads\myriagent-0.2.9\Myriagent.exe";

    /// <summary>交棒要先拿草稿快照：回一份空的，不必等逾時</summary>
    private static async Task AnswerDraftSnapshotAsync(Fixture f, int mark)
    {
        var request = await f.H.Main.WaitForAsync("draftSnapshotRequest", since: mark, timeoutMs: 5000);
        f.H.Command(f.H.Main, "draftSnapshot", new { requestId = request.Value<string>("requestId"), drafts = Array.Empty<object>() });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 較新的版本請求交棒_開發版也照排空流程_寫resume後以handed_over啟動新版_不經幫手(bool usesDefaultData)
    {
        using var f = await Fixture.CreateAsync(managed: false, usesDefaultData: usesDefaultData);
        var (id, s) = f.H.StartConversation(f.H.Main, f.H.Claude, "做事");
        f.H.ProcessStarted(s, "p1", ClaudeOld);
        s.SafePoint = true;
        s.SucceedRestarts("p");
        f.Shell.MainWindowHidden = true;   // 系統匣裡的舊版：使用者剛開了新版，新版照樣要顯示
        var mark = f.H.Main.Mark();

        f.H.Ui.Invoke(() => f.Coordinator.BeginHandover(NewerExe, NewerRelease));
        await AnswerDraftSnapshotAsync(f, mark);
        await f.H.WaitUntilAsync(() => f.Shell.HandedOver.Count == 1, "交棒給新版");

        f.Shell.ShowMainCount.Should().Be(1, "使用者剛開了新版：舊版的視窗叫出來顯示進度");
        f.Shell.Helpers.Should().BeEmpty("交棒不經換版幫手");
        f.Shell.ExitedWith.Should().BeNull("結束由 HandOver 自己收尾");
        var handed = f.Shell.HandedOver.Single();
        handed.Exe.Should().Be(NewerExe);
        handed.Args[0].Should().Be("--handed-over");
        var token = handed.Args[1];
        if (usesDefaultData)
            handed.Args.Should().HaveCount(2);
        else
            handed.Args.Skip(2).Should().Equal("--data", f.H.Temp.Root);
        var file = ResumeFileStore.Load(f.H.Temp.Root)!;
        file.Token.Should().Be(token);
        file.From.Should().Be(AppVersionInfo.Current.Release, "開發版也能交棒：From 是自己的 release（build 是 dev）");
        file.To.Should().Be(NewerRelease);
        file.Conversations.Should().ContainSingle().Which.Id.Should().Be(id);
        file.Ui.MainHidden.Should().BeFalse("交棒是使用者剛開了新版，不藏主視窗");
        var state = f.LastState()!;
        state.Value<string>("phase").Should().Be("restarting");
        state.Value<string>("handoverTo").Should().Be("0.2.9");
    }

    [Fact]
    public async Task 交棒排空中的狀態_橫幅說明切換到新開的版本()
    {
        using var f = await Fixture.CreateAsync(managed: false);
        StartBusyCodex(f);

        f.H.Ui.Invoke(() => f.Coordinator.BeginHandover(NewerExe, NewerRelease));
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "開始排空");

        var state = f.LastState()!;
        state.Value<string>("phase").Should().Be("draining");
        state.Value<string>("handoverTo").Should().Be("0.2.9");
        state.Value<int>("waiting").Should().Be(1, "執行中的 Codex 還沒停下");
    }

    [Fact]
    public async Task 交棒排空中按取消_就地接續_通知_不影響app自動更新的排程()
    {
        using var f = await Fixture.CreateAsync();   // 受管理、有已備妥的新版
        StartBusyCodex(f);
        f.H.Ui.Invoke(() => f.Coordinator.BeginHandover(NewerExe, NewerRelease));
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "開始排空");

        f.H.Command(f.H.Main, "appUpdateCancel");

        f.Coordinator.Draining.Should().BeFalse();
        f.H.OnUi(() => f.H.Hub.AppUpdateDraining).Should().BeFalse("還沒停下的照常跑");
        File.Exists(ResumeFileStore.PathFor(f.H.Temp.Root)).Should().BeFalse();
        f.Shell.Notices.Should().ContainSingle().Which.Message.Should().Contain("沒有切換到新開的 0.2.9");
        var state = f.LastState()!;
        state.Value<string>("handoverTo").Should().BeNull();
        state.Value<string>("blocker").Should().BeNull("取消交棒不是取消 app 更新：沒有 6 小時的暫停");
        f.Shell.HandedOver.Should().BeEmpty();

        f.Tick();
        f.Coordinator.Draining.Should().BeTrue("app 自動更新照常：閒置就開始套用已備妥的版本");
        f.LastState()!.Value<string>("handoverTo").Should().BeNull();
    }

    [Fact]
    public async Task 正在套用更新時收到交棒請求_不交棒_通知說明()
    {
        using var f = await Fixture.CreateAsync();
        StartBusyCodex(f);
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Coordinator.Draining, "開始套用更新的排空");

        f.H.Ui.Invoke(() => f.Coordinator.BeginHandover(NewerExe, NewerRelease));

        f.Shell.Notices.Should().ContainSingle().Which.Message.Should().Contain("正在套用更新");
        f.Shell.ShowMainCount.Should().Be(1);
        f.Coordinator.Draining.Should().BeTrue("套用更新照常進行");
        f.LastState()!.Value<string>("handoverTo").Should().BeNull();
    }

    [Fact]
    public async Task 新版啟動不了_就地接續_刪resume_通知()
    {
        using var f = await Fixture.CreateAsync(managed: false);
        var (_, s) = f.H.StartConversation(f.H.Main, f.H.Claude, "做事");
        f.H.ProcessStarted(s, "p1", ClaudeOld);
        s.SafePoint = true;
        s.SucceedRestarts("p");
        f.Shell.HandOverSucceeds = false;
        var mark = f.H.Main.Mark();

        f.H.Ui.Invoke(() => f.Coordinator.BeginHandover(NewerExe, NewerRelease));
        await AnswerDraftSnapshotAsync(f, mark);
        await f.H.WaitUntilAsync(() => f.Shell.HandedOver.Count == 1 && !f.Coordinator.Draining, "啟動失敗後放棄");
        await f.H.WaitUntilAsync(() => s.Restarts.All.Count == 2, "停下的對話就地接續");

        File.Exists(ResumeFileStore.PathFor(f.H.Temp.Root)).Should().BeFalse();
        f.Shell.Notices.Should().ContainSingle().Which.Message.Should().Contain("沒有切換到新開的 0.2.9");
        f.LastState()!.Value<string>("phase").Should().NotBe("restarting");
    }

    [Fact]
    public async Task 以handed_over啟動_照resume接續_通知寫已切換到_不寫健康訊號()
    {
        using var f = await Fixture.CreateAsync();
        var token = StartupOptions.NewToken();
        ResumeFileStore.Save(f.H.Temp.Root, new AppResumeFile { Token = token, From = "0.2.0-dev", To = Current, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });

        f.H.Ui.Invoke(() => f.Coordinator.ScheduleStartupResume(new StartupOptions { HandedOverToken = token }, AppUpdateCoordinator.LoadStartupResume(f.H.Temp.Root)));
        f.H.Command(f.H.Main, "ready");
        await f.H.WaitUntilAsync(() => f.Shell.Notices.Count > 0, "接續送出後通知");

        f.Shell.Notices.Single().Message.Should().Be($"已切換到 {AppVersionInfo.VersionText}");
        File.Exists(ResumeFileStore.PathFor(f.H.Temp.Root)).Should().BeFalse();
        File.Exists(f.Layout.HealthFile(token)).Should().BeFalse("沒有換版幫手在等健康訊號");
    }

    [Fact]
    public async Task 換版後啟動_交給中樞接續_主視窗就緒時寫健康訊號_接續送出後刪resume並通知()
    {
        using var f = await Fixture.CreateAsync();
        var token = StartupOptions.NewToken();
        var file = new AppResumeFile { Token = token, From = "0.2.0-0000000", To = Current, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        file.Ui.Popouts.Add(new ResumePopout("c-pop", null));
        ResumeFileStore.Save(f.H.Temp.Root, file);

        f.H.Ui.Invoke(() => f.Coordinator.ScheduleStartupResume(new StartupOptions { AfterUpdateToken = token }, AppUpdateCoordinator.LoadStartupResume(f.H.Temp.Root)));
        f.H.Command(f.H.Main, "ready");
        await f.H.WaitUntilAsync(() => f.Shell.Notices.Count > 0, "接續送出後通知");

        File.Exists(f.Layout.HealthFile(token)).Should().BeTrue();
        File.Exists(ResumeFileStore.PathFor(f.H.Temp.Root)).Should().BeFalse();
        f.Shell.Notices.Single().Message.Should().Be($"已更新到 {AppVersionInfo.VersionText}", "沒有對話要接續就不加「N 個對話已自動接續」");
        f.Shell.Opened.Should().ContainSingle().Which.ConversationId.Should().Be("c-pop");
    }

    [Fact]
    public async Task 接續送出時resume被別的程式開著_之後的Tick再刪()
    {
        using var f = await Fixture.CreateAsync();
        f.Shell.AnyWindowForeground = true;   // 使用者在用：這次 Tick 不開始排空（排空會用新 token 重寫 resume.json）
        f.Shell.SinceLastInput = TimeSpan.FromSeconds(3);
        var token = StartupOptions.NewToken();
        ResumeFileStore.Save(f.H.Temp.Root, new AppResumeFile { Token = token, From = "0.2.0-0000000", To = Current, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        var path = ResumeFileStore.PathFor(f.H.Temp.Root);

        // 防毒、備份軟體開著檔而且沒給 FILE_SHARE_DELETE：OnResumeDispatched 刪不掉
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            f.H.Ui.Invoke(() => f.Coordinator.ScheduleStartupResume(new StartupOptions { AfterUpdateToken = token }, AppUpdateCoordinator.LoadStartupResume(f.H.Temp.Root)));
            f.H.Command(f.H.Main, "ready");
            await f.H.WaitUntilAsync(() => f.Shell.Notices.Count > 0, "接續送出後通知");
            File.Exists(path).Should().BeTrue("被開著：這次刪不掉");
        }

        f.Tick();
        File.Exists(path).Should().BeFalse("不然下次一般啟動會把已接續的對話當成上次中斷");
    }

    [Fact]
    public async Task 帶token啟動時resume被鎖住讀不到_草稿資料夾與resume都留著_通知說明這次沒有接續()
    {
        using var f = await Fixture.CreateAsync();
        f.Shell.AnyWindowForeground = true;   // 使用者在用：Tick 不開始排空（排空會用新 token 重寫 resume.json）
        f.Shell.SinceLastInput = TimeSpan.FromSeconds(3);
        var token = StartupOptions.NewToken();
        var draftId = Guid.NewGuid().ToString("N");
        var file = new AppResumeFile { Token = token, From = "0.2.0-0000000", To = Current, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        file.Ui.NewConversations.Add(new ResumeNewConversation(draftId, "acc", null, "還沒送出的第一則", new List<AgentAttachment>()));
        ResumeFileStore.Save(f.H.Temp.Root, file);
        // 還沒送出的新對話的資料夾（只有附件）：啟動清理會當成上次沒送出的草稿殘留
        var draftDir = Path.Combine(f.H.Store.RootDirectory, draftId);
        Directory.CreateDirectory(Path.Combine(draftDir, "attachments"));
        var path = ResumeFileStore.PathFor(f.H.Temp.Root);

        // 新版啟動的那一刻，備份或防毒軟體不共享讀取地開著 resume.json，比啟動時的重試還久
        StartupResume startup;
        IReadOnlyList<string> leftovers;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            startup = AppUpdateCoordinator.LoadStartupResume(f.H.Temp.Root, retryFor: TimeSpan.FromMilliseconds(300));
            leftovers = ResumeIntentRules.DraftLeftoversToDelete(f.H.Store.FindDraftLeftovers(), startup);   // AppController 的啟動清理
        }
        f.H.Ui.Invoke(() => f.Coordinator.ScheduleStartupResume(new StartupOptions { AfterUpdateToken = token }, startup));
        f.H.Command(f.H.Main, "ready");
        await f.H.WaitUntilAsync(() => f.Shell.Notices.Count > 0, "接續送出後通知");
        f.Tick();   // 也不能當成「刪不掉、等 Tick 再刪」

        File.Exists(path).Should().BeTrue("沒讀過的 resume.json 不刪：留給下次啟動，停下的對話以 token 不符給「繼續」");
        ResumeFileStore.Load(f.H.Temp.Root)!.Token.Should().Be(token);
        leftovers.Should().NotContain(draftId, "不知道哪些草稿要留給新版還原：這次不清");
        Directory.Exists(draftDir).Should().BeTrue();
        f.Shell.Notices.Single().Message.Should().Contain("沒有自動接續", "停下的對話這次看起來像閒置：要說一聲");
        File.Exists(f.Layout.HealthFile(token)).Should().BeTrue("健康訊號照寫：新版本身沒有問題");
    }

    [Fact]
    public async Task 幫手放棄後以失敗旗標啟動_備妥還在_六小時內不自動再排空_不寫健康訊號()
    {
        using var f = await Fixture.CreateAsync();   // updater-state 的 Staged 仍是 Staged：幫手走 Aborted、OldProcessHung 時不清
        var token = StartupOptions.NewToken();
        var retryLater = AppUpdateRestartRules.DescribeBlocker(AppRestartBlocker.RetryLater);

        f.H.Ui.Invoke(() => f.Coordinator.ScheduleStartupResume(new StartupOptions { AfterUpdateFailedToken = token }, StartupResume.None));
        f.H.Command(f.H.Main, "ready");
        await f.H.WaitUntilAsync(() => f.H.Main.Last("appUpdate")?["state"]?.Value<string>("blocker") == retryLater, "設了 6 小時的冷卻");

        f.Tick();   // 使用者閒置（FakeShell 預設：沒有視窗在前景、10 分鐘沒輸入）
        f.Coordinator.Draining.Should().BeFalse("幫手剛放棄：6 小時內不自動再排空");
        f.Shell.Helpers.Should().BeEmpty();
        File.Exists(f.Layout.HealthFile(token)).Should().BeFalse("退回後的啟動沒有幫手在等健康訊號");

        f.Now = f.Now.AddHours(6).AddMinutes(1);
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Shell.Helpers.Count == 1, "6 小時後再試");
    }

    [Fact]
    public async Task 健康閘門回滾後以失敗旗標啟動_備妥已清_不設冷卻()
    {
        using var f = await Fixture.CreateAsync();
        // 閘門失敗的回滾（OnGateFailed）清掉 Staged：下一次檢查再備妥同一版就要能馬上再試（第二次失敗才列壞版）
        UpdaterStateStore.Update(f.Layout, s =>
        {
            s.Staged = null;
            return s;
        });

        f.H.Ui.Invoke(() => f.Coordinator.ScheduleStartupResume(new StartupOptions { AfterUpdateFailedToken = StartupOptions.NewToken() }, StartupResume.None));
        f.H.Command(f.H.Main, "ready");
        await f.H.WaitUntilAsync(() => f.Manager.Status.StagedRelease == null, "主視窗就緒時重讀 updater-state");
        await f.H.Ui.DrainAsync();   // 重讀後排回 UI 執行緒的狀態更新
        f.H.Main.Last("appUpdate").Should().NotBeNull();
        f.H.Main.Last("appUpdate")?["state"]?.Value<string>("blocker").Should().BeNull("沒有冷卻");

        // 下一次檢查又備妥同一版：閒置就馬上開始
        UpdaterStateStore.Update(f.Layout, s => UpdaterStateRules.OnStaged(s, Staged));
        f.Manager.RefreshFromState();
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Shell.Helpers.Count == 1, "沒有冷卻：閒置就再試");
    }

    [Fact]
    public async Task 以失敗旗標啟動_備妥還在_主視窗就緒前的Tick不開始排空()
    {
        using var f = await Fixture.CreateAsync();   // Staged 還在：幫手走 Aborted、OldProcessHung 時不清
        f.H.Ui.Invoke(() => f.Coordinator.ScheduleStartupResume(new StartupOptions { AfterUpdateFailedToken = StartupOptions.NewToken() }, StartupResume.None));

        f.Tick();   // WebView2 比第一次 Tick（Normal 15 秒、E2E 1 秒）慢就緒：主視窗還沒送 ready；使用者閒置（FakeShell 預設）
        await f.H.Ui.DrainAsync();

        f.Coordinator.Draining.Should().BeFalse("幫手剛放棄：不能趁主視窗還沒就緒就再排空、再交棒（反覆失敗的迴圈）");
        f.H.OnUi(() => f.H.Hub.AppUpdateDraining).Should().BeFalse();
        f.Shell.Helpers.Should().BeEmpty();
    }

    [Fact]
    public async Task 一般啟動讀到上次排空留下的resume_主視窗就緒前不開始新的排空_就緒後新的resume不被舊的接續刪掉()
    {
        using var f = await Fixture.CreateAsync();
        f.Shell.ArmWith = f.Layout;
        var old = new AppResumeFile { Token = StartupOptions.NewToken(), From = Current, To = Staged, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        ResumeFileStore.Save(f.H.Temp.Root, old);
        var path = ResumeFileStore.PathFor(f.H.Temp.Root);
        f.H.Ui.Invoke(() => f.Coordinator.ScheduleStartupResume(new StartupOptions(), AppUpdateCoordinator.LoadStartupResume(f.H.Temp.Root)));

        f.Tick();   // 主視窗還沒送 ready：上次中斷的對話還沒交給中樞
        await f.H.Ui.DrainAsync();
        f.Coordinator.Draining.Should().BeFalse("就緒前開始的排空會用新 token 覆寫 resume.json，就緒時舊的接續送出又把它刪掉");
        ResumeFileStore.Load(f.H.Temp.Root)!.Token.Should().Be(old.Token, "上次中斷的紀錄還沒交給中樞");

        f.H.Command(f.H.Main, "ready");
        await f.H.WaitUntilAsync(() => !File.Exists(path), "舊的接續送出後刪掉舊的 resume.json");
        f.Tick();
        await f.H.WaitUntilAsync(() => f.Shell.ExitedWith != null, "就緒之後照常排空、交棒");

        var file = ResumeFileStore.Load(f.H.Temp.Root);
        file.Should().NotBeNull("新的排空寫的 resume.json 要留給新版接續");
        file!.Token.Should().Be(f.Shell.Helpers.Single().Args[^1]);
    }

    [Fact]
    public async Task 閘門回滾後以失敗旗標啟動_就緒前檢查又備妥同一版_不設冷卻()
    {
        using var f = await Fixture.CreateAsync();
        // 閘門失敗的回滾（OnGateFailed）清掉 Staged，再以 --after-update-failed 啟動舊版
        UpdaterStateStore.Update(f.Layout, s =>
        {
            s.Staged = null;
            return s;
        });
        f.H.Ui.Invoke(() => f.Coordinator.ScheduleStartupResume(new StartupOptions { AfterUpdateFailedToken = StartupOptions.NewToken() }, StartupResume.None));

        // 主視窗就緒前，第一次檢查（E2E 在 1 秒後）又下載並備妥同一版：還沒列壞版（同一版第二次閘門失敗才列）
        UpdaterStateStore.Update(f.Layout, s => UpdaterStateRules.OnStaged(s, Staged));
        f.Manager.RefreshFromState();
        await f.H.Ui.DrainAsync();

        var mark = f.H.Main.Mark();
        f.H.Command(f.H.Main, "ready");
        await f.H.Main.WaitForAsync("appUpdate", since: mark);
        await f.H.Ui.DrainAsync();
        var last = f.H.Main.Last("appUpdate");
        last.Should().NotBeNull();
        last!["state"]!.Value<string>("blocker").Should().BeNull("這份備妥是重新檢查得到的，不是幫手沒消耗的那份：不設冷卻（否則第二次閘門失敗永遠等不到）");

        f.Tick();
        await f.H.WaitUntilAsync(() => f.Shell.Helpers.Count == 1, "閒置就再試");
    }

    [Fact]
    public async Task 退回舊版後啟動_通知說明哪一版啟動失敗()
    {
        using var f = await Fixture.CreateAsync();
        var token = StartupOptions.NewToken();
        var file = new AppResumeFile { Token = token, From = Current, To = Staged, CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };
        ResumeFileStore.Save(f.H.Temp.Root, file);

        f.H.Ui.Invoke(() => f.Coordinator.ScheduleStartupResume(new StartupOptions { AfterUpdateFailedToken = token }, AppUpdateCoordinator.LoadStartupResume(f.H.Temp.Root)));
        f.H.Command(f.H.Main, "ready");
        await f.H.WaitUntilAsync(() => f.Shell.Notices.Count > 0, "接續送出後通知");

        f.Shell.Notices.Single().Message.Should().Be($"新版 0.2.2 啟動失敗，已退回 {AppVersionInfo.VersionText}", "設計 8.5 第 6 步「新版 X 啟動失敗，已退回 Y」");
        File.Exists(ResumeFileStore.PathFor(f.H.Temp.Root)).Should().BeFalse();
    }

    [Fact]
    public async Task 檢查進行中結束_已備妥的版本照樣在結束時套用()
    {
        var source = new PendingSource();
        using var f = await Fixture.CreateAsync(source);
        var check = f.Manager.CheckAsync();
        try
        {
            await f.H.WaitUntilAsync(() => f.Manager.Status.Phase == AppUpdatePhase.Checking, "檢查開始");
            f.Manager.Status.StagedRelease.Should().Be(Staged, "檢查較新版的期間，已備妥的版本仍在");

            var action = f.H.OnUi(() => f.Coordinator.PrepareExit(windowsShuttingDown: false));
            f.H.Ui.Invoke(() => f.Coordinator.LaunchExitHelper());

            action.Should().Be(AppExitAction.ApplyOnExit, "設計 8.5「有已備妥的版本」就在結束時換版");
            var helper = f.Shell.Helpers.Should().ContainSingle().Subject;
            helper.Args.Should().Contain(Staged).And.Contain("--no-relaunch");
        }
        finally
        {
            source.Latest.SetResult(null);
            await check;
        }
    }

    [Fact]
    public async Task 關掉自動更新時結束_不換版()
    {
        using var f = await Fixture.CreateAsync();
        await f.Manager.CheckAsync();   // 沒有來源：Disabled
        f.Manager.Status.Phase.Should().Be(AppUpdatePhase.Disabled);
        f.Manager.Status.StagedRelease.Should().Be(Staged, "備妥的版本還在");

        f.H.OnUi(() => f.Coordinator.PrepareExit(windowsShuttingDown: false)).Should().Be(AppExitAction.Plain);
    }

    [Theory]
    [InlineData(nameof(UpdateBlockReason.Disabled))]
    [InlineData(nameof(UpdateBlockReason.CustomData))]
    public async Task 啟用條件不成立_啟動時已有備妥的版本_閒置也不排空_結束時也不換版(string reason)
    {
        // 自動更新開著時備妥、之後關掉再啟動，或從 current 以 --data 執行：第一次檢查（1 分鐘後）之前的 Tick 不能開始排空
        using var f = await Fixture.CreateAsync(block: Enum.Parse<UpdateBlockReason>(reason));
        f.Manager.Status.StagedRelease.Should().Be(Staged);

        f.Tick();
        await f.H.Ui.DrainAsync();

        f.Coordinator.Draining.Should().BeFalse("設計 8.2：啟用條件都成立才自動更新");
        f.Shell.Helpers.Should().BeEmpty();
        File.Exists(ResumeFileStore.PathFor(f.H.Temp.Root)).Should().BeFalse();
        f.H.OnUi(() => f.Coordinator.PrepareExit(windowsShuttingDown: false)).Should().Be(AppExitAction.Plain);
    }

    [Fact]
    public async Task 檢查進行中關掉自動更新_結束時不套用已備妥的版本()
    {
        var source = new PendingSource();
        using var f = await Fixture.CreateAsync(source);
        var check = f.Manager.CheckAsync();
        try
        {
            await f.H.WaitUntilAsync(() => f.Manager.Status.Phase == AppUpdatePhase.Checking, "檢查開始");
            f.Block = UpdateBlockReason.Disabled;   // 設定頁關掉自動更新：AgentHub 呼叫的 Check() 併進正在跑的這次

            f.H.OnUi(() => f.Coordinator.PrepareExit(windowsShuttingDown: false)).Should().Be(AppExitAction.Plain, "關掉自動更新就不在結束時換版");
        }
        finally
        {
            source.Latest.SetResult(null);
            await check;
        }
        f.Manager.Status.Phase.Should().Be(AppUpdatePhase.Disabled, "併進的那次檢查結束時照樣反映啟用條件");
    }

    [Fact]
    public async Task 狀態送到網頁_帶版本與階段()
    {
        using var f = await Fixture.CreateAsync();
        f.H.Ui.Invoke(() => f.Coordinator.Start());
        f.H.Command(f.H.Main, "ready");
        var init = f.H.Main.Last("init")!;
        var state = init["appUpdate"]!;
        state.Value<string>("phase").Should().Be("staged");
        state.Value<string>("staged").Should().Be("0.2.2");
        state.Value<bool>("managed").Should().BeTrue();
        state.Value<bool>("canInstall").Should().BeFalse("已經從安裝位置執行");
        init["settings"]!.Value<bool>("autoUpdateApp").Should().BeTrue("預設開");
        // 版號前面的日期時間：init.app 與 AppUpdateView 都帶這一版的提交時間（Unix 毫秒；在 repo 裡建置一定有）
        AppVersionInfo.CommitAtMs.Should().NotBeNull();
        state.Value<long?>("commitAt").Should().Be(AppVersionInfo.CommitAtMs);
        init["app"]!.Value<long?>("commitAt").Should().Be(AppVersionInfo.CommitAtMs);
    }

    [Fact]
    public async Task 關掉自動更新app_存進設定並立即重新檢查()
    {
        using var f = await Fixture.CreateAsync();
        f.H.Command(f.H.Main, "setting", new { key = "autoUpdateApp", value = false });

        f.H.Settings.AutoUpdateApp.Should().BeFalse();
        f.H.Main.Last("settings")!["settings"]!.Value<bool>("autoUpdateApp").Should().BeFalse();
        await f.H.WaitUntilAsync(() => f.Manager.Status.Phase == AppUpdatePhase.Disabled, "改了設定就重新檢查（這裡沒有來源：停用）");
    }

    [Fact]
    public async Task 非預設資料夾_不提供也不執行安裝到這台電腦()
    {
        // 對照組：還沒安裝、不是從安裝位置執行，用預設資料夾時提供（開發版本來就不提供）
        using (var withDefaultData = await Fixture.CreateAsync(managed: false, usesDefaultData: true))
        {
            withDefaultData.H.Ui.Invoke(() => withDefaultData.Coordinator.Start());
            withDefaultData.H.Command(withDefaultData.H.Main, "ready");
            withDefaultData.H.Main.Last("init")!["appUpdate"]!.Value<bool>("canInstall").Should().Be(!AppVersionInfo.Current.IsDev);
        }

        using var f = await Fixture.CreateAsync(managed: false, usesDefaultData: false);
        // 萬一閘門失效，安裝在第一步（建立 versions 資料夾）就失敗：不會複製檔案，更不會建立真的開始選單捷徑與移除項目
        f.H.Temp.WriteFile(Path.Combine("Programs", "Myriagent", "versions"), "擋住");
        f.H.Ui.Invoke(() => f.Coordinator.Start());
        f.H.Command(f.H.Main, "ready");
        f.H.Main.Last("init")!["appUpdate"]!.Value<bool>("canInstall").Should().BeFalse("--data、MYRIAGENT_AGENT_DATA：裝好的那份會改開預設資料夾");

        var mark = f.H.Main.Mark();
        f.H.Command(f.H.Main, "appInstallHere");
        await f.H.Ui.DrainAsync();

        f.H.Main.Messages("appUpdate", mark).Should().NotContain(m => m["state"]!.Value<string>("phase") == "installing", "指令被忽略");
        f.Shell.Notices.Should().BeEmpty("沒有嘗試安裝（安裝失敗也會通知）");
        f.Shell.RestartedInto.Should().BeEmpty();
    }
}
