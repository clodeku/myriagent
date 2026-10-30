using System.Security.Cryptography;
using Myriagent.Accounts;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 遠端開新對話（WP4.4 計畫步 9；D26）：對話 id 由 cid 推得，立刻存成正式對話（不是草稿、不留遠端檢視）、出現在清單；資料夾照指令給的
/// （沒給是對話自己的 workspace，不取最近資料夾）；有文字就以這個指令的來源送出，不等它。<b>rejected 一定表示什麼都沒建</b>：
/// id 撞到、回收區、資料夾不行、帳戶未登入、上傳還沒實作都在建立之前擋下，不輸出 sendRejected。這台的政策是 Control、不允許遠端完全自動
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubRemoteCreateTests
{
    private const string HeldNoticeText = "從遠端排進佇列的訊息暫停了：這個對話的權限模式或這台電腦的遠端操作設定已經改變。請確認後按送出，或從佇列取消。";

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>conversationCreate：預設是 Claude 主帳戶、每步詢問；<paramref name="fields"/> 的值是 null 時拿掉那個欄位</summary>
    private static JObject Create(params (string Key, JToken? Value)[] fields)
    {
        var command = RemoteSamples.Command(RemoteCommandCatalog.ConversationCreate, ("accountId", "acc-claude"), ("permissionMode", "Ask"));
        foreach (var (key, value) in fields)
        {
            if (value == null)
                command.Remove(key);
            else
                command[key] = value;
        }

        return command;
    }

    /// <summary>經中樞的入口執行；回結果與這個指令推得的對話 id</summary>
    private static async Task<(RemoteOutcome Outcome, string Id)> RunCreateAsync(HubHarness h, JObject create, Recorder<JObject>? output = null, string? cid = null)
    {
        var body = RemoteEntrySamples.Body(create, cid: cid);
        var outcome = await RemoteEntrySamples.RunAsync(h, body, output);
        return (outcome, RemoteConversationIdRules.FromCommandId(body.Cid)!);
    }

    private static int ConversationFolders(HubHarness h) => Directory.Exists(h.Store.RootDirectory) ? Directory.GetDirectories(h.Store.RootDirectory).Length : 0;

    /// <summary>什麼都沒建：中樞沒有這個 id（含只在記憶體的草稿）、沒有存檔、對話資料夾數不變</summary>
    private static void NothingCreated(HubHarness h, string id, int folders)
    {
        h.Conversation(id).Should().BeNull("rejected 一定表示什麼都沒建（P45 D33）");
        h.OnUi(() => h.Store.LoadConversations()).Should().NotContain(m => m.Id == id);
        ConversationFolders(h).Should().Be(folders);
    }

    private static IReadOnlyList<(string Id, string Text, RemoteOrigin? Origin, bool Held)> Entries(HubHarness h, string conversationId)
        => h.OnUi(() => h.Hub.QueueEntriesForTest(conversationId));

    /// <summary>長檔名、正規化之後的資料夾（探針套用的形式）</summary>
    private static string Normalized(string path)
        => RemoteFolderRules.TryNormalize(WindowsRemoteFolderFileSystem.Instance.LongPath(path), out var normalized) ? normalized : throw new InvalidOperationException(path);

    private static bool InList(JObject message, string id) => ((JArray)message["items"]!).Any(i => i.Value<string>("id") == id);

    /// <summary>
    /// 探針用的檔案系統：固定磁碟；<paramref name="ghost"/>＝探針看到的資料夾都存在（長檔名照原樣），建立之後送出時才發現資料夾不在；
    /// <paramref name="hang"/>＝查磁碟種類時卡住（測試放開之前）
    /// </summary>
    private sealed class TestFolderFileSystem(bool ghost = false, ManualResetEventSlim? hang = null) : IRemoteFolderFileSystem
    {
        public RemoteDriveKind DriveKind(string fullPath)
        {
            hang?.Wait(TimeSpan.FromSeconds(30));
            return RemoteDriveKind.Fixed;
        }

        public string? LongPath(string fullPath) => ghost ? fullPath : WindowsRemoteFolderFileSystem.Instance.LongPath(fullPath);

        public bool DirectoryExists(string path) => ghost || Directory.Exists(path);

        public string? FinalPath(string normalizedPath) => ghost ? @"\\?\" + normalizedPath : WindowsRemoteFolderFileSystem.Instance.FinalPath(normalizedPath);
    }

    // ───────────────────────────── 建立 ─────────────────────────────

    [Fact]
    public async Task 建立_id是FromCommandId_出現在清單_已存檔_不是草稿()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var mark = h.Main.Mark();
        var sessions = h.Sessions.Count;

        var (outcome, id) = await RunCreateAsync(h, Create());

        outcome.Should().Be(new RemoteOutcome(RemoteResultStates.Done, null, null, 1, false, null));
        id.Should().MatchRegex("^[0-9a-f]{32}$");
        ConversationIds.ToWire(id).Should().Be(id, "推得的 id 就是線上 id");
        var meta = h.Conversation(id)!;
        (meta.Id, meta.AccountId, meta.Provider, meta.PermissionMode, meta.Title).Should().Be((id, "acc-claude", AgentProvider.Claude, AgentPermissionMode.Ask, "新對話"));
        h.OnUi(() => h.Store.LoadConversations()).Should().ContainSingle(m => m.Id == id, "立即存成正式對話");
        await h.Main.WaitForAsync("conversations", m => InList(m, id), mark);
        h.Items(id).Should().BeEmpty();
        h.Sessions.Count.Should().Be(sessions, "沒有文字：不啟動 CLI");
    }

    [Fact]
    public async Task 建立_遠端檢視id不在ViewIds()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);

        var (outcome, id) = await RunCreateAsync(h, Create());

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        h.OnUi(() => h.Hub.ViewIdsForTest(id)).Should().BeEmpty("遠端檢視不是任何對話的檢視（D26）");
        h.Command(h.Main, "open", new { conversationId = id });
        h.OnUi(() => h.Hub.ViewIdsForTest(id)).Should().Equal("main");
    }

    [Fact]
    public async Task id已存在_conflict_什麼都沒建()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var cid = RemoteEntrySamples.NewCid();
        var (first, id) = await RunCreateAsync(h, Create(("title", "第一次")), cid: cid);
        first.State.Should().Be(RemoteResultStates.Done, first.Message);
        var folders = ConversationFolders(h);

        // 同一個 cid 再來一次（中樞看到的是已在清單的 id）
        var (again, sameId) = await RunCreateAsync(h, Create(("title", "第二次")), cid: cid);

        sameId.Should().Be(id);
        RemoteEntrySamples.ShouldBeRejected(again, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        h.Conversation(id)!.Title.Should().Be("第一次");
        ConversationFolders(h).Should().Be(folders);

        // 對話資料夾已在磁碟上（沒有載入的對話）
        var otherCid = RemoteEntrySamples.NewCid();
        var otherId = RemoteConversationIdRules.FromCommandId(otherCid)!;
        Directory.CreateDirectory(h.Store.GetConversationDirectory(otherId));
        var (onDisk, _) = await RunCreateAsync(h, Create(), cid: otherCid);
        RemoteEntrySamples.ShouldBeRejected(onDisk, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        h.Conversation(otherId).Should().BeNull();
    }

    [Fact]
    public async Task id在回收區_conflict()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var cid = RemoteEntrySamples.NewCid();
        var id = RemoteConversationIdRules.FromCommandId(cid)!;
        Directory.CreateDirectory(Path.Combine(ConversationTrash.RootFor(h.Store.RootDirectory), id));
        var folders = ConversationFolders(h);

        var (outcome, _) = await RunCreateAsync(h, Create(("text", "hi")), cid: cid);

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        outcome.Message.Should().Contain("回收區");
        NothingCreated(h, id, folders);
    }

    [Fact]
    public async Task conversationCreate_id是副本_conflict()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var cid = RemoteEntrySamples.NewCid();
        var id = RemoteConversationIdRules.FromCommandId(cid)!;
        // 其他電腦的對話在這台的副本（WP4.3）：清單裡已拿掉（例如關掉顯示），儲存端仍記得它的 id
        h.Store.MarkReplica(id);
        h.Conversation(id).Should().BeNull("前提：清單裡沒有它");
        var folders = ConversationFolders(h);

        var (outcome, _) = await RunCreateAsync(h, Create(("text", "hi")), cid: cid);

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        outcome.Message.Should().Contain("另一台電腦");
        NothingCreated(h, id, folders);
    }

    // ───────────────────────────── 資料夾 ─────────────────────────────

    [Fact]
    public async Task folder省略_用對話自己的workspace_不取最近資料夾()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        RemoteEntrySamples.AllowRemote(h);
        var (localId, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        await h.SetFolderAsync(h.Main, localId, folder.CreateDirectory("recent"));
        h.OnUi(() => h.Settings.RecentFolders.ToList()).Should().NotBeEmpty("前提：有最近使用的資料夾");
        h.Ui.Invoke(() => h.Settings.LastAccountId = "acc-codex");
        var mark = h.Main.Mark();

        var (outcome, id) = await RunCreateAsync(h, Create(("folder", null)));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        h.Conversation(id)!.WorkingDirectory.Should().BeNull("對話自己的 workspace（不取最近資料夾）");
        var listed = await h.Main.WaitForAsync("conversations", m => InList(m, id), mark);
        var item = ((JArray)listed["items"]!).Single(i => i.Value<string>("id") == id);
        item.Value<bool>("customFolder").Should().BeFalse();
        item.Value<string>("workingDirectory").Should().Be(h.Store.GetDefaultWorkingDirectory(id));
        h.OnUi(() => h.Settings.LastAccountId).Should().Be("acc-codex", "restoring 的路徑不寫上次使用的帳戶");
    }

    [Fact]
    public async Task folder給了_探針Ok才建立_不進最近資料夾()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        RemoteEntrySamples.AllowRemote(h);
        var proj = folder.CreateDirectory("proj");
        var recent = h.OnUi(() => h.Settings.RecentFolders.ToList());

        var (outcome, id) = await RunCreateAsync(h, Create(("folder", proj)));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        h.Conversation(id)!.WorkingDirectory.Should().Be(Normalized(proj));
        h.OnUi(() => h.Store.LoadConversations().Single(m => m.Id == id).WorkingDirectory).Should().Be(Normalized(proj));
        h.OnUi(() => h.Settings.RecentFolders.ToList()).Should().Equal(recent, "遠端選的資料夾不進最近資料夾（NoRememberFolder）");
    }

    [Fact]
    public async Task folder不存在_conflict而且沒有建立()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        RemoteEntrySamples.AllowRemote(h);
        var folders = ConversationFolders(h);

        var (outcome, id) = await RunCreateAsync(h, Create(("folder", folder.Combine("missing")), ("text", "hi")));

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        outcome.Message.Should().Contain("找不到資料夾");
        NothingCreated(h, id, folders);
    }

    [Fact]
    public async Task folder是禁止的資料夾_policy_沒有建立()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var folders = ConversationFolders(h);

        foreach (var forbidden in new[] { h.Temp.Root, h.Temp.CreateDirectory("logs") })
        {
            var (outcome, id) = await RunCreateAsync(h, Create(("folder", forbidden)));
            RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Policy, RemoteGateSteps.Handler);
            NothingCreated(h, id, folders);
        }

        var (invalid, invalidId) = await RunCreateAsync(h, Create(("folder", @"\\srv\share\proj")));
        RemoteEntrySamples.ShouldBeRejected(invalid, RemoteResultCodes.Invalid, RemoteGateSteps.Handler);
        NothingCreated(h, invalidId, folders);
    }

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task folder是指到資料根目錄的junction_policy_沒有建立()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        using var junctions = new TestJunctions();
        RemoteEntrySamples.AllowRemote(h);
        Directory.CreateDirectory(h.Store.RootDirectory);
        var folders = ConversationFolders(h);
        var link = junctions.Create(folder.Combine("link-data"), h.Temp.Root);

        foreach (var path in new[] { link, Path.Combine(link, "conversations") })
        {
            var (outcome, id) = await RunCreateAsync(h, Create(("folder", path), ("text", "hi")));
            RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Policy, RemoteGateSteps.Handler);
            NothingCreated(h, id, folders);
        }
    }

    [Fact]
    public async Task 檢查資料夾期間同一個id進了回收區_conflict_沒有建立()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        using var hang = new ManualResetEventSlim();
        RemoteEntrySamples.AllowRemote(h);
        h.Ui.Invoke(() => h.Hub.RemoteFolderFileSystem = new TestFolderFileSystem(hang: hang));
        var cid = RemoteEntrySamples.NewCid();
        var id = RemoteConversationIdRules.FromCommandId(cid)!;
        var folders = ConversationFolders(h);

        var pending = RunCreateAsync(h, Create(("folder", folder.CreateDirectory("proj"))), cid: cid);
        Directory.CreateDirectory(Path.Combine(ConversationTrash.RootFor(h.Store.RootDirectory), id));
        hang.Set();
        var (outcome, _) = await pending;

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        NothingCreated(h, id, folders);
    }

    // ───────────────────────────── 文字與標題 ─────────────────────────────

    [Fact]
    public async Task 有文字_建立並送出_模式照代理種類正規化_標題取自文字()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var sessions = h.Sessions.Count;

        var (outcome, id) = await RunCreateAsync(h, Create(("accountId", "acc-codex"), ("permissionMode", "Plan"), ("text", "  幫我整理上週的訂單\n第二行")));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        await h.WaitUntilAsync(() => h.Sessions.Count == sessions + 1 && h.Sessions[^1].Sent.Count == 1, "建立之後送出第一則");
        h.Sessions[^1].Sent.All[0].Input.Text.Should().Be("幫我整理上週的訂單\n第二行");
        var meta = h.Conversation(id)!;
        (meta.Provider, meta.PermissionMode, meta.Title).Should().Be((AgentProvider.Codex, AgentPermissionMode.Ask, "幫我整理上週的訂單"), "Codex 沒有規劃模式（換成每步詢問）");
        h.Items(id).Single(i => i.Kind == AgentItemKind.User).Text.Should().Be("幫我整理上週的訂單\n第二行");
        h.IsRunning(id).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n ")]
    public async Task 沒文字_存成空對話_done(string? text)
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var sessions = h.Sessions.Count;

        // null＝省略這個欄位（string 的 null 轉成 JToken 會變成 JSON 的 null，形狀檢查不收）
        var (outcome, id) = await RunCreateAsync(h, Create(("text", text is null ? null : (JToken)text)));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        h.OnUi(() => h.Store.LoadConversations()).Should().ContainSingle(m => m.Id == id);
        h.Conversation(id)!.Title.Should().Be("新對話");
        h.Items(id).Should().BeEmpty();
        await h.Ui.DrainAsync();
        h.Sessions.Count.Should().Be(sessions);
    }

    [Fact]
    public async Task title給了_用它_model與effort有給才套用()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);

        var (outcome, id) = await RunCreateAsync(h, Create(("title", "  遠端給的標題  "), ("text", "文字的第一行"), ("model", "遠端的模型"), ("effort", "high")));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var meta = h.Conversation(id)!;
        (meta.Title, meta.Model, meta.Effort).Should().Be(("遠端給的標題", "遠端的模型", "high"));
        h.OnUi(() => h.Settings.AccountDefaults.TryGetValue("acc-claude", out var defaults) ? defaults.Model : null)
            .Should().BeNull("模型不記成帳戶預設（persistAccountDefaults: false）");

        var (plain, plainId) = await RunCreateAsync(h, Create(("model", " "), ("effort", null)));
        plain.State.Should().Be(RemoteResultStates.Done, plain.Message);
        var defaultsMeta = h.Conversation(plainId)!;
        (defaultsMeta.Model, defaultsMeta.Effort).Should().Be((null, null), "空白的模型與沒給的強度用帳戶預設");
    }

    [Fact]
    public async Task 建立之後送出被留下_結果仍是done_訊息在新對話的佇列_來源的模式是正規化後的()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        // 5d 時帳戶沒有預先放行（層級 1）；建立之後送出前的再查看到對話有（要 FullAccess）→ 留下
        h.Ui.Invoke(() =>
        {
            h.Hub.AccountPreApprovedForTest = (_, _) => false;
            h.Hub.ConversationPreApprovedForTest = _ => true;
        });
        var cid = RemoteEntrySamples.NewCid();
        var sessions = h.Sessions.Count;

        var (outcome, id) = await RunCreateAsync(h, Create(("accountId", "acc-codex"), ("permissionMode", "Plan"), ("text", "從網頁開的")), cid: cid);

        outcome.Should().Be(new RemoteOutcome(RemoteResultStates.Done, null, null, 1, false, null));
        await h.WaitUntilAsync(() => h.Hub.QueueEntriesForTest(id).Count == 1, "第一則留在新對話的佇列");
        Entries(h, id).Single().Should().Be(("r" + cid, "從網頁開的", new RemoteOrigin(cid, AgentPermissionMode.Ask, 1, false), true));
        h.Items(id).Count(i => i.Kind == AgentItemKind.Notice && i.Text == HeldNoticeText).Should().Be(1);
        h.OnUi(() => h.Store.LoadConversations()).Should().ContainSingle(m => m.Id == id, "對話照樣建立（P45 D33：done 才會建）");
        h.Sessions.Count.Should().Be(sessions, "沒有送進 CLI");
    }

    [Fact]
    public async Task 建立之後送出被拒_結果仍是done_不輸出sendRejected_文字與提示給這台的視窗()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        RemoteEntrySamples.AllowRemote(h);
        // 探針看到資料夾存在，送出前的檢查（真的檔案系統）發現不在
        h.Ui.Invoke(() => h.Hub.RemoteFolderFileSystem = new TestFolderFileSystem(ghost: true));
        var output = new Recorder<JObject>();
        var mark = h.Main.Mark();

        var (outcome, id) = await RunCreateAsync(h, Create(("folder", folder.Combine("ghost")), ("text", "被拒的第一則")), output);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var rejected = await h.Main.WaitForAsync("sendRejected", null, mark);
        (rejected.Value<string>("conversationId"), rejected.Value<string>("text")).Should().Be((id, "被拒的第一則"));
        h.Main.Messages("toast", mark).Should().Contain(m => m.Value<string>("text")!.Contains("工作資料夾不存在"));
        output.All.Should().BeEmpty("結果已是 done，之後的輸出不再轉給瀏覽器");
        h.Conversation(id).Should().NotBeNull();
        h.Items(id).Should().BeEmpty();
    }

    [Fact]
    public async Task 被拒的每一種情況都不輸出sendRejected()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        RemoteEntrySamples.AllowRemote(h);
        var cid = RemoteEntrySamples.NewCid();
        (await RunCreateAsync(h, Create(), cid: cid)).Outcome.State.Should().Be(RemoteResultStates.Done);

        var cases = new (string Name, Action Arrange, JObject Command, string? Cid)[]
        {
            ("id 已存在", () => { }, Create(("text", "a")), cid),
            ("資料夾不存在", () => { }, Create(("folder", folder.Combine("missing")), ("text", "b")), null),
            ("禁止的資料夾", () => { }, Create(("folder", h.Temp.Root), ("text", "c")), null),
            ("上傳", () => { }, Create(("uploads", new JArray(RemoteSamples.Upload())), ("text", "d")), null),
            ("未登入", () => h.Ui.Invoke(() => h.Accounts.GetState("acc-claude").Status = AccountStatus.NotLoggedIn), Create(("text", "e")), null),
        };

        foreach (var (name, arrange, command, caseCid) in cases)
        {
            arrange();
            var output = new Recorder<JObject>();
            var (outcome, _) = await RunCreateAsync(h, command, output, caseCid);
            outcome.State.Should().Be(RemoteResultStates.Rejected, name);
            output.All.Should().NotContain(m => m.Value<string>("type") == "sendRejected", $"{name}：被拒時伺服器自己以草稿 id 還原文字（P45 D22）");
        }

        h.Main.Messages("sendRejected").Should().BeEmpty();
    }

    // ───────────────────────────── 帳戶與上傳 ─────────────────────────────

    [Theory]
    [InlineData("NotLoggedIn")]
    [InlineData("NotInstalled")]
    public async Task 帳戶未登入或未安裝_conflict_什麼都沒建(string status)
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        h.Ui.Invoke(() => h.Accounts.GetState("acc-claude").Status = Enum.Parse<AccountStatus>(status));
        var folders = ConversationFolders(h);

        var (outcome, id) = await RunCreateAsync(h, Create(("text", "hi")));

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Conflict, RemoteGateSteps.Handler);
        outcome.Message.Should().Contain("Claude 主帳戶");
        NothingCreated(h, id, folders);
    }

    [Fact]
    public async Task uploads不是空的_沒有和伺服器連線時unsupported_什麼都沒建()
    {
        // WP4.7 接上登記點之後（計畫步 13），拒絕的是「收不到網頁上傳的檔案」；有傳輸的情形見 RemoteFilesTests
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var folders = ConversationFolders(h);

        var (outcome, id) = await RunCreateAsync(h, Create(("uploads", new JArray(RemoteSamples.Upload())), ("text", "附檔")));

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Unsupported, RemoteGateSteps.Handler);
        outcome.Message.Should().Contain("收不到網頁上傳的檔案");
        NothingCreated(h, id, folders);
    }

    [Fact]
    public async Task 帳戶不存在_invalid在5c_Antigravity帳戶照常建立()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        h.Ui.Invoke(() => h.Accounts.Seed([new AgentAccount { Id = "local-antigravity", Provider = AgentProvider.Antigravity, Label = "Antigravity", Color = "#4285F4" }],
            AccountStatus.Ready));
        var folders = ConversationFolders(h);

        var (missing, missingId) = await RunCreateAsync(h, Create(("accountId", "acc-missing"), ("text", "hi")));
        RemoteEntrySamples.ShouldBeRejected(missing, RemoteResultCodes.Invalid, RemoteGateSteps.Conversation);
        NothingCreated(h, missingId, folders);

        // 2026-10-22 起四種都同步：Antigravity 的帳戶也能從遠端開新對話（沒有 prov1 時由同步引擎延後發布）
        var (antigravity, _) = await RunCreateAsync(h, Create(("accountId", "local-antigravity"), ("permissionMode", "AcceptEdits"), ("text", "hi")));
        antigravity.State.Should().Be(RemoteResultStates.Done, antigravity.Message);
    }

    [Fact]
    public async Task 不改LastAccountId_不記帳戶預設_settings雜湊不變()
    {
        using var h = new HubHarness();
        using var folder = new TempDirectory("remote-folder");
        RemoteEntrySamples.AllowRemote(h);
        // 先在本機用主帳戶開一個（對話預設值的遷移只在第一次取用時做）；遠端用從沒用過的副帳戶
        var (_, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Ui.Invoke(() =>
        {
            h.Settings.LastAccountId = "acc-codex";
            h.Settings.Save();
        });
        var saved = Sha256(AppPaths.SettingsFile);
        h.Ui.Invoke(() => h.Settings.Save());
        Sha256(AppPaths.SettingsFile).Should().Be(saved, "前提：同一份設定連續存兩次內容相同（計畫 R10）");
        h.OnUi(() => h.Settings.AccountDefaults.ContainsKey("acc-claude2")).Should().BeFalse("前提：副帳戶還沒有帳戶預設");
        var recent = h.OnUi(() => h.Settings.RecentFolders.ToList());
        var sessions = h.Sessions.Count;

        var (outcome, id) = await RunCreateAsync(h, Create(("accountId", "acc-claude2"), ("folder", folder.CreateDirectory("proj")), ("model", "遠端的模型"),
            ("text", "從網頁開的")));
        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        await h.WaitUntilAsync(() => h.Sessions.Count == sessions + 1 && h.Sessions[^1].Sent.Count == 1, "送出第一則（會存設定）");

        h.Conversation(id)!.Model.Should().Be("遠端的模型");
        h.OnUi(() => h.Settings.LastAccountId).Should().Be("acc-codex", "遠端不寫這台的持久設定（D23）");
        h.OnUi(() => h.Settings.RecentFolders.ToList()).Should().Equal(recent);
        h.OnUi(() => h.Settings.AccountDefaults.ContainsKey("acc-claude2")).Should().BeFalse("建立時惰性補上的空帳戶預設不留");
        h.Ui.Invoke(() => h.Hub.RunMaintenance());
        await h.Ui.DrainAsync();
        Sha256(AppPaths.SettingsFile).Should().Be(saved, "送出時存的設定與之前內容相同");
    }

    [Fact]
    public async Task 處理者_沒授權就Reject_什麼都沒建()
    {
        using var h = new HubHarness();
        RemoteEntrySamples.AllowRemote(h);
        var handlers = h.OnUi(() => h.Hub.RemoteHandlersForTest);
        var cid = RemoteEntrySamples.NewCid();
        var context = new RemoteViewContext(cid, RemoteSamples.DeviceId, null, null, 1, false, null, 2_000, false, RemoteEntrySamples.IssuerLabel);
        var folders = ConversationFolders(h);

        h.OnUi(() => handlers.Any(handler => handler.TryHandle(context, h.Main, RemoteCommandCatalog.ConversationCreate, Create(("text", "hi")))))
            .Should().BeTrue("conversationCreate 是這一包的指令");
        var outcome = await context.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        RemoteEntrySamples.ShouldBeRejected(outcome, RemoteResultCodes.Invalid, RemoteGateSteps.Handler);
        NothingCreated(h, RemoteConversationIdRules.FromCommandId(cid)!, folders);
    }
}
