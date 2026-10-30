using System.Security.Cryptography;
using Myriagent.Agents;
using Myriagent.Cli;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端的 send（WP4.4 計畫步 8；D19、D20、D22、gate/F3、F7）：經本機的 SendAsync，帶這個指令的來源（佇列、CLI 更新、換版都帶著走）；
/// 不取本機輸入區的附件；使用者項目建立就回 done（不等 CLI）；被拒時先轉 sendRejected 再回 conflict；被 HoldRemoteSend 留下的保留 Id、只提示一次、
/// 本機看過提示再按送出就當本機訊息；取出時另外重查同步政策與副本；拒絕附言帶遠端來源。這台的政策是 Control、不允許遠端完全自動（Q1 照回答 A）
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteSendTests
{
    private const string HeldNoticeText = "從遠端排進佇列的訊息暫停了：這個對話的權限模式或這台電腦的遠端操作設定已經改變。請確認後按送出，或從佇列取消。";

    private const string HeldToastText = "從遠端排進佇列的訊息暫停了，請在這台電腦上確認";

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>閒置的 Claude 對話（政策 Control、不允許遠端完全自動；模式是預設的每步詢問）</summary>
    private static (string Id, FakeSession Session) IdleConversation(HubHarness h)
    {
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Conversation(id)!.PermissionMode.Should().Be(AgentPermissionMode.Ask);
        return (id, s);
    }

    /// <summary>執行中的 Claude 對話（政策 Control、不允許遠端完全自動）</summary>
    private static (string Id, FakeSession Session) RunningConversation(HubHarness h)
    {
        RemoteEntrySamples.AllowRemote(h);
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.IsRunning(id).Should().BeTrue();
        return (id, s);
    }

    private static Task<RemoteOutcome> RemoteSendAsync(HubHarness h, string conversationId, string text, Recorder<JObject>? output = null, string? cid = null)
        => RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("send", conversationId, new { text }), cid: cid), output);

    private static IReadOnlyList<(string Id, string Text, RemoteOrigin? Origin, bool Held)> Entries(HubHarness h, string conversationId)
        => h.OnUi(() => h.Hub.QueueEntriesForTest(conversationId));

    /// <summary>這台的來源（層級 1、不要完全自動、入列時每步詢問）</summary>
    private static RemoteOrigin AskOrigin(string cid) => new(cid, AgentPermissionMode.Ask, 1, false);

    private static async Task AddLocalAttachmentAsync(HubHarness h, string conversationId, string name)
    {
        var mark = h.Main.Mark();
        h.Command(h.Main, "addFiles", new { conversationId }, new[] { h.Temp.WriteFile(@"in\" + name, "a,b") });
        await h.Main.WaitForAsync("composer", m => m["composer"]!["attachments"]!.Count() == 1, mark, conversationId: conversationId);
    }

    private static JToken ComposerAttachments(HubHarness h, string conversationId) => h.Main.Last("composer", conversationId)!["composer"]!["attachments"]!;

    /// <summary>佇列暫停中的對話：執行中排進一則本機訊息，這一輪被中斷（佇列不自動送出）</summary>
    private static (string Id, FakeSession Session) PausedQueue(HubHarness h, string queued = "排隊A")
    {
        var (id, s) = RunningConversation(h);
        h.Send(h.Main, id, queued);
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);
        h.IsRunning(id).Should().BeFalse();
        h.Queue(id).Should().Equal(queued);
        return (id, s);
    }

    private static int Notices(HubHarness h, string conversationId) => h.Items(conversationId).Count(i => i.Kind == AgentItemKind.Notice && i.Text == HeldNoticeText);

    private static int HeldToasts(HubHarness h) => h.Main.Messages("toast").Count(m => m.Value<string>("text") == HeldToastText);

    // ───────────────────────────── 送出 ─────────────────────────────

    [Fact]
    public async Task 遠端send_閒置時送出_建立使用者項目_結果done_本機待送附件留著()
    {
        using var h = new HubHarness();
        var (id, s) = IdleConversation(h);
        await AddLocalAttachmentAsync(h, id, "本機的.csv");

        var outcome = await RemoteSendAsync(h, id, "從手機送的");

        outcome.Should().Be(new RemoteOutcome(RemoteResultStates.Done, null, null, 1, false, null));
        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("從手機送的");
        s.Sent.All[1].Input.Attachments.Should().BeEmpty("遠端不取本機輸入區的待送附件");
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Text.Should().Be("從手機送的");
        ComposerAttachments(h, id).Should().ContainSingle("本機的待送附件留在輸入區");
    }

    [Fact]
    public async Task 遠端send_使用者項目建立就done_不等CLI()
    {
        using var h = new HubHarness();
        var (id, s) = IdleConversation(h);
        var gate = new TaskCompletionSource();
        s.SendGate = gate;

        var outcome = await RemoteSendAsync(h, id, "從手機送的");

        outcome.State.Should().Be(RemoteResultStates.Done, "CLI 還在啟動（SendAsync 卡住）時就已經是 done");
        s.Sent.Count.Should().Be(2);
        h.Items(id).Last(i => i.Kind == AgentItemKind.User).Text.Should().Be("從手機送的");
        gate.SetResult();
        await h.Ui.DrainAsync();
    }

    [Fact]
    public async Task 遠端send_執行中入列帶來源_Id是r加cid_這一輪結束後模式調高就留下_結果policy不會出現_因為已done()
    {
        using var h = new HubHarness();
        var (id, s) = RunningConversation(h);
        var cid = RemoteEntrySamples.NewCid();

        var outcome = await RemoteSendAsync(h, id, "排隊的遠端訊息", cid: cid);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var entry = Entries(h, id).Single();
        entry.Id.Should().Be("r" + cid);
        entry.Text.Should().Be("排隊的遠端訊息");
        entry.Origin.Should().Be(AskOrigin(cid));
        entry.Held.Should().BeFalse();

        // 入列之後本機把模式調高：這一輪結束接著送時留下（結果早已是 done，不會再變）
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "FullAccess" });
        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(1);
        var held = Entries(h, id).Single();
        (held.Id, held.Origin, held.Held).Should().Be(("r" + cid, AskOrigin(cid), true));
        Notices(h, id).Should().Be(1);
    }

    [Fact]
    public async Task 遠端send_執行中插進這一輪_回done_加成使用者訊息_之後本機的也照常插入_這一輪結束不再送()
    {
        using var h = new HubHarness();
        var (id, s) = RunningConversation(h);
        s.CanSteer = true;
        var cid = RemoteEntrySamples.NewCid();

        var outcome = await RemoteSendAsync(h, id, "遠端的", cid: cid);
        h.Send(h.Main, id, "本機的");

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        s.Steered.All.Select(i => i.Text).Should().Equal(new[] { "遠端的", "本機的" }, "模式與政策沒變：遠端的也直接插進這一輪，先後照送出的順序");
        Entries(h, id).Should().BeEmpty();
        h.Items(id).Where(i => i.Kind == AgentItemKind.User).Select(i => i.Text).Should().EndWith(new[] { "遠端的", "本機的" });
        h.IsRunning(id).Should().BeTrue("還是同一輪");

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(1, "已經插進那一輪，不能再當成下一則送出");
    }

    [Fact]
    public async Task 遠端send_執行中插不進去_照舊排隊_這一輪結束後送出()
    {
        using var h = new HubHarness();
        var (id, s) = RunningConversation(h);
        s.CanSteer = false;
        var cid = RemoteEntrySamples.NewCid();

        var outcome = await RemoteSendAsync(h, id, "遠端的", cid: cid);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        s.SteerAttempts.Should().Be(1);
        Entries(h, id).Single().Should().Be(("r" + cid, "遠端的", AskOrigin(cid), false));

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("遠端的");
        Entries(h, id).Should().BeEmpty();
    }

    [Fact]
    public async Task 遠端訊息入列後模式調高_不插進這一輪_排在後面的本機訊息也跟著等_這一輪結束時遠端那則留下()
    {
        using var h = new HubHarness();
        var (id, s) = RunningConversation(h);
        s.CanSteer = false;
        var cid = RemoteEntrySamples.NewCid();
        (await RemoteSendAsync(h, id, "遠端的", cid: cid)).State.Should().Be(RemoteResultStates.Done);
        s.SteerAttempts.Should().Be(1, "當時插不進去，留在佇列");

        // 入列之後本機把模式調高（不允許遠端完全自動）、代理又能插入了：本機再送一則
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "FullAccess" });
        s.CanSteer = true;
        h.Send(h.Main, id, "本機的");

        s.SteerAttempts.Should().Be(1, "遠端那則插之前重查：已不允許，不插；排在它後面的本機訊息維持先後，也不插");
        Entries(h, id).Select(e => e.Text).Should().Equal("遠端的", "本機的");

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(1, "遠端那則照舊經 HoldRemoteSend 留下，佇列暫停");
        Entries(h, id).Select(e => (e.Text, e.Held)).Should().Equal(("遠端的", true), ("本機的", false));
        Notices(h, id).Should().Be(1);
    }

    [Fact]
    public async Task 已被留下的遠端訊息_條件恢復後本機送出也不會自動插進這一輪()
    {
        using var h = new HubHarness();
        var (id, s) = RunningConversation(h);
        s.CanSteer = false;
        var cid = RemoteEntrySamples.NewCid();
        (await RemoteSendAsync(h, id, "遠端的", cid: cid)).State.Should().Be(RemoteResultStates.Done);
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "FullAccess" });
        h.CompleteTurn(s);
        Entries(h, id).Single().Held.Should().BeTrue();

        // 模式改回每步詢問（條件又允許了），本機送新的文字開始新的一輪（空送會直接送出留下的那則，所以送新文字）
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "Ask" });
        h.Send(h.Main, id, "本機開的新一輪");
        h.IsRunning(id).Should().BeTrue();
        s.CanSteer = true;
        h.Send(h.Main, id, "本機插的");

        s.Steered.All.Should().BeEmpty("留下的那則要本機確認（按送出或取消）才送：不自動插入，排在它後面的也跟著等");
        Entries(h, id).Select(e => e.Text).Should().Equal("遠端的", "本機插的");
    }

    [Fact]
    public async Task 遠端send_CLI更新中延後_延後的那則帶來源()
    {
        using var h = new HubHarness();
        var (id, s) = IdleConversation(h);
        h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Claude, state => state with { InstallChanging = true }));
        var output = new Recorder<JObject>();
        var cid = RemoteEntrySamples.NewCid();

        var outcome = await RemoteSendAsync(h, id, "更新中送的", output, cid);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        output.All.Should().ContainSingle(m => m.Value<string>("type") == "toast" && m.Value<string>("text")!.Contains("更新完成後自動送出"), "提示只回發起的瀏覽器");
        var entry = Entries(h, id).Single();
        (entry.Id, entry.Origin).Should().Be(("r" + cid, AskOrigin(cid)));
        s.Sent.Count.Should().Be(1);

        h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Claude, state => state with { InstallChanging = false }));
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "更新完成後送出");

        s.Sent.All[1].Input.Text.Should().Be("更新中送的");
        h.Queue(id).Should().BeEmpty();
    }

    [Fact]
    public async Task 遠端send_工作資料夾不存在_先輸出sendRejected再rejected_conflict()
    {
        using var h = new HubHarness();
        var (id, s) = IdleConversation(h);
        var missing = Path.Combine(h.Temp.Root, "不存在的資料夾");
        h.Ui.Invoke(() => h.Hub.GetConversation(id)!.WorkingDirectory = missing);
        var output = new Recorder<JObject>();

        var outcome = await RemoteSendAsync(h, id, "從手機送的", output);

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        outcome.Message.Should().Contain("工作資料夾不存在");
        output.All.Select(m => m.Value<string>("type")).Should().Equal("sendRejected", "toast");
        output.All[0].Value<string>("text").Should().Be("從手機送的", "網頁把文字放回輸入框");
        output.All[0].Value<string>("conversationId").Should().Be(ConversationIds.ToWire(id));
        s.Sent.Count.Should().Be(1);
        h.Main.Messages("sendRejected").Should().BeEmpty("只回給發起的瀏覽器");
    }

    [Fact]
    public async Task 遠端send_等待途中對話被刪_conflict()
    {
        using var h = new HubHarness();
        var (id, s) = IdleConversation(h);
        var folder = h.Temp.CreateDirectory("proj");
        h.Ui.Invoke(() => h.Hub.GetConversation(id)!.WorkingDirectory = folder);
        var body = RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("send", id, new { text = "從手機送的" }));

        // 同一個 UI 工作裡：遠端 send 停在背景檢查資料夾，本機接著刪掉這個對話
        var task = h.Ui.Invoke(() =>
        {
            var running = h.Hub.ExecuteRemoteCommandAsync(body, _ => { });
            h.Hub.HandleCommand(h.Main, HubHarness.MakeCommand("delete", new { conversationId = id }));
            return running;
        });
        var outcome = await task.WaitAsync(TimeSpan.FromSeconds(10));

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        s.Sent.Count.Should().Be(1);
    }

    [Fact]
    public async Task 遠端send_不改LastAccountId與最近資料夾_settings雜湊不變()
    {
        using var h = new HubHarness();
        var (id, s) = IdleConversation(h);
        var picked = h.Temp.CreateDirectory("remote-picked");
        h.Ui.Invoke(() =>
        {
            h.Hub.GetConversation(id)!.WorkingDirectory = picked;
            h.Settings.LastAccountId = "acc-codex";
            h.Settings.Save();
        });
        var saved = Sha256(AppPaths.SettingsFile);
        h.Ui.Invoke(() => h.Settings.Save());
        Sha256(AppPaths.SettingsFile).Should().Be(saved, "前提：同一份設定連續存兩次內容相同（計畫 R10）");
        var recent = h.OnUi(() => h.Settings.RecentFolders.ToList());

        var outcome = await RemoteSendAsync(h, id, "從手機送的");
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "在背景確認資料夾後送出");

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        h.OnUi(() => h.Settings.LastAccountId).Should().Be("acc-codex", "遠端不寫這台的持久設定（D23）");
        h.OnUi(() => h.Settings.RecentFolders.ToList()).Should().Equal(recent);
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        Sha256(AppPaths.SettingsFile).Should().Be(saved, "送出時存的設定與之前內容相同");
    }

    [Fact]
    public async Task 本機send_行為不變_附件被取走_LastAccountId被寫()
    {
        using var h = new HubHarness();
        var (id, s) = IdleConversation(h);
        h.Ui.Invoke(() => h.Settings.LastAccountId = "acc-codex");
        await AddLocalAttachmentAsync(h, id, "本機的.csv");

        h.Send(h.Main, id, "本機送的");

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Attachments.Should().ContainSingle("本機的送出照舊帶輸入區的附件");
        ComposerAttachments(h, id).Should().BeEmpty();
        h.OnUi(() => h.Settings.LastAccountId).Should().Be("acc-claude");
    }

    // ───────────────────────────── 空送 ─────────────────────────────

    [Fact]
    public async Task 遠端空送_佇列空_conflict()
    {
        using var h = new HubHarness();
        var (id, s) = IdleConversation(h);
        var output = new Recorder<JObject>();

        var outcome = await RemoteSendAsync(h, id, "  ", output);

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        output.Count.Should().Be(0);
        s.Sent.Count.Should().Be(1);
    }

    [Fact]
    public async Task 遠端空送_執行中_conflict()
    {
        using var h = new HubHarness();
        var (id, s) = RunningConversation(h);
        h.Send(h.Main, id, "排隊A");

        var outcome = await RemoteSendAsync(h, id, "");

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        h.Queue(id).Should().Equal("排隊A");
        s.Sent.Count.Should().Be(1);
    }

    [Fact]
    public async Task 遠端空送_本機有附件也不帶_從佇列第一則接著送()
    {
        using var h = new HubHarness();
        var (id, s) = PausedQueue(h);
        await AddLocalAttachmentAsync(h, id, "本機的.csv");

        var outcome = await RemoteSendAsync(h, id, "");

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("排隊A");
        s.Sent.All[1].Input.Attachments.Should().BeEmpty();
        ComposerAttachments(h, id).Should().ContainSingle("本機的待送附件留在輸入區");
        h.Queue(id).Should().BeEmpty();
    }

    [Fact]
    public async Task 遠端空送_取出時用這次的來源()
    {
        using var h = new HubHarness();
        var (id, s) = PausedQueue(h);
        h.Ui.Invoke(() => h.Settings.LastAccountId = "acc-codex");

        var outcome = await RemoteSendAsync(h, id, "");

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        s.Sent.All[^1].Input.Text.Should().Be("排隊A");
        h.OnUi(() => h.Settings.LastAccountId).Should().Be("acc-codex", "佇列那則是本機的，但這次是遠端送的：以遠端來源送出，不寫這台的上次帳戶");
    }

    [Fact]
    public async Task 遠端空送_CLI更新中_延後的那則改用這次的來源_Id不變()
    {
        using var h = new HubHarness();
        var (id, s) = PausedQueue(h);
        var before = Entries(h, id).Single();
        before.Origin.Should().BeNull();
        h.Ui.Invoke(() =>
        {
            h.Settings.LastAccountId = "acc-codex";
            h.Cli.Update(AgentProvider.Claude, state => state with { InstallChanging = true });
        });
        var cid = RemoteEntrySamples.NewCid();

        var outcome = await RemoteSendAsync(h, id, "", cid: cid);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var deferred = Entries(h, id).Single();
        (deferred.Id, deferred.Text, deferred.Origin).Should().Be((before.Id, "排隊A", AskOrigin(cid)));

        h.Ui.Invoke(() => h.Cli.Update(AgentProvider.Claude, state => state with { InstallChanging = false }));
        await h.WaitUntilAsync(() => s.Sent.Count == 2, "更新完成後送出");
        s.Sent.All[1].Input.Text.Should().Be("排隊A");
        h.OnUi(() => h.Settings.LastAccountId).Should().Be("acc-codex");
    }

    // ───────────────────────────── 被留下的遠端訊息（gate/F3、F7） ─────────────────────────────

    [Fact]
    public async Task 被留下的遠端訊息_重試三次Id不變_Notice與toast只出一次()
    {
        using var h = new HubHarness();
        var (id, s) = RunningConversation(h);
        var cid = RemoteEntrySamples.NewCid();
        (await RemoteSendAsync(h, id, "遠端的訊息", cid: cid)).State.Should().Be(RemoteResultStates.Done);
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "FullAccess" });

        for (var round = 1; round <= 3; round++)
        {
            // 每一輪正常結束都接著送佇列第一則：遠端那則每次都被留下
            h.CompleteTurn(h.Sessions[^1]);
            var entry = Entries(h, id).Single();
            (entry.Id, entry.Held).Should().Be(("r" + cid, true), $"第 {round} 次留下");
            h.Send(h.Main, id, $"本機第 {round} 則");
            h.IsRunning(id).Should().BeTrue();
        }

        Notices(h, id).Should().Be(1, "同一個指令只提示一次");
        HeldToasts(h).Should().Be(1);
        s.Sent.All.Select(x => x.Input.Text).Should().NotContain("遠端的訊息");
    }

    [Fact]
    public void 被留下的遠端訊息_第一次按送出照舊重查_本機第二次按送出就當本機訊息送出_同一個Id()
    {
        using var h = new HubHarness();
        var (id, s) = IdleConversation(h);
        var cid = RemoteEntrySamples.NewCid();
        h.Ui.Invoke(() => h.Hub.EnqueueRemoteForTest(id, "遠端的訊息", AskOrigin(cid)));
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "FullAccess" });
        Entries(h, id).Single().Id.Should().Be("r" + cid);

        // 第一次：照 WP4.1 的語意以遠端來源重查，模式被調高就留下
        h.Send(h.Main, id, "");

        s.Sent.Count.Should().Be(1);
        var held = Entries(h, id).Single();
        (held.Id, held.Origin, held.Held).Should().Be(("r" + cid, AskOrigin(cid), true));
        Notices(h, id).Should().Be(1);

        // 第二次：本機使用者看過提示親自按送出，當成本機的訊息送出（同一則、同一個 Id 從佇列取走）
        h.Send(h.Main, id, "");

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("遠端的訊息");
        s.Sent.All[1].Settings.PermissionMode.Should().Be(AgentPermissionMode.FullAccess);
        h.Queue(id).Should().BeEmpty();
        Notices(h, id).Should().Be(1);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("replica")]
    public async Task 已排隊的遠端訊息_對話改成metadata或成為副本_取出時留下(string change)
    {
        using var h = new HubHarness();
        var (id, s) = RunningConversation(h);
        var cid = RemoteEntrySamples.NewCid();
        (await RemoteSendAsync(h, id, "遠端的訊息", cid: cid)).State.Should().Be(RemoteResultStates.Done);
        h.Ui.Invoke(() =>
        {
            if (change == "metadata")
                h.Hub.GetConversation(id)!.SyncPolicy = ConversationSyncPolicies.Metadata;
            else
                h.Hub.MarkReplicaForTest(id);
        });

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(1, "閘門 5c 的條件取出時再查一次（gate/F7）");
        (Entries(h, id).Single().Id, Entries(h, id).Single().Held).Should().Be(("r" + cid, true));
    }

    [Fact]
    public async Task 已排隊的遠端訊息_條件都沒變_這一輪結束接著送出()
    {
        using var h = new HubHarness();
        var (id, s) = RunningConversation(h);
        (await RemoteSendAsync(h, id, "遠端的訊息")).State.Should().Be(RemoteResultStates.Done);

        h.CompleteTurn(s);

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("遠端的訊息");
        h.Queue(id).Should().BeEmpty();
    }

    /// <summary>同一個 UI 工作裡：遠端 send 停在背景檢查資料夾，本機接著把模式調高（送出前的再查擋下）；回這個指令的結果</summary>
    private static async Task<RemoteOutcome> RemoteSendRacingLocalModeChangeAsync(HubHarness h, string id, string text, string cid)
    {
        var folder = h.Temp.CreateDirectory("proj");
        h.Ui.Invoke(() => h.Hub.GetConversation(id)!.WorkingDirectory = folder);
        var body = RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("send", id, new { text }), cid: cid);
        var task = h.Ui.Invoke(() =>
        {
            var running = h.Hub.ExecuteRemoteCommandAsync(body, _ => { });
            h.Hub.HandleCommand(h.Main, HubHarness.MakeCommand("setOption", new { conversationId = id, key = "permission", value = "FullAccess" }));
            return running;
        });
        return await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task 遠端send_送出前被留下_這個指令回policy_文字不留在這台的佇列()
    {
        using var h = new HubHarness();
        var (id, s) = IdleConversation(h);

        var outcome = await RemoteSendRacingLocalModeChangeAsync(h, id, "從手機送的", RemoteEntrySamples.NewCid());

        // rejected＝什麼都沒執行：伺服器把文字還給瀏覽器的輸入框，這台不再留一份（不然同一段輸入之後會送進代理兩次；合併前審查 AMO-02、F1）
        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Policy, RemoteGateSteps.Handler);
        outcome.Message.Should().Contain("沒有送出");
        s.Sent.Count.Should().Be(1);
        Entries(h, id).Should().BeEmpty();
        Notices(h, id).Should().Be(0, "沒有留在這台，本機不用確認");
        HeldToasts(h).Should().Be(0);

        // 之後本機送一則、正常結束：遠端那段文字不會被接著送出
        h.Send(h.Main, id, "本機的");
        await h.WaitUntilAsync(() => h.Sessions.Sum(x => x.Sent.Count) == 2, "本機那則送進代理（工作資料夾在背景檢查）");
        h.CompleteTurn(h.Sessions[^1]);
        await h.Ui.DrainAsync();
        h.Sessions.SelectMany(x => x.Sent.All).Select(x => x.Input.Text).Should().NotContain(t => t.Contains("從手機送的"));
        Entries(h, id).Should().BeEmpty();
    }

    [Fact]
    public async Task 遠端空送_送出前被留下_回policy_佇列那則放回最前面()
    {
        using var h = new HubHarness();
        var (id, s) = PausedQueue(h);
        var cid = RemoteEntrySamples.NewCid();

        var outcome = await RemoteSendRacingLocalModeChangeAsync(h, id, "", cid);

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Policy, RemoteGateSteps.Handler);
        outcome.Message.Should().Contain("留在那台電腦的佇列");
        s.Sent.Count.Should().Be(1);
        var held = Entries(h, id).Single();
        (held.Text, held.Origin?.CommandId, held.Held).Should().Be(("排隊A", cid, true), "空送取出的那則本來就在佇列：放回最前面，不會因為這個指令被拒而不見");
        Notices(h, id).Should().Be(1, "本機看得到提示，可以確認後按送出或從佇列取消");
    }

    // ───────────────────────────── 拒絕附言的來源（D22） ─────────────────────────────

    /// <summary>執行中的 Codex 對話畫出一張核准卡，遠端以附言拒絕；回（對話 id、會話、這個指令的來源）</summary>
    private static async Task<(string Id, FakeSession Session, RemoteOrigin Origin)> RemoteDenyAsync(HubHarness h, string feedback, Action<FakeSession>? started = null)
    {
        RemoteEntrySamples.AllowRemote(h, allowFullAccess: true);
        var (id, s) = h.StartConversation(h.Main, h.Codex, "刪除暫存檔");
        started?.Invoke(s);
        h.Raise(s, new AgentApprovalEvent("0", "執行指令", "bash", "rm -rf tmp", "rm -rf tmp", "exec-1", CanAllowForSession: true));
        var card = h.Items(id).Single(i => i.Kind == AgentItemKind.Approval);
        var cid = RemoteEntrySamples.NewCid();

        var outcome = await RemoteEntrySamples.RunAsync(h, RemoteEntrySamples.Body(RemoteEntrySamples.Cmd("approve", id,
            new { itemId = card.Id, choice = "deny", message = feedback, requestDigest = card.RequestDigest }), cid: cid));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var origin = new RemoteOrigin(cid, h.Conversation(id)!.PermissionMode, outcome.Tier, outcome.FullAccess);
        h.OnUi(() => h.Hub.PendingFeedbackForTest(id)).Single().RemoteOrigin.Should().Be(origin, "拒絕附言是代理輸入，帶這個指令的來源");
        return (id, s, origin);
    }

    [Fact]
    public async Task 拒絕附言_遠端deny帶附言_PendingFeedback帶來源_重新啟動後放回佇列的那則也帶()
    {
        using var h = new HubHarness();
        var (id, _, origin) = await RemoteDenyAsync(h, "請改用資源回收筒");
        QueueStore.Load(h.Store.GetConversationDirectory(id))!.PendingFeedback.Single().RemoteOrigin.Should().Be(origin, "拒絕當下就寫檔，來源一起存");

        // 這一輪沒結束就關掉 app：重新啟動時（RestoreQueue）說明排到佇列最前面，帶著來源
        await h.RestartAsync();

        var entry = Entries(h, id).Single();
        entry.Text.Should().Be("（我剛才拒絕了那個操作）請改用資源回收筒");
        entry.Origin.Should().Be(origin);
        entry.Id.Should().Be("r" + origin.CommandId);
    }

    [Fact]
    public async Task 拒絕附言_一輪失敗時放回佇列的那則帶來源()
    {
        using var h = new HubHarness();
        var (id, s, origin) = await RemoteDenyAsync(h, "請改用資源回收筒");

        // 這一輪失敗（AgentHub.Events.cs 的 CompleteTurn）：說明排到佇列最前面、跟著佇列暫停，帶著來源
        h.CompleteTurn(s, AgentTurnOutcome.Failed);

        s.Sent.Count.Should().Be(1);
        var entry = Entries(h, id).Single();
        (entry.Text, entry.Origin, entry.Id).Should().Be(("（我剛才拒絕了那個操作）請改用資源回收筒", origin, "r" + origin.CommandId));
    }

    [Fact]
    public async Task 拒絕附言_一輪結束接著送時帶來源_模式調高就留下()
    {
        using var h = new HubHarness();
        var (id, s, origin) = await RemoteDenyAsync(h, "請改用資源回收筒");

        // 拒絕之後本機把模式調高：這一輪因拒絕結束、接著送附言時（AgentHub.Events.cs）照來源重查，留在佇列
        h.Command(h.Main, "setOption", new { conversationId = id, key = "permission", value = "FullAccess" });
        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        s.Sent.Count.Should().Be(1, "附言是遠端的代理輸入：模式調高之後不送");
        var held = Entries(h, id).Single();
        (held.Text, held.Origin, held.Id, held.Held).Should().Be(("（我剛才拒絕了那個操作）請改用資源回收筒", origin, "r" + origin.CommandId, true));
        Notices(h, id).Should().Be(1);
    }

    [Fact]
    public async Task 拒絕附言_一輪結束接著送_條件沒變照常送出()
    {
        using var h = new HubHarness();
        var (id, s, _) = await RemoteDenyAsync(h, "請改用資源回收筒");

        h.CompleteTurn(s, AgentTurnOutcome.Interrupted);

        s.Sent.Count.Should().Be(2);
        s.Sent.All[1].Input.Text.Should().Be("（我剛才拒絕了那個操作）請改用資源回收筒");
        h.Queue(id).Should().BeEmpty();
    }

    [Fact]
    public async Task 拒絕附言_遠端deny帶附言_CLI更新後沒有接續_放回佇列的那則也帶()
    {
        using var h = new HubHarness();
        var codexOld = CliIdentities.Codex("0.160.1", fileId: 1);
        await h.SwitchCliAsync(AgentProvider.Codex, null, codexOld);
        h.Ui.Invoke(() => h.Hub.BackgroundSettle = TimeSpan.Zero);
        var (id, s, origin) = await RemoteDenyAsync(h, "請改用資源回收筒", started: session => h.ProcessStarted(session, "p1", codexOld));
        s.SafePoint = true;
        s.SucceedRestarts();
        var participant = h.Cli.UpdateParticipant!;
        (await Task.Run(() => participant.PrepareForUpdateAsync(AgentProvider.Codex, CliUpdateUrgency.Normal, CancellationToken.None))
            .WaitAsync(TimeSpan.FromSeconds(5))).Held.Should().Be(1);

        // 更新失敗、這一輪沒有接續（WrapUpInterruptedTurn）：說明排到佇列最前面，帶著來源
        participant.UpdateFinished(new CliUpdateCompletion(AgentProvider.Codex, CliUpdateOutcome.Failed, null, "npm 更新失敗：EBUSY"));
        await h.Ui.DrainAsync();

        h.IsRunning(id).Should().BeFalse();
        var entry = Entries(h, id).Single();
        entry.Text.Should().Be("（我剛才拒絕了那個操作）請改用資源回收筒");
        entry.Origin.Should().Be(origin);
    }
}
