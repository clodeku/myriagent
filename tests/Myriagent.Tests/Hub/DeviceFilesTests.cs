using System.Security.Cryptography;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Protocol.Remote;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 電腦狀態面板的檔案傳輸在被操作那台（AgentHub.DeviceFiles.cs；docs/sync-protocol.md 14.15）：
/// deviceBrowse、deviceFetch、devicePut 經真的閘門；磁碟根目錄與常用資料夾以測試接縫指到暫存資料夾所在的磁碟
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class DeviceFilesTests
{
    private sealed class FakeTransfer : IRemoteFileTransfer
    {
        public bool CanTransfer { get; set; } = true;

        public Recorder<(string ConversationId, string Kind, IReadOnlyList<string> Paths)> Registered { get; } = new();

        public Func<RemoteUpload, string, CancellationToken, Task>? Download { get; set; }

        public Task<IReadOnlyList<string>> RegisterRefsAsync(string conversationId, string kind, IReadOnlyList<string> fullPaths, CancellationToken ct)
        {
            Registered.Add((conversationId, kind, fullPaths.ToList()));
            IReadOnlyList<string> refs = fullPaths.Select(_ => MediaRefIssueRules.NewRef(RandomNumberGenerator.GetBytes)).ToList();
            return Task.FromResult(refs);
        }

        public async Task DownloadUploadAsync(RemoteUpload upload, string destinationFile, CancellationToken ct)
        {
            if (Download is { } download)
                await download(upload, destinationFile, ct);
            else
                await File.WriteAllBytesAsync(destinationFile, new byte[upload.Size], ct);
        }
    }

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            H = new HubHarness();
            var drive = Path.GetPathRoot(Outside.Root)!.ToUpperInvariant();
            H.Ui.Invoke(() =>
            {
                H.Settings.Sync.RemoteControlLevel = SyncSettings.LevelControl;
                H.Hub.RemoteFileTransferForTest = Transfer;
                H.Hub.DeviceFilesElevatedForTest = () => Elevated;
                H.Hub.DeviceDriveRootsForTest = () => [drive];
                H.Hub.DevicePlacesForTest = () => [("測試資料夾", Outside.Root)];
            });
            Drive = drive;
        }

        public HubHarness H { get; }

        public string Drive { get; }

        public bool Elevated { get; set; }

        public FakeTransfer Transfer { get; } = new();

        public TempDirectory Outside { get; } = new("device-files");

        public async Task<(RemoteOutcome Outcome, IReadOnlyList<JObject> Output)> RunAsync(string cmd, JObject args, int tier = 2)
        {
            var command = new JObject { ["cmd"] = cmd, ["deviceId"] = RemoteSamples.DeviceId };
            foreach (var p in args.Properties())
                command[p.Name] = p.Value;
            var context = new RemoteViewContext(RemoteEntrySamples.NewCid(), RemoteSamples.DeviceId, null, null, tier, false, 1_000, 2_000, false, "電腦「書房」");
            var output = new Recorder<JObject>();
            var outcome = await H.Ui.Invoke(() => H.Hub.ExecuteRemoteAsync(context, command, output.Add)).WaitAsync(TimeSpan.FromSeconds(20));
            return (outcome, output.All);
        }

        public Task<(RemoteOutcome Outcome, IReadOnlyList<JObject> Output)> BrowseAsync(string? path)
            => RunAsync(RemoteCommandCatalog.DeviceBrowse, new JObject { ["requestId"] = "b1", ["path"] = path is null ? JValue.CreateNull() : path });

        public void Dispose()
        {
            H.Dispose();
            Outside.Dispose();
        }
    }

    private static void ShouldBeRejected(RemoteOutcome outcome, string code)
    {
        outcome.State.Should().Be(RemoteResultStates.Rejected, outcome.Message);
        outcome.Code.Should().Be(code, outcome.Message);
    }

    private static RemoteUpload Upload(string id, string name, long size = 5) => new(id, name, size, new string('a', 64));

    private static JObject PutArgs(string folder, params RemoteUpload[] uploads) => new()
    {
        ["requestId"] = "p1",
        ["folder"] = folder,
        ["uploads"] = new JArray(uploads.Select(u => new JObject { ["uploadId"] = u.UploadId, ["name"] = u.Name, ["size"] = u.Size, ["sha256"] = u.Sha256 })),
    };

    // ───────────────────────────── deviceBrowse ─────────────────────────────

    [Fact]
    public async Task deviceBrowse的path是null_列磁碟與驗得過的常用資料夾_沒有對話id()
    {
        using var rig = new Rig();

        var (outcome, output) = await rig.BrowseAsync(null);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var files = output.Should().ContainSingle().Which;
        files.Value<string>("type").Should().Be("files");
        files.Value<string>("requestId").Should().Be("b1");
        files["conversationId"]!.Type.Should().Be(JTokenType.Null);
        files["path"]!.Type.Should().Be(JTokenType.Null);
        var entries = files["entries"]!.Cast<JObject>().ToList();
        entries.Select(e => e.Value<string>("path")).Should().Contain(rig.Drive);
        entries.Should().Contain(e => e.Value<string>("name") == "測試資料夾");
        entries.First().Value<string>("name").Should().EndWith("(" + rig.Drive[..2] + ")");
    }

    [Fact]
    public async Task deviceBrowse列資料夾與檔案_parent是上一層_app的資料夾回policy()
    {
        using var rig = new Rig();
        var folder = rig.Outside.CreateDirectory("專案");
        rig.Outside.CreateDirectory(@"專案\src");
        rig.Outside.WriteFile(@"專案\報告.docx", "x");

        var (outcome, output) = await rig.BrowseAsync(folder);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var files = output.Single();
        files.Value<string>("path")!.ToUpperInvariant().Should().Be(folder.ToUpperInvariant());
        files.Value<string>("parent")!.ToUpperInvariant().Should().Be(rig.Outside.Root.ToUpperInvariant());
        files["entries"]!.Cast<JObject>().Select(e => e.Value<string>("name")).Should().BeEquivalentTo(["src", "報告.docx"]);

        var appFolder = rig.H.Store.GetDefaultWorkingDirectory("c-device-files");
        Directory.CreateDirectory(appFolder);
        var (refused, none) = await rig.BrowseAsync(appFolder);
        ShouldBeRejected(refused, RemoteResultCodes.Policy);
        refused.Message.Should().Be(DeviceTransferRules.RefusedPathMessage);
        none.Should().BeEmpty();

        // 網路芳鄰（2026-11-09 起）：UNC 改走網路分支，這台自己的分享以 error 說明、不列內容
        var (unc, uncOutput) = await rig.BrowseAsync(@"\\localhost\C$\Windows");
        unc.State.Should().Be(RemoteResultStates.Done, unc.Message);
        uncOutput.Single().Value<string>("error").Should().Contain("自己");
        uncOutput.Single()["entries"]!.Should().BeEmpty();
    }

    [Fact]
    public async Task 以系統管理員執行時三個指令都拒絕()
    {
        using var rig = new Rig { Elevated = true };
        var file = rig.Outside.WriteFile("a.txt", "x");

        var (browse, _) = await rig.BrowseAsync(null);
        var (fetch, _) = await rig.RunAsync(RemoteCommandCatalog.DeviceFetch, new JObject { ["requestId"] = "f1", ["path"] = file });
        var (put, _) = await rig.RunAsync(RemoteCommandCatalog.DevicePut, PutArgs(rig.Outside.Root, Upload("u1", "a.txt")));

        foreach (var outcome in new[] { browse, fetch, put })
        {
            ShouldBeRejected(outcome, RemoteResultCodes.Policy);
            outcome.Message.Should().Be(DeviceTransferRules.ElevatedMessage);
        }
    }

    [Fact]
    public async Task 政策不是可以操作時在閘門拒絕()
    {
        using var rig = new Rig();
        rig.H.Ui.Invoke(() => rig.H.Settings.Sync.RemoteControlLevel = SyncSettings.LevelView);

        var (outcome, output) = await rig.BrowseAsync(null);

        outcome.State.Should().Be(RemoteResultStates.Rejected);
        outcome.Step.Should().Be(RemoteGateSteps.Policy);
        output.Should().BeEmpty();
    }

    // ───────────────────────────── deviceFetch ─────────────────────────────

    [Fact]
    public async Task deviceFetch發xfer參照_沒有對話_憑證類檔名回error()
    {
        using var rig = new Rig();
        var file = rig.Outside.WriteFile("大檔.bin", new string('x', 1000));
        var key = rig.Outside.WriteFile("id_rsa", "secret");

        var (outcome, output) = await rig.RunAsync(RemoteCommandCatalog.DeviceFetch, new JObject { ["requestId"] = "f1", ["path"] = file });

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var reference = output.Single();
        reference.Value<string>("type").Should().Be("fileRef");
        reference["conversationId"]!.Type.Should().Be(JTokenType.Null);
        reference.Value<string>("name").Should().Be("大檔.bin");
        reference.Value<long>("size").Should().Be(1000);
        reference.Value<string>("ref").Should().NotBeNullOrEmpty();
        var registered = rig.Transfer.Registered.All.Should().ContainSingle().Which;
        registered.ConversationId.Should().BeEmpty();
        registered.Kind.Should().Be(MediaServerRules.TransferItemId);

        var (refused, refusedOutput) = await rig.RunAsync(RemoteCommandCatalog.DeviceFetch, new JObject { ["requestId"] = "f2", ["path"] = key });
        refused.State.Should().Be(RemoteResultStates.Done);
        refusedOutput.Single().Value<string>("error").Should().NotBeNullOrEmpty();
        rig.Transfer.Registered.Count.Should().Be(1);
    }

    [Fact]
    public async Task deviceFetch沒有傳輸時回unsupported()
    {
        using var rig = new Rig();
        rig.Transfer.CanTransfer = false;
        var file = rig.Outside.WriteFile("a.txt", "x");

        var (outcome, _) = await rig.RunAsync(RemoteCommandCatalog.DeviceFetch, new JObject { ["requestId"] = "f1", ["path"] = file });

        ShouldBeRejected(outcome, RemoteResultCodes.Unsupported);
    }

    // ───────────────────────────── devicePut ─────────────────────────────

    [Fact]
    public async Task devicePut接下後在背景存檔_同名自動改名_進度在接收表_做完在這台提示()
    {
        using var rig = new Rig();
        var folder = rig.Outside.CreateDirectory("收件");
        rig.Outside.WriteFile(@"收件\報告.docx", "old");

        var (outcome, output) = await rig.RunAsync(RemoteCommandCatalog.DevicePut, PutArgs(folder, Upload("u1", "報告.docx", 7), Upload("u2", "新.txt", 3)));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        output.Should().BeEmpty();
        await rig.H.WaitUntilAsync(() => rig.H.Hub.DeviceXferSnapshot()?.Count(x => x.State == "saved") == 2, "兩個檔都存好", 10_000);
        var snapshot = rig.H.Hub.DeviceXferSnapshot()!;
        snapshot.Single(x => x.Id == "u1").Saved.Should().Be("報告 (2).docx");
        snapshot.Single(x => x.Id == "u2").Saved.Should().Be("新.txt");
        File.ReadAllText(Path.Combine(folder, "報告.docx")).Should().Be("old", "絕不覆寫");
        new FileInfo(Path.Combine(folder, "報告 (2).docx")).Length.Should().Be(7);
        Directory.GetFiles(folder, "*" + DeviceTransferRules.PartSuffix).Should().BeEmpty();
        await rig.H.WaitUntilAsync(() => rig.H.Main.All().Any(m => m.Value<string>("type") == "toast" && (m.Value<string>("text") ?? "").Contains("2 個檔案")), "這台提示收到了");
    }

    [Fact]
    public async Task devicePut下載失敗時標成失敗_暫存檔刪掉()
    {
        using var rig = new Rig();
        var folder = rig.Outside.CreateDirectory("收件");
        rig.Transfer.Download = async (u, file, ct) =>
        {
            await File.WriteAllTextAsync(file, "half", ct);
            throw new InvalidDataException("雜湊不符");
        };

        var (outcome, _) = await rig.RunAsync(RemoteCommandCatalog.DevicePut, PutArgs(folder, Upload("u1", "a.txt")));

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        await rig.H.WaitUntilAsync(() => rig.H.Hub.DeviceXferSnapshot()?.Any(x => x.State == "failed") == true, "標成失敗", 10_000);
        Directory.GetFiles(folder).Should().BeEmpty();
    }

    [Fact]
    public async Task devicePut的資料夾不存在回conflict_app的資料夾回policy_上傳不合格回invalid()
    {
        using var rig = new Rig();
        var (missing, _) = await rig.RunAsync(RemoteCommandCatalog.DevicePut, PutArgs(rig.Outside.Combine("沒有"), Upload("u1", "a.txt")));
        ShouldBeRejected(missing, RemoteResultCodes.Conflict);

        var appFolder = rig.H.Store.GetDefaultWorkingDirectory("c-device-put");
        Directory.CreateDirectory(appFolder);
        var (forbidden, _) = await rig.RunAsync(RemoteCommandCatalog.DevicePut, PutArgs(appFolder, Upload("u2", "a.txt")));
        ShouldBeRejected(forbidden, RemoteResultCodes.Policy);

        var (tooBig, _) = await rig.RunAsync(RemoteCommandCatalog.DevicePut, PutArgs(rig.Outside.Root, Upload("u3", "a.bin", DeviceTransferRules.MaxPutFileBytes + 1)));
        ShouldBeRejected(tooBig, RemoteResultCodes.Invalid);
        rig.H.Hub.DeviceXferSnapshot().Should().BeNull();
    }

    // ───────────────────────────── 整個資料夾（deviceTree、deviceFetchMany、devicePut 的 subfolder） ─────────────────────────────

    [Fact]
    public async Task deviceTree逐層列出相對路徑與大小_不列憑證類檔名_app的資料夾回policy()
    {
        using var rig = new Rig();
        var folder = rig.Outside.CreateDirectory("相簿");
        rig.Outside.WriteFile(@"相簿\a.txt", "12345");
        rig.Outside.WriteFile(@"相簿\2024\06\b.jpg", "xy");
        rig.Outside.CreateDirectory(@"相簿\空的");
        rig.Outside.WriteFile(@"相簿\金鑰\id_rsa", "secret");

        var (outcome, output) = await rig.RunAsync(RemoteCommandCatalog.DeviceTree, new JObject { ["requestId"] = "t1", ["path"] = folder });

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var tree = output.Single();
        tree.Value<string>("type").Should().Be("fileTree");
        tree.Value<string>("requestId").Should().Be("t1");
        tree["conversationId"]!.Type.Should().Be(JTokenType.Null);
        tree.Value<string>("name").Should().Be("相簿");
        tree.Value<bool>("truncated").Should().BeFalse();
        var files = tree["files"]!.OfType<JObject>().ToDictionary(f => f.Value<string>("path")!, f => f.Value<long>("size"));
        files.Should().BeEquivalentTo(new Dictionary<string, long> { ["a.txt"] = 5, [@"2024\06\b.jpg"] = 2 });
        tree["dirs"]!.Values<string>().Should().Contain([@"2024", @"2024\06", "空的", "金鑰"]);

        var appFolder = rig.H.Store.GetDefaultWorkingDirectory("c-device-tree");
        Directory.CreateDirectory(appFolder);
        var (forbidden, _) = await rig.RunAsync(RemoteCommandCatalog.DeviceTree, new JObject { ["requestId"] = "t2", ["path"] = appFolder });
        ShouldBeRejected(forbidden, RemoteResultCodes.Policy);
    }

    [Fact]
    public async Task deviceFetchMany一次發多個xfer參照_跳出根目錄與憑證類的那一項帶error()
    {
        using var rig = new Rig();
        var folder = rig.Outside.CreateDirectory("相簿");
        rig.Outside.WriteFile(@"相簿\a.txt", "12345");
        rig.Outside.WriteFile(@"相簿\2024\b.jpg", "xy");
        rig.Outside.WriteFile(@"相簿\id_rsa", "secret");
        rig.Outside.WriteFile("外面.txt", "out");
        var paths = new JArray("a.txt", "2024/b.jpg", "id_rsa", @"..\外面.txt", "沒有.txt");

        var (outcome, output) = await rig.RunAsync(RemoteCommandCatalog.DeviceFetchMany,
            new JObject { ["requestId"] = "m1", ["root"] = folder, ["pathsJson"] = paths.ToString(Newtonsoft.Json.Formatting.None) });

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        var refs = output.Single();
        refs.Value<string>("type").Should().Be("fileRefs");
        refs.Value<string>("requestId").Should().Be("m1");
        var items = refs["refs"]!.OfType<JObject>().ToList();
        items.Should().HaveCount(5);
        items[0].Value<string>("ref").Should().NotBeNullOrEmpty();
        items[0].Value<long>("size").Should().Be(5);
        items[1].Value<string>("path").Should().Be(@"2024\b.jpg");
        items[1].Value<string>("ref").Should().NotBeNullOrEmpty();
        items.Skip(2).Should().OnlyContain(i => i["ref"] == null && !string.IsNullOrEmpty(i.Value<string>("error")));
        var registered = rig.Transfer.Registered.All.Should().ContainSingle().Which;
        registered.ConversationId.Should().BeEmpty();
        registered.Paths.Should().HaveCount(2, "一次登記開得了的兩個");

        var (bad, _) = await rig.RunAsync(RemoteCommandCatalog.DeviceFetchMany, new JObject { ["requestId"] = "m2", ["root"] = folder, ["pathsJson"] = "不是 JSON" });
        ShouldBeRejected(bad, RemoteResultCodes.Invalid);
        var tooMany = new JArray(Enumerable.Range(0, DeviceFolderRules.MaxFetchMany + 1).Select(i => (object)("f" + i)).ToArray());
        var (many, _) = await rig.RunAsync(RemoteCommandCatalog.DeviceFetchMany,
            new JObject { ["requestId"] = "m3", ["root"] = folder, ["pathsJson"] = tooMany.ToString(Newtonsoft.Json.Formatting.None) });
        ShouldBeRejected(many, RemoteResultCodes.Invalid);
    }

    [Fact]
    public async Task devicePut帶subfolder時建好子資料夾再存_點點回invalid()
    {
        using var rig = new Rig();
        var folder = rig.Outside.CreateDirectory("收件");
        var args = PutArgs(folder, Upload("u1", "b.jpg", 4));
        args[RemoteCommandCatalog.DevicePutSubfolder] = "相簿/2024:06";

        var (outcome, _) = await rig.RunAsync(RemoteCommandCatalog.DevicePut, args);

        outcome.State.Should().Be(RemoteResultStates.Done, outcome.Message);
        await rig.H.WaitUntilAsync(() => rig.H.Hub.DeviceXferSnapshot()?.Any(x => x.State == "saved") == true, "存好了", 10_000);
        new FileInfo(Path.Combine(folder, "相簿", "2024_06", "b.jpg")).Length.Should().Be(4);

        var bad = PutArgs(folder, Upload("u2", "c.txt"));
        bad[RemoteCommandCatalog.DevicePutSubfolder] = @"相簿\..\..\外面";
        var (refused, _) = await rig.RunAsync(RemoteCommandCatalog.DevicePut, bad);
        ShouldBeRejected(refused, RemoteResultCodes.Invalid);
    }
}

