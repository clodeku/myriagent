using System.Net;
using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Engine;

/// <summary>
/// 項目欄位 blob 分支與截斷重送（WP4.7 計畫步 10，D14、D15、D31、D32）：引擎＋盒子＋假 UI（<see cref="FakeConversationUi"/>）＋假伺服器，
/// E5 是 <see cref="FakeBlobEndpoint"/>（掛在 <see cref="FakeRelayHandler"/> 上）
/// </summary>
public sealed class ItemBlobTests
{
    private static readonly DateTime T0 = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Local);

    private static readonly string[] WithMedia = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1, DeviceFeatureRules.Media1];

    private static readonly string[] WithoutMedia = [DeviceFeatureRules.Conv1, DeviceFeatureRules.Live1];

    private const string BlobPrefix = "/api/v1/device/blobs/";

    private sealed class Rig : IAsyncDisposable
    {
        public Rig(bool media = true)
        {
            Box = new ConversationChangeBox();
            H = new EngineHarness(conversations: Box) { Policy = new DevicePolicy("View", false) };
            Ui = new FakeConversationUi(Box, () => H.Engine);
            Box.UiPort = Ui;
            Box.MediaScope = Scope;
            H.Server.Features = media ? WithMedia : WithoutMedia;
            _ = H.Server.Conversations;
            Blobs = new FakeBlobEndpoint(Relay);
            Relay.Responder = Respond;
        }

        public EngineHarness H { get; }

        public ConversationChangeBox Box { get; }

        public FakeConversationUi Ui { get; }

        public FakeMediaScope Scope { get; } = new();

        public FakeRelayHandler Relay { get; } = new();

        public FakeBlobEndpoint Blobs { get; }

        public Task StartAsync() => H.StartAsync(e => e.MediaHttpHandlerForTest = Relay);

        public async Task RestartAsync()
        {
            await H.Engine.StopAsync(TimeSpan.FromSeconds(5));
            await StartAsync();
        }

        public Task IdleAsync() => H.IdleAsync();

        public Task SendAsync(double seconds = 1) => H.AdvanceAsync(seconds);

        /// <summary>送幾輪（每輪撥 2 秒）：截斷版送出確認 → 重整走一次 → blob 版送出</summary>
        public async Task SettleAsync(int rounds = 5)
        {
            await IdleAsync();
            for (var i = 0; i < rounds; i++)
                await SendAsync(2);
        }

        /// <summary>新對話（中繼資料先送出確認：created 確認過才會整份走）</summary>
        public async Task<FakeConversation> NewAsync()
        {
            var c = Ui.Add(new FakeConversation(new AgentConversation
            {
                Id = Guid.NewGuid().ToString("N"),
                Title = "大項目",
                AccountId = EngineHarness.LocalAccount,
                Provider = AgentProvider.Claude,
                CreatedAt = T0,
                UpdatedAt = T0,
            }));
            Ui.Put(new MetaChanged(c.Id, c.Snapshot()));
            await IdleAsync();
            await SendAsync(2);
            return c;
        }

        public AgentChatItem Item(FakeConversation c, AgentChatItem item)
        {
            item.Ord ??= c.NextOrd++;
            c.Items.Add(item);
            Changed(c, item);
            return item;
        }

        public void Changed(FakeConversation c, AgentChatItem item) => Ui.Put(new ItemChanged(c.Id, ChatItemCopyRules.Copy(item)));

        public async Task<IReadOnlyList<JObject>> PendingAsync(FakeConversation c, AgentChatItem item)
            => (await H.Inspect(v => v.Outbox))
                .Where(e => e.ConversationId == c.Id && e.Kind == ConversationEventKinds.Item && e.State == OutboxState.Pending)
                .Select(e => Obj(e.Enc, e.Payload))
                .Where(p => p["id"]?.Value<string>() == item.Id)
                .ToList();

        /// <summary>伺服器收到的這個項目的 conv.item（依序）</summary>
        public IReadOnlyList<JObject> Sent(FakeConversation c, AgentChatItem item)
            => H.Server.Batches.SelectMany(b => b.Events)
                .Where(e => e.ConversationId == c.Id && e.Kind == ConversationEventKinds.Item)
                .Select(e => Obj(e.Enc, e.Payload))
                .Where(p => p["id"]?.Value<string>() == item.Id)
                .ToList();

        public IReadOnlyList<RelayPut> BlobRequests() => Relay.Requests.Where(r => r.Path.StartsWith(BlobPrefix, StringComparison.Ordinal)).ToList();

        public async Task<PublishedItemRow> PublishedAsync(FakeConversation c, AgentChatItem item)
            => (await H.Inspect(v => v.Store!.LoadPublishedItems(c.Id))).Single(r => r.ItemId == item.Id);

        public async Task ReconnectAsync()
        {
            H.Server.ConnectFailure = null;
            if (H.Server.Current is { Closed: false } session)
                session.Drop();
            await IdleAsync();
            H.Engine.RetryNow();
            await IdleAsync();
            await SendAsync(2);
            (await H.StateAsync()).Should().Be(SyncEngineState.Live);
        }

        /// <summary>縮圖的 E10 回空清單（步 9 的拉取不干擾）</summary>
        private static HttpResponseMessage? Respond(RelayPut request)
            => request.Method == HttpMethod.Get && request.Path == "/api/v1/device/thumbs/missing"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"refs":[],"next":null}""", Encoding.UTF8, "application/json") }
                : null;

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    private static JObject Obj(string? enc, string payload)
        => JObject.Parse(PayloadRules.TryDecode(enc, payload, 16 << 20, out var json) ? json : throw new InvalidOperationException("解不開"));

    /// <summary>
    /// 一般的長文字（ASCII、有空白：不是 base64 片段）：每 10 個字換行（output 截斷時切點所在的第一行整行換掉，一整行的話什麼都不剩）
    /// </summary>
    private static string Words(int chars, string seed)
    {
        var sb = new StringBuilder(chars + 16);
        for (var i = 0; sb.Length < chars; i++)
            sb.Append(seed).Append(i).Append(i % 10 == 9 ? (char)10 : ' ');
        return sb.ToString(0, chars);
    }

    private static AgentChatItem Tool(string output, AgentItemStatus status = AgentItemStatus.Done) => new()
    {
        Kind = AgentItemKind.Tool, Status = status, ToolName = "Bash", Title = "跑測試", Output = output, CreatedAt = T0, UpdatedAt = T0,
    };

    private static ConversationBlobRef? BlobRef(JObject payload, string field)
        => ConversationEventRules.TryReadBlobRef(payload[field], out var blobRef) ? blobRef : null;

    /// <summary>WP4.3 的截斷版：output 留結尾、開頭是「…（前 N 字省略）」；其他欄位留開頭、結尾是說明</summary>
    private static bool IsTruncated(JObject payload, string field)
        => payload[field] is { Type: JTokenType.String } value
           && (field == "output"
               ? value.Value<string>()!.StartsWith(OmittedMark, StringComparison.Ordinal)
               : value.Value<string>()!.EndsWith("完整內容在擁有者電腦）", StringComparison.Ordinal));

    private static readonly string OmittedMark = (char)0x2026 + "（前 ";

    /// <summary>遮蔽之後的欄位（投影器送的就是它）</summary>
    private static string MaskedOutput(AgentChatItem item) => CredentialLeakRules.MaskItem(ChatItemCopyRules.Copy(item)).Output!;

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>執行時組出像真憑證的字串（固定種子；repo 裡沒有這種字串）</summary>
    private static string RuntimeToken(int seed)
    {
        var random = new Random(seed);
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        return "sk-" + "ant-" + "api03-" + new string(Enumerable.Range(0, 40).Select(_ => chars[random.Next(chars.Length)]).ToArray());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!await condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"{timeoutMs} ms 內沒有等到：{what}");
            await Task.Delay(5);
        }
    }

    // ───────────────────────────── 定稿的大項目（D14、D31） ─────────────────────────────

    [Fact]
    public async Task 四十KB欄位的定稿項目_先送截斷版_上傳後改送blob_內容是遮蔽後的原文()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.NewAsync();
        var secret = RuntimeToken(3);
        var output = Words(20_000, "a") + " ANTHROPIC_API_KEY=" + secret + " " + Words(20_000, "b");
        CredentialLeakRules.Mask(output).Should().NotContain(secret, "這個測試的前提：權杖 DLP 遮得到");
        rig.Blobs.HoldPuts = true;

        var item = rig.Item(c, Tool(output));
        await rig.Blobs.WaitForHeldAsync(1);

        // 上傳還沒完成：outbox 裡是 WP4.3 的截斷版
        var pending = (await rig.PendingAsync(c, item)).Should().ContainSingle().Subject;
        IsTruncated(pending, "output").Should().BeTrue();
        rig.Blobs.Release.SetResult();
        await rig.SettleAsync();

        var sent = rig.Sent(c, item);
        sent.Should().HaveCount(2);
        IsTruncated(sent[0], "output").Should().BeTrue("第一筆是截斷版");
        var blobRef = BlobRef(sent[1], "output");
        blobRef.Should().NotBeNull("上傳完成後第二筆是 {blob, length}");
        ConversationWire.TryReadItem(sent[1], rig.Blobs.Read, out var read).Should().BeTrue();
        read!.Output.Should().Be(MaskedOutput(item), "blob 的內容取自遮蔽後的 payload");
        read.Output.Should().NotContain(secret).And.Contain(CredentialLeakRules.Replacement);
        rig.Blobs.Stored.Values.Should().OnlyContain(b => !Encoding.UTF8.GetString(b).Contains(secret, StringComparison.Ordinal), "伺服器上的 blob 沒有權杖本體");
        rig.Blobs.Puts.Should().Equal(blobRef!.Blob);
        rig.BlobRequests().Should().OnlyContain(r => r.Authorization == "Bearer cha_test");
        rig.H.Server.Conversations.Rejected.Should().BeEmpty();

        var row = await rig.PublishedAsync(c, item);
        row.Truncated.Should().BeFalse("blob 版沒有截斷");
        row.SrcSha256.Should().Be(PublishDigestRules.Source(c.Items[0]));
        (await rig.H.Inspect(v => v.Store!.LoadTruncatedItems())).Should().BeEmpty();
    }

    [Fact]
    public async Task 小項目照舊內嵌_不呼叫E5()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.NewAsync();
        var item = rig.Item(c, Tool(Words(10_000, "small")));
        await rig.SettleAsync();

        rig.BlobRequests().Should().BeEmpty("沒有超過內嵌上限的欄位：沒有 blob 要傳");
        var sent = rig.Sent(c, item).Should().ContainSingle().Subject;
        sent["output"]!.Value<string>().Should().Be(item.Output);
    }

    [Fact]
    public async Task 執行中的項目不走blob_定稿後恰好上傳一次()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.NewAsync();
        var item = rig.Item(c, Tool(Words(40_000, "run"), AgentItemStatus.Running));
        await rig.IdleAsync();
        for (var i = 0; i < 10; i++)
        {
            item.Output += Words(1_000, "more" + i);
            rig.Changed(c, item);
            await rig.IdleAsync();
            await rig.SendAsync(1);
        }

        await rig.SendAsync(10);
        rig.BlobRequests().Should().BeEmpty("執行中的項目不走 blob（內容還在變）");

        item.Status = AgentItemStatus.Done;
        rig.Changed(c, item);
        await rig.SettleAsync();

        rig.Blobs.Puts.Should().ContainSingle("定稿後恰好上傳一次");
        rig.Blobs.Heads.Should().Be(1);
        var last = rig.Sent(c, item)[^1];
        BlobRef(last, "output")!.Blob.Should().Be(Sha(MaskedOutput(item)));
        last["status"]!.Value<string>().Should().Be("Done");
    }

    [Fact]
    public async Task 上傳期間又更新_送的是最新的快照()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.NewAsync();
        rig.Blobs.HoldPuts = true;
        var item = rig.Item(c, Tool(Words(40_000, "one")));
        await rig.Blobs.WaitForHeldAsync(1);
        var first = Sha(MaskedOutput(item));

        item.Output = Words(40_000, "two");
        rig.Changed(c, item);
        await WaitUntilAsync(async () => (await rig.PendingAsync(c, item)).Any(p => p["output"]?.Value<string>() is { } o && o.Contains("two", StringComparison.Ordinal) && !o.Contains("one", StringComparison.Ordinal)),
            "新版的截斷版入列");
        rig.Blobs.Release.SetResult();
        await rig.SettleAsync(6);

        var sent = rig.Sent(c, item);
        var last = sent[^1];
        BlobRef(last, "output")!.Blob.Should().Be(Sha(MaskedOutput(item)), "最後一筆是最新快照的 blob 版");
        ConversationWire.TryReadItem(last, rig.Blobs.Read, out var read).Should().BeTrue();
        read!.Output.Should().Be(MaskedOutput(item)).And.StartWith("two0 ").And.NotContain("one");
        sent.Select(p => BlobRef(p, "output")?.Blob).Should().NotContain(first, "上傳開始時的那一版沒有被送出");
        rig.Blobs.Puts.Should().Equal(first, Sha(MaskedOutput(item)));
    }

    // ───────────────────────────── 不走 blob 的情況（D15） ─────────────────────────────

    [Fact]
    public async Task 沒有media1_只有截斷版_不呼叫E5()
    {
        await using var rig = new Rig(media: false);
        await rig.StartAsync();
        var c = await rig.NewAsync();
        var item = rig.Item(c, Tool(Words(40_000, "old")));
        await rig.SettleAsync();

        rig.BlobRequests().Should().BeEmpty("伺服器沒有 media1：BlobUploader 一次都不呼叫");
        IsTruncated(rig.Sent(c, item).Should().ContainSingle().Subject, "output").Should().BeTrue();
        (await rig.PublishedAsync(c, item)).Truncated.Should().BeTrue("之後協商出 media1 時重送（D32）");
    }

    [Fact]
    public async Task 拒收422退回截斷版_之後再改也不再試()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.NewAsync();
        rig.Blobs.PutResult = (_, _) => HttpStatusCode.UnprocessableEntity;
        var item = rig.Item(c, Tool(Words(40_000, "dlp")));
        await rig.SettleAsync();

        rig.Blobs.Puts.Should().ContainSingle();
        rig.Sent(c, item).Should().ContainSingle().Which.Should().Match<JObject>(p => IsTruncated(p, "output"));

        item.Output = Words(41_000, "again");
        rig.Changed(c, item);
        await rig.SettleAsync();

        rig.Blobs.Heads.Should().Be(1, "這個項目在本次執行不再試");
        rig.Blobs.Puts.Should().ContainSingle();
        var sent = rig.Sent(c, item);
        sent.Should().HaveCount(2);
        sent.Should().OnlyContain(p => IsTruncated(p, "output"));
        (await rig.PublishedAsync(c, item)).Truncated.Should().BeTrue();
    }

    [Fact]
    public async Task 暫時失敗_5分鐘後再試()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.NewAsync();
        rig.Blobs.PutResult = (_, _) => HttpStatusCode.InsufficientStorage;
        var item = rig.Item(c, Tool(Words(40_000, "full")));
        await rig.SettleAsync();
        rig.Blobs.Puts.Should().ContainSingle();
        rig.Sent(c, item).Should().ContainSingle();

        rig.Blobs.PutResult = null;
        await rig.SendAsync(60);
        rig.Blobs.Puts.Should().ContainSingle("5 分鐘內不再試");
        await rig.SendAsync(ItemBlobRules.RetryAfter.TotalSeconds);
        await rig.SettleAsync();

        rig.Blobs.Puts.Should().HaveCount(2);
        BlobRef(rig.Sent(c, item)[^1], "output").Should().NotBeNull();
    }

    [Fact]
    public async Task 超過4MB的欄位先截到4MB()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.NewAsync();
        var item = rig.Item(c, Tool(Words(5 * 1024 * 1024, "huge")));
        await rig.SettleAsync();

        var last = rig.Sent(c, item)[^1];
        var blobRef = BlobRef(last, "output");
        blobRef.Should().NotBeNull($"sent={rig.Sent(c, item).Count} type={last["output"]?.Type} len={last["output"]?.ToString().Length} heads={rig.Blobs.Heads} puts={rig.Blobs.Puts.Count}");
        blobRef!.Length.Should().BeLessThanOrEqualTo(ItemBlobRules.MaxItemBlobBytes);
        ConversationWire.TryReadItem(last, rig.Blobs.Read, out var read).Should().BeTrue();
        read!.Output.Should().StartWith(OmittedMark, "output 留結尾（同 WP4.3）").And.EndWith(item.Output![^100..]);
        (await rig.PublishedAsync(c, item)).Truncated.Should().BeTrue("PublishRecord.Truncated：每欄截到 4 MB 也算截斷");
    }

    [Fact]
    public async Task base64片段超過DLP的解碼範圍_不走blob()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.NewAsync();
        // 權杖 DLP 只解碼片段的開頭 64 KB：憑證放在更後面、每 76 字折行（像 base64 指令的輸出）
        var secret = RuntimeToken(5);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('x', 120_000) + "{\"refresh_token\":\"" + secret + "\"}"));
        var output = "$ base64 creds.json" + (char)10 + string.Join((char)10, encoded.Chunk(76).Select(chunk => new string(chunk)));
        ItemBlobRules.LongestBase64Run(output).Should().BeGreaterThan(ItemBlobRules.DlpDecodeWindowChars, "這個測試的前提");
        var item = rig.Item(c, Tool(output));
        await rig.SettleAsync();

        rig.BlobRequests().Should().BeEmpty("整段 base64 不送上伺服器");
        var sent = rig.Sent(c, item).Should().ContainSingle().Subject;
        IsTruncated(sent, "output").Should().BeTrue("照 WP4.3 截斷（每欄 ≤30 KB，DLP 整段解得到）");
        (await rig.PublishedAsync(c, item)).Truncated.Should().BeTrue();
    }

    // ───────────────────────────── 截斷過的項目重送（D32） ─────────────────────────────

    [Fact]
    public async Task RequeueTruncatedForMedia_截斷過的兩筆在協商出media1後各重送一次()
    {
        await using var rig = new Rig(media: false);
        await rig.StartAsync();
        var c = await rig.NewAsync();
        var a = rig.Item(c, Tool(Words(40_000, "first")));
        var b = rig.Item(c, Tool(Words(50_000, "second")));
        var small = rig.Item(c, Tool("ok"));
        await rig.SettleAsync();
        (await rig.H.Inspect(v => v.Store!.LoadTruncatedItems())).Select(k => k.ItemId).Should().BeEquivalentTo([a.Id, b.Id]);
        var before = new[] { a, b, small }.ToDictionary(i => i.Id, i => rig.Sent(c, i).Count);

        rig.H.Server.Features = WithMedia;
        await rig.ReconnectAsync();
        await rig.SettleAsync(8);

        foreach (var item in new[] { a, b })
        {
            var resent = rig.Sent(c, item).Skip(before[item.Id]).ToList();
            resent.Should().ContainSingle("各重送一次");
            BlobRef(resent[0], "output")!.Blob.Should().Be(Sha(MaskedOutput(item)));
        }

        rig.Sent(c, small).Should().HaveCount(before[small.Id], "沒截斷過的不重送");
        rig.Blobs.Puts.Should().HaveCount(2);
        (await rig.H.Inspect(v => v.Store!.LoadTruncatedItems())).Should().BeEmpty();

        // 再連一次（同一次執行、同一台伺服器）：不再重排
        var heads = rig.Blobs.Heads;
        await rig.ReconnectAsync();
        await rig.SettleAsync();
        rig.Blobs.Heads.Should().Be(heads);
    }

    [Fact]
    public async Task 離線時定稿_連上之後上傳()
    {
        await using var rig = new Rig();
        await rig.StartAsync();
        var c = await rig.NewAsync();
        rig.H.Server.ConnectFailure = _ => EngineHarness.Transient();
        rig.H.Server.Current!.Drop();
        await rig.IdleAsync();

        var item = rig.Item(c, Tool(Words(40_000, "offline")));
        await rig.IdleAsync();
        rig.BlobRequests().Should().BeEmpty("離線時不傳");

        await rig.ReconnectAsync();
        await rig.SettleAsync(6);

        rig.Blobs.Puts.Should().ContainSingle();
        var sent = rig.Sent(c, item);
        IsTruncated(sent[0], "output").Should().BeTrue("離線期間排的是截斷版");
        BlobRef(sent[^1], "output")!.Blob.Should().Be(Sha(MaskedOutput(item)));
    }
}
