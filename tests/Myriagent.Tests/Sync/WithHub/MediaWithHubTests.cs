using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Media;
using Myriagent.Remote;
using Myriagent.Sync;
using Myriagent.Tests.Sync.LongRun;
using Myriagent.Tests.TestSupport;
using FakeDevice;
using Newtonsoft.Json.Linq;
using Xunit.Abstractions;
using Xunit.Sdk;
using LongRunGate = Myriagent.Tests.Sync.LongRun.LongRun;

namespace Myriagent.Tests.Sync.WithHub;

/// <summary>
/// WP4.7 計畫步 13 的端到端：瀏覽器 → 同一棵樹的 MyriagentHub 子程序（Testing 環境、暫存資料根、高埠）→ 整條桌面
/// （tools/fake-device 的 DeviceRuntime：中樞、發布者、引擎，劇本化的會話）。桌面呼叫伺服器 HTTP 端點的傳輸換成 <see cref="DeviceHttpRecorder"/>
/// （<see cref="SyncEngine.MediaHttpHandlerForTest"/>：照常連到伺服器，另外記下每個請求的方法、路徑與本文長度）。
/// <list type="bullet">
/// <item>瀏覽器 <c>GET /m/</c> → H1（<c>MediaRequest</c>）→ 桌面的 MediaServer → E7：圖片整份、100 MB 的影片隨機 50 個範圍（固定種子），
/// 位元組的 SHA-256 等於本機，每次轉送的本文 ≤8 MB。</item>
/// <item>E4 上傳 → 遠端 <c>addUploaded</c> → E6 下載 → 遠端 <c>send</c>：使用者項目的附件 SHA-256 等於上傳的。</item>
/// <item>新桌面配舊伺服器（<c>MYRIAGENT_SYNC_PREV_HUB</c>：WP4.7 合併前的組建）：協商不到 media1，桌面的 E5、E8（任何 <c>/api/v1/device/*</c>）0 次；
/// 大項目照 WP4.3 截斷；附件的參照照樣配發（<c>media_refs_local</c> 有列）。</item>
/// </list>
/// 前兩例要 <c>MYRIAGENT_SYNC_IT=1</c>（同 WP4.4 的遠端指令情境，各要十幾秒到半分鐘）；第三例沒設變數就略過
/// </summary>
[Collection(MyriagentHubCollection.Name)]
public sealed class MediaWithHubTests(ITestOutputHelper output)
{
    /// <summary>WP4.7 合併前的任何組建的 MyriagentHub.exe（同 MyriagentHub.Tests 的 CommandMixedVersionTests.PrevHubVariable；建法見 src/MyriagentHub/README.md）</summary>
    public const string PrevHubVariable = "MYRIAGENT_SYNC_PREV_HUB";

    private const string PrevHubName = "prev";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    private static readonly FakeAccountSpec Account = LongRunDevice.Account("media", AgentProvider.Claude, "media@example.test", "org-media");

    // ───────────────────────────── /m/ 的轉送 ─────────────────────────────

    [EnvFact(LongRunGate.Gate, "媒體的端到端：瀏覽器 → 同一棵樹的 MyriagentHub → 桌面的 MediaServer → E7")]
    public Task 瀏覽器GET_m_經伺服器轉送到桌面_圖片整份與100MB影片的50個隨機範圍_SHA256等於本機_每次轉送不超過8MB() =>
        RunAsync(alternate: null, RelayScenarioAsync);