/// <summary>整個資料夾傳輸的路徑規則（DeviceFolderRules）</summary>
public sealed class DeviceFolderRulesTests
{
    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("a/b/c.txt", @"a\b\c.txt")]
    [InlineData(@"a\b\c.txt", @"a\b\c.txt")]
    [InlineData(@"..\x", null)]
    [InlineData(@"a\.\b", null)]
    [InlineData(@"\a", null)]
    [InlineData(@"C:\a", null)]
    [InlineData("a:stream", null)]
    [InlineData(@"a\\b", null)]
    [InlineData("a.", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CheckRelative只收乾淨的相對路徑(string? input, string? expected)
        => DeviceFolderRules.CheckRelative(input).Should().Be(expected);

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("photos/2024", @"photos\2024")]
    [InlineData(@"\photos\\2024\", @"photos\2024")]
    [InlineData("a:b/c*d", @"a_b\c_d")]
    [InlineData("a/../b", null)]
    [InlineData(".", null)]
    public void CleanSubfolder清理每一段_點點不收(string? input, string? expected)
        => DeviceFolderRules.CleanSubfolder(input).Should().Be(expected);

    [Fact]
    public void RelativeTo與FolderName()
    {
        DeviceFolderRules.RelativeTo(@"C:\x", @"C:\x\a\b.txt").Should().Be(@"a\b.txt");
        DeviceFolderRules.RelativeTo(@"C:\x\", @"C:\X\a").Should().Be("a");
        DeviceFolderRules.RelativeTo(@"C:\x", @"C:\xy\a").Should().BeNull();
        DeviceFolderRules.RelativeTo(@"C:\x", @"C:\x").Should().BeNull();
        DeviceFolderRules.FolderName(@"C:\Users\me\相簿\").Should().Be("相簿");
        DeviceFolderRules.FolderName(@"D:\").Should().Be("D");
        DeviceFolderRules.FolderName(@"\\nas\share").Should().Be("share");
    }
}

/// <summary>電腦狀態面板的檔案傳輸的規則（DeviceTransferRules、DeviceXferBoard）</summary>
public sealed class DeviceTransferRulesTests
{
    [Fact]
    public void UniquePath同名加編號_檔或資料夾都算_清理檔名()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\in\a.txt", @"D:\in\a (2).txt", @"D:\in\b" };
        DeviceTransferRules.UniquePath(@"D:\in", "a.txt", taken.Contains).Should().Be(@"D:\in\a (3).txt");
        DeviceTransferRules.UniquePath(@"D:\in", "B", taken.Contains).Should().Be(@"D:\in\B (2)");
        DeviceTransferRules.UniquePath(@"D:\in", @"..\..\evil.txt", taken.Contains).Should().Be(@"D:\in\evil.txt", "只留檔名");
        DeviceTransferRules.UniquePath(@"D:\in", "CON", taken.Contains).Should().Be(@"D:\in\_CON");
        DeviceTransferRules.UniquePath(@"D:\in", "x" + DeviceTransferRules.PartSuffix, taken.Contains).Should().EndWith(DeviceTransferRules.PartSuffix + "_");
        DeviceTransferRules.UniquePath(@"D:\in", "a.txt", _ => true).Should().BeNull();
    }

    [Fact]
    public void 磁碟名稱與根目錄()
    {
        DeviceTransferRules.DriveLabel(@"c:\", null).Should().Be("本機磁碟 (C:)");
        DeviceTransferRules.DriveLabel(@"D:\", " 資料 ").Should().Be("資料 (D:)");
        DeviceTransferRules.IsDriveRoot(@"C:\").Should().BeTrue();
        DeviceTransferRules.IsDriveRoot(@"C:\a").Should().BeFalse();
        DeviceTransferRules.IsDriveRoot(@"\\s\x").Should().BeFalse();
    }

    [Fact]
    public void 傳輸另外禁止金鑰憑證與發佈金鑰的資料夾()
    {
        var roots = new MediaFolderRoots(@"C:\Users\u", @"C:\Users\u\AppData\Local", @"C:\Users\u\AppData\Roaming", @"C:\data", [@"C:\acc"]);
        var extra = DeviceTransferRules.ExtraForbiddenFolders(roots);
        extra.Should().Contain([@"C:\Users\u\.aws", @"C:\Users\u\.gnupg", @"C:\Users\u\AppData\Local\MyriagentRelease", @"C:\Users\u\AppData\Roaming\Microsoft\Protect"]);
        var merged = DeviceTransferRules.WithExtraForbidden(roots);
        merged.AccountConfigDirectories.Should().StartWith(@"C:\acc");
        merged.AccountConfigDirectories.Should().Contain(@"C:\Users\u\.aws");
        var context = new RemoteFolderContext(roots.UserProfile, roots.LocalAppData, roots.RoamingAppData, roots.AppDataRoot, merged.AccountConfigDirectories);
        RemoteFolderRules.IsForbidden(@"C:\Users\u\.aws\credentials", RemoteDriveKind.Fixed, context).Should().BeTrue();
        RemoteFolderRules.IsForbidden(@"C:\Users\u\Documents\a.txt", RemoteDriveKind.Fixed, context).Should().BeFalse();
    }

    [Fact]
    public void 接收表_進度量暫存檔_結束的留十分鐘_超過上限丟最早結束的()
    {
        long now = 0;
        var sizes = new Dictionary<string, long> { [@"D:\~a"] = 40 };
        var board = new DeviceXferBoard(() => now, p => sizes.TryGetValue(p, out var n) ? n : null);
        board.Snapshot().Should().BeNull();
        board.Queue("u1", "a.bin", 100, @"D:\~a");
        board.IsActive("u1").Should().BeTrue();
        board.Snapshot()!.Single().Should().Be(new XferStats("u1", "a.bin", 100, 0, "receiving"));
        board.Start("u1");
        board.Snapshot()!.Single().Done.Should().Be(40);
        sizes[@"D:\~a"] = 999;
        board.Snapshot()!.Single().Done.Should().Be(100, "夾在大小以內");
        board.Saved("u1", "a (2).bin");
        board.IsActive("u1").Should().BeFalse();
        board.Snapshot()!.Single().Should().Be(new XferStats("u1", "a.bin", 100, 100, "saved", "a (2).bin"));
        now += (long)DeviceXferBoard.KeepFinished.TotalMilliseconds + 1;
        board.Snapshot().Should().BeNull();

        for (var i = 0; i < DeviceStatsRules.MaxXfers + 5; i++)
            board.Queue("q" + i, "f", 1, "p");
        board.Snapshot()!.Count.Should().Be(DeviceStatsRules.MaxXfers);
        board.Snapshot()!.Should().AllSatisfy(x => DeviceStatsRules.CheckXfer(x).Should().BeNull());
    }

    [Fact]
    public void 接收狀態的格式檢查()
    {
        DeviceStatsRules.CheckXfer(new XferStats("u1", "a", 10, 5, "receiving")).Should().BeNull();
        DeviceStatsRules.CheckXfer(new XferStats("u1", "a", 10, 10, "saved", "a (2)")).Should().BeNull();
        DeviceStatsRules.CheckXfer(new XferStats("bad id!", "a", 10, 5, "receiving")).Should().NotBeNull();
        DeviceStatsRules.CheckXfer(new XferStats("u1", "", 10, 5, "receiving")).Should().NotBeNull();
        DeviceStatsRules.CheckXfer(new XferStats("u1", "a", 10, 11, "receiving")).Should().NotBeNull();
        DeviceStatsRules.CheckXfer(new XferStats("u1", "a", 10, 5, "weird")).Should().NotBeNull();
        DeviceStatsRules.CheckXfer(new XferStats("u1", "a", 10, 5, "receiving", "x")).Should().NotBeNull("只有 saved 帶存檔名稱");
    }

    [Fact]
    public void 媒體轉送的xfer只給可以操作_對上xfer的參照()
    {
        var control = new DevicePolicy(SyncSettings.LevelControl, false);
        var view = new DevicePolicy(SyncSettings.LevelView, false);
        MediaServerRules.Decide(true, true, control, MediaPurposes.Transfer).Should().BeNull();
        MediaServerRules.Decide(true, true, view, MediaPurposes.Transfer)!.Value.Status.Should().Be(403);
        MediaServerRules.PurposeMatches(MediaPurposes.Transfer, MediaServerRules.TransferItemId).Should().BeTrue();
        MediaServerRules.PurposeMatches(MediaPurposes.Media, MediaServerRules.TransferItemId).Should().BeFalse("xfer 的參照不能當成圖片要");
        MediaServerRules.PurposeMatches(MediaPurposes.File, MediaServerRules.TransferItemId).Should().BeFalse();
        LooseMediaRefRules.IsConversationFor(MediaServerRules.TransferItemId, string.Empty).Should().BeTrue();
        LooseMediaRefRules.IsConversationFor(MediaServerRules.TransferItemId, "0123456789abcdef0123456789abcdef").Should().BeFalse();
    }
}
