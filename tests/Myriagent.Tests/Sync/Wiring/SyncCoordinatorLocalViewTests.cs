using System.Collections.Concurrent;
using System.Net;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Media;
using Myriagent.Remote;
using Myriagent.Security;
using Myriagent.Speech;
using Myriagent.Sync;
using Myriagent.Tests.Hub;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.Sync.Pairing;
using Myriagent.Tests.Sync.Rules;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit.Sdk;

namespace Myriagent.Tests.Sync.Wiring;

/// <summary>
/// 協調者給這台網頁的同步檢視（WP4.6 計畫步 9；D23、D26、D27、D29、D44）：掛上發布者時打開副本的顯示（先設再掛，整份清單只要一次）、
/// 回補進度重送 sync、syncSetPolicy 的 notifyRemoteActions、遠端操作紀錄（多讀一筆判斷 hasMore、以 (at, seq) 翻頁、只回給發問的檢視、匯出）、
/// 回收區（背景讀、回 UI 執行緒才送、還原）。假引擎（RecordingSyncEngine）＋真的中樞與發布者；協調者的宿主可以把 BeginInvoke 先扣住
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class SyncCoordinatorLocalViewTests
{
    private const long At = 1_791_600_000_000;

    /// <summary>協調者的宿主：<see cref="Hold"/> 時 BeginInvoke 先放進佇列（看「背景做完、還沒回到 UI 執行緒」的那一刻），其他照 HubHarness 的宿主</summary>
    private sealed class GateHost(IAgentHost inner) : IAgentHost
    {
        private readonly ConcurrentQueue<Action> held = new();

        public volatile bool Hold;

        public int HeldCount => held.Count;

        public void BeginInvoke(Action action)
        {
            if (Hold)
                held.Enqueue(action);
            else
                inner.BeginInvoke(action);
        }

        /// <summary>放開：扣住的照順序排進 UI 執行緒</summary>
        public void Release()
        {
            Hold = false;
            while (held.TryDequeue(out var action))
                inner.BeginInvoke(action);
        }

        public IReadOnlyList<string> PickFiles(IAgentView view) => inner.PickFiles(view);

        public string? PickFolder(IAgentView view, string? initialDirectory) => inner.PickFolder(view, initialDirectory);

        public string? PickSaveFile(IAgentView view, string fileName) => inner.PickSaveFile(view, fileName);

        public void OpenWithShell(string pathOrUrl) => inner.OpenWithShell(pathOrUrl);

        public void RevealInExplorer(string path) => inner.RevealInExplorer(path);

        public void CopyText(string text) => inner.CopyText(text);

        public bool CopyFile(string path) => inner.CopyFile(path);

        public void OpenPopout(string conversationId) => inner.OpenPopout(conversationId);

        public void ConversationDeleted(string conversationId) => inner.ConversationDeleted(conversationId);

        public bool IsViewForeground(string viewId) => inner.IsViewForeground(viewId);

        public void Notify(string title, string message, string? conversationId) => inner.Notify(title, message, conversationId);

        public void RequestAttention(IReadOnlyCollection<string> viewIds) => inner.RequestAttention(viewIds);

        public void Speak(string text, VoiceTuning tuning, Action? started) => inner.Speak(text, tuning, started);

        public void StopSpeaking() => inner.StopSpeaking();

        public void ApplyTheme(string theme) => inner.ApplyTheme(theme);

        public void UpdateTray(string tooltip, int running, int waiting) => inner.UpdateTray(tooltip, running, waiting);
    }

    private sealed class Rig : IDisposable
    {
        /// <param name="paired">有 device.json（啟動就建引擎）</param>
        /// <param name="conversations">假引擎有對話盒子（支援發布，協調者會掛上發布者）</param>
        public Rig(bool paired = true, bool conversations = true)
        {
            H = new HubHarness(alwaysPost: true);
            Box = conversations ? new ConversationChangeBox() : null;
            Engine = new RecordingSyncEngine { Conversations = Box };
            Host = new GateHost(H.Host);
            Paths = new SyncPaths(H.Temp.Combine("sync"));
            Http = new ScriptedHttpHandler(Time);
            if (paired)
            {
                Directory.CreateDirectory(Paths.Directory);
                Device = PairingTestData.Device();
                DeviceFile.Save(Paths.DeviceFile, Device);
            }

            Coordinator = H.OnUi(() => new SyncCoordinator(H.Hub, H.Accounts, Host, H.Settings, Paths,
                new SyncCoordinatorOptions(false, null, false, "0.5.0", "OFFICE-PC", "user"), Time,
                args =>
                {
                    Created.Add(args);
                    return Engine;
                },
                (paths, _, device) =>
                {
                    DeviceFile.Save(paths.DeviceFile, device);
                    return new PairingCommitResult(device);
                },
                Http,
                (_, _) =>
                {
                    var signer = new InMemoryDeviceSigner();
                    Signers.Add(signer);
                    return signer;
                }));
            H.Ui.Invoke(() => H.Hub.AttachSync(Coordinator));
        }

        public HubHarness H { get; }

        public FakeView Main => H.Main;

        public ManualTimeProvider Time { get; } = new();

        public ConversationChangeBox? Box { get; }

        public RecordingSyncEngine Engine { get; }

        public GateHost Host { get; }

        public SyncPaths Paths { get; }

        public ScriptedHttpHandler Http { get; }

        public DeviceFileData? Device { get; }

        public List<SyncEngineCreateArgs> Created { get; } = [];

        public List<InMemoryDeviceSigner> Signers { get; } = [];

        public SyncCoordinator Coordinator { get; }

        public async Task StartAsync()
        {
            H.Ui.Invoke(() => Coordinator.Start());
            await Coordinator.WhenStarted.WaitAsync(TimeSpan.FromSeconds(10));
            await H.Ui.DrainAsync();
        }

        public void Command(string cmd, object? args = null) => H.Command(Main, cmd, args);

        /// <summary>原樣的指令（JSON null、字串之類 JObject.FromObject 組不出來的值）</summary>
        public void Raw(FakeView view, JObject command) => H.Ui.Invoke(() => H.Hub.HandleCommand(view, command));

        public T OnUi<T>(Func<T> read) => H.OnUi(read);

        public async Task UnpairAsync()
        {
            var mark = Main.Mark();
            Command("syncUnpair");
            await Main.WaitForAsync("sync", m => m["sync"]?.Type == JTokenType.Null, mark, timeoutMs: 10000);
        }

        public void Dispose()
        {
            Host.Release();
            H.Ui.Invoke(() => Coordinator.Dispose());
            foreach (var signer in Signers)
                signer.Dispose();
            H.Dispose();
        }
    }

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

    private static Func<JObject, bool> Local(Func<JObject, bool> predicate) =>
        m => m["sync"] is JObject sync && sync["local"] is JObject local && predicate(local);

    private static RemoteAuditEntry Audit(long seq, long at, string cmd = "send") =>
        new(seq, at, "01J9QZ3K8M5R7T2V4W6X8Y0A" + seq.ToString("D2"), cmd, null, "Chrome・Windows", 1, 1, false, "Control", false, "done", null, null, false, 1);

    /// <summary>照 sync.db 的語意讀紀錄（at 由新到舊、同 at 依 seq；(at, seq) 翻頁；一次最多 200 筆）</summary>
    private static Func<long?, int, long?, Task<IReadOnlyList<RemoteAuditEntry>>> FakeStore(IReadOnlyList<RemoteAuditEntry> all) => (before, limit, beforeSeq) =>
    {
        IEnumerable<RemoteAuditEntry> rows = all.OrderByDescending(e => e.At).ThenByDescending(e => e.Seq);
        if (before is { } b)
            rows = beforeSeq is { } s ? rows.Where(e => e.At < b || (e.At == b && e.Seq < s)) : rows.Where(e => e.At < b);
        return Task.FromResult<IReadOnlyList<RemoteAuditEntry>>(rows.Take(Math.Clamp(limit, 1, SyncStore.MaxRemoteAuditPage)).ToList());
    };

    private static IReadOnlyList<long> Seqs(JObject page) => ((JArray)page["entries"]!).Select(e => e.Value<long>("seq")).ToList();

    // ───────────────────────────── 發布者與副本（D23） ─────────────────────────────

    [Fact]
    public async Task 建立發布者_ShowReplicas為true_拿掉時為false()
    {
        using (var rig = new Rig())
        {
            rig.OnUi(() => rig.H.Hub.ShowReplicas).Should().BeFalse("還沒建引擎");
            await rig.StartAsync();

            rig.OnUi(() => rig.H.Hub.ShowReplicas).Should().BeTrue("掛上發布者就顯示其他電腦的對話");
            rig.OnUi(() => rig.H.Hub.ConversationSink).Should().BeOfType<ConversationPublisher>();

            await rig.UnpairAsync();
            rig.OnUi(() => rig.H.Hub.ShowReplicas).Should().BeFalse("解除配對：拿掉發布者，副本一起移除");
            rig.OnUi(() => rig.H.Hub.ConversationSink).Should().BeNull();
            rig.OnUi(() => rig.Coordinator.Backfill).Should().BeNull();
        }

        // 引擎不發布對話（沒有盒子）：沒有發布者，也不顯示副本
        using var plain = new Rig(conversations: false);
        await plain.StartAsync();
        plain.OnUi(() => plain.H.Hub.ConversationSink).Should().BeNull();
        plain.OnUi(() => plain.H.Hub.ShowReplicas).Should().BeFalse();
    }

    [Fact]
    public async Task 先設ShowReplicas再掛接收者_RequestReplica整份一次()
    {
        using var rig = new Rig();
        await rig.StartAsync();

        var requests = rig.Box!.TakeAll().OfType<ReplicaRequested>().ToList();

        requests.Should().ContainSingle("打開的那一刻還沒有接收者；掛上時向新的接收者要一次").Which.ConversationId.Should().BeNull("整份清單");
    }

    [Fact]
    public async Task 掛上與拿掉發布者_開著的本機對話重送標頭()
    {
        // 協調者的 AttachPublisher 呼叫 RefreshSyncHeaders：配對（建引擎）之後政策選單出現、解除配對之後消失，不用重開對話
        using var rig = new Rig();
        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Claude);
        rig.H.CompleteTurn(s);
        // 等排定的合併送出（50 ms）送完：之後的標頭才是掛上發布者送的
        await Task.Delay(200);
        await rig.H.Ui.DrainAsync();
        ((JObject)rig.Main.Last("conversation", id)!["header"]!).ContainsKey("conversationSync").Should().BeFalse("還沒有接收者");
        var mark = rig.Main.Mark();

        await rig.StartAsync();

        var pushed = await rig.Main.WaitForAsync("header", m => m["header"]!["conversationSync"] is JObject, mark, conversationId: id);
        pushed["header"]!["conversationSync"]!.Value<string>("policy").Should().Be(ConversationSyncPolicies.Full);

        var detached = rig.Main.Mark();
        await rig.UnpairAsync();
        await rig.Main.WaitForAsync("header", m => m["header"] is JObject h && !h.ContainsKey("conversationSync"), detached, conversationId: id);
    }

    /// <summary>
    /// K-M2（D23；WP4.7 步 12 的 ReplicaMediaUrls 接在本包打開的 ShowReplicas 上）：經協調者掛上的發布者（正式的路徑，不是測試直接設 ShowReplicas）
    /// 打開副本之後，引擎有媒體來源時副本的圖片附件有本機的 /m/、/t/ 網址、AgentWebView 只轉送這些鍵；沒有媒體來源時照 WP4.3 沒有網址
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task 副本圖片項目_有媒體來源才顯示(bool withSource)
    {
        using var rig = new Rig();
        var source = new FakeReplicaMediaSource();
        if (withSource)
            rig.Box!.ReplicaMedia = source;
        await rig.StartAsync();
        rig.OnUi(() => rig.H.Hub.ShowReplicas).Should().BeTrue("協調者掛上發布者就打開副本的顯示");
        var publisher = rig.OnUi(() => rig.H.Hub.ConversationSink).Should().BeOfType<ConversationPublisher>().Subject;

        const string id = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d9";
        var owner = SyncIds.NewDeviceId();
        var imageRef = MediaTestFiles.NewId();
        var meta = ReplicaRules.ToMeta(new ReplicaMetaRow(id, owner, 3,
            ConversationWire.Serialize(new ConversationMetaPayload("Claude", "acc-owner", "擁有者的對話", "Ask", "default", "default", false, @"C:\owner\repo",
                ConversationSyncPolicies.Full, 1791500000000, 1791500060000, false, false)), false, 7))!;
        var user = new AgentChatItem
        {
            Id = "i1", Ord = 1, Kind = AgentItemKind.User, Status = AgentItemStatus.Done, Text = "附圖",
            Attachments = { new AgentAttachment { Id = "x1", FileName = "screen.png", StoredPath = @"C:\owner\att\screen.png", Kind = AgentAttachmentKind.Image, SizeBytes = 2048 } },
            MediaRefs = [new AgentMediaRef { Ref = imageRef, AttachmentId = "x1" }],
        };
        ConversationWire.TryItem(user, 1, out var wire).Should().BeTrue();
        var item = ReplicaRules.ToItem(ConversationWire.Serialize(wire!))!;

        rig.H.Ui.Invoke(() => publisher.ApplyReplicaDelta(new ReplicaDelta([meta], [], [], Full: true)));
        rig.Command("open", new { conversationId = id });
        var mark = rig.Main.Mark();
        rig.H.Ui.Invoke(() => publisher.ApplyReplicaDelta(new ReplicaDelta([], [], [new ReplicaItems(id, true, [item], [])])));
        rig.Command("loadOlder", new { conversationId = id });
        await rig.H.Ui.DrainAsync();

        var shown = rig.Main.All().Skip(mark)
            .SelectMany(m => m.Value<string>("type") switch
            {
                "item" => new[] { (JObject)m["item"]! },
                "conversation" => ((JArray)m["items"]!).Cast<JObject>(),
                _ => Enumerable.Empty<JObject>(),
            })
            .Last(i => i.Value<string>("id") == "i1");
        var attachment = (JObject)((JArray)shown["attachments"]!).Single();
        var key = MediaKeyRules.Format(owner, imageRef);
        if (withSource)
        {
            attachment.Value<string>("url").Should().Be("https://myriagent.local/m/" + key);
            attachment.Value<string>("thumbUrl").Should().Be("https://myriagent.local/t/" + key + "?s=320");
            rig.OnUi(() => rig.H.Hub.ReplicaMediaFor(key, MediaPurposes.Media)).Should().BeSameAs(source, "AgentWebView 的 /m/ 轉送這個鍵");
        }
        else
        {
            attachment.Value<string>("url").Should().BeNull("沒有媒體來源：照 WP4.3 沒有網址");
            attachment.Value<string>("thumbUrl").Should().BeNull();
            rig.OnUi(() => rig.H.Hub.ReplicaMediaFor(key, MediaPurposes.Media)).Should().BeNull();
        }

        attachment.Value<string>("name").Should().Be("screen.png");
        shown.ToString().Should().NotContain(@"C:\owner", "擁有者電腦的路徑不給網頁");
    }

    [Fact]
    public async Task 回補進度改變_重送sync帶backfill()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var port = (IConversationUiPort)rig.OnUi(() => rig.H.Hub.ConversationSink)!;
        var statusChanges = 0;
        rig.H.Ui.Invoke(() => rig.Coordinator.StatusChanged += () => statusChanges++);
        var progress = new SyncBackfillProgress(12, 50L * 1024 * 1024);
        var mark = rig.Main.Mark();

        // 引擎執行緒回報
        await Task.Run(() => port.ReportProgress(progress, null));

        var sync = await rig.Main.WaitForAsync("sync", m => m["sync"]?["backfill"] is JObject, mark, timeoutMs: 5000);
        JToken.DeepEquals(sync["sync"]!["backfill"], JObject.Parse("""{"conversationsLeft":12,"bytesLeft":52428800}""")).Should().BeTrue();
        rig.OnUi(() => rig.Coordinator.Backfill).Should().Be(progress);
        rig.OnUi(() => rig.Coordinator.BackfillPaused).Should().BeFalse();
        var status = rig.OnUi(() => rig.Coordinator.Status);
        TraySyncRules.Items(status, true, false, false, rig.OnUi(() => rig.Coordinator.Backfill), rig.OnUi(() => rig.Coordinator.BackfillPaused))[1].Text
            .Should().Be("回補到伺服器：還有 12 個對話（約 50 MB）");
        TraySyncRules.TrayText(status, true, rig.OnUi(() => rig.Coordinator.Backfill)).Should().EndWith("・回補 12 個對話");
        statusChanges.Should().BeGreaterThanOrEqualTo(1, "系統匣跟著 StatusChanged 更新");

        // 因磁碟保護暫停：只在系統匣顯示（sync.backfill 的形狀不變）
        var pausedMark = rig.Main.Mark();
        await Task.Run(() => port.ReportProgress(progress, new BackfillPause(BackfillHold.LowDisk, At, 0)));
        var paused = await rig.Main.WaitForAsync("sync", since: pausedMark, timeoutMs: 5000);
        Keys((JObject)paused["sync"]!["backfill"]!).Should().Equal("conversationsLeft", "bytesLeft");
        rig.OnUi(() => rig.Coordinator.BackfillPaused).Should().BeTrue();
        TraySyncRules.Items(rig.OnUi(() => rig.Coordinator.Status), true, false, false, progress, rig.OnUi(() => rig.Coordinator.BackfillPaused))[1].Text
            .Should().Be("回補已暫停（保護這台的磁碟）");

        // 回補完：backfill 拿掉
        var doneMark = rig.Main.Mark();
        await Task.Run(() => port.ReportProgress(null, null));
        var done = await rig.Main.WaitForAsync("sync", since: doneMark, timeoutMs: 5000);
        ((JObject)done["sync"]!).ContainsKey("backfill").Should().BeFalse();
        rig.OnUi(() => rig.Coordinator.Backfill).Should().BeNull();
    }

    private static IEnumerable<string> Keys(JObject o) => o.Properties().Select(p => p.Name);

    // ───────────────────────────── syncSetPolicy 的 notifyRemoteActions（D26） ─────────────────────────────

    [Fact]
    public async Task syncSetPolicy帶notifyRemoteActions_存進設定並重送local()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        rig.Command("ready");
        rig.Main.Last("init")!["remote"]!["sync"]!["local"]!.Value<bool>("notifyRemoteActions").Should().BeTrue("預設 true，init 就帶");
        var mark = rig.Main.Mark();

        rig.Command("syncSetPolicy", new { notifyRemoteActions = false });

        var sync = await rig.Main.WaitForAsync("sync", Local(l => l.Value<bool?>("notifyRemoteActions") == false), mark);
        sync["sync"]!["local"]!.Value<string>("remoteControlLevel").Should().Be("Off", "沒帶的政策沿用目前的");
        rig.OnUi(() => rig.H.Settings.Sync.NotifyRemoteActions).Should().BeFalse();
        JObject.Parse(File.ReadAllText(AppPaths.SettingsFile))["Sync"]!.Value<bool>("NotifyRemoteActions").Should().BeFalse("存進設定檔");

        // 只改政策時通知開關沿用
        var level = rig.Main.Mark();
        rig.Command("syncSetPolicy", new { remoteControlLevel = "View" });
        var changed = await rig.Main.WaitForAsync("sync", Local(l => l.Value<string>("remoteControlLevel") == "View"), level);
        changed["sync"]!["local"]!.Value<bool>("notifyRemoteActions").Should().BeFalse();
        rig.Engine.Policies.Should().NotBeEmpty("政策照常回報給引擎");
    }

    [Fact]
    public async Task notifyRemoteActions型別不對_整個指令忽略_政策也不改()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var policies = rig.Engine.Policies.Count;
        var mark = rig.Main.Mark();

        rig.Raw(rig.Main, new JObject { ["cmd"] = "syncSetPolicy", ["remoteControlLevel"] = "Control", ["notifyRemoteActions"] = "no" });
        rig.Raw(rig.Main, new JObject { ["cmd"] = "syncSetPolicy", ["remoteControlLevel"] = "Control", ["notifyRemoteActions"] = JValue.CreateNull() });
        rig.Raw(rig.Main, new JObject { ["cmd"] = "syncSetPolicy", ["allowRemoteFullAccess"] = true, ["notifyRemoteActions"] = 0 });
        await rig.H.Ui.DrainAsync();

        rig.OnUi(() => rig.H.Settings.Sync.RemoteControlLevel).Should().Be(SyncSettings.LevelOff);
        rig.OnUi(() => rig.H.Settings.Sync.AllowRemoteFullAccess).Should().BeFalse();
        rig.OnUi(() => rig.H.Settings.Sync.NotifyRemoteActions).Should().BeTrue();
        rig.Main.Messages("sync", mark).Should().BeEmpty("整個指令忽略、不重送");
        rig.Engine.Policies.Count.Should().Be(policies, "沒有新的政策交給引擎");
    }

    [Fact]
    public async Task 未配對_local沒有notifyRemoteActions()
    {
        using var rig = new Rig(paired: false);
        rig.Http.Respond(HttpStatusCode.OK, SyncJson.Serialize(PairingTestData.Start()));
        await rig.StartAsync();
        rig.Command("ready");
        rig.Main.Last("init")!["remote"]!.Type.Should().Be(JTokenType.Null, "未配對不送 sync");
        var mark = rig.Main.Mark();

        // 配對中（還沒有引擎）：sync 帶 local，但沒有通知開關與回補
        rig.Command("syncPair", new { deviceName = "OFFICE-PC", server = PairingTestData.Server });
        var pairing = await rig.Main.WaitForAsync("sync", m => m["sync"] is JObject, mark);

        var sync = (JObject)pairing["sync"]!;
        sync.Value<string>("state").Should().Be("pairing");
        ((JObject)sync["local"]!).ContainsKey("notifyRemoteActions").Should().BeFalse("未配對時是 null，不輸出");
        sync.ContainsKey("backfill").Should().BeFalse();
        rig.Command("syncCancelPair");
    }

    // ───────────────────────────── 遠端操作紀錄（D27、D44） ─────────────────────────────

    [Fact]
    public async Task syncLoadRemoteAudit_只回給發問的檢視_多一筆才hasMore()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var other = rig.H.AddView("other");
        rig.Engine.Audit = FakeStore([Audit(1, At + 1), Audit(2, At + 2), Audit(3, At + 3)]);
        var mark = rig.Main.Mark();
        var otherMark = other.Mark();

        rig.Command("syncLoadRemoteAudit", new { limit = 2 });

        var page = await rig.Main.WaitForAsync("remoteAudit", since: mark);
        Seqs(page).Should().Equal(3, 2);
        page.Value<bool>("hasMore").Should().BeTrue();
        rig.Engine.AuditLoads.Last().Should().Be(((long?)null, 3, (long?)null), "多讀一筆判斷 hasMore");
        ((JArray)page["entries"]!)[0].Value<string>("issuer").Should().Be("Chrome・Windows");
        await rig.H.Ui.DrainAsync();
        other.Messages("remoteAudit", otherMark).Should().BeEmpty("只送給發問的檢視");

        // 剛好 limit 筆：沒有更早的
        var exact = rig.Main.Mark();
        rig.Command("syncLoadRemoteAudit", new { limit = 3 });
        var all = await rig.Main.WaitForAsync("remoteAudit", since: exact);
        Seqs(all).Should().Equal(3, 2, 1);
        all.Value<bool>("hasMore").Should().BeFalse();

        // before、beforeSeq 照傳給引擎
        var next = rig.Main.Mark();
        rig.Command("syncLoadRemoteAudit", new { before = At + 2, beforeSeq = 2, limit = 2 });
        var older = await rig.Main.WaitForAsync("remoteAudit", since: next);
        Seqs(older).Should().Equal(1);
        rig.Engine.AuditLoads.Last().Should().Be(((long?)At + 2, 3, (long?)2));

        // 沒帶 limit 是 50 筆
        rig.Command("syncLoadRemoteAudit");
        await WaitUntilAsync(() => rig.Engine.AuditLoads.Last().Limit == LocalSyncViewRules.AuditDefaultLimit + 1, "預設一頁 50 筆（多讀一筆）");
    }

    [Fact]
    public async Task syncLoadRemoteAudit_limit是上限200_sync_db一次只給200筆_以at與seq多探一筆()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var rows = Enumerable.Range(1, 201).Select(i => Audit(i, At + i / 3)).ToList();
        rig.Engine.Audit = FakeStore(rows);
        var mark = rig.Main.Mark();

        rig.Command("syncLoadRemoteAudit", new { limit = 200 });

        var page = await rig.Main.WaitForAsync("remoteAudit", since: mark);
        ((JArray)page["entries"]!).Should().HaveCount(200);
        page.Value<bool>("hasMore").Should().BeTrue("還有第 201 筆");
        var loads = rig.Engine.AuditLoads.ToList();
        loads.Should().HaveCount(2);
        loads[0].Should().Be(((long?)null, 200, (long?)null));
        var oldest = ((JArray)page["entries"]!).Last();
        loads[1].Should().Be(((long?)oldest.Value<long>("at"), 1, (long?)oldest.Value<long>("seq")), "以這一頁最舊一筆的 (at, seq) 探下一筆");

        // 剛好 200 筆：探不到，沒有更早的
        rig.Engine.Audit = FakeStore(rows.Skip(1).ToList());
        var exact = rig.Main.Mark();
        rig.Command("syncLoadRemoteAudit", new { limit = 200 });
        var full = await rig.Main.WaitForAsync("remoteAudit", since: exact);
        ((JArray)full["entries"]!).Should().HaveCount(200);
        full.Value<bool>("hasMore").Should().BeFalse();
    }

    [Fact]
    public async Task AuditPage_同一毫秒跨頁不漏()
    {
        // 真的 sync.db：3 列同一個 at，一頁 2 列；第二頁照網頁的 auditNextPage 以最舊一筆的 (at, seq) 取
        using var temp = new TempDirectory("local-audit");
        var self = TestDevice.Create("OFFICE-PC");
        using var store = SyncStore.Create(temp.Combine("sync", "sync.db"), self.DeviceId, 1791500000000, new ServerInstance("srv-instance-1", 0),
            new TrustBundle(3, [self.Entry()]));
        foreach (var cid in new[] { "01J9QZ3K8M5R7T2V4W6X8Y0A1B", "01J9QZ3K8M5R7T2V4W6X8Y0A1C", "01J9QZ3K8M5R7T2V4W6X8Y0A1D" })
        {
            store.AuditRejected(new CommandAuditFacts(cid, "rename", "5f3a0c1d2e3f4a5b6c7d8e9f0a1b2c3d", DeclaredTier: 1, DefaultPassword: false,
                Iat: At - 500, Created: At - 1_000, null, null, new string('c', 64), new string('s', 10)), CommandEnvelopeSteps.Signature, RemoteResultCodes.Invalid, At);
        }

        using var rig = new Rig();
        await rig.StartAsync();
        rig.Engine.Audit = (before, limit, beforeSeq) => Task.FromResult(store.LoadRemoteAudit(before, limit, beforeSeq));
        var mark = rig.Main.Mark();

        rig.Command("syncLoadRemoteAudit", new { limit = 2 });
        var first = await rig.Main.WaitForAsync("remoteAudit", since: mark);
        first.Value<bool>("hasMore").Should().BeTrue();
        var entries = (JArray)first["entries"]!;
        entries.Should().HaveCount(2);
        var oldest = entries.OrderBy(e => e.Value<long>("at")).ThenBy(e => e.Value<long>("seq")).First();

        var nextMark = rig.Main.Mark();
        rig.Command("syncLoadRemoteAudit", new { before = oldest.Value<long>("at"), beforeSeq = oldest.Value<long>("seq"), limit = 2 });
        var second = await rig.Main.WaitForAsync("remoteAudit", since: nextMark);

        second.Value<bool>("hasMore").Should().BeFalse();
        var seqs = Seqs(first).Concat(Seqs(second)).ToList();
        seqs.Should().HaveCount(3).And.OnlyHaveUniqueItems("同一毫秒的三列跨兩頁，不漏也不重複");
        seqs.Should().BeEquivalentTo(store.LoadRemoteAudit(null, 10).Select(e => e.Seq));
    }

    [Fact]
    public async Task limit是201_不回覆()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var mark = rig.Main.Mark();

        rig.Command("syncLoadRemoteAudit", new { limit = 201 });
        rig.Command("syncLoadRemoteAudit", new { beforeSeq = 3 });
        rig.Raw(rig.Main, new JObject { ["cmd"] = "syncLoadRemoteAudit", ["limit"] = "50" });
        await rig.H.Ui.DrainAsync();

        rig.Main.Messages("remoteAudit", mark).Should().BeEmpty("參數不合格整個指令忽略");
        rig.Engine.AuditLoads.Should().BeEmpty();
    }

    [Fact]
    public async Task syncLoadRemoteAudit_未配對回空的一頁()
    {
        using var rig = new Rig(paired: false);
        await rig.StartAsync();
        var mark = rig.Main.Mark();

        rig.Command("syncLoadRemoteAudit");

        var page = await rig.Main.WaitForAsync("remoteAudit", since: mark);
        ((JArray)page["entries"]!).Should().BeEmpty();
        page.Value<bool>("hasMore").Should().BeFalse();
    }

    [Fact]
    public async Task syncExportRemoteAudit_路徑不是解除配對的檔()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        rig.Engine.Export = (path, _) =>
        {
            // 同 SyncStore.ExportRemoteAudit：資料夾不存在就建
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}\n");
            return Task.FromResult(4);
        };
        var mark = rig.Main.Mark();

        rig.Command("syncExportRemoteAudit");

        var result = await rig.Main.WaitForAsync("remoteAuditExport", since: mark);
        var unpairPath = SyncCommandRules.RemoteAuditExportPath(rig.Paths, rig.Device!.DeviceId);
        var path = result.Value<string>("path")!;
        path.Should().NotBe(unpairPath, "不蓋掉解除配對時寫的那一份");
        Path.GetDirectoryName(path).Should().Be(Path.GetDirectoryName(unpairPath));
        path.Should().StartWith(rig.H.Temp.Root, "不碰真的資料根目錄");
        result.Value<int>("count").Should().Be(4);
        result["error"]!.Type.Should().Be(JTokenType.Null);
        rig.Engine.Exports.Single().Path.Should().Be(path);

        // 引擎沒寫檔（sync.db 沒開）：沒有路徑可以給
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        rig.Engine.Export = null;
        var none = rig.Main.Mark();
        rig.Command("syncExportRemoteAudit");
        var missing = await rig.Main.WaitForAsync("remoteAuditExport", since: none);
        missing["path"]!.Type.Should().Be(JTokenType.Null);
        missing.Value<string>("error").Should().NotBeNullOrEmpty();

        // 匯出失敗：錯誤文字
        rig.Engine.Export = (_, _) => Task.FromException<int>(new IOException("磁碟滿了"));
        var failed = rig.Main.Mark();
        rig.Command("syncExportRemoteAudit");
        var error = await rig.Main.WaitForAsync("remoteAuditExport", since: failed);
        error.Value<string>("error").Should().Contain("磁碟滿了");
        error["path"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public async Task syncExportRemoteAudit_未配對回錯誤()
    {
        using var rig = new Rig(paired: false);
        await rig.StartAsync();
        var mark = rig.Main.Mark();

        rig.Command("syncExportRemoteAudit");

        var result = await rig.Main.WaitForAsync("remoteAuditExport", since: mark);
        result["path"]!.Type.Should().Be(JTokenType.Null);
        result.Value<string>("error").Should().Contain("還沒配對");
        rig.Engine.Exports.Should().BeEmpty();
    }

    // ───────────────────────────── 回收區（D27） ─────────────────────────────

    /// <summary>閒置的 Claude 對話被遠端刪除、資料夾已搬進回收區（同 AgentHubTrashRestoreTests）；回對話 id</summary>
    private static async Task<string> RemoteDeletedAsync(Rig rig, string text = "要還原的對話")
    {
        RemoteEntrySamples.AllowRemote(rig.H);
        var (id, s) = rig.H.StartConversation(rig.Main, rig.H.Claude, text);
        rig.H.CompleteTurn(s);
        var outcome = await RemoteEntrySamples.RunAsync(rig.H, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("delete", id), tier: 2));
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var trash = rig.OnUi(() => rig.H.Hub.Trash);
        await WaitUntilAsync(() => trash.Contains(id) && !File.Exists(Path.Combine(trash.RootDirectory, ConversationTrash.PendingFileName)), "資料夾在背景移到回收區");
        return id;
    }

    [Fact]
    public async Task syncListTrash_在背景讀_回到UI執行緒才送()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var id = await RemoteDeletedAsync(rig);
        var other = rig.H.AddView("other");
        var mark = rig.Main.Mark();
        var otherMark = other.Mark();

        rig.Host.Hold = true;
        rig.Command("syncListTrash");

        rig.Main.Messages("trash", mark).Should().BeEmpty("指令在 UI 執行緒上不讀磁碟、不直接回");
        await WaitUntilAsync(() => rig.Host.HeldCount >= 1, "背景讀完、排進 UI 執行緒");
        await rig.H.Ui.DrainAsync();
        rig.Main.Messages("trash", mark).Should().BeEmpty("BeginInvoke 還沒執行");

        rig.Host.Release();
        var trash = await rig.Main.WaitForAsync("trash", since: mark);
        await rig.H.Ui.DrainAsync();
        rig.Main.Messages("trash", mark).Should().HaveCount(1);
        other.Messages("trash", otherMark).Should().BeEmpty("只送給發問的檢視");
        var entry = ((JArray)trash["entries"]!).Single();
        entry.Value<string>("conversationId").Should().Be(id);
        entry.Value<string>("title").Should().Be("要還原的對話");
        entry.Value<string>("issuer").Should().Be(RemoteEntrySamples.IssuerLabel);
        (entry.Value<long>("expiresAt") - entry.Value<long>("trashedAt")).Should().Be(ConversationTrashRules.RetentionMs);
    }

    [Fact]
    public async Task syncRestoreTrash_成功重送清單_失敗toast()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var id = await RemoteDeletedAsync(rig);
        var other = rig.H.AddView("other");
        var mark = rig.Main.Mark();
        var otherMark = other.Mark();

        rig.Command("syncRestoreTrash", new { conversationId = id });

        rig.H.Conversation(id).Should().NotBeNull("搬回清單");
        var toast = rig.Main.Messages("toast", mark).Single();
        (toast.Value<string>("text"), toast.Value<string>("level")).Should().Be((SyncCoordinator.TrashRestoredText, "info"));
        var list = await rig.Main.WaitForAsync("trash", since: mark);
        ((JArray)list["entries"]!).Should().BeEmpty("還原之後重送清單");

        // 再還原一次：已不在回收區 → 錯誤提示，清單照樣重送
        var again = rig.Main.Mark();
        rig.Command("syncRestoreTrash", new { conversationId = id });
        var failed = rig.Main.Messages("toast", again).Single();
        (failed.Value<string>("text"), failed.Value<string>("level")).Should().Be((SyncCoordinator.TrashRestoreFailedText, "error"));
        await rig.Main.WaitForAsync("trash", since: again);

        // conversationId 不是字串：忽略
        var bad = rig.Main.Mark();
        rig.Raw(rig.Main, new JObject { ["cmd"] = "syncRestoreTrash", ["conversationId"] = 5 });
        await rig.H.Ui.DrainAsync();
        rig.Main.Messages("toast", bad).Should().BeEmpty();
        rig.Main.Messages("trash", bad).Should().BeEmpty();
        other.Messages("toast", otherMark).Should().BeEmpty("提示只給送指令的視窗");
    }

    [Fact]
    public async Task 鏡像檢視送的sync指令一律不理()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        var mirror = new FakeView(AgentHub.SyncViewId);
        var mark = rig.Main.Mark();

        foreach (var cmd in new[] { "syncLoadRemoteAudit", "syncExportRemoteAudit", "syncListTrash", "syncRestoreTrash" })
        {
            var command = HubHarness.MakeCommand(cmd, new { conversationId = "0123456789abcdef0123456789abcdef" });
            rig.OnUi(() => rig.Coordinator.HandleCommand(mirror, cmd, command)).Should().BeFalse(cmd);
        }

        await Task.Delay(100);
        await rig.H.Ui.DrainAsync();
        mirror.All().Should().BeEmpty();
        rig.Main.Messages("remoteAudit", mark).Should().BeEmpty();
        rig.Main.Messages("trash", mark).Should().BeEmpty();
        rig.Engine.AuditLoads.Should().BeEmpty();
        rig.Engine.Exports.Should().BeEmpty();
    }
}