    private async Task RelayScenarioAsync(MyriagentHubProcess hub, OwnerClient owner, DeviceHttpRecorder recorder)
    {
        using var files = new TempDirectory("media-e2e");
        var image = NoisePng(1000, 1000, seed: 4701);
        var imagePath = Path.Combine(files.Root, "e2e-圖片.png");
        await File.WriteAllBytesAsync(imagePath, image);
        var video = new byte[100 * 1024 * 1024];
        new Random(4702).NextBytes(video);
        var videoPath = Path.Combine(files.Root, "e2e-影片.mp4");
        await File.WriteAllBytesAsync(videoPath, video);
        output.WriteLine($"圖片 {image.Length:N0} 位元組、影片 {video.Length:N0} 位元組");

        await using var device = await StartDeviceAsync(hub, owner, "media-relay", recorder);
        await ControlAsync(device);
        var chat = await device.Runtime.ChatAsync(new ChatSpec("媒體轉送", Account.Alias, "附兩個檔", [imagePath, videoPath], null, Wait));
        chat.Outcome.Should().Be(AgentTurnOutcome.Completed);
        var wire = ConversationIds.ToWire(chat.ConversationId)!;
        (await device.Runtime.WaitDrainedAsync(Wait)).Drained.Should().BeTrue("對話事件送完");
        var stored = await StoredAttachmentsAsync(device, chat.ConversationId);
        stored.Should().HaveCount(2);

        await using var browser = await BrowserHubClient.ConnectAsync(hub.BaseAddress, owner.Cookies, hub.PublicOrigin);
        var tiles = await OpenAttachmentsAsync(browser, wire);
        var imageTile = tiles[stored.Single(a => a.Kind == AgentAttachmentKind.Image).Id];
        var videoTile = tiles[stored.Single(a => a.Kind == AgentAttachmentKind.Video).Id];
        output.WriteLine($"圖片 {imageTile}；影片 {videoTile}");
        var imageUrl = imageTile.Value<string>("url")!;
        var videoUrl = videoTile.Value<string>("url")!;
        imageUrl.Should().StartWith("/m/" + device.DeviceId + "-");
        videoUrl.Should().StartWith("/m/" + device.DeviceId + "-");

        // 圖片：沒帶 Range，伺服器以一段（≤8 MB）接成 200
        var relaysBefore = recorder.Relays().Count;
        var whole = await GetUntilFoundAsync(owner, imageUrl, range: null);
        whole.Status.Should().Be(HttpStatusCode.OK);
        Sha(whole.Body).Should().Be(Sha(image), "圖片經伺服器轉送的位元組等於本機的檔");
        recorder.Relays().Count.Should().BeGreaterThan(relaysBefore, "位元組由桌面以 E7 回");

        // 縮圖：320 px（E8 先傳上去的 thumb_blob，或轉送一次由桌面產生）
        var thumb = await GetUntilFoundAsync(owner, imageTile.Value<string>("thumbUrl")!, range: null);
        thumb.Status.Should().Be(HttpStatusCode.OK);
        thumb.Body.AsSpan(0, 8).ToArray().Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);

        // 影片：50 個隨機範圍（固定種子；開放式由伺服器縮成 8 MB、後綴先問總長）
        var total = (long)video.Length;
        var random = new Random(4703);
        relaysBefore = recorder.Relays().Count;
        for (var i = 0; i < 50; i++)
        {
            var (header, start, end) = NextRange(random, i, total);
            var part = await GetUntilFoundAsync(owner, videoUrl, header);
            part.Status.Should().Be(HttpStatusCode.PartialContent, header);
            part.ContentRange.Should().Be($"bytes {start}-{end}/{total}", header);
            part.Body.LongLength.Should().Be(end - start + 1, header);
            Sha(part.Body).Should().Be(Sha(video.AsSpan((int)start, (int)(end - start + 1))), $"{header} 的位元組等於本機的那一段");
        }

