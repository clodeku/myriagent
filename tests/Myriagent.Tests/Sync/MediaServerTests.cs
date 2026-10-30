using System.Collections.Concurrent;
using System.Text;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Hub;
using Myriagent.Media;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Newtonsoft.Json;

namespace Myriagent.Tests.Sync;

/// <summary>
/// 桌面回覆媒體轉送（WP4.7 計畫步 8，D26、D28、D36）：真的守門員與暫存檔、假的 E7（<see cref="FakeRelayHandler"/>）、假的中樞快照。
/// 每個轉送都斷言伺服器收得下的形狀：拒絕不帶本文、206 的 Content-Range 與本文一致
/// </summary>
public sealed class MediaServerTests : IDisposable
{
    private static readonly DevicePolicy View = new("View", false);

    private static readonly DevicePolicy Off = new("Off", false);

    private const string DeviceId = "d_01J9Q0000000000000000000ME";

    private readonly MediaTestFiles files = new();
    private readonly FakeRelayHandler relay = new();
    private readonly AccessTokenHolder tokens = new();
    private readonly ConcurrentQueue<MediaScopeQuery> queries = new();
    private readonly RemoteFileGuard guard = MediaTestFiles.Guard();
    private Func<MediaScopeQuery, MediaScopeSnapshot?> answer = _ => null;
    private Func<string, int, CancellationToken, Task<byte[]?>> thumbnail = (_, _, _) => Task.FromResult<byte[]?>(MediaTestFiles.Png);
    private int refreshes;
    private MediaServer? server;

    public void Dispose()
    {
        server?.Dispose();
        files.Dispose();
    }

    private MediaServer Server()
    {
        if (server is not null)
            return server;
        tokens.Set("cha_unit", TimeProvider.System.GetTimestamp());
        var hub = new Uri("https://hub.example/");
        var http = new DeviceHttpClient(hub, hub, false, tokens, _ =>
        {
            Interlocked.Increment(ref refreshes);
            return Task.FromResult(false);
        }, TimeProvider.System, relay);
        server = new MediaServer(http, (query, _) =>
        {
            queries.Enqueue(query);
            return Task.FromResult(answer(query));
        }, guard, (path, size, ct) => thumbnail(path, size, ct));
        return server;
    }

    private static MediaJob Job(LocalMediaRefRow? row, string purpose, MediaRange? range = null, DevicePolicy? policy = null, int generation = 1, string? relayId = null)
        => new(generation, new MediaRequest(relayId ?? MediaTestFiles.NewId(), row?.Ref ?? MediaTestFiles.NewId(), purpose, range), row, policy ?? View, DeviceId);

    private static LocalMediaRefRow Attachment(string attachmentId = "att-1", string itemId = "item-1")
        => new(MediaTestFiles.NewId(), MediaTestFiles.ConversationId, itemId, attachmentId, null, 1791600000000);

    private static LocalMediaRefRow Loose(string itemId, string path) => new(MediaTestFiles.NewId(), MediaTestFiles.ConversationId, itemId, null, path, 1791600000000);

    private async Task<RelayPut> RunAsync(MediaJob job)
    {
        await Server().HandleAsync(job, CancellationToken.None);
        return relay.Puts.Should().ContainSingle(p => p.RelayId == job.Request.RelayId).Subject;
    }

    private string Photo(string name = "photo.png") => files.Write(Path.Combine("data", "conversations", MediaTestFiles.ConversationId, "attachments", name),
        Encoding.ASCII.GetBytes("0123456789"));

    private static void ShouldBeBodyless(RelayPut put)
    {
        put.Body.Should().BeEmpty();
        put.Header("Content-Length").Should().Be("0", "拒絕的回覆不帶本文");
        put.Header("Content-Range").Should().BeNull();
    }

    // ── 參照與對話 ──

