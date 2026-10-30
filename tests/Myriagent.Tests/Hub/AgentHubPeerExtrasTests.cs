using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 桌面遠端操作的附件（peerUpload）、檔案下載（peerFileSave）與在其他電腦開新對話（draftSetDevice、遠端草稿的第一次 send）：sync-protocol 14.7、web-protocol 第二節
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class AgentHubPeerExtrasTests
{
    private static readonly string OwnDevice = "d_" + new string('A', 26);

    private static readonly string OtherDevice = "d_" + new string('B', 26);

    private static readonly string Logical = "la_" + new string('C', 26);

    private const string MediaRef = "0123456789ABCDEFGHJKMNPQRS";

    private static string NewId() => SyncIds.NewUlid(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private sealed class FakeSink : IPeerCommandSink
    {
        public List<(string CommandId, JObject Cmd)> Calls { get; } = [];

        public string? DeviceId => OwnDevice;

        public void SubmitPeerCommand(string commandId, JObject cmd) => Calls.Add((commandId, cmd));
    }

    private sealed class FakeUploader : IPeerUploader
    {
        public List<(string UploadId, string Path, byte[] Bytes)> Calls { get; } = [];

        public Task<PeerUploadResult> UploadAsync(string uploadId, string path, CancellationToken ct)
        {
            Calls.Add((uploadId, path, File.ReadAllBytes(path)));
            return Task.FromResult(new PeerUploadResult(201, $$"""{"uploadId":"{{uploadId}}","sha256":"ab","size":3}"""));
        }
    }

    private sealed class FakeFiles(PeerFile? file) : IPeerFileSource
    {
        public List<string> Keys { get; } = [];

        public Task<PeerFile?> FetchFileAsync(string key, CancellationToken ct)
        {
            Keys.Add(key);
            return Task.FromResult(file);
        }
    }

    private static (string Id, FakeSink Sink) LiveReplica(HubHarness h)
    {
        var (id, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        h.Ui.Invoke(() => h.Hub.MarkReplicaForTest(id));
        var sink = new FakeSink();
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachPeerSink(sink);
            h.Hub.PeerLive = true;
        });
        return (id, sink);
    }

    // ───── peerUpload ─────

    [Fact]
    public async Task peerUpload_本機路徑交給上傳者_結果只回給那個檢視()
    {
        using var h = new HubHarness();
        var (id, _) = LiveReplica(h);
        var uploader = new FakeUploader();
        h.Hub.PeerUploaderForTest = uploader;
        var file = h.Temp.Combine("a.txt");
        File.WriteAllText(file, "abc");
        var mark = h.Main.Mark();

        h.Command(h.Main, "peerUpload", new { uploadId = "u1", conversationId = id }, [file]);
        await h.WaitUntilAsync(() => h.Main.Messages("peerUpload", mark).Count > 0, "回結果");

        uploader.Calls.Should().ContainSingle().Which.UploadId.Should().Be("u1");
        var reply = h.Main.Messages("peerUpload", mark).Single();
        (reply.Value<string>("uploadId"), reply.Value<int>("status")).Should().Be(("u1", 201));
        JObject.Parse(reply.Value<string>("json")!)["uploadId"]!.Value<string>().Should().Be("u1");
    }

    [Fact]
    public async Task peerUpload_沒有路徑回no_path_帶dataUrl就解碼上傳()
    {
        using var h = new HubHarness();
        var (id, _) = LiveReplica(h);
        var uploader = new FakeUploader();
        h.Hub.PeerUploaderForTest = uploader;
        var mark = h.Main.Mark();

        h.Command(h.Main, "peerUpload", new { uploadId = "u2", conversationId = id });
        await h.Ui.DrainAsync();
        var noPath = h.Main.Messages("peerUpload", mark).Single();
        noPath.Value<int>("status").Should().Be(400);
        noPath.Value<string>("json").Should().Contain("no_path");

        h.Command(h.Main, "peerUpload", new { uploadId = "u2", conversationId = id, dataUrl = "data:image/png;base64," + Convert.ToBase64String("xyz"u8.ToArray()), name = "image.png" });
        await h.WaitUntilAsync(() => h.Main.Messages("peerUpload", mark).Count > 1, "回結果");

        uploader.Calls.Should().ContainSingle().Which.Bytes.Should().Equal("xyz"u8.ToArray());
        File.Exists(uploader.Calls[0].Path).Should().BeFalse("暫存檔上傳完就刪掉");
    }

    [Fact]
    public async Task peerUpload_不是副本或不能操作_回unavailable_不上傳()
    {
        using var h = new HubHarness();
        var (local, s) = h.StartConversation(h.Main, h.Claude);
        h.CompleteTurn(s);
        var uploader = new FakeUploader();
        h.Hub.PeerUploaderForTest = uploader;
        var file = h.Temp.Combine("b.txt");
        File.WriteAllText(file, "abc");
        var mark = h.Main.Mark();

        h.Command(h.Main, "peerUpload", new { uploadId = "u3", conversationId = local }, [file]);
        await h.Ui.DrainAsync();

        h.Main.Messages("peerUpload", mark).Single().Value<int>("status").Should().Be(503);
        uploader.Calls.Should().BeEmpty();
    }

    [Fact]
    public void PeerUploadRules_來源檢查()
    {
        PeerUploadRules.CheckSource(null, null).Should().Be(PeerUploadRules.NoPath);
        PeerUploadRules.CheckSource(null, "data:image/png;base64,AAAA").Should().BeNull();
        PeerUploadRules.CheckSource(null, "http://x").Should().Be(PeerUploadRules.NoPath);
        PeerUploadRules.CheckSource(@"\\server\share\a.txt", null).Should().Be(PeerUploadRules.NotFound, "UNC 一律不認");
        PeerUploadRules.CheckSource(@"C:\沒有這個資料夾\a.txt", null).Should().Be(PeerUploadRules.NotFound);
    }

    // ───── peerFileSave ─────

    [Fact]
    public async Task peerFileSave_登記過的f鍵下載到下載資料夾_在檔案總管顯示()
    {
        using var h = new HubHarness();
        var (id, _) = LiveReplica(h);
        var downloads = h.Temp.Combine("Downloads");
        h.Hub.PeerDownloadsForTest = downloads;
        var files = new FakeFiles(new PeerFile("hello"u8.ToArray(), "報告.txt"));
        h.Hub.PeerFileSourceForTest = files;
        var key = OtherDevice + "-" + MediaRef;
        var cid = NewId();
        h.Command(h.Main, "fileFetch", new { conversationId = id, requestId = "r1", path = "a.txt", commandId = cid });
        h.Ui.Invoke(() => h.Hub.OnPeerReply(cid, new JObject { ["type"] = "fileRef", ["requestId"] = "r1", ["conversationId"] = id, ["url"] = "/f/" + key, ["name"] = "報告.txt" }));
        var mark = h.Main.Mark();

        h.Command(h.Main, "peerFileSave", new { conversationId = id, url = "/f/" + key, name = "報告.txt" });
        await h.WaitUntilAsync(() => h.Main.Messages("toast", mark).Count > 0, "提示");

        files.Keys.Should().Equal(key);
        var saved = Path.Combine(downloads, "報告.txt");
        File.ReadAllBytes(saved).Should().Equal("hello"u8.ToArray());
        h.Host.Revealed.All.Should().Contain(saved);
        h.Main.Messages("toast", mark).Single().Value<string>("level").Should().Be("success");
    }

    [Fact]
    public async Task peerFileSave_沒登記過的鍵不下載()
    {
        using var h = new HubHarness();
        var (id, _) = LiveReplica(h);
        var files = new FakeFiles(new PeerFile([1], "x"));
        h.Hub.PeerFileSourceForTest = files;
        var mark = h.Main.Mark();

        h.Command(h.Main, "peerFileSave", new { conversationId = id, url = "/f/" + OtherDevice + "-" + MediaRef, name = "x" });
        await h.Ui.DrainAsync();

        files.Keys.Should().BeEmpty();
        h.Main.Messages("toast", mark).Single().Value<string>("level").Should().Be("error");
    }

    [Theory]
    [InlineData("報告.txt", "報告.txt")]
    [InlineData(@"..\..\evil.exe", "evil.exe")]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("a\u202Eb.txt", "a_b.txt")]
    [InlineData("  ", PeerFileRules.DefaultName)]
    [InlineData("x...", "x")]
    public void PeerFileRules_安全的檔名(string name, string expected) => PeerFileRules.SafeFileName(name).Should().Be(expected);

    [Fact]
    public async Task PeerFileRules_同名不覆寫()
    {
        using var temp = new TempDirectory("peer-save");
        var root = temp.Combine();
        var first = await PeerFileRules.SaveAsync(root, "a.txt", [1]);
        var second = await PeerFileRules.SaveAsync(root, "a.txt", [2]);
        Path.GetFileName(first).Should().Be("a.txt");
        Path.GetFileName(second).Should().Be("a (2).txt");
        File.ReadAllBytes(first).Should().Equal(1);
    }

    [Fact]
    public void ReplicaMediaRules_伺服器給的相對網址()
    {
        var key = OtherDevice + "-" + MediaRef;
        ReplicaMediaRules.KeyOfRelativeUrl("/m/" + key).Should().Be((key, "media"));
        ReplicaMediaRules.KeyOfRelativeUrl("/t/" + key + "?s=160&v=3").Should().Be((key, "thumb"));
        ReplicaMediaRules.KeyOfRelativeUrl("/f/" + key).Should().Be((key, "file"));
        ReplicaMediaRules.KeyOfRelativeUrl("https://evil/f/" + key).Should().BeNull();
        ReplicaMediaRules.KeyOfRelativeUrl("//evil/f/" + key).Should().BeNull();
        ReplicaMediaRules.KeyOfRelativeUrl("/x/" + key).Should().BeNull();
        ReplicaMediaRules.KeyOfRelativeUrl("/f/not-a-key").Should().BeNull();
        ReplicaMediaRules.KeyOfRelativeUrl(null).Should().BeNull();
    }

    // ───── 在其他電腦開新對話 ─────

    [Fact]
    public async Task 遠端草稿_第一次send組conversationCreate交給同步_不啟動CLI()
    {
        using var h = new HubHarness();
        var sink = new FakeSink();
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachPeerSink(sink);
            h.Hub.PeerLive = true;
        });
        var draft = h.NewConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Command(h.Main, "draftSetDevice", new { conversationId = draft, deviceId = OtherDevice, accountId = Logical, provider = "Claude" });
        var header = (await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("deviceId") == OtherDevice, mark, conversationId: draft))["header"]!;
        header.Value<string>("deviceId").Should().Be(OtherDevice);
        header.Value<string>("accountId").Should().Be(Logical, "這台沒有連結這個邏輯帳戶：顯示邏輯 id");
        header["workingDirectory"]!.Type.Should().Be(JTokenType.Null);

        var sessions = h.Sessions.Count;
        var cid = NewId();
        h.Command(h.Main, "send", new { conversationId = draft, text = "在那台做事", commandId = cid });
        await h.Ui.DrainAsync();

        h.Sessions.Count.Should().Be(sessions, "不啟動這台的 CLI");
        var call = sink.Calls.Should().ContainSingle().Subject;
        call.CommandId.Should().Be(cid);
        call.Cmd.Value<string>("cmd").Should().Be("conversationCreate");
        (call.Cmd.Value<string>("deviceId"), call.Cmd.Value<string>("accountId"), call.Cmd.Value<string>("text")).Should().Be((OtherDevice, Logical, "在那台做事"));
        call.Cmd.ContainsKey("folder").Should().BeFalse();
    }

    [Fact]
    public async Task 遠端草稿_peerCreated之後副本出現_草稿換成副本()
    {
        using var h = new HubHarness();
        var sink = new FakeSink();
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachPeerSink(sink);
            h.Hub.PeerLive = true;
            h.Hub.ShowReplicas = true;
        });
        var draft = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "draftSetDevice", new { conversationId = draft, deviceId = OtherDevice, accountId = Logical, provider = "Claude" });
        var cid = NewId();
        h.Command(h.Main, "send", new { conversationId = draft, text = "hi", commandId = cid });
        await h.Ui.DrainAsync();
        var created = "abcdef0123456789abcdef0123456789";
        var mark = h.Main.Mark();

        h.Ui.Invoke(() => h.Hub.OnPeerReply(cid, new JObject { ["type"] = "peerCreated", ["commandId"] = cid, ["conversationId"] = created }));
        h.Main.Messages("peerCreated", mark).Should().BeEmpty("中樞自己處理");
        h.Main.Messages("conversationClosed", mark).Should().BeEmpty("副本還沒出現");
        var meta = new AgentConversation { Id = created, Provider = AgentProvider.Claude, Title = "hi", AccountId = string.Empty };
        h.Ui.Invoke(() => h.Hub.ApplyReplicaDelta(new ReplicaDelta([new ReplicaMeta(created, OtherDevice, meta, Running: true, Waiting: false, ItemsAvailable: true)], [], [])));
        await h.Ui.DrainAsync();

        var closed = h.Main.Messages("conversationClosed", mark).Should().ContainSingle().Subject;
        (closed.Value<string>("conversationId"), closed.Value<string>("replacedBy")).Should().Be((draft, created));
        h.Conversation(draft).Should().BeNull("草稿丟掉了");
    }

    [Fact]
    public async Task 遠端草稿_被拒時文字還給草稿()
    {
        using var h = new HubHarness();
        var sink = new FakeSink();
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachPeerSink(sink);
            h.Hub.PeerLive = true;
        });
        var draft = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "draftSetDevice", new { conversationId = draft, deviceId = OtherDevice, accountId = Logical, provider = "Claude" });
        var cid = NewId();
        h.Command(h.Main, "send", new { conversationId = draft, text = "被拒的", commandId = cid });
        await h.Ui.DrainAsync();
        var mark = h.Main.Mark();

        h.Ui.Invoke(() => h.Hub.OnPeerReply(cid, new JObject { ["type"] = "commandResult", ["commandId"] = cid, ["state"] = "rejected", ["code"] = "policy" }));
        await h.Ui.DrainAsync();

        var restored = h.Main.Messages("sendRejected", mark).Should().ContainSingle().Subject;
        (restored.Value<string>("conversationId"), restored.Value<string>("text")).Should().Be((draft, "被拒的"));
        h.Main.Messages("commandResult", mark).Should().ContainSingle("結果照樣轉給網頁（卡片與輸入區的追蹤）");

        var again = NewId();
        h.Command(h.Main, "send", new { conversationId = draft, text = "再送一次", commandId = again });
        await h.Ui.DrainAsync();
        sink.Calls.Should().HaveCount(2, "被拒之後可以再送");
    }

    [Fact]
    public async Task 遠端草稿_不能附加或用這台的資料夾對話框_回到這台就照本機()
    {
        using var h = new HubHarness();
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachPeerSink(new FakeSink());
            h.Hub.PeerLive = true;
        });
        var draft = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "draftSetDevice", new { conversationId = draft, deviceId = OtherDevice, accountId = Logical, provider = "Claude" });
        var mark = h.Main.Mark();

        h.Command(h.Main, "attach", new { conversationId = draft });
        await h.Ui.DrainAsync();
        h.Main.Messages("toast", mark).Single().Value<string>("text").Should().Contain("送出第一則訊息之後");

        var choose = h.Main.Mark();
        h.Command(h.Main, "chooseFolder", new { conversationId = draft });
        await h.Ui.DrainAsync();
        h.Main.Messages("toast", choose).Single().Value<string>("text").Should().Contain("輸入那台電腦上的路徑", "這台的資料夾對話框挑的是這台的資料夾");

        var back = h.Main.Mark();
        h.Command(h.Main, "draftSetDevice", new { conversationId = draft, deviceId = OwnDevice });
        var local = await h.Main.WaitForAsync("header", since: back, conversationId: draft);
        local["header"]!.Value<string>("deviceId").Should().BeNull("回到這台的本機草稿");
    }

    [Fact]
    public async Task 遠端草稿_送出前選那台的資料夾_第一次send帶folder()
    {
        using var h = new HubHarness();
        var sink = new FakeSink();
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachPeerSink(sink);
            h.Hub.PeerLive = true;
        });
        var draft = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "draftSetDevice", new { conversationId = draft, deviceId = OtherDevice, accountId = Logical, provider = "Claude" });
        await h.Ui.DrainAsync();
        var mark = h.Main.Mark();

        h.Command(h.Main, "setFolder", new { conversationId = draft, path = @"  ""D:\work\demo""  " });
        var header = (await h.Main.WaitForAsync("header", m => m["header"]!.Value<bool>("customFolder"), mark, conversationId: draft))["header"]!;
        header.Value<string>("workingDirectory").Should().Be(@"D:\work\demo", "去掉前後空白與引號；這台不檢查存不存在（路徑在那台電腦上）");
        header.Value<string>("deviceId").Should().Be(OtherDevice);
        h.Main.Messages("toast", mark).Should().BeEmpty();

        var cid = NewId();
        h.Command(h.Main, "send", new { conversationId = draft, text = "在那台的 repo 做事", commandId = cid });
        await h.Ui.DrainAsync();
        var call = sink.Calls.Should().ContainSingle().Subject;
        call.Cmd.Value<string>("folder").Should().Be(@"D:\work\demo");

        // 建立中不能換
        var busy = h.Main.Mark();
        h.Command(h.Main, "resetFolder", new { conversationId = draft });
        await h.Ui.DrainAsync();
        h.Main.Messages("toast", busy).Single().Value<string>("text").Should().Contain("建立中");

        // 被拒（例如那台找不到資料夾）之後資料夾留著，改了再送
        h.Ui.Invoke(() => h.Hub.OnPeerReply(cid, new JObject { ["type"] = "commandResult", ["commandId"] = cid, ["state"] = "rejected", ["code"] = "conflict" }));
        await h.Ui.DrainAsync();
        var reset = h.Main.Mark();
        h.Command(h.Main, "resetFolder", new { conversationId = draft });
        var cleared = (await h.Main.WaitForAsync("header", m => !m["header"]!.Value<bool>("customFolder"), reset, conversationId: draft))["header"]!;
        cleared["workingDirectory"]!.Type.Should().Be(JTokenType.Null, "那台對話自己的工作區");
        h.Command(h.Main, "send", new { conversationId = draft, text = "改用預設工作區", commandId = NewId() });
        await h.Ui.DrainAsync();
        sink.Calls.Should().HaveCount(2);
        sink.Calls[1].Cmd.ContainsKey("folder").Should().BeFalse();
    }

    [Fact]
    public async Task 遠端草稿_資料夾不合格跳提示_換電腦或回到這台就清掉()
    {
        using var h = new HubHarness();
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachPeerSink(new FakeSink());
            h.Hub.PeerLive = true;
        });
        var draft = h.NewConversation(h.Main, h.Claude);
        h.Command(h.Main, "draftSetDevice", new { conversationId = draft, deviceId = OtherDevice, accountId = Logical, provider = "Claude" });
        await h.Ui.DrainAsync();

        foreach (var bad in new object[] { @"\\server\share\repo", "repo", "", 42 })
        {
            var mark = h.Main.Mark();
            h.Command(h.Main, "setFolder", new { conversationId = draft, path = bad });
            await h.Ui.DrainAsync();
            h.Main.Messages("toast", mark).Single().Value<string>("text").Should().Contain("完整路徑", $"「{bad}」不收");
        }

        h.Command(h.Main, "setFolder", new { conversationId = draft, path = @"C:\repo\demo" });
        await h.Ui.DrainAsync();
        // 只換帳戶（同一台）留著
        var sameDevice = h.Main.Mark();
        h.Command(h.Main, "draftSetDevice", new { conversationId = draft, deviceId = OtherDevice, accountId = "la_" + new string('E', 26), provider = "Codex" });
        var kept = (await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("provider") == "Codex", sameDevice, conversationId: draft))["header"]!;
        kept.Value<string>("workingDirectory").Should().Be(@"C:\repo\demo");

        // 換到另一台：資料夾是那台的路徑，回到那台對話自己的工作區
        var third = "d_" + new string('D', 26);
        var moved = h.Main.Mark();
        h.Command(h.Main, "draftSetDevice", new { conversationId = draft, deviceId = third, accountId = Logical, provider = "Claude" });
        var other = (await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("deviceId") == third, moved, conversationId: draft))["header"]!;
        other.Value<bool>("customFolder").Should().BeFalse();
        other["workingDirectory"]!.Type.Should().Be(JTokenType.Null);

        // 回到這台再換回去：也是預設工作區
        h.Command(h.Main, "setFolder", new { conversationId = draft, path = @"C:\repo\demo" });
        h.Command(h.Main, "draftSetDevice", new { conversationId = draft, deviceId = OwnDevice });
        await h.Ui.DrainAsync();
        var again = h.Main.Mark();
        h.Command(h.Main, "draftSetDevice", new { conversationId = draft, deviceId = third, accountId = Logical, provider = "Claude" });
        var back = (await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("deviceId") == third, again, conversationId: draft))["header"]!;
        back.Value<bool>("customFolder").Should().BeFalse();
    }

    [Fact]
    public async Task 遠端草稿_最近使用是那台的副本的自訂資料夾()
    {
        using var h = new HubHarness();
        h.Ui.Invoke(() =>
        {
            h.Hub.AttachPeerSink(new FakeSink());
            h.Hub.PeerLive = true;
            h.Hub.ShowReplicas = true;
        });
        var now = DateTime.Now;
        ReplicaMeta Replica(string id, string owner, string? folder, int minutesAgo) => new(id, owner,
            new AgentConversation { Id = id, Provider = AgentProvider.Claude, Title = id[..4], AccountId = string.Empty, UpdatedAt = now.AddMinutes(-minutesAgo) },
            Running: false, Waiting: false, ItemsAvailable: true, CustomFolder: folder != null, Folder: folder);
        h.Ui.Invoke(() => h.Hub.ApplyReplicaDelta(new ReplicaDelta(
        [
            Replica("aaaa" + new string('0', 28), OtherDevice, @"C:\repo\old", 60),
            Replica("bbbb" + new string('0', 28), OtherDevice, @"C:\repo\new", 1),
            Replica("cccc" + new string('0', 28), OtherDevice, @"c:\REPO\NEW", 5),
            Replica("dddd" + new string('0', 28), OtherDevice, null, 0),
            Replica("eeee" + new string('0', 28), "d_" + new string('D', 26), @"C:\elsewhere", 0),
        ], [], [])));
        await h.Ui.DrainAsync();
        var draft = h.NewConversation(h.Main, h.Claude);
        var mark = h.Main.Mark();

        h.Command(h.Main, "draftSetDevice", new { conversationId = draft, deviceId = OtherDevice, accountId = Logical, provider = "Claude" });
        var header = (await h.Main.WaitForAsync("header", m => m["header"]!.Value<string>("deviceId") == OtherDevice, mark, conversationId: draft))["header"]!;
        header["recentFolders"]!.Values<string>().Should().Equal(new[] { @"C:\repo\new", @"C:\repo\old" }, "只列那台的、由新到舊、不分大小寫去重；預設工作區與其他電腦的不列");
    }

    [Fact]
    public void PeerDraftRules_conversationCreate()
    {
        var meta = new AgentConversation { Provider = AgentProvider.Claude, Title = "新對話", Model = "default", PermissionMode = AgentPermissionMode.Plan };
        var create = PeerDraftRules.ConversationCreate(OtherDevice, Logical, meta, "做事");
        create.Properties().Select(p => p.Name).Should().Equal("cmd", "deviceId", "accountId", "permissionMode", "text");
        create.Value<string>("permissionMode").Should().Be("Plan");

        meta.Title = "改過的標題";
        meta.Model = "opus";
        meta.Effort = "high";
        var named = PeerDraftRules.ConversationCreate(OtherDevice, Logical, meta, "做事");
        (named.Value<string>("title"), named.Value<string>("model"), named.Value<string>("effort")).Should().Be(("改過的標題", "opus", "high"));

        var withFolder = PeerDraftRules.ConversationCreate(OtherDevice, Logical, meta, "做事", @"C:\repo\demo");
        withFolder.Properties().Select(p => p.Name).Should().Equal(new[] { "cmd", "deviceId", "accountId", "permissionMode", "model", "effort", "folder", "title", "text" }, "鍵照型錄的順序");
        withFolder.Value<string>("folder").Should().Be(@"C:\repo\demo");
    }

    [Theory]
    [InlineData(@"C:\repo\demo", @"C:\repo\demo")]
    [InlineData(@"  d:/work  ", @"d:/work")]
    [InlineData(@"""C:\Program Files\app""", @"C:\Program Files\app")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"\\server\share", null)]
    [InlineData(@"repo\demo", null)]
    [InlineData("C:", null)]
    [InlineData("C:\\a\tb", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void PeerDraftRules_資料夾(string? path, string? expected) => PeerDraftRules.Folder(path).Should().Be(expected);

    [Fact]
    public void PeerDraftRules_資料夾太長不收()
    {
        var max = @"C:\" + new string('a', RemoteCommandShapeRules.MaxTextLength - 3);
        PeerDraftRules.Folder(max).Should().Be(max);
        PeerDraftRules.Folder(max + "a").Should().BeNull("超過型錄的上限，伺服器會整則拒絕");
    }

    [Fact]
    public void PeerDraftRules_最近資料夾最多十個()
    {
        var t = new DateTime(2026, 10, 27, 9, 0, 0);
        var folders = Enumerable.Range(0, 15).Select(i => ((string?)$@"C:\repo\p{i:00}", t.AddMinutes(i))).Append((@"\\nas\x", t.AddDays(1))).Append((null, t.AddDays(1)));
        PeerDraftRules.RecentFolders(folders).Should().Equal(Enumerable.Range(5, 10).Reverse().Select(i => $@"C:\repo\p{i:00}"));
    }
}