        var relays = recorder.Relays().Skip(relaysBefore).ToList();
        output.WriteLine($"影片的 50 個範圍：E7 {relays.Count} 次，最大本文 {relays.Max(r => r.Length ?? 0):N0} 位元組");
        relays.Count.Should().BeGreaterThanOrEqualTo(50, "每個範圍至少一次轉送（後綴範圍另有一次問總長）");
        relays.Should().OnlyContain(r => r.Length != null && r.Length <= MediaRelayRules.MaxSegmentBytes, "每次轉送的本文 ≤8 MB");
        await device.StopAsync();
    }

    /// <summary>第 i 個範圍：每 5 個裡一個開放式、一個後綴、其他是 ≤8 MB 的封閉區間（可能超過結尾，回應縮到檔尾）。回傳（Range 標頭, 預期的起點, 預期的終點）</summary>
    private static (string Header, long Start, long End) NextRange(Random random, int i, long total)
    {
        var max = MediaRelayRules.MaxSegmentBytes;
        switch (i % 5)
        {
            case 0:
            {
                var start = random.NextInt64(0, total);
                return ($"bytes={start}-", start, Math.Min(total - 1, start + max - 1));
            }
            case 1:
            {
                var suffix = random.NextInt64(1, max + 1);
                return ($"bytes=-{suffix}", Math.Max(0, total - suffix), total - 1);
            }
            default:
            {
                var start = random.NextInt64(0, total);
                var end = start + random.NextInt64(1, max + 1) - 1;
                return ($"bytes={start}-{end}", start, Math.Min(end, total - 1));
            }
        }
    }

    // ───────────────────────────── E4 → addUploaded → E6 ─────────────────────────────

    [EnvFact(LongRunGate.Gate, "上傳的端到端：E4 → 遠端 addUploaded → 桌面以 E6 下載 → 遠端 send")]
    public Task 網頁上傳_遠端addUploaded_桌面以E6下載_遠端send的附件SHA256等於上傳的() =>
        RunAsync(alternate: null, UploadScenarioAsync);

    private async Task UploadScenarioAsync(MyriagentHubProcess hub, OwnerClient owner, DeviceHttpRecorder recorder)
    {
        await using var device = await StartDeviceAsync(hub, owner, "media-upload", recorder);
        await ControlAsync(device);
        var chat = await device.Runtime.ChatAsync(new ChatSpec("網頁上傳", Account.Alias, "本機的第一則", [], null, Wait));
        chat.Outcome.Should().Be(AgentTurnOutcome.Completed);
        var wire = ConversationIds.ToWire(chat.ConversationId)!;
        (await device.Runtime.WaitDrainedAsync(Wait)).Drained.Should().BeTrue();

        // E4：擁有者 cookie＋Origin＋CSRF，201 {uploadId, sha256, size}
        var content = new byte[3 * 1024 * 1024 + 17];
        new Random(4704).NextBytes(content);
        const string uploadId = "e2e-upload-1";
        JObject receipt;
        using (var put = await owner.Http.PutAsync("/api/v1/uploads/" + uploadId, new ByteArrayContent(content)))
        {
            var text = await put.Content.ReadAsStringAsync();
            put.StatusCode.Should().Be(HttpStatusCode.Created, text);
            receipt = JObject.Parse(text);
        }

        receipt.Value<string>("uploadId").Should().Be(uploadId);
        receipt.Value<string>("sha256").Should().Be(Sha(content));
        receipt.Value<long>("size").Should().Be(content.LongLength);

        await using var browser = await BrowserHubClient.ConnectAsync(hub.BaseAddress, owner.Cookies, hub.PublicOrigin);
        await ReadyWithConversationAsync(browser, wire);
        var added = await browser.RemoteAsync(new JObject
        {
            ["cmd"] = "addUploaded",
            ["conversationId"] = wire,
            ["uploads"] = new JArray(new JObject { ["uploadId"] = uploadId, ["name"] = "網頁上傳的檔.bin", ["size"] = content.LongLength, ["sha256"] = Sha(content) }),
        }, Wait);
        output.WriteLine("addUploaded：" + added.ToString(Newtonsoft.Json.Formatting.None));
        added.Value<string>("state").Should().Be(RemoteResultStates.Done);

        (await device.Runtime.WaitUntilAsync(() =>
        {
            var (importing, names) = device.Runtime.Hub.ComposerStateForTest(chat.ConversationId);
            return importing == 0 && names.Count == 1;
        }, Wait)).Should().BeTrue("下載、匯入完成，輸入區有一個附件");
        recorder.Requests.Should().Contain(r => r.Method == "GET" && r.Path == "/api/v1/device/blobs/" + Sha(content), "桌面以 E6 下載上傳的檔");

        var marker = "遠端送的 " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        var sent = await browser.RemoteAsync(new JObject { ["cmd"] = "send", ["conversationId"] = wire, ["text"] = marker }, Wait);
        output.WriteLine("send：" + sent.ToString(Newtonsoft.Json.Formatting.None));
        sent.Value<string>("state").Should().Be(RemoteResultStates.Done);
        (await device.Runtime.WaitUntilAsync(() => device.Runtime.Hub.GetItems(chat.ConversationId).Any(i => i.Kind == AgentItemKind.User && i.Text == marker), Wait))
            .Should().BeTrue("遠端的訊息成為使用者項目");
        var item = await device.Runtime.OnUi(() => device.Runtime.Hub.GetItems(chat.ConversationId).Single(i => i.Kind == AgentItemKind.User && i.Text == marker));
        var attachment = item.Attachments.Should().ContainSingle("網頁上傳的附件和這則一起送出").Subject;
        attachment.FileName.Should().Be("網頁上傳的檔.bin");
        Sha(await File.ReadAllBytesAsync(attachment.StoredPath)).Should().Be(receipt.Value<string>("sha256"), "附件的位元組等於上傳的");
        await device.StopAsync();
    }

    // ───────────────────────────── 新桌面配舊伺服器 ─────────────────────────────

    [EnvFileFact(PrevHubVariable, "WP4.7 合併前的組建（例如 git archive 390145d）的 MyriagentHub.exe 完整路徑，建法見 src/MyriagentHub/README.md")]
    public Task 新桌面配舊伺服器_協商不到media1_E5與E8呼叫0次_大項目照WP4點3截斷_參照照樣配發() =>
        RunAsync(alternate: (PrevHubName, Environment.GetEnvironmentVariable(PrevHubVariable)!), OldServerScenarioAsync);

    private async Task OldServerScenarioAsync(MyriagentHubProcess hub, OwnerClient owner, DeviceHttpRecorder recorder)
    {
        output.WriteLine($"舊伺服器：{Environment.GetEnvironmentVariable(PrevHubVariable)}（複製成 {hub.ExePath}）");
        using var files = new TempDirectory("media-e2e");
        var imagePath = Path.Combine(files.Root, "舊伺服器.png");
        await File.WriteAllBytesAsync(imagePath, NoisePng(300, 200, seed: 4705));
        await using var device = await StartDeviceAsync(hub, owner, "media-old-hub", recorder, media1: false);

        // 40 KB 的回覆：WP4.3 的欄位上限 30 KB（沒有 media1 就沒有 blob 可用，只能截斷）
        var reply = string.Concat(Enumerable.Repeat("lorem ipsum dolor sit amet. ", 40 * 1024 / 28 + 1));
        var chat = await device.Runtime.ChatAsync(new ChatSpec("舊伺服器", Account.Alias, "附一張圖", [imagePath], ScriptedTurn.Reply(reply), Wait));
        chat.Outcome.Should().Be(AgentTurnOutcome.Completed);
        var wire = ConversationIds.ToWire(chat.ConversationId)!;
        (await device.Runtime.WaitDrainedAsync(Wait)).Drained.Should().BeTrue("對話事件送完（舊伺服器照收）");
        await Task.Delay(TimeSpan.FromSeconds(2));

        var items = await device.Runtime.OnUi(() => device.Runtime.Hub.GetItems(chat.ConversationId).ToList());
        var assistant = items.Last(i => i.Kind == AgentItemKind.Assistant);
        assistant.Text.Should().Be(reply, "本機留完整內容");
        var user = items.Single(i => i.Kind == AgentItemKind.User);
        var (truncated, refs) = await device.Inspect(v => (v.Store!.LoadTruncatedItems(), v.Store!.LoadMediaRefs(wire)));
        truncated.Should().Contain((wire, assistant.Id), "截斷的項目記進 published_items.truncated（之後協商出 media1 再重送）");
        refs.Should().ContainSingle(r => r.ItemId == user.Id && r.AttachmentId == user.Attachments.Single().Id, "附件的參照照樣配發");

        // 舊伺服器收下的項目（執行中唯讀開它的 sync.db）：回覆照 WP4.3 截斷、不是 {blob}；使用者項目帶著參照（伺服器之後升級就能索引）
        var stored = ServerItems(hub.SyncDbPath, device.DeviceId, wire);
        var line = stored.Last(p => p.Value<string>("id") == assistant.Id);
        line["text"]!.Type.Should().Be(JTokenType.String, "沒有 media1：不改成 {blob}");
        line.Value<string>("text")!.Should().Contain("字未同步", "照 WP4.3 截斷");
        Encoding.UTF8.GetByteCount(line.Value<string>("text")!).Should().BeLessThanOrEqualTo(ConversationEventRules.MaxInlineStringBytes);
        stored.Last(p => p.Value<string>("id") == user.Id)["attachments"]!.ToString(Newtonsoft.Json.Formatting.None).Should().Contain(refs.Single().Ref);

        recorder.Requests.Should().BeEmpty("協商不到 media1：沒有 cha_、不呼叫任何 /api/v1/device/*（E5、E8、E10 都是 0 次）");
        await device.StopAsync();
    }

    // ───────────────────────────── 共用 ─────────────────────────────

    /// <summary>啟動伺服器（<paramref name="alternate"/>：另一個組建）、擁有者登入、跑情境；結束時伺服器要正常停止、不留孤兒金鑰檔</summary>
    private async Task RunAsync((string Name, string SourceExe)? alternate, Func<MyriagentHubProcess, OwnerClient, DeviceHttpRecorder, Task> scenario)
    {
        var needle = alternate is { } other ? MyriagentHubExe.KeyNeedle(MyriagentHubExe.AlternateDirectoryName(other.Name)) : MyriagentHubExe.KeyNeedle(MyriagentHubExe.CopyDirectoryName);
        var keysBefore = UserKeyStore.FilesNaming(needle);
        var recorder = new DeviceHttpRecorder();
        try
        {
            var hub = await MyriagentHubProcess.StartAsync(alternate: alternate, publicOriginIsBaseAddress: true);
            try
            {
                using var owner = await OwnerClient.LoginAsync(hub);
                await scenario(hub, owner, recorder);
            }
            finally
            {
                await hub.DisposeAsync();
            }

            hub.ForcedKill.Should().BeFalse("MyriagentHub 要能正常停止（強制結束會留下孤兒金鑰檔）");
            UserKeyStore.FilesNaming(needle).Except(keysBefore).Should().BeEmpty("正常停止的 MyriagentHub 不留下金鑰檔（D5）");
        }
        finally
        {
            recorder.Release();
        }
    }

    /// <summary>配對、以記錄 HTTP 的引擎啟動，等連上（<paramref name="media1"/>：協商出的功能有沒有 media1）</summary>
    private static async Task<LongRunDevice> StartDeviceAsync(MyriagentHubProcess hub, OwnerClient owner, string name, DeviceHttpRecorder recorder, bool media1 = true)
    {
        var (dir, paired) = await LongRunDevice.PairAsync(hub, owner, name);
        var options = HubEngine.FastBackoff(0.2, 0.5, 1);
        var device = await LongRunDevice.StartPairedAsync(dir, paired, name, [Account], options, args =>
        {
            var engine = (SyncEngine)SyncEngineLoader.Create(args with { SignerFactory = paired.SignerFactory, Options = options });
            engine.MediaHttpHandlerForTest = recorder;
            return engine;
        });
        try
        {
            await device.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.Features.Contains(DeviceFeatureRules.Conv1)
                                             && v.Features.Contains(DeviceFeatureRules.Media1) == media1, Wait,
                media1 ? "連上、協商出 media1" : "連上、協商出 conv1 但沒有 media1");
        }
        catch
        {
            await device.DisposeAsync();
            throw;
        }

        return device;
    }

    /// <summary>
    /// 遠端政策改成 Control：預設 Off 時網頁只看得到縮圖（Q1-A）、遠端指令被拒；引擎以新的政策重新 Hello
    /// </summary>
    private static async Task ControlAsync(LongRunDevice device)
    {
        var generation = await device.Inspect(v => v.ConnectionGeneration);
        await device.Runtime.CommandAsync("syncSetPolicy", new JObject { ["remoteControlLevel"] = "Control" });
        await device.WaitUntilAsync(v => v.State == SyncEngineState.Live && v.ConnectionGeneration > generation && v.Features.Contains(DeviceFeatureRules.Media1), Wait,
            "以新的政策重新連上");
    }

    /// <summary>使用者項目的附件（本機存放的複本）</summary>
    private static Task<List<AgentAttachment>> StoredAttachmentsAsync(LongRunDevice device, string conversationId) =>
        device.Runtime.OnUi(() => device.Runtime.Hub.GetItems(conversationId).Where(i => i.Kind == AgentItemKind.User).SelectMany(i => i.Attachments).ToList());

    /// <summary>ready、等對話清單有這個對話而且擁有者在線</summary>
    private static async Task ReadyWithConversationAsync(BrowserHubClient browser, string wire)
    {
        var init = await browser.ReadyAsync(Wait);
        if (!Lists(init, wire))
            await browser.WaitForAsync(m => m.Value<string>("type") == "conversations" && Lists(m, wire), Wait, "對話清單有這個對話、擁有者在線");
    }

    /// <summary>open 這個對話，回傳使用者項目的附件（id → 渲染出的附件；等到每個附件都有網址）</summary>
    private static async Task<Dictionary<string, JObject>> OpenAttachmentsAsync(BrowserHubClient browser, string wire)
    {
        await ReadyWithConversationAsync(browser, wire);
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            var accepted = await browser.CmdAsync(new JObject { ["cmd"] = "open", ["conversationId"] = wire });
            accepted.Value<bool?>("accepted").Should().BeTrue(accepted.ToString(Newtonsoft.Json.Formatting.None));
            var opened = await browser.WaitForAsync(m => m.Value<string>("type") == "conversation" && m.Value<string>("conversationId") == wire, Wait, "conversation");
            var tiles = ((JArray)opened["items"]!).OfType<JObject>()
                .SelectMany(i => (i["attachments"] as JArray)?.OfType<JObject>() ?? [])
                .ToDictionary(a => a.Value<string>("id")!, StringComparer.Ordinal);
            if (tiles.Count > 0 && tiles.Values.All(a => a.Value<string?>("url") is not null))
                return tiles;
            if (DateTime.UtcNow > deadline)
                throw new XunitException("附件沒有網址：" + opened.ToString(Newtonsoft.Json.Formatting.None));
            await Task.Delay(500);
        }
    }

    private static bool Lists(JObject message, string conversationId) =>
        message["conversations"] is JArray list
            ? list.OfType<JObject>().Any(c => c.Value<string>("id") == conversationId && c.Value<bool?>("deviceOnline") == true)
            : message["items"] is JArray items && items.OfType<JObject>().Any(c => c.Value<string>("id") == conversationId && c.Value<bool?>("deviceOnline") == true);

    private sealed record Fetched(HttpStatusCode Status, string ContentRange, byte[] Body);

    /// <summary>GET（擁有者 cookie）；伺服器的索引器還沒追上時是 404，最多重試到 <see cref="Wait"/></summary>
    private static async Task<Fetched> GetUntilFoundAsync(OwnerClient owner, string path, string? range)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (range is not null)
                request.Headers.TryAddWithoutValidation("Range", range).Should().BeTrue();
            using var response = await owner.Http.SendAsync(request);
            var body = await response.Content.ReadAsByteArrayAsync();
            if (response.StatusCode != HttpStatusCode.NotFound || DateTime.UtcNow > deadline)
                return new Fetched(response.StatusCode, response.Content.Headers.ContentRange?.ToString() ?? string.Empty, body);
            await Task.Delay(300);
        }
    }

    /// <summary>伺服器收下的這個對話的 conv.item（依 seq；唯讀、連線不用池）</summary>
    private static List<JObject> ServerItems(string syncDb, string deviceId, string conversationId)
    {
        using var connection = HubDb.OpenReadOnly(syncDb);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT enc, payload FROM events WHERE author = $a AND conversation_id = $c AND kind = $k ORDER BY seq";
        cmd.Parameters.AddWithValue("$a", deviceId);
        cmd.Parameters.AddWithValue("$c", conversationId);
        cmd.Parameters.AddWithValue("$k", ConversationEventKinds.Item);
        using var reader = cmd.ExecuteReader();
        var items = new List<JObject>();
        while (reader.Read())
        {
            var enc = reader.IsDBNull(0) ? null : reader.GetString(0);
            var payload = reader.IsDBNull(1) ? null : reader.GetString(1);
            items.Add(PayloadRules.TryDecode(enc, payload, 16 << 20, out var json) ? JObject.Parse(json) : throw new XunitException($"伺服器存的 payload 解不開（enc {enc}）"));
        }

        return items;
    }

    private static string Sha(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>固定種子的雜訊 PNG（壓不下來：1000×1000 約 3 MB；真的 PNG，縮圖做得出來）</summary>
    private static byte[] NoisePng(int width, int height, int seed)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var pixels = new byte[data.Stride * height];
            new Random(seed).NextBytes(pixels);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}