    [Fact]
    public async Task 不認得的參照立刻回404_沒有本文_不問中樞()
    {
        var put = await RunAsync(Job(null, MediaPurposes.Media));

        put.Method.Should().Be(HttpMethod.Put);
        put.Path.Should().Be("/api/v1/device/relay/" + put.RelayId);
        put.Authorization.Should().Be("Bearer cha_unit");
        put.Status.Should().Be(404);
        put.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.NotFound);
        ShouldBeBodyless(put);
        queries.Should().BeEmpty("引擎查不到參照就不必問中樞");
        Server().Active.Should().Be(0);
    }

    [Fact]
    public async Task 用途和參照不符回404()
    {
        var put = await RunAsync(Job(Loose(MediaServerRules.FileItemId, Path.Combine(files.WorkingDirectory, "a.png")), MediaPurposes.Media));

        put.Status.Should().Be(404, "fileFetch 的參照不能從 /m/ 取");
        queries.Should().BeEmpty();
    }

    [Fact]
    public async Task 範圍2到5_本文是第2到5個位元組_Content_Range與ETag()
    {
        var path = Photo();
        answer = _ => files.Snapshot(resolved: (path, "螢幕截圖.png"));

        var put = await RunAsync(Job(Attachment(), MediaPurposes.Media, new MediaRange(2, 5)));

        put.Status.Should().Be(206);
        Encoding.ASCII.GetString(put.Body).Should().Be("2345");
        put.Header("Content-Range").Should().Be("bytes 2-5/10");
        put.Header("Content-Length").Should().Be("4");
        put.Header(MediaRelayHeaders.TotalLength).Should().Be("10");
        put.Header("Content-Type").Should().Be("image/png");
        put.Header("ETag").Should().Be(MediaServerRules.ETag(10, File.GetLastWriteTimeUtc(path)));
        put.Header(MediaRelayHeaders.FileName).Should().Be(MediaHeaderRules.EncodeFileName("螢幕截圖.png"));
        var query = queries.Should().ContainSingle().Subject;
        query.Should().Be(new MediaScopeQuery(MediaTestFiles.ConversationId, "item-1", "att-1", null, DeviceId));
    }

    [Fact]
    public async Task 起點不在檔案裡回416_帶總長_沒有本文()
    {
        var path = Photo();
        answer = _ => files.Snapshot(resolved: (path, "photo.png"));

        var put = await RunAsync(Job(Attachment(), MediaPurposes.Media, new MediaRange(10, 20)));

        put.Status.Should().Be(416);
        put.Header(MediaRelayHeaders.TotalLength).Should().Be("10");
        ShouldBeBodyless(put);
    }

    [Fact]
    public async Task 對話不可見回404_不碰檔案系統()
    {
        Photo();
        answer = _ => MediaScopeSnapshot.Hidden(MediaTestFiles.ConversationId);

        var put = await RunAsync(Job(Attachment(), MediaPurposes.Media, new MediaRange(0, 5)));

        put.Status.Should().Be(404);
        put.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.NotFound);
        ShouldBeBodyless(put);
        guard.FileSystemStages.Should().Be(0);
    }

    [Fact]
    public async Task 對話不存在回404()
    {
        answer = _ => null;

        var put = await RunAsync(Job(Attachment(), MediaPurposes.Thumb));

        put.Status.Should().Be(404);
        guard.FileSystemStages.Should().Be(0);
    }

    [Fact]
    public async Task 附件已經不在回404()
    {
        answer = _ => files.Snapshot(resolved: null);

        (await RunAsync(Job(Attachment(), MediaPurposes.Media, new MediaRange(0, 5)))).Status.Should().Be(404);
    }

    [Fact]
    public async Task 項目沒載入時唯讀讀items_json找附件()
    {
        var path = Photo();
        var items = new List<AgentChatItem>
        {
            new() { Id = "item-1", Text = "看這張圖", Attachments = [new AgentAttachment { Id = "att-1", FileName = "圖.png", StoredPath = path, Kind = AgentAttachmentKind.Image }] },
        };
        File.WriteAllText(Path.Combine(files.ConversationDirectory, "items.json"), JsonConvert.SerializeObject(items, JsonFile.Settings));
        answer = _ => files.Snapshot(itemsLoaded: false);

        var put = await RunAsync(Job(Attachment(), MediaPurposes.Media, new MediaRange(0, 2)));
        var gone = await RunAsync(Job(Attachment("att-9"), MediaPurposes.Media, new MediaRange(0, 2)));

        put.Status.Should().Be(206);
        Encoding.ASCII.GetString(put.Body).Should().Be("012");
        put.Header(MediaRelayHeaders.FileName).Should().Be(MediaHeaderRules.EncodeFileName("圖.png"));
        gone.Status.Should().Be(404, "items.json 裡沒有這個附件");
    }

    // ── 遠端政策（Q1-A） ──

    [Fact]
    public async Task 政策Off時media回403_帶型別不碰檔案_thumb回200()
    {
        var path = Photo();
        answer = _ => files.Snapshot(resolved: (path, "photo.png"));
        var thumbCalls = new ConcurrentQueue<(string Path, int Size)>();
        thumbnail = (p, size, _) =>
        {
            thumbCalls.Enqueue((p, size));
            return Task.FromResult<byte[]?>(MediaTestFiles.Png);
        };

        var media = await RunAsync(Job(Attachment(), MediaPurposes.Media, new MediaRange(0, 5), Off));
        guard.FileSystemStages.Should().Be(0, "政策擋下的不開檔");
        var thumb = await RunAsync(Job(Attachment(), MediaPurposes.Thumb, policy: Off));

        media.Status.Should().Be(403);
        media.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.Policy);
        media.Header("Content-Type").Should().Be("image/png", "伺服器據此把圖片改回縮圖（D29）");
        ShouldBeBodyless(media);
        thumb.Status.Should().Be(200);
        thumb.Body.Should().Equal(MediaTestFiles.Png);
        thumb.Header("Content-Type").Should().Be("image/png");
        thumb.Header(MediaRelayHeaders.TotalLength).Should().Be(MediaTestFiles.Png.Length.ToString());
        thumb.Header("Content-Range").Should().BeNull("縮圖是整份 200");
        var (thumbPath, thumbSize) = thumbCalls.Should().ContainSingle().Subject;
        thumbSize.Should().Be(320);
        Path.GetFileName(thumbPath).Should().Be("photo.png", "以守門員驗過的真實路徑產縮圖");
    }

    [Fact]
    public async Task 縮圖產不出來404_太大413()
    {
        var path = Photo();
        answer = _ => files.Snapshot(resolved: (path, "photo.png"));

        thumbnail = (_, _, _) => Task.FromResult<byte[]?>(null);
        var none = await RunAsync(Job(Attachment(), MediaPurposes.Thumb));
        thumbnail = (_, _, _) => Task.FromResult<byte[]?>(new byte[MediaServerRules.MaxThumbBytes + 1]);
        var big = await RunAsync(Job(Attachment(), MediaPurposes.Thumb));

        none.Status.Should().Be(404);
        ShouldBeBodyless(none);
        big.Status.Should().Be(413);
        big.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.TooLarge);
        ShouldBeBodyless(big);
    }

    // ── 檔案下載（D36） ──

    [Fact]
    public async Task file命中DLP回403dlp()
    {
        var token = RuntimeGitHubToken(seed: 23);
        var path = files.Write(Path.Combine("repo", "notes.txt"), Encoding.UTF8.GetBytes("部署用的權杖：" + token + "\n"));
        answer = _ => files.Snapshot();

        var put = await RunAsync(Job(Loose(MediaServerRules.FileItemId, path), MediaPurposes.File, new MediaRange(0, 8 * 1024 * 1024 - 1)));

        put.Status.Should().Be(403);
        put.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.Dlp);
        ShouldBeBodyless(put);
        CredentialLeakRules.HasLeak(token).Should().BeTrue("對照：這個字串確實會被當成權杖");
    }

    [Fact]
    public async Task file的UTF16權杖也命中DLP_沒有權杖的UTF16文字以下載給()
    {
        // 合併前審查 F1：Windows PowerShell 5.1 的「> token.txt」寫 UTF-16LE 加 BOM；只以 UTF-8 解碼時掃不到，又以 text/plain inline 預覽
        var token = RuntimeGitHubToken(29);
        var leaky = files.Write(Path.Combine("repo", "token.txt"), [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(token + "\r\n")]);
        var noExtension = files.Write(Path.Combine("repo", "secret"), [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("token=" + token)]);
        var clean = files.Write(Path.Combine("repo", "notes.txt"), [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("沒有秘密的內容")]);
        answer = _ => files.Snapshot();

        foreach (var path in new[] { leaky, noExtension })
        {
            var put = await RunAsync(Job(Loose(MediaServerRules.FileItemId, path), MediaPurposes.File, new MediaRange(0, 8 * 1024 * 1024 - 1)));
            put.Status.Should().Be(403, path);
            put.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.Dlp, path);
            ShouldBeBodyless(put);
        }

        var ok = await RunAsync(Job(Loose(MediaServerRules.FileItemId, clean), MediaPurposes.File, new MediaRange(0, 8 * 1024 * 1024 - 1)));
        ok.Status.Should().Be(206);
        ok.Header(MediaRelayHeaders.ContentKind).Should().Be(MediaContentKinds.Binary, "不是嚴格的 UTF-8：伺服器不以 charset=utf-8 inline 預覽，改成下載");
        ok.Body.Should().Equal(File.ReadAllBytes(clean));
    }

    [Fact]
    public async Task 中文檔名的X_File_Name_文字檔是text()
    {
        var path = files.Write(Path.Combine("repo", "報告.txt"), Encoding.UTF8.GetBytes("沒有秘密的內容"));
        answer = _ => files.Snapshot();

        var put = await RunAsync(Job(Loose(MediaServerRules.FileItemId, path), MediaPurposes.File, new MediaRange(0, 8 * 1024 * 1024 - 1)));

        put.Status.Should().Be(206);
        put.Header(MediaRelayHeaders.FileName).Should().Be("UTF-8''%E5%A0%B1%E5%91%8A.txt");
        put.Header(MediaRelayHeaders.ContentKind).Should().Be(MediaContentKinds.Text);
        Encoding.UTF8.GetString(put.Body).Should().Be("沒有秘密的內容");
        put.Header("Content-Range").Should().Be($"bytes 0-{put.Body.Length - 1}/{put.Body.Length}");
        put.Header("Content-Type").Should().BeNull("不是媒體就不宣告型別（伺服器依 X-Content-Kind 決定預覽或下載）");
    }

    [Fact]
    public async Task 二進位檔是binary_範圍照樣回()
    {
        var content = new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0xFF, 0x10, 0x20 };
        var path = files.Write(Path.Combine("repo", "build.zip"), content);
        answer = _ => files.Snapshot();

        var put = await RunAsync(Job(Loose(MediaServerRules.FileItemId, path), MediaPurposes.File, new MediaRange(4, 6)));

        put.Status.Should().Be(206);
        put.Header(MediaRelayHeaders.ContentKind).Should().Be(MediaContentKinds.Binary);
        put.Body.Should().Equal(0x00, 0xFF, 0x10);
        put.Header("Content-Range").Should().Be("bytes 4-6/8");
    }

    [Fact]
    public async Task 超過20MB的二進位檔_依範圍串流_可能是文字的超過20MB回413()
    {
        // 43 MB 的簡報這類大檔：開頭就證明是二進位，不必整份讀進記憶體跑 DLP
        var size = (int)MediaServerRules.MaxFileBytes + 1024;
        var binary = new byte[size];
        new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }.CopyTo(binary, 0);
        for (int i = 6; i < size; i++)
            binary[i] = (byte)(i * 31);
        var pptx = files.Write(Path.Combine("repo", "output", "簡報.pptx"), binary);
        var text = new byte[size];
        Array.Fill(text, (byte)'a');
        var log = files.Write(Path.Combine("repo", "output", "big.dat"), text);
        answer = _ => files.Snapshot();

        var start = MediaServerRules.MaxFileBytes;
        var tail = await RunAsync(Job(Loose(MediaServerRules.FileItemId, pptx), MediaPurposes.File, new MediaRange(start, start + 9)));
        var big = await RunAsync(Job(Loose(MediaServerRules.FileItemId, log), MediaPurposes.File, new MediaRange(0, 8 * 1024 * 1024 - 1)));

        tail.Status.Should().Be(206);
        tail.Header(MediaRelayHeaders.ContentKind).Should().Be(MediaContentKinds.Binary);
        tail.Header("Content-Range").Should().Be($"bytes {start}-{start + 9}/{size}");
        tail.Body.Should().Equal(binary.AsSpan((int)start, 10).ToArray());
        big.Status.Should().Be(413, "沒有證明是二進位的檔要整份掃權杖，上限照舊 20 MB");
        big.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.TooLarge);
        ShouldBeBodyless(big);
    }

    [Fact]
    public async Task file在範圍外回403_不存在回404_政策Off回403policy()
    {
        var outside = files.Write(Path.Combine("elsewhere", "a.txt"), Encoding.UTF8.GetBytes("x"));
        answer = _ => files.Snapshot();

        var scope = await RunAsync(Job(Loose(MediaServerRules.FileItemId, outside), MediaPurposes.File, new MediaRange(0, 0)));
        var missing = await RunAsync(Job(Loose(MediaServerRules.FileItemId, Path.Combine(files.WorkingDirectory, "沒有.txt")), MediaPurposes.File, new MediaRange(0, 0)));
        var policy = await RunAsync(Job(Loose(MediaServerRules.FileItemId, outside), MediaPurposes.File, new MediaRange(0, 0), Off));

        scope.Status.Should().Be(403);
        scope.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.Forbidden);
        missing.Status.Should().Be(404);
        policy.Status.Should().Be(403);
        policy.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.Policy);
        policy.Header("Content-Type").Should().BeNull("檔案不帶型別");
    }

    // ── 範圍與禁止清單 ──

    [Fact]
    public async Task 資料根目錄裡允許的子樹以外的圖回403()
    {
        var hidden = files.Write(Path.Combine("data", "accounts", "acc-1", "avatar.png"), MediaTestFiles.Png);
        answer = _ => files.Snapshot(resolved: (hidden, "avatar.png"));

        var put = await RunAsync(Job(new LocalMediaRefRow(MediaTestFiles.NewId(), MediaTestFiles.ConversationId, "item-1", null, hidden, 1), MediaPurposes.Media,
            new MediaRange(0, 3)));

        put.Status.Should().Be(403, "資料根目錄只放行這個對話的 workspace 與 attachments");
        put.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.Forbidden);
    }

    [Fact]
    public async Task 抽屜的檔不在目前的範圍回403_在範圍內回206()
    {
        var inRepo = files.Write(Path.Combine("repo", "shot.png"), MediaTestFiles.Png);
        var oldRepo = files.Write(Path.Combine("old-repo", "shot.png"), MediaTestFiles.Png);
        answer = _ => files.Snapshot();

        var ok = await RunAsync(Job(Loose(MediaServerRules.ListItemId, inRepo), MediaPurposes.Media, new MediaRange(0, 3)));
        var moved = await RunAsync(Job(Loose(MediaServerRules.ListItemId, oldRepo), MediaPurposes.Media, new MediaRange(0, 3)));

        ok.Status.Should().Be(206);
        ok.Body.Should().Equal(MediaTestFiles.Png.Take(4));
        ok.Header(MediaRelayHeaders.FileName).Should().Be(MediaHeaderRules.EncodeFileName("shot.png"));
        moved.Status.Should().Be(403);
        moved.Header(MediaRelayHeaders.Error).Should().Be(MediaErrorCodes.Forbidden);
    }

    // ── 名額與取消 ──

    [Fact]
    public async Task 第21個同時回503_不佔名額()
    {
        var path = Photo();
        answer = _ => files.Snapshot(resolved: (path, "photo.png"));
        relay.Hold = put => put.Status == 206;
        var jobs = Enumerable.Range(0, MediaRelayRules.MaxConcurrentPerDevice + 1).Select(_ => Job(Attachment(), MediaPurposes.Media, new MediaRange(0, 1))).ToList();

        var running = jobs.Select(job => Server().HandleAsync(job, CancellationToken.None)).ToList();
        await relay.WaitForHeldAsync(MediaRelayRules.MaxConcurrentPerDevice);
        // 第 21 個不等前面的放行就回 503（不限名額時它也被擋住：10 秒逾時，不讓測試卡住）
        await running[^1].WaitAsync(TimeSpan.FromSeconds(10));

        Server().Active.Should().Be(20);
        var busy = relay.Puts.Should().ContainSingle(p => p.Status == 503).Subject;
        busy.RelayId.Should().Be(jobs[^1].Request.RelayId);
        ShouldBeBodyless(busy);
        relay.Release.SetResult();
        await Task.WhenAll(running);
        Server().Active.Should().Be(0);
        relay.Puts.Count(p => p.Status == 206).Should().Be(20);
    }

    [Fact]
    public async Task 連線關閉取消那條連線的轉送_另一條連線上的不受影響()
    {
        var path = Photo();
        answer = _ => files.Snapshot(resolved: (path, "photo.png"));
        relay.Hold = _ => true;
        var first = Job(Attachment(), MediaPurposes.Media, new MediaRange(0, 1), generation: 1);
        var second = Job(Attachment(), MediaPurposes.Media, new MediaRange(0, 1), generation: 2);

        var a = Server().HandleAsync(first, CancellationToken.None);
        var b = Server().HandleAsync(second, CancellationToken.None);
        await relay.WaitForHeldAsync(2);
        Server().CancelConnection(1);
        await a;

        relay.Cancelled.Should().Equal(first.Request.RelayId);
        b.IsCompleted.Should().BeFalse();
        relay.Release.SetResult();
        await b;
        relay.Cancelled.Should().ContainSingle();
        Server().Active.Should().Be(0);
    }

    [Fact]
    public async Task 連線的取消權杖取消時轉送也停()
    {
        var path = Photo();
        answer = _ => files.Snapshot(resolved: (path, "photo.png"));
        relay.Hold = _ => true;
        using var connection = new CancellationTokenSource();
        var job = Job(Attachment(), MediaPurposes.Media, new MediaRange(0, 1));

        var task = Server().HandleAsync(job, connection.Token);
        await relay.WaitForHeldAsync(1);
        await connection.CancelAsync();
        await task;

        relay.Cancelled.Should().Equal(job.Request.RelayId);
        Server().Active.Should().Be(0);
    }

    [Fact]
    public async Task 轉送id不合格直接丟掉_不送也不佔名額()
    {
        await Server().HandleAsync(Job(null, MediaPurposes.Media, relayId: "../../admin"), CancellationToken.None);

        relay.Puts.Should().BeEmpty();
        Server().Active.Should().Be(0);
    }

    [Fact]
    public async Task 沒有權杖不送()
    {
        Server();
        tokens.Clear();

        await Server().HandleAsync(Job(null, MediaPurposes.Media), CancellationToken.None);

        relay.Puts.Should().BeEmpty();
        refreshes.Should().Be(1, "先請引擎換一次");
    }

    // ── Codex 產生的圖片（步 9，Q2-A） ──

    /// <summary>完整的 PNG（魔數與 IEND）：複製前的內容檢查認得</summary>
    private static readonly byte[] CompletePng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 7, 7, 7, 7,
        0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

    /// <summary>CODEX_HOME 在暫存資料夾裡、Codex 把圖片寫在它的 generated_images\（禁止清單裡）</summary>
    private (LocalMediaRefRow Row, string Source, string Copy) Generated()
    {
        var codexHome = files.Dir.CreateDirectory("codex-home");
        var source = files.Write(Path.Combine("codex-home", "generated_images", "s1", "img.png"), CompletePng);
        var row = Loose("item-7", source);
        var folders = files.Folders with { CodexHome = codexHome };
        answer = _ => files.Snapshot(resolved: (source, "img.png")) with { Folders = folders, MediaSequence = 1 };
        return (row, source, Path.Combine(files.AttachmentDirectory, GeneratedMediaRules.CopyFolder, "item-7-1.png"));
    }

    [Fact]
    public async Task Codex產生的圖片複製後服務複本_原檔刪了照樣看得到()
    {
        var (row, source, copy) = Generated();

        var first = await RunAsync(Job(row, MediaPurposes.Media));

        first.Status.Should().Be(200);
        first.Body.Should().Equal(CompletePng);
        first.Header("Content-Type").Should().Be("image/png");
        File.ReadAllBytes(copy).Should().Equal(CompletePng);
        Server().Resolver.Copier.Copied.Should().Be(1);

        File.Delete(source);
        var second = await RunAsync(Job(row, MediaPurposes.Media));
        second.Status.Should().Be(200, "只服務 attachments\\generated\\ 的複本");
        second.Body.Should().Equal(CompletePng);
        Server().Resolver.Copier.Copied.Should().Be(1, "複本已經在：不再複製");
    }

    [Fact]
    public async Task Codex產生的圖片_政策擋下時不複製()
    {
        var (row, _, copy) = Generated();

        var put = await RunAsync(Job(row, MediaPurposes.Media, policy: Off));

        put.Status.Should().Be(403);
        File.Exists(copy).Should().BeFalse("被擋的請求不碰檔案系統");
        Server().Resolver.Copier.Copied.Should().Be(0);
    }

    [Fact]
    public async Task Codex產生的圖片_原檔本身不能直接給()
    {
        var (row, source, _) = Generated();
        // 中樞的快照沒有序號（算不出複本的路徑）：不退回原檔
        answer = _ => files.Snapshot(resolved: (source, "img.png")) with { Folders = files.Folders with { CodexHome = files.Dir.Combine("codex-home") } };

        var put = await RunAsync(Job(row, MediaPurposes.Media));

        put.Status.Should().Be(404);
        ShouldBeBodyless(put);
    }

    /// <summary>執行時組出像 GitHub 權杖的字串（固定種子；repo 裡沒有這種字串）</summary>
    private static string RuntimeGitHubToken(int seed)
    {
        var random = new Random(seed);
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        return "gh" + "p_" + new string(Enumerable.Range(0, 36).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }
}
