using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Files;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.Sync;
using Myriagent.Tests.TestSupport;
using Myriagent.Update;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 中樞的遠端檔案指令（WP4.7 計畫步 11、13；D22、D33～D36）：listFiles（含 dirsOnly）、fileSearch、fileFetch、addUploaded，
/// WP4.4 的 conversationCreate 帶 uploads（登記點 PrepareRemoteUploads），以及遠端 send 只帶網頁上傳的附件。
/// 經真的閘門（WP4.4 正式的授權者）與真的守門員，傳輸換成假的（記錄登記的參照、照指示下載）；工作資料夾是對話自己的 workspace
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class RemoteFilesTests
{
    private const string CommandId = "01J9QZ3K8M5R7T2V4W6X8Y0A1B";

    /// <summary>假的傳輸：參照是新的亂數（不寫資料庫）；下載預設寫 Size 個位元組，可換成自己的</summary>
    private sealed class FakeTransfer : IRemoteFileTransfer
    {
        public bool CanTransfer { get; set; } = true;

        public Recorder<(string ConversationId, string Kind, IReadOnlyList<string> Paths)> Registered { get; } = new();

        public Recorder<(RemoteUpload Upload, string File)> Downloads { get; } = new();

        public Func<RemoteUpload, string, CancellationToken, Task>? Download { get; set; }

        public Task<IReadOnlyList<string>> RegisterRefsAsync(string conversationId, string kind, IReadOnlyList<string> fullPaths, CancellationToken ct)
        {
            Registered.Add((conversationId, kind, fullPaths.ToList()));
            IReadOnlyList<string> refs = fullPaths.Select(_ => MediaRefIssueRules.NewRef(RandomNumberGenerator.GetBytes)).ToList();
            return Task.FromResult(refs);
        }

        public async Task DownloadUploadAsync(RemoteUpload upload, string destinationFile, CancellationToken ct)
        {
            Downloads.Add((upload, destinationFile));
            if (Download is { } download)
                await download(upload, destinationFile, ct);
            else
                await File.WriteAllBytesAsync(destinationFile, new byte[upload.Size], ct);
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig(bool canTransfer = true)
        {
            H = new HubHarness();
            H.Ui.Invoke(() =>
            {
                H.Settings.Sync.RemoteControlLevel = SyncSettings.LevelControl;
                H.Settings.Sync.AllowRemoteFullAccess = true;
            });
            var (id, session) = H.StartConversation(H.Main, H.Claude);
            H.CompleteTurn(session);
            Id = id;
            Session = session;
            Wire = ConversationIds.ToWire(id)!;
            Transfer.CanTransfer = canTransfer;
            H.Ui.Invoke(() =>
            {
                // 授權者是 WP4.4 正式的（依擁有者指示放行），閘門與層級都是真的
                H.Hub.RemoteFileTransferForTest = Transfer;
                H.Hub.RemoteFilesTime = Clock;
            });
            Workspace = Directory.CreateDirectory(H.Store.GetDefaultWorkingDirectory(id)).FullName;
        }

        public HubHarness H { get; }

        public string Id { get; }

        /// <summary>這個對話的 CLI（假的；遠端 send 送進來的附件看 <c>Sent</c>）</summary>
        public FakeSession Session { get; }

        public string Wire { get; }

        public string Workspace { get; }

        public FakeTransfer Transfer { get; } = new();

        public ManualTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-10-21T08:00:00Z"));

        public TempDirectory Outside { get; } = new("remote-files-outside");

        public string Write(string relative, string content = "x")
        {
            var path = Path.Combine(Workspace, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public async Task<(RemoteOutcome Outcome, IReadOnlyList<JObject> Output)> RunAsync(string cmd, object? args = null, int tier = 1)
        {
            var command = HubHarness.MakeCommand(cmd, args);
            command["conversationId"] = Wire;
            var context = new RemoteViewContext(CommandId, RemoteSamples.DeviceId, Wire, 1, tier, false, 1_000, 2_000, false, "Chrome・Windows");
            var output = new Recorder<JObject>();
            var outcome = await H.Ui.Invoke(() => H.Hub.ExecuteRemoteAsync(context, command, output.Add)).WaitAsync(TimeSpan.FromSeconds(20));
            return (outcome, output.All);
        }

        /// <summary>新對話的資料夾挑選 listFolders（路由 Device，沒有 conv）經真的閘門</summary>
        public async Task<(RemoteOutcome Outcome, IReadOnlyList<JObject> Output)> RunFoldersAsync(string requestId, string? path, int tier = 2)
        {
            var command = RemoteSamples.Command(RemoteCommandCatalog.ListFolders, ("deviceId", RemoteSamples.DeviceId), ("requestId", requestId));
            command["path"] = path is null ? JValue.CreateNull() : new JValue(path);
            var context = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, null, null, tier, false, 1_000, 2_000, false, "Chrome・Windows");
            var output = new Recorder<JObject>();
            var outcome = await H.Ui.Invoke(() => H.Hub.ExecuteRemoteAsync(context, command, output.Add)).WaitAsync(TimeSpan.FromSeconds(20));
            return (outcome, output.All);
        }

        /// <summary>WP4.4 的 conversationCreate（路由 Device，沒有 conv）經真的閘門；回結果與推得的新對話 id</summary>
        public async Task<(RemoteOutcome Outcome, string Id)> RunCreateAsync(string? text, params RemoteUpload[] uploads)
        {
            var cid = RemoteEntrySamples.NewCid();
            var command = RemoteSamples.Command(RemoteCommandCatalog.ConversationCreate, ("accountId", "acc-claude"), ("permissionMode", "Ask"));
            if (text != null)
                command["text"] = text;
            if (uploads.Length > 0)
                command["uploads"] = JObject.FromObject(UploadsArgs(uploads))["uploads"];
            var context = new RemoteViewContext(cid, RemoteSamples.DeviceId, null, null, 1, false, 1_000, 2_000, false, "Chrome・Windows");
            var outcome = await H.Ui.Invoke(() => H.Hub.ExecuteRemoteAsync(context, command, _ => { })).WaitAsync(TimeSpan.FromSeconds(20));
            return (outcome, RemoteConversationIdRules.FromCommandId(cid)!);
        }

        /// <summary>同一個 UI 工作裡：指令停在第一個 await（遠端 send 在背景檢查工作資料夾）時做 <paramref name="meanwhile"/>；回這個指令的結果</summary>
        public async Task<RemoteOutcome> RunRacingAsync(string cmd, object? args, Action meanwhile, int tier = 1)
        {
            var command = HubHarness.MakeCommand(cmd, args);
            command["conversationId"] = Wire;
            var context = new RemoteViewContext(CommandId, RemoteSamples.DeviceId, Wire, 1, tier, false, 1_000, 2_000, false, "Chrome・Windows");
            return await H.Ui.Invoke(() =>
            {
                var running = H.Hub.ExecuteRemoteAsync(context, command, _ => { });
                meanwhile();
                return running;
            }).WaitAsync(TimeSpan.FromSeconds(20));
        }

        /// <summary>另一個指令 id 的遠端 send（在 UI 執行緒呼叫，例如 <see cref="RunRacingAsync"/> 的 meanwhile）；回它的結果</summary>
        public Task<RemoteOutcome> StartSend(string text)
        {
            var command = HubHarness.MakeCommand("send", new { text });
            command["conversationId"] = Wire;
            var context = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, Wire, 1, 1, false, 1_000, 2_000, false, "Chrome・Windows");
            return H.Hub.ExecuteRemoteAsync(context, command, _ => { });
        }

        /// <summary>這個對話送進代理的最後一則（Claude 的會話可能換過）與總則數</summary>
        public (int Count, AgentTurnInput? Last) Sent() => (H.Sessions.Sum(s => s.Sent.Count), H.Sessions.SelectMany(s => s.Sent.All).Select(x => x.Input).LastOrDefault());

        public (int Importing, IReadOnlyList<string> Attachments) Composer() => H.Ui.Invoke(() => H.Hub.ComposerStateForTest(Id));

        public void Dispose()
        {
            if (Directory.Exists(Workspace))
            {
                foreach (var link in Directory.EnumerateDirectories(Workspace, "*", SearchOption.AllDirectories).Where(Junction.IsJunction).ToList())
                    Directory.Delete(link);
            }

            H.Dispose();
            Outside.Dispose();
        }
    }

    private static RemoteUpload Upload(int i, string? name = null, long size = 5)
        => new($"upload-{i}", name ?? $"檔案{i}.txt", size, new string('a', 64));

    private static object UploadsArgs(params RemoteUpload[] uploads)
        => new { uploads = uploads.Select(u => new { uploadId = u.UploadId, name = u.Name, size = u.Size, sha256 = u.Sha256 }).ToArray() };

    private static string UploadTemp(RemoteUpload upload) => Path.Combine(AgentHub.UploadCacheDirectory, upload.UploadId);

    private static string Token(int seed)
    {
        var random = new Random(seed);
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return "gh" + "p_" + new string(Enumerable.Range(0, 36).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }

    private static string Upper(string path) => path.ToUpperInvariant();

    private static void ShouldBeDone(RemoteOutcome outcome) => outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);

    private static void ShouldBeRejected(RemoteOutcome outcome, string code)
    {
        outcome.State.Should().Be(RemoteResultStates.Rejected, outcome.Message);
        outcome.Code.Should().Be(code, outcome.Message);
        outcome.Step.Should().Be(RemoteGateSteps.Handler);
    }

    // ───────────────────────────── listFiles ─────────────────────────────

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task 範圍內的listFiles_媒體帶參照沒有網址_拒絕清單的檔名照列沒有參照_junction不列_根的parent是null()
    {
        using var rig = new Rig();
        var photo = rig.Write("photo.png", "png");
        var clip = rig.Write("clip.mp4", "mp4");
        rig.Write("notes.txt", "hello");
        rig.Write(".env", "A=1");
        rig.Write("id_rsa.png", "key");
        Directory.CreateDirectory(Path.Combine(rig.Workspace, "sub"));
        rig.Outside.WriteFile(@"elsewhere\inside.png", "x");
        Junction.Create(Path.Combine(rig.Workspace, "link"), rig.Outside.Combine("elsewhere"));

        var (outcome, output) = await rig.RunAsync("listFiles", new { requestId = "r1", path = (string?)null });

        ShouldBeDone(outcome);
        var files = output.Should().ContainSingle().Which;
        files.Value<string>("type").Should().Be("files");
        files.Value<string>("requestId").Should().Be("r1");
        files.Value<string>("conversationId").Should().Be(rig.Wire);
        files.Value<string>("path").Should().BeEquivalentTo(rig.Workspace);
        files["parent"]!.Type.Should().Be(JTokenType.Null, "在範圍的根");
        files.Value<bool>("truncated").Should().BeFalse();
        files["error"]!.Type.Should().Be(JTokenType.Null);
        var entries = files["entries"]!.Cast<JObject>().ToDictionary(e => e.Value<string>("name")!);
        entries.Keys.Should().BeEquivalentTo(["sub", "photo.png", "clip.mp4", "notes.txt", ".env", "id_rsa.png"], "junction 不列");
        entries.Values.Should().AllSatisfy(e =>
        {
            e.ContainsKey("url").Should().BeFalse();
            e.ContainsKey("thumbUrl").Should().BeFalse();
        });
        entries["photo.png"].Value<string>("ref").Should().NotBeNullOrEmpty();
        entries["photo.png"].Value<long>("ver").Should().Be(File.GetLastWriteTimeUtc(photo).Ticks);
        entries["clip.mp4"].Value<string>("ref").Should().NotBeNullOrEmpty();
        entries["photo.png"].Value<string>("ref").Should().NotBe(entries["clip.mp4"].Value<string>("ref"));
        foreach (var name in new[] { ".env", "id_rsa.png", "notes.txt", "sub" })
            entries[name].ContainsKey("ref").Should().BeFalse(name);
        JsonConvert.SerializeObject(files).Should().NotContain("myriagent.local").And.NotContain("/media").And.NotContain("/thumb");

        var registered = rig.Transfer.Registered.All.Should().ContainSingle().Which;
        registered.ConversationId.Should().Be(rig.Wire);
        registered.Kind.Should().Be(MediaServerRules.ListItemId);
        registered.Paths.Select(Upper).Should().BeEquivalentTo([Upper(photo), Upper(clip)]);
    }

    [Fact]
    public async Task listFiles子資料夾的parent是上一層_沒有傳輸時照列名稱沒有參照()
    {
        using var rig = new Rig(canTransfer: false);
        rig.Write(@"sub\pic.png", "png");

        var (outcome, output) = await rig.RunAsync("listFiles", new { requestId = "r2", path = Path.Combine(rig.Workspace, "sub") });

        ShouldBeDone(outcome);
        var files = output.Single();
        files.Value<string>("parent").Should().BeEquivalentTo(rig.Workspace);
        var entry = files["entries"]!.Cast<JObject>().Single();
        entry.Value<string>("name").Should().Be("pic.png");
        entry.ContainsKey("ref").Should().BeFalse();
        rig.Transfer.Registered.Count.Should().Be(0);
    }

    [Fact]
    public async Task 範圍外回policy_對話資料夾本身也是_不登記參照()
    {
        using var rig = new Rig();
        rig.Outside.WriteFile("a.png", "x");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(rig.H.Store.GetConversationDirectory(rig.Id)).FullName, "x.png"), "x");

        foreach (var path in new[] { rig.Outside.Root, rig.H.Store.GetConversationDirectory(rig.Id), @"\\server\share" })
        {
            var (outcome, output) = await rig.RunAsync("listFiles", new { requestId = "r3", path }, tier: 2);
            ShouldBeRejected(outcome, RemoteResultCodes.Policy);
            output.Should().BeEmpty(path);
        }

        rig.Transfer.Registered.Count.Should().Be(0);
    }

    [Fact]
    public async Task listFiles不存在的資料夾回帶error的files()
    {
        using var rig = new Rig();

        var (outcome, output) = await rig.RunAsync("listFiles", new { requestId = "r4", path = Path.Combine(rig.Workspace, "missing") });

        ShouldBeDone(outcome);
        output.Single().Value<string>("error").Should().Be("找不到資料夾");
        output.Single()["entries"]!.Should().BeEmpty();
    }

    [Fact]
    public async Task 三千筆長路徑的資料夾_輸出不超過700KB_truncated()
    {
        using var rig = new Rig();
        var dir = Directory.CreateDirectory(Path.Combine(rig.Workspace, "many")).FullName;
        var stem = new string('長', 90);
        for (var i = 0; i < 3000; i++)
            File.WriteAllBytes(Path.Combine(dir, $"{stem}{i:0000}.png"), []);

        var (outcome, output) = await rig.RunAsync("listFiles", new { requestId = "r5", path = dir });

        ShouldBeDone(outcome);
        var files = output.Single();
        files.Value<bool>("truncated").Should().BeTrue();
        Encoding.UTF8.GetByteCount(files["entries"]!.ToString(Formatting.None)).Should().BeLessThanOrEqualTo(RemoteListRules.MaxOutputBytes);
        ((JArray)files["entries"]!).Count.Should().BeGreaterThan(100).And.BeLessThan(3000);
    }

    [Fact]
    public async Task dirsOnly且path是null_entries是守門員驗得過的最近資料夾_全是dir()
    {
        using var rig = new Rig();
        var a = rig.Outside.CreateDirectory("project-a");
        var b = rig.Outside.CreateDirectory("project-b");
        rig.H.Ui.Invoke(() =>
        {
            rig.H.Settings.RecentFolders.Clear();
            rig.H.Settings.RecentFolders.AddRange([a, rig.Outside.Combine("missing"), rig.H.Store.GetConversationDirectory(rig.Id), b, a.ToUpperInvariant()]);
        });

        var (outcome, output) = await rig.RunAsync("listFiles", new { requestId = "r6", path = (string?)null, dirsOnly = true }, tier: 2);

        ShouldBeDone(outcome);
        var files = output.Single();
        files["path"]!.Type.Should().Be(JTokenType.Null);
        files["parent"]!.Type.Should().Be(JTokenType.Null);
        var entries = files["entries"]!.Cast<JObject>().ToList();
        entries.Select(e => Upper(e.Value<string>("path")!)).Should().BeEquivalentTo([Upper(a), Upper(b)]);
        entries.Should().AllSatisfy(e =>
        {
            e.Value<string>("kind").Should().Be("dir");
            e.Value<string>("name").Should().Be(e.Value<string>("path"));
            e.ContainsKey("ref").Should().BeFalse();
        });
        rig.Transfer.Registered.Count.Should().Be(0);
    }

    // ───────────────────────────── listFolders（瀏覽器新對話的資料夾挑選） ─────────────────────────────

    [Fact]
    public async Task listFolders的path是null_列守門員驗得過的最近資料夾_沒有對話id與工作資料夾_app資料夾不給()
    {
        using var rig = new Rig();
        var a = rig.Outside.CreateDirectory("project-a");
        var b = rig.Outside.CreateDirectory("project-b");
        rig.H.Ui.Invoke(() =>
        {
            rig.H.Settings.RecentFolders.Clear();
            rig.H.Settings.RecentFolders.AddRange([a, rig.Outside.Combine("missing"), rig.Workspace, b, a.ToUpperInvariant()]);
        });

        var (outcome, output) = await rig.RunFoldersAsync("f1", null);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var files = output.Should().ContainSingle().Which;
        files.Value<string>("type").Should().Be("files");
        files.Value<string>("requestId").Should().Be("f1");
        files["conversationId"]!.Type.Should().Be(JTokenType.Null, "那台還沒有這個對話（伺服器換成草稿 id）");
        files["root"]!.Type.Should().Be(JTokenType.Null);
        files["path"]!.Type.Should().Be(JTokenType.Null);
        files["entries"]!.Cast<JObject>().Select(e => Upper(e.Value<string>("path")!)).Should().BeEquivalentTo([Upper(a), Upper(b)],
            "不存在的不列；別的對話的預設工作區在 app 的資料夾裡，沒有對話時整個不給");
        rig.Transfer.Registered.Count.Should().Be(0);
    }

    [Fact]
    public async Task listFolders往下一層只列資料夾_parent是最近資料夾_範圍外回policy()
    {
        using var rig = new Rig();
        var a = rig.Outside.CreateDirectory("project-a");
        rig.Outside.CreateDirectory(@"project-a\src");
        rig.Outside.WriteFile(@"project-a\readme.md", "x");
        rig.Outside.CreateDirectory("other");
        rig.H.Ui.Invoke(() =>
        {
            rig.H.Settings.RecentFolders.Clear();
            rig.H.Settings.RecentFolders.Add(a);
        });

        var (outcome, output) = await rig.RunFoldersAsync("f2", a);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var files = output.Single();
        files["parent"]!.Type.Should().Be(JTokenType.Null, "在範圍的根");
        files["entries"]!.Cast<JObject>().Select(e => e.Value<string>("name")).Should().Equal("src");

        foreach (var path in new[] { rig.Outside.Combine("other"), rig.Workspace, @"\server\share" })
        {
            var (refused, none) = await rig.RunFoldersAsync("f3", path);
            ShouldBeRejected(refused, RemoteResultCodes.Policy);
            none.Should().BeEmpty(path);
        }
    }

    [Fact]
    public async Task listFolders是層級2_政策不是Control時在閘門拒絕()
    {
        using var rig = new Rig();
        rig.H.Ui.Invoke(() => rig.H.Settings.Sync.RemoteControlLevel = SyncSettings.LevelView);

        var (outcome, output) = await rig.RunFoldersAsync("f4", null, tier: 1);

        outcome.State.Should().Be(RemoteResultStates.Rejected);
        outcome.Step.Should().Be(RemoteGateSteps.Policy);
        output.Should().BeEmpty();
    }

    // ───────────────────────────── fileSearch ─────────────────────────────

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task fileSearch不會找到junction背後的檔()
    {
        using var rig = new Rig();
        var readme = rig.Write(@"docs\readme.md", "# 說明");
        rig.Outside.WriteFile(@"secret\readme-secret.md", "x");
        Junction.Create(Path.Combine(rig.Workspace, "link"), rig.Outside.Combine("secret"));

        var (outcome, output) = await rig.RunAsync("fileSearch", new { requestId = "s1", query = "readme" });

        ShouldBeDone(outcome);
        var search = output.Single();
        search.Value<string>("type").Should().Be("fileSearch");
        search.Value<string>("conversationId").Should().Be(rig.Wire);
        search.Value<string>("query").Should().Be("readme");
        var result = search["results"]!.Cast<JObject>().Should().ContainSingle().Which;
        result.Value<string>("relativePath").Should().Be(@"docs\readme.md");
        result.Value<string>("fullPath").Should().BeEquivalentTo(readme);
    }

    // ───────────────────────────── fileFetch ─────────────────────────────

    [Fact]
    public async Task fileFetch_成功時發file參照_expiresAt是現在加15分鐘()
    {
        using var rig = new Rig();
        var hello = rig.Write("hello.txt", "hello");

        var (outcome, output) = await rig.RunAsync("fileFetch", new { requestId = "f1", path = hello }, tier: 2);

        ShouldBeDone(outcome);
        var fileRef = output.Single();
        fileRef.Value<string>("type").Should().Be("fileRef");
        fileRef.Value<string>("requestId").Should().Be("f1");
        fileRef.Value<string>("conversationId").Should().Be(rig.Wire);
        fileRef.Value<string>("name").Should().Be("hello.txt");
        fileRef.Value<long>("size").Should().Be(5);
        fileRef.Value<long>("expiresAt").Should().Be(rig.Clock.NowMs + 15 * 60_000);
        fileRef["error"]!.Type.Should().Be(JTokenType.Null);
        var registered = rig.Transfer.Registered.All.Single();
        registered.Kind.Should().Be(MediaServerRules.FileItemId);
        registered.Paths.Single().Should().BeEquivalentTo(hello);
        fileRef.Value<string>("ref").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task fileFetch_超過20MB的二進位簡報_發參照_大小照實()
    {
        // 43 MB 的 PowerPoint：開頭（ZIP 檔頭有 NUL）就證明是二進位，不必整份讀進記憶體跑 DLP
        using var rig = new Rig();
        var pptx = Path.Combine(rig.Workspace, "output", "年度 簡報.pptx");
        Directory.CreateDirectory(Path.GetDirectoryName(pptx)!);
        using (var stream = File.Create(pptx))
        {
            stream.Write([0x50, 0x4B, 0x03, 0x04, 0x14, 0x00]);
            stream.SetLength(43L * 1024 * 1024);
        }

        var (outcome, output) = await rig.RunAsync("fileFetch", new { requestId = "f6", path = pptx }, tier: 2);

        ShouldBeDone(outcome);
        var fileRef = output.Single();
        fileRef["error"]!.Type.Should().Be(JTokenType.Null);
        fileRef.Value<string>("name").Should().Be("年度 簡報.pptx");
        fileRef.Value<long>("size").Should().Be(43L * 1024 * 1024);
        rig.Transfer.Registered.All.Single().Paths.Single().Should().BeEquivalentTo(pptx);
    }

    [Fact]
    public async Task fileFetch_可能是文字的21MB與含權杖的文字檔回固定的錯誤文字_不發參照()
    {
        using var rig = new Rig();
        var big = Path.Combine(rig.Workspace, "big.bin");
        File.WriteAllBytes(big, Enumerable.Repeat((byte)'a', 21 * 1024 * 1024).ToArray());
        var leaky = rig.Write("deploy-notes.txt", "token: " + Token(5) + "\n");
        var forbidden = rig.Write(".env", "A=1");

        foreach (var path in new[] { big, leaky, forbidden })
        {
            var (outcome, output) = await rig.RunAsync("fileFetch", new { requestId = "f2", path }, tier: 2);
            ShouldBeDone(outcome);
            var fileRef = output.Single();
            fileRef.Value<string>("error").Should().Be("這個檔案不能從網頁讀取", path);
            fileRef.ContainsKey("ref").Should().BeFalse();
        }

        rig.Transfer.Registered.Count.Should().Be(0);
    }

    [Fact]
    public async Task fileFetch_UTF16的權杖檔也回固定的錯誤文字_不發參照()
    {
        // 合併前審查 F1：Windows PowerShell 5.1 的「> token.txt」寫 UTF-16LE 加 BOM，只以 UTF-8 解碼時掃不到
        using var rig = new Rig();
        var token = Token(7);
        var txt = Path.Combine(rig.Workspace, "token.txt");
        File.WriteAllBytes(txt, [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(token + "\r\n")]);
        var noExtension = Path.Combine(rig.Workspace, "gh-token");
        File.WriteAllBytes(noExtension, [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(token)]);

        foreach (var path in new[] { txt, noExtension })
        {
            var (outcome, output) = await rig.RunAsync("fileFetch", new { requestId = "f5", path }, tier: 2);
            ShouldBeDone(outcome);
            var fileRef = output.Single();
            fileRef.Value<string>("error").Should().Be("這個檔案不能從網頁讀取", path);
            fileRef.ContainsKey("ref").Should().BeFalse();
        }

        rig.Transfer.Registered.Count.Should().Be(0);
    }

    // ───────────────────────────── 路徑上任何一層的 junction（listFiles、fileFetch） ─────────────────────────────

    [FileSystemFact(FileSystemFeature.Junction)]
    public async Task listFiles與fileFetch_路徑上任何一層有junction都回policy_指到範圍外_資料根目錄_範圍內都一樣_不發參照()
    {
        // 守門員逐層拒絕重新解析點（設計 8.7 第 3 步：指到範圍內別處的 junction 也拒絕），開檔／開資料夾之後再以真實路徑重判一次
        using var rig = new Rig();
        using var junctions = new TestJunctions();
        rig.Outside.WriteFile("secret.txt", "outside");
        rig.Outside.WriteFile(@"sub\secret.txt", "outside");
        var dataNotes = Path.Combine(Directory.CreateDirectory(Path.Combine(rig.H.Temp.Root, "accounts", "acc-x")).FullName, "notes.txt");
        File.WriteAllText(dataNotes, "data root");
        var real = rig.Write(@"real\a.txt", "inside");
        rig.Write(@"real\sub\b.txt", "inside");
        var toOutside = junctions.Create(Path.Combine(rig.Workspace, "link-out"), rig.Outside.Root);
        var toData = junctions.Create(Path.Combine(rig.Workspace, "link-data"), rig.H.Temp.Root);
        var toInside = junctions.Create(Path.Combine(rig.Workspace, "link-in"), Path.Combine(rig.Workspace, "real"));
        var items = Path.Combine(rig.H.Store.GetConversationDirectory(rig.Id), "items.json");
        File.Exists(items).Should().BeTrue("前提：對話資料夾有 items.json");

        foreach (var path in new[]
                 {
                     toOutside, Path.Combine(toOutside, "sub"), toData, Path.Combine(toData, "accounts"), Path.Combine(toData, "accounts", "acc-x"),
                     Path.Combine(toData, "conversations", rig.Id), toInside, Path.Combine(toInside, "sub"),
                 })
        {
            var (outcome, output) = await rig.RunAsync("listFiles", new { requestId = "j1", path }, tier: 2);
            ShouldBeRejected(outcome, RemoteResultCodes.Policy);
            output.Should().BeEmpty(path);
        }

        foreach (var path in new[]
                 {
                     Path.Combine(toOutside, "secret.txt"), Path.Combine(toOutside, "sub", "secret.txt"), Path.Combine(toData, "accounts", "acc-x", "notes.txt"),
                     Path.Combine(toData, "conversations", rig.Id, "items.json"), Path.Combine(toInside, "a.txt"), Path.Combine(toInside, "sub", "b.txt"),
                 })
        {
            var (outcome, output) = await rig.RunAsync("fileFetch", new { requestId = "j2", path }, tier: 2);
            ShouldBeRejected(outcome, RemoteResultCodes.Policy);
            output.Should().BeEmpty(path);
        }

        rig.Transfer.Registered.Count.Should().Be(0, "被拒的路徑不發參照");

        // 對照：同一個檔不經 junction 照樣給
        ShouldBeDone((await rig.RunAsync("fileFetch", new { requestId = "j3", path = real }, tier: 2)).Outcome);
    }

    [Fact]
    public async Task fileFetch_範圍外回policy_沒有傳輸回unsupported()
    {
        using var rig = new Rig();
        var outside = rig.Outside.WriteFile("x.txt", "x");

        ShouldBeRejected((await rig.RunAsync("fileFetch", new { requestId = "f3", path = outside }, tier: 2)).Outcome, RemoteResultCodes.Policy);

        rig.Transfer.CanTransfer = false;
        ShouldBeRejected((await rig.RunAsync("fileFetch", new { requestId = "f4", path = rig.Write("y.txt") }, tier: 2)).Outcome, RemoteResultCodes.Unsupported);
        rig.Transfer.Registered.Count.Should().Be(0);
    }

    // ───────────────────────────── addUploaded ─────────────────────────────

    [Fact]
    public async Task addUploaded_下載完成前就done_計數等於筆數時送出被拒_之後成為附件()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transfer.Download = async (u, file, ct) =>
        {
            await gate.Task;
            await File.WriteAllTextAsync(file, "內容", ct);
        };
        var uploads = new[] { Upload(1), Upload(2) };

        var (outcome, _) = await rig.RunAsync("addUploaded", UploadsArgs(uploads));

        ShouldBeDone(outcome);
        rig.Composer().Importing.Should().Be(2);
        var mark = rig.H.Main.Mark();
        rig.H.Send(rig.H.Main, rig.Id, "現在送");
        rig.H.Main.Messages("toast", mark).Select(m => m.Value<string>("text")).Should().Contain("附件還在處理中，請稍候");
        rig.H.Main.Messages("sendRejected", mark).Should().ContainSingle();

        gate.SetResult();
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id).Attachments.Count == 2, "兩個上傳成為附件");
        rig.Composer().Importing.Should().Be(0);
        rig.Composer().Attachments.Should().BeEquivalentTo(["檔案1.txt", "檔案2.txt"]);
        await rig.H.WaitUntilAsync(() => uploads.All(u => !Directory.Exists(UploadTemp(u))), "暫存資料夾刪了");
    }

    [Fact]
    public async Task addUploaded_20個全部成為附件()
    {
        using var rig = new Rig();
        var uploads = Enumerable.Range(1, AgentAttachmentRules.MaxAttachmentsPerMessage).Select(i => Upload(i)).ToArray();

        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(uploads))).Outcome);

        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id).Attachments.Count == uploads.Length, "20 個上傳成為附件", timeoutMs: 20_000);
        rig.Composer().Importing.Should().Be(0);

        ShouldBeRejected((await rig.RunAsync("addUploaded", UploadsArgs(Upload(99)))).Outcome, RemoteResultCodes.Invalid);
    }

    [Fact]
    public async Task addUploaded_雜湊不符_Notice_沒有附件_暫存刪了()
    {
        using var rig = new Rig();
        rig.Transfer.Download = async (u, file, ct) =>
        {
            await File.WriteAllTextAsync(file, "壞掉", ct);
            throw new InvalidDataException("SHA-256 不符");
        };
        var upload = Upload(1);

        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(upload))).Outcome);

        await rig.H.WaitUntilAsync(() => rig.H.Hub.GetItems(rig.Id).Any(i => i.Kind == AgentItemKind.Notice && i.Text.Contains("從網頁上傳的檔案沒有加進附件")),
            "加了 Notice");
        await rig.H.WaitUntilAsync(() => !Directory.Exists(UploadTemp(upload)), "暫存資料夾刪了");
        rig.Composer().Importing.Should().Be(0);
        rig.Composer().Attachments.Should().BeEmpty();
        rig.H.Items(rig.Id).Single(i => i.Kind == AgentItemKind.Notice && i.Text.Contains("從網頁上傳")).Text.Should().Contain("內容和上傳的不一樣");
    }

    [Fact]
    public async Task addUploaded_磁碟空間不夠_Notice_沒有下載()
    {
        using var rig = new Rig();
        rig.H.Ui.Invoke(() => rig.H.Hub.UploadFreeSpaceForTest = _ => UploadNameRules.DiskMarginBytes);

        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1), Upload(2)))).Outcome);

        await rig.H.WaitUntilAsync(() => rig.H.Hub.GetItems(rig.Id).Any(i => i.Kind == AgentItemKind.Notice && i.Text.Contains("磁碟空間不夠")), "加了 Notice");
        rig.Transfer.Downloads.Count.Should().Be(0);
        rig.Composer().Importing.Should().Be(0);
        rig.Composer().Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task addUploaded_檔名只留最後一段_保留裝置名稱加底線()
    {
        using var rig = new Rig();
        var evil = Upload(1, @"..\..\evil.exe");
        var con = Upload(2, "CON.txt");

        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(evil, con))).Outcome);

        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id).Attachments.Count == 2, "兩個上傳成為附件");
        var downloads = rig.Transfer.Downloads.All.ToDictionary(d => d.Upload.UploadId, d => d.File);
        downloads[evil.UploadId].Should().Be(Path.Combine(UploadTemp(evil), "evil.exe"));
        downloads[con.UploadId].Should().Be(Path.Combine(UploadTemp(con), "_CON.txt"));
        rig.Composer().Attachments.Should().BeEquivalentTo(["evil.exe", "_CON.txt"]);
    }

    [Fact]
    public async Task addUploaded_下載期間對話被刪_暫存刪了_沒有例外()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transfer.Download = async (u, file, ct) =>
        {
            await File.WriteAllTextAsync(file, "內容", ct);
            started.TrySetResult();
            await gate.Task;
        };
        var upload = Upload(1);

        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(upload))).Outcome);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Directory.Exists(UploadTemp(upload)).Should().BeTrue();
        rig.H.Command(rig.H.Main, "delete", new { conversationId = rig.Id });
        rig.H.Conversation(rig.Id).Should().BeNull();

        gate.SetResult();

        await rig.H.WaitUntilAsync(() => !Directory.Exists(UploadTemp(upload)), "暫存資料夾刪了");
        await rig.H.Ui.DrainAsync();
        rig.H.Ui.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task addUploaded_沒有傳輸回unsupported_重複的id回invalid_下載中的id回busy()
    {
        using var rig = new Rig(canTransfer: false);
        ShouldBeRejected((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1)))).Outcome, RemoteResultCodes.Unsupported);
        rig.Composer().Importing.Should().Be(0);

        rig.Transfer.CanTransfer = true;
        ShouldBeRejected((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1), Upload(1)))).Outcome, RemoteResultCodes.Invalid);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transfer.Download = async (u, file, ct) =>
        {
            await gate.Task;
            await File.WriteAllTextAsync(file, "內容", ct);
        };
        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1)))).Outcome);
        ShouldBeRejected((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1)))).Outcome, RemoteResultCodes.Busy);
        gate.SetResult();
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id).Attachments.Count == 1, "成為附件");
    }

    [Fact]
    public void 啟動時清掉上次留下的上傳暫存_只刪名稱是上傳id的資料夾()
    {
        // 合併前審查 R6：下載或匯入途中程式被關閉時暫存留下，網頁每次上傳的 id 都是新的，「同一個 id 下載前先刪」永遠輪不到
        using var temp = new TempDirectory("upload-cache");
        var cache = temp.CreateDirectory("uploads");
        var stale = Directory.CreateDirectory(Path.Combine(cache, "01J9QZ3K8M5R7T2V4W6X8Y0A1B")).FullName;
        File.WriteAllBytes(Path.Combine(stale, "影片.mp4"), new byte[4096]);
        var half = Directory.CreateDirectory(Path.Combine(cache, "upload-2")).FullName;
        Directory.CreateDirectory(Path.Combine(half, "sub"));
        var other = Directory.CreateDirectory(Path.Combine(cache, "不是上傳 id")).FullName;
        var loose = Path.Combine(cache, "readme.txt");
        File.WriteAllText(loose, "x");

        AgentHub.CleanStaleUploadTemp(cache).Should().Be(2);

        Directory.Exists(stale).Should().BeFalse();
        Directory.Exists(half).Should().BeFalse();
        Directory.Exists(other).Should().BeTrue("名稱不是上傳 id 的不是這裡建的，不碰");
        File.Exists(loose).Should().BeTrue();
        AgentHub.CleanStaleUploadTemp(cache).Should().Be(0);
        AgentHub.CleanStaleUploadTemp(Path.Combine(temp.Root, "沒有這個資料夾")).Should().Be(0);
    }

    // ───────────────────────────── 遠端 send 帶網頁上傳的附件（步 13） ─────────────────────────────

    [Fact]
    public async Task 遠端send只帶網頁上傳的附件_本機附的留在輸入區_本機送出帶走剩下的()
    {
        using var rig = new Rig();
        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1, "網頁上傳.txt")))).Outcome);
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id).Attachments.Count == 1, "上傳成為附件");
        rig.H.Command(rig.H.Main, "addFiles", new { conversationId = rig.Id }, new[] { rig.H.Temp.WriteFile(@"in\本機的.csv", "a,b") });
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id).Attachments.Count == 2, "本機也附了一個");

        ShouldBeDone((await rig.RunAsync("send", new { text = "看一下附件" })).Outcome);

        rig.Session.Sent.All[^1].Input.Text.Should().Be("看一下附件");
        rig.Session.Sent.All[^1].Input.Attachments.Select(a => a.FileName).Should().Equal(["網頁上傳.txt"], "遠端只帶網頁上傳的");
        rig.H.Items(rig.Id).Last(i => i.Kind == AgentItemKind.User).Attachments.Select(a => a.FileName).Should().Equal("網頁上傳.txt");
        rig.Composer().Attachments.Should().Equal(["本機的.csv"], "本機使用者自己附的留在輸入區（WP4.4 計畫 D19）");

        // 下一則遠端 send 沒有網頁上傳的附件可帶
        rig.H.CompleteTurn(rig.Session);
        ShouldBeDone((await rig.RunAsync("send", new { text = "再一則" })).Outcome);
        rig.Session.Sent.All[^1].Input.Attachments.Should().BeEmpty();
        rig.Composer().Attachments.Should().Equal(["本機的.csv"]);
    }

    [Fact]
    public async Task 遠端send遇到還在下載的上傳_附件還在處理中_conflict_上傳完之後再送就帶上()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transfer.Download = async (u, file, ct) =>
        {
            await gate.Task;
            await File.WriteAllTextAsync(file, "內容", ct);
        };
        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1)))).Outcome);
        var sent = rig.Session.Sent.Count;

        var (early, output) = await rig.RunAsync("send", new { text = "先送" });
        ShouldBeRejected(early, RemoteResultCodes.Conflict);
        early.Message.Should().Be("附件還在處理中，請稍候");
        output.Should().Contain(m => m.Value<string>("type") == "sendRejected", "文字還給瀏覽器");
        rig.Session.Sent.Count.Should().Be(sent);

        gate.SetResult();
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id) is { Importing: 0, Attachments.Count: 1 }, "上傳成為附件");
        ShouldBeDone((await rig.RunAsync("send", new { text = "現在送" })).Outcome);
        rig.Session.Sent.All[^1].Input.Attachments.Select(a => a.FileName).Should().Equal("檔案1.txt");
    }

    [Fact]
    public async Task 遠端send只附檔不打字_送出網頁上傳的附件()
    {
        // 合併前審查 INT-1：原本遠端空送一律是「接著送佇列」，佇列是空的就 conflict，網頁上傳之後只送截圖的訊息永遠送不出去
        using var rig = new Rig();
        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1, "截圖.png")))).Outcome);
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id).Attachments.Count == 1, "上傳成為附件");
        var before = rig.Sent().Count;

        ShouldBeDone((await rig.RunAsync("send", new { text = "" })).Outcome);

        var (count, last) = rig.Sent();
        count.Should().Be(before + 1);
        last!.Text.Should().BeEmpty();
        last.Attachments.Select(a => a.FileName).Should().Equal("截圖.png");
        rig.Composer().Attachments.Should().BeEmpty();
        rig.H.Items(rig.Id).Last(i => i.Kind == AgentItemKind.User).Attachments.Select(a => a.FileName).Should().Equal("截圖.png");

        // 沒有網頁上傳的附件時照舊：空送＝接著送佇列，佇列是空的就 conflict
        rig.H.CompleteTurn(rig.H.Sessions[^1]);
        ShouldBeRejected((await rig.RunAsync("send", new { text = "" })).Outcome, RemoteResultCodes.Conflict);
    }

    [Fact]
    public async Task 遠端send只附檔不打字_佇列暫停中也送出附件_佇列那則留著()
    {
        using var rig = new Rig();
        rig.H.Send(rig.H.Main, rig.Id, "本機的一輪");
        rig.H.Send(rig.H.Main, rig.Id, "排隊A");
        rig.H.CompleteTurn(rig.H.Sessions[^1], AgentTurnOutcome.Interrupted);
        rig.H.Queue(rig.Id).Should().Equal("排隊A");
        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1, "截圖.png")))).Outcome);
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id).Attachments.Count == 1, "上傳成為附件");
        var before = rig.Sent().Count;

        ShouldBeDone((await rig.RunAsync("send", new { text = "" })).Outcome);

        var (count, last) = rig.Sent();
        count.Should().Be(before + 1);
        last!.Text.Should().BeEmpty("送的是網頁上傳的附件，不是佇列那則");
        last.Attachments.Select(a => a.FileName).Should().Equal("截圖.png");
        rig.H.Queue(rig.Id).Should().Equal("排隊A");
    }

    [Fact]
    public async Task 遠端send只附檔不打字_上傳還在下載時回附件還在處理中()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transfer.Download = async (u, file, ct) =>
        {
            await gate.Task;
            await File.WriteAllTextAsync(file, "內容", ct);
        };
        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1)))).Outcome);

        var (early, _) = await rig.RunAsync("send", new { text = "" });

        ShouldBeRejected(early, RemoteResultCodes.Conflict);
        early.Message.Should().Be("附件還在處理中，請稍候", "不是「佇列是空的」：addUploaded 回 done 時檔案還在下載");
        gate.SetResult();
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id) is { Importing: 0, Attachments.Count: 1 }, "上傳成為附件");
    }

    [Fact]
    public async Task 遠端send送出前被擋下_帶的網頁上傳附件放回輸入區_之後的遠端send帶走()
    {
        // 合併前審查 INT-4：附件在背景檢查工作資料夾之前就從輸入區取走，擋下時只把文字還給瀏覽器，附件就此不見
        using var rig = new Rig();
        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1, "截圖.png")))).Outcome);
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id).Attachments.Count == 1, "上傳成為附件");
        var folder = rig.H.Temp.CreateDirectory("proj");
        var policy = rig.H.OnUi(() => rig.H.Hub.GetConversation(rig.Id)!.SyncPolicy);
        rig.H.Ui.Invoke(() => rig.H.Hub.GetConversation(rig.Id)!.WorkingDirectory = folder);
        var before = rig.Sent().Count;

        // 停在背景檢查資料夾時對話改成只同步標題：送出前的再查擋下
        var held = await rig.RunRacingAsync("send", new { text = "看這張" },
            () => rig.H.Hub.GetConversation(rig.Id)!.SyncPolicy = ConversationSyncPolicies.Metadata);

        held.State.Should().Be(RemoteResultStates.Rejected, held.Message);
        held.Code.Should().Be(RemoteResultCodes.Policy);
        rig.Sent().Count.Should().Be(before);
        rig.Composer().Attachments.Should().Equal(["截圖.png"], "附件放回輸入區（瀏覽器那一側沒有附件可還）");

        rig.H.Ui.Invoke(() => rig.H.Hub.GetConversation(rig.Id)!.SyncPolicy = policy);
        ShouldBeDone((await rig.RunAsync("send", new { text = "再送一次" })).Outcome);
        await rig.H.WaitUntilAsync(() => rig.Sent().Count == before + 1, "送進代理（工作資料夾在背景檢查）");
        rig.Sent().Last!.Attachments.Select(a => a.FileName).Should().Equal(["截圖.png"], "仍算網頁上傳的，遠端 send 帶走");
        rig.Composer().Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task 遠端send只附檔不打字_送出前被擋下_附件放回輸入區_不留在佇列()
    {
        // 合併前審查 INT-1、INT-4：只帶網頁上傳附件的 send 也是這個指令自己帶來的內容，不是空送從佇列取出的那則
        using var rig = new Rig();
        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1, "截圖.png")))).Outcome);
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id).Attachments.Count == 1, "上傳成為附件");
        var folder = rig.H.Temp.CreateDirectory("proj");
        rig.H.Ui.Invoke(() => rig.H.Hub.GetConversation(rig.Id)!.WorkingDirectory = folder);
        var before = rig.Sent().Count;

        var held = await rig.RunRacingAsync("send", new { text = "" },
            () => rig.H.Hub.GetConversation(rig.Id)!.SyncPolicy = ConversationSyncPolicies.Metadata);

        held.State.Should().Be(RemoteResultStates.Rejected, held.Message);
        held.Code.Should().Be(RemoteResultCodes.Policy);
        rig.H.Queue(rig.Id).Should().BeEmpty("不放回佇列、不在這台留一則暫停的訊息");
        rig.Composer().Attachments.Should().Equal(["截圖.png"], "附件放回輸入區");
        rig.Sent().Count.Should().Be(before);
    }

    [Fact]
    public async Task 遠端send工作資料夾不見了_附件放回輸入區_等待期間另一則遠端send之後仍算網頁上傳的()
    {
        // 合併前審查 INT-4：SendAsync 檢查工作資料夾失敗時附件放回輸入區；等待期間另一則遠端 send 查網頁上傳的附件時，
        // 這則帶走的已不在輸入區（id 被拿掉），放回時要補回 id，之後的遠端 send 才帶得走
        using var rig = new Rig();
        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1, "截圖.png")))).Outcome);
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id).Attachments.Count == 1, "上傳成為附件");
        var missing = Path.Combine(rig.H.Temp.Root, "不見了");
        rig.H.Ui.Invoke(() => rig.H.Hub.GetConversation(rig.Id)!.WorkingDirectory = missing);
        var before = rig.Sent().Count;
        Task<RemoteOutcome>? other = null;

        var first = await rig.RunRacingAsync("send", new { text = "看這張" }, () => other = rig.StartSend("另一則"));
        var second = await other!.WaitAsync(TimeSpan.FromSeconds(20));

        ShouldBeRejected(first, RemoteResultCodes.Conflict);
        ShouldBeRejected(second, RemoteResultCodes.Conflict);
        rig.Sent().Count.Should().Be(before);
        rig.Composer().Attachments.Should().Equal(["截圖.png"], "沒送出的附件還回輸入區");

        var folder = rig.H.Temp.CreateDirectory("proj");
        rig.H.Ui.Invoke(() => rig.H.Hub.GetConversation(rig.Id)!.WorkingDirectory = folder);
        ShouldBeDone((await rig.RunAsync("send", new { text = "再送一次" })).Outcome);
        await rig.H.WaitUntilAsync(() => rig.Sent().Count == before + 1, "送進代理");
        rig.Sent().Last!.Attachments.Select(a => a.FileName).Should().Equal(["截圖.png"], "仍算網頁上傳的，遠端 send 帶走");
    }

    // ───────────────────────────── conversationCreate 帶 uploads（WP4.4 的登記點 PrepareRemoteUploads，步 13） ─────────────────────────────

    [Fact]
    public async Task conversationCreate帶uploads_done_第一則等上傳處理完才送_附件和第一則一起送出()
    {
        using var rig = new Rig();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transfer.Download = async (u, file, ct) =>
        {
            await gate.Task;
            await File.WriteAllTextAsync(file, "內容" + u.UploadId, ct);
        };
        var sessions = rig.H.Sessions.Count;
        var uploads = new[] { Upload(1, "規格.txt"), Upload(2, "截圖.png") };

        var (outcome, id) = await rig.RunCreateAsync("照附件整理", uploads);

        ShouldBeDone(outcome);
        rig.H.Conversation(id).Should().NotBeNull("對話照建");
        rig.H.OnUi(() => rig.H.Store.LoadConversations()).Should().ContainSingle(m => m.Id == id, "存成正式對話");
        rig.H.OnUi(() => rig.H.Hub.ComposerStateForTest(id)).Importing.Should().Be(2, "下載期間匯入計數是筆數");
        await rig.H.Ui.DrainAsync();
        rig.H.Sessions.Count.Should().Be(sessions, "上傳處理完之前不送第一則（送了會被「附件還在處理中」拒絕）");
        rig.H.Items(id).Should().NotContain(i => i.Kind == AgentItemKind.User);

        gate.SetResult();

        await rig.H.WaitUntilAsync(() => rig.H.Sessions.Count == sessions + 1 && rig.H.Sessions[^1].Sent.Count == 1, "上傳處理完之後送出第一則");
        var first = rig.H.Sessions[^1].Sent.All[0].Input;
        first.Text.Should().Be("照附件整理");
        first.Attachments.Select(a => a.FileName).Should().BeEquivalentTo(["規格.txt", "截圖.png"]);
        rig.H.OnUi(() => rig.H.Hub.ComposerStateForTest(id)).Attachments.Should().BeEmpty("附件隨第一則送出");
        await rig.H.WaitUntilAsync(() => uploads.All(u => !Directory.Exists(UploadTemp(u))), "暫存資料夾刪了");
    }

    [Fact]
    public async Task conversationCreate帶uploads_上傳失敗時第一則照樣送_沒有附件_有Notice()
    {
        using var rig = new Rig();
        rig.Transfer.Download = async (u, file, ct) =>
        {
            await File.WriteAllTextAsync(file, "壞掉", ct);
            throw new InvalidDataException("SHA-256 不符");
        };
        var sessions = rig.H.Sessions.Count;

        var (outcome, id) = await rig.RunCreateAsync("照附件整理", Upload(1));

        ShouldBeDone(outcome);
        await rig.H.WaitUntilAsync(() => rig.H.Sessions.Count == sessions + 1 && rig.H.Sessions[^1].Sent.Count == 1, "第一則照樣送出");
        rig.H.Sessions[^1].Sent.All[0].Input.Attachments.Should().BeEmpty();
        rig.H.Items(id).Should().Contain(i => i.Kind == AgentItemKind.Notice && i.Text.Contains("從網頁上傳的檔案沒有加進附件"));
    }

    [Fact]
    public async Task conversationCreate帶uploads_沒有文字_附件留在輸入區_之後遠端send帶走()
    {
        using var rig = new Rig();
        var sessions = rig.H.Sessions.Count;

        var (outcome, id) = await rig.RunCreateAsync(null, Upload(1, "a.txt"));

        ShouldBeDone(outcome);
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(id) is { Importing: 0, Attachments.Count: 1 }, "上傳成為附件");
        rig.H.Sessions.Count.Should().Be(sessions, "沒有文字：不送");
    }

    [Fact]
    public async Task conversationCreate帶uploads_沒有傳輸回unsupported_下載中的id回busy_什麼都沒建()
    {
        using var rig = new Rig(canTransfer: false);
        var (noTransfer, noTransferId) = await rig.RunCreateAsync("hi", Upload(1));
        ShouldBeRejected(noTransfer, RemoteResultCodes.Unsupported);
        noTransfer.Message.Should().Contain("收不到網頁上傳的檔案", "拒絕的是守門的原因，不是 WP4.4 的「還不支援」");
        rig.H.Conversation(noTransferId).Should().BeNull();

        rig.Transfer.CanTransfer = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Transfer.Download = async (u, file, ct) =>
        {
            await gate.Task;
            await File.WriteAllTextAsync(file, "內容", ct);
        };
        ShouldBeDone((await rig.RunAsync("addUploaded", UploadsArgs(Upload(1)))).Outcome);

        var (busy, busyId) = await rig.RunCreateAsync("hi", Upload(1));
        ShouldBeRejected(busy, RemoteResultCodes.Busy);
        rig.H.Conversation(busyId).Should().BeNull("rejected 一定表示什麼都沒建");
        rig.H.OnUi(() => rig.H.Store.LoadConversations()).Should().NotContain(m => m.Id == busyId);

        gate.SetResult();
        await rig.H.WaitUntilAsync(() => rig.H.Hub.ComposerStateForTest(rig.Id).Attachments.Count == 1, "addUploaded 的照樣成為附件");
        rig.Transfer.Downloads.Count.Should().Be(1, "被拒的那個沒有下載");
    }

    [Fact]
    public async Task 層級_工作資料夾的listFiles是1_dirsOnly與fileFetch是2()
    {
        using var rig = new Rig();
        var file = rig.Write("a.txt", "a");

        (await rig.RunAsync("listFiles", new { requestId = "t1", path = (string?)null })).Outcome.Tier.Should().Be(1);
        (await rig.RunAsync("listFiles", new { requestId = "t2", path = (string?)null, dirsOnly = true })).Outcome.Tier.Should().Be(2, "dirsOnly 是層級 2（信封宣告 1 也取較高者）");
        (await rig.RunAsync("fileFetch", new { requestId = "t3", path = file })).Outcome.Tier.Should().Be(2, "fileFetch 是層級 2");
        (await rig.RunAsync("fileSearch", new { requestId = "t4", query = "a" })).Outcome.Tier.Should().Be(1);
    }

    // ───────────────────────────── 授權 ─────────────────────────────

    [Fact]
    public async Task context_Authorized是false時拒絕_什麼都不做()
    {
        using var rig = new Rig();
        rig.Write("hello.txt", "hello");
        var handler = rig.H.Ui.Invoke(() => rig.H.Hub.CreateFileRemoteHandlerForTest());

        foreach (var cmd in new[] { "listFiles", "fileSearch", "fileFetch", "addUploaded" })
        {
            var context = new RemoteViewContext(CommandId, RemoteSamples.DeviceId, rig.Wire, 1, 2, false, 1_000, 2_000, false, "Chrome・Windows");
            var command = cmd == "addUploaded"
                ? JObject.FromObject(UploadsArgs(Upload(1)))
                : new JObject { ["requestId"] = "x", ["path"] = Path.Combine(rig.Workspace, "hello.txt"), ["query"] = "hello" };
            command["cmd"] = cmd;
            command["conversationId"] = rig.Id;

            rig.H.Ui.Invoke(() => handler.TryHandle(context, null!, cmd, command)).Should().BeTrue(cmd);
            var outcome = await context.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            outcome.State.Should().Be(RemoteResultStates.Rejected, cmd);
            outcome.Code.Should().Be(RemoteResultCodes.Invalid, cmd);
        }

        rig.H.Ui.Invoke(() => handler.TryHandle(new RemoteViewContext(CommandId, RemoteSamples.DeviceId, rig.Wire, 1, 1, false, 1_000, 2_000, false, "x"),
            null!, "rename", new JObject())).Should().BeFalse("不是檔案指令");
        rig.Transfer.Registered.Count.Should().Be(0);
        rig.Transfer.Downloads.Count.Should().Be(0);
        rig.Composer().Importing.Should().Be(0);
    }

    // ───── 語音訊息（voiceMessage，2026-10-23）：照 addUploaded 下載匯入，辨識後照遠端 send 送出 ─────

    private static RemoteUpload VoiceUpload(int i = 1) => Upload(i, $"語音訊息_20261023_12000{i}.weba", 64);

    [Fact]
    public async Task voiceMessage_下載_辨識_送出_畫面是語音訊息_代理收到逐字稿與錄音()
    {
        using var rig = new Rig();
        var voice = new FakeVoiceTranscriber();
        rig.H.Ui.Invoke(() => rig.H.Hub.AttachVoice(voice));

        var (outcome, _) = await rig.RunAsync("voiceMessage", UploadsArgs(VoiceUpload()));

        ShouldBeDone(outcome);
        await rig.H.WaitUntilAsync(() => rig.Session.Sent.Count == 2, "語音訊息送進代理");
        var input = rig.Session.Sent.All[1].Input;
        input.Text.Should().Contain("逐字稿：「請幫我整理九月的報表」").And.Contain("請先用一段文字複誦");
        input.Attachments.Should().ContainSingle(a => a.FileName.StartsWith(VoiceMessageRules.FilePrefix));
        voice.Paths.Should().ContainSingle();
        var user = rig.H.Items(rig.Id).Last(i => i.Kind == AgentItemKind.User);
        user.Text.Should().Be("語音訊息（0:12）");
        rig.Composer().Attachments.Should().BeEmpty();
    }

    [Fact]
    public async Task voiceMessage_沒有辨識器_unsupported_不下載()
    {
        using var rig = new Rig();

        var (outcome, _) = await rig.RunAsync("voiceMessage", UploadsArgs(VoiceUpload()));

        ShouldBeRejected(outcome, RemoteResultCodes.Unsupported);
        rig.Transfer.Downloads.Count.Should().Be(0);
    }

    [Fact]
    public async Task voiceMessage_不是一個錄音檔_invalid_不下載()
    {
        using var rig = new Rig();
        rig.H.Ui.Invoke(() => rig.H.Hub.AttachVoice(new FakeVoiceTranscriber()));

        ShouldBeRejected((await rig.RunAsync("voiceMessage", UploadsArgs(Upload(1, "報表.txt")))).Outcome, RemoteResultCodes.Invalid);
        ShouldBeRejected((await rig.RunAsync("voiceMessage", UploadsArgs(VoiceUpload(1), VoiceUpload(2)))).Outcome, RemoteResultCodes.Invalid);
        rig.Transfer.Downloads.Count.Should().Be(0);
    }

    [Fact]
    public async Task voiceMessage_辨識失敗_rejected說明原因_錄音留在輸入區_沒送出()
    {
        using var rig = new Rig();
        var voice = new FakeVoiceTranscriber { Failure = new Myriagent.Voice.VoiceTranscribeException("這台電腦找不到 Python") };
        rig.H.Ui.Invoke(() => rig.H.Hub.AttachVoice(voice));

        var (outcome, _) = await rig.RunAsync("voiceMessage", UploadsArgs(VoiceUpload()));

        ShouldBeRejected(outcome, RemoteResultCodes.Unsupported);
        outcome.Message.Should().Contain("找不到 Python").And.Contain("錄音已放進輸入區的附件");
        rig.Composer().Attachments.Should().ContainSingle(n => n.StartsWith(VoiceMessageRules.FilePrefix));
        rig.Session.Sent.Count.Should().Be(1, "只有開頭那則");
    }
}