/// <summary>
/// 桌面呼叫伺服器 HTTP 端點的傳輸（<see cref="SyncEngine.MediaHttpHandlerForTest"/>）：照常連到伺服器（自簽憑證不驗），另外記下每個請求的
/// 方法、路徑與本文長度（<c>Content-Length</c>，串流本文由 TryComputeLength 算）。引擎的每個 DeviceHttpClient 共用這一個實例、各自 Dispose 時不關，
/// 測試結束時才關內層
/// </summary>
internal sealed class DeviceHttpRecorder : DelegatingHandler
{
    public DeviceHttpRecorder()
        : base(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        })
    {
    }

    public ConcurrentQueue<(string Method, string Path, long? Length)> Requests { get; } = new();

    /// <summary>E7（轉送的回覆）</summary>
    public List<(string Method, string Path, long? Length)> Relays() => Requests.Where(r => r.Method == "PUT" && r.Path.StartsWith("/api/v1/device/relay/", StringComparison.Ordinal)).ToList();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue((request.Method.Method, request.RequestUri?.AbsolutePath ?? string.Empty, request.Content?.Headers.ContentLength));
        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>引擎的 HTTP 用戶端 Dispose 時不關（共用）；測試以 <see cref="Release"/> 關</summary>
    protected override void Dispose(bool disposing)
    {
    }

    public void Release() => InnerHandler?.Dispose();
}
