using System.Net;
using System.Text;
using Myriagent.Files;
using Myriagent.Hub;
using Myriagent.Media;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;

namespace Myriagent.Tests.Sync;

/// <summary>
/// 縮圖上傳（WP4.7 計畫步 9，D30）：E10 的解析、佇列（≤500、滿了丟最舊的、同一個參照只排一次）、自己的 ThumbnailService、
/// 一筆上傳的形狀（E8 的路徑、對話標頭、PNG）。真的守門員與暫存檔、假的 E8／E10（<see cref="FakeRelayHandler"/>）、假的中樞快照
/// </summary>
public sealed class ThumbnailPublisherTests : IDisposable
{
    private const string DeviceId = "d_01J9Q0000000000000000000ME";

    private readonly MediaTestFiles files = new();
    private readonly FakeRelayHandler relay = new();
    private readonly AccessTokenHolder tokens = new();
    private Func<MediaScopeQuery, MediaScopeSnapshot?> answer = _ => null;
    private Func<string, int, CancellationToken, Task<byte[]?>>? thumbnail = (_, _, _) => Task.FromResult<byte[]?>(MediaTestFiles.Png);
    private ThumbnailPublisher? publisher;

    public void Dispose()
    {
        publisher?.Dispose();
        files.Dispose();
    }

    private ThumbnailPublisher Publisher()
    {
        if (publisher is not null)
            return publisher;
        tokens.Set("cha_unit", TimeProvider.System.GetTimestamp());
        var hub = new Uri("https://hub.example/");
        var http = new DeviceHttpClient(hub, hub, false, tokens, _ => Task.FromResult(false), TimeProvider.System, relay);
        var resolver = new LocalMediaResolver((query, _) => Task.FromResult(answer(query)), MediaTestFiles.Guard());
        publisher = new ThumbnailPublisher(http, resolver, thumbnail);
        return publisher;
    }

    private static LocalMediaRefRow Row(string itemId = "item-1", string? attachmentId = "att-1", string? mediaPath = null)
        => new(MediaTestFiles.NewId(), MediaTestFiles.ConversationId, itemId, attachmentId, mediaPath, 1791600000000);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // ── 規則 ──

    [Fact]
    public void E8與E10的網址()
    {
        var reference = MediaTestFiles.NewId();
        ThumbnailPublishRules.PutPath(reference).Should().Be("/api/v1/device/thumbs/" + reference);
        FluentActions.Invoking(() => ThumbnailPublishRules.PutPath("../x")).Should().Throw<ArgumentException>();
        ThumbnailPublishRules.MissingPath(null).Should().Be("/api/v1/device/thumbs/missing?limit=200");
        ThumbnailPublishRules.MissingPath("1791600000000_ABC").Should().Be("/api/v1/device/thumbs/missing?limit=200&before=1791600000000_ABC");
        ThumbnailPublishRules.MayPull(300).Should().BeTrue();
        ThumbnailPublishRules.MayPull(301).Should().BeFalse("一頁 200 筆要放得下，否則會把最新的擠掉");
    }

    [Fact]
    public void ParseMissing_略過不合格的項目_next太長當成沒有()
    {
        var good = MediaTestFiles.NewId();
        var json = $$"""
            {"refs":[
              {"ref":"{{good}}","conversationId":"{{MediaTestFiles.ConversationId}}","itemId":"item-1"},
              {"ref":"short","conversationId":"{{MediaTestFiles.ConversationId}}","itemId":"item-2"},
              {"ref":"{{MediaTestFiles.NewId()}}","conversationId":"NOT-HEX","itemId":"item-3"},
              {"ref":"{{MediaTestFiles.NewId()}}","conversationId":"{{MediaTestFiles.ConversationId}}","itemId":"@list"},
              7
            ],"next":"1791600000000_{{good}}"}
            """;

        var page = ThumbnailPublishRules.ParseMissing(json)!;

        page.Refs.Should().Equal(new MissingThumb(good, MediaTestFiles.ConversationId, "item-1"));
        page.Next.Should().Be("1791600000000_" + good);
        ThumbnailPublishRules.ParseMissing($$"""{"refs":[],"next":"{{new string('x', 65)}}"}""")!.Next.Should().BeNull();
        ThumbnailPublishRules.ParseMissing("""{"refs":[],"next":null}""")!.Refs.Should().BeEmpty();
        ThumbnailPublishRules.ParseMissing("""{"next":null}""").Should().BeNull();
        ThumbnailPublishRules.ParseMissing("[]").Should().BeNull();
        ThumbnailPublishRules.ParseMissing("{").Should().BeNull();
        ThumbnailPublishRules.ParseMissing(null).Should().BeNull();
    }

    // ── 佇列 ──

    [Fact]
    public void 縮圖_佇列滿了丟最舊的()
    {
        var p = Publisher();
        var rows = Enumerable.Range(0, ThumbnailPublishRules.MaxQueue + 1).Select(i => Row($"item-{i}")).ToList();

        foreach (var row in rows)
            p.Enqueue(row).Should().BeTrue();

        p.Count.Should().Be(ThumbnailPublishRules.MaxQueue);
        p.Overflowed.Should().Be(1);
        p.TryTake().Should().Be(rows[1], "最舊的那一筆被丟掉（之後由 E10 補）");
        p.Enqueue(rows[0]).Should().BeTrue("被丟掉的之後還能再排");
    }

    [Fact]
    public void 佇列_同一個參照只排一次_上傳過的不再排_抽屜的不排()
    {
        var p = Publisher();
        var row = Row();

        p.Enqueue(row).Should().BeTrue();
        p.Enqueue(row).Should().BeFalse("已經排著");
        p.TryTake().Should().Be(row);
        p.MarkUploaded(row.Ref);
        p.Enqueue(row).Should().BeFalse("這次啟動上傳過");
        p.Enqueue(new LocalMediaRefRow(MediaTestFiles.NewId(), MediaTestFiles.ConversationId, MediaServerRules.ListItemId, null, @"C:\repo\a.png", 1)).Should().BeFalse("抽屜的縮圖一律按需轉送");
        p.TryTake().Should().BeNull();
    }

    [Fact]
    public void 佇列_伺服器缺的忘掉上傳過_換實例或改回full全部忘掉()
    {
        // 合併前審查 DI-1：伺服器的縮圖沒了（還原、降級再改回 full）時，「這次上傳過」不能擋住重傳
        var p = Publisher();
        var row = Row();
        var other = Row("item-2");
        p.MarkUploaded(row.Ref);
        p.MarkUploaded(other.Ref);
        p.Enqueue(row).Should().BeFalse();

        p.ForgetUploaded(row.Ref);

        p.Enqueue(row).Should().BeTrue("E10 列出的以伺服器為準");
        p.Enqueue(other).Should().BeFalse("只忘掉那一個");
        p.ForgetAllUploaded();
        p.Enqueue(other).Should().BeTrue("伺服器換了實例、對話改回 full");
    }

    [Fact]
    public void 縮圖_用自己的ThumbnailService實例()
    {
        thumbnail = null;
        var p = Publisher();
        using var hubs = new ThumbnailService();
        var hub = new Uri("https://hub.example/");
        using var media = new MediaServer(new DeviceHttpClient(hub, hub, false, tokens, _ => Task.FromResult(false), TimeProvider.System, new FakeRelayHandler()),
            (_, _) => Task.FromResult<MediaScopeSnapshot?>(null), MediaTestFiles.Guard());

        var own = p.OwnThumbnails();

        own.Should().BeSameAs(p.OwnThumbnails(), "第一次用時建、之後重用");
        own.Should().NotBeSameAs(hubs, "不和本機介面共用 STA 執行緒與快取（F33）");
        own.Should().NotBeSameAs(media.OwnThumbnails(), "媒體轉送也有自己的");
    }

    // ── 一筆上傳 ──

    [Fact]
    public async Task 上傳_PUT_E8_帶對話標頭與PNG_200算成功()
    {
        var path = files.Write(Path.Combine("data", "conversations", MediaTestFiles.ConversationId, "attachments", "photo.png"), MediaTestFiles.Png);
        answer = _ => files.Snapshot(resolved: (path, "photo.png"));
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9 };
        string? thumbPath = null;
        thumbnail = (p, size, _) =>
        {
            thumbPath = p;
            size.Should().Be(320);
            return Task.FromResult<byte[]?>(png);
        };
        relay.Responder = _ => Json(HttpStatusCode.OK, """{"sha256":"x","length":6}""");
        var row = Row();

        (await Publisher().PublishAsync(row, DeviceId, CancellationToken.None)).Should().Be(ThumbOutcome.Uploaded);

        var put = relay.Requests.Should().ContainSingle().Subject;
        put.Method.Should().Be(HttpMethod.Put);
        put.Path.Should().Be("/api/v1/device/thumbs/" + row.Ref);
        put.Header(MediaRelayHeaders.Conversation).Should().Be(MediaTestFiles.ConversationId);
        put.Header("Content-Type").Should().Be("image/png");
        put.Authorization.Should().Be("Bearer cha_unit");
        put.Body.Should().Equal(png);
        thumbPath.Should().BeEquivalentTo(path, "產縮圖之前先過守門員，給它真實路徑");
        relay.Puts.Should().BeEmpty("不是 E7 的轉送");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    public async Task 上傳_伺服器拒絕就丟掉這筆(HttpStatusCode status)
    {
        var path = files.Write(Path.Combine("data", "conversations", MediaTestFiles.ConversationId, "attachments", "photo.png"), MediaTestFiles.Png);
        answer = _ => files.Snapshot(resolved: (path, "photo.png"));
        relay.Responder = _ => Json(status, """{"error":"x"}""");

        (await Publisher().PublishAsync(Row(), DeviceId, CancellationToken.None)).Should().Be(ThumbOutcome.Dropped);
        relay.Requests.Should().ContainSingle("不重試（之後的 E10 會再列出缺的）");
    }

    [Fact]
    public async Task 上傳_對話看不到_守門員拒絕_縮圖太大_都不送()
    {
        var p = Publisher();
        answer = q => MediaScopeSnapshot.Hidden(q.ConversationId);
        (await p.PublishAsync(Row(), DeviceId, CancellationToken.None)).Should().Be(ThumbOutcome.Dropped);

        // 守門員擋下的（UNC）不產縮圖；產出來超過 512 KB 的不送
        var called = 0;
        thumbnail = (_, _, _) =>
        {
            Interlocked.Increment(ref called);
            return Task.FromResult<byte[]?>(new byte[MediaServerRules.MaxThumbBytes + 1]);
        };
        publisher!.Dispose();
        publisher = null;
        p = Publisher();
        answer = _ => files.Snapshot(resolved: (@"\\nas\share\secret.png", "secret.png"));
        (await p.PublishAsync(Row(), DeviceId, CancellationToken.None)).Should().Be(ThumbOutcome.Dropped);
        called.Should().Be(0, "UNC 在字串層就擋下");

        var inside = files.Write(Path.Combine("data", "conversations", MediaTestFiles.ConversationId, "attachments", "big.png"), MediaTestFiles.Png);
        answer = _ => files.Snapshot(resolved: (inside, "big.png"));
        (await p.PublishAsync(Row(), DeviceId, CancellationToken.None)).Should().Be(ThumbOutcome.Dropped);
        called.Should().Be(1);
        relay.Requests.Should().BeEmpty("縮圖超過 512 KB 不送");
    }

    [Fact]
    public async Task 上傳_原檔已經不在_回Gone_不產縮圖也不送()
    {
        var dir = Path.GetDirectoryName(files.Write(Path.Combine("data", "conversations", MediaTestFiles.ConversationId, "attachments", "other.png"), MediaTestFiles.Png))!;
        answer = _ => files.Snapshot(resolved: (Path.Combine(dir, "deleted.png"), "deleted.png"));
        var called = 0;
        thumbnail = (_, _, _) =>
        {
            Interlocked.Increment(ref called);
            return Task.FromResult<byte[]?>(MediaTestFiles.Png);
        };

        (await Publisher().PublishAsync(Row(), DeviceId, CancellationToken.None)).Should().Be(ThumbOutcome.Gone);

        called.Should().Be(0);
        relay.Requests.Should().BeEmpty();
    }

    [Fact]
    public void 原檔不在的參照_記下來到期前不再排_換實例或改回full不清_存成kv再讀回()
    {
        var p = Publisher();
        var gone = Row();
        const long now = 1_800_000_000_000;

        p.MarkGone(gone.Ref, now).Should().BeTrue("第一次記要存回 kv");
        p.MarkGone(gone.Ref, now + 1000).Should().BeFalse("已經記著");
        p.IsGone(gone.Ref, now + 1000).Should().BeTrue();
        p.ForgetAllUploaded();
        p.IsGone(gone.Ref, now + 1000).Should().BeTrue("伺服器的狀態和檔案在不在無關");
        p.IsGone(gone.Ref, now + (long)ThumbnailPublishRules.GoneRetryAfter.TotalMilliseconds).Should().BeFalse("到期再試一次");

        var json = p.SerializeGone();
        var reloaded = Publisher();
        reloaded.LoadGone(json, now + 5000);
        reloaded.IsGone(gone.Ref, now + 5000).Should().BeTrue();
        reloaded.LoadGone(json, now + (long)ThumbnailPublishRules.GoneRetryAfter.TotalMilliseconds);
        reloaded.SerializeGone().Should().Be("{}", "讀回時到期的丟掉");
    }

    [Fact]
    public void 原檔不在的規則_解析略過不合格的_依參照排序寫出_時鐘調回去不會一直不試_超過上限丟最早記的()
    {
        var a = MediaTestFiles.NewId();
        var b = MediaTestFiles.NewId();
        var (first, second) = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
        var parsed = ThumbnailPublishRules.ParseGone($$"""{"{{second}}":2000,"{{first}}":1000,"short":5,"{{MediaTestFiles.NewId()}}":"x","{{MediaTestFiles.NewId()}}":-1}""");
        parsed.Should().BeEquivalentTo(new Dictionary<string, long> { [first] = 1000, [second] = 2000 });
        ThumbnailPublishRules.SerializeGone(parsed).Should().Be($$"""{"{{first}}":1000,"{{second}}":2000}""");
        ThumbnailPublishRules.ParseGone("[1]").Should().BeEmpty();
        ThumbnailPublishRules.ParseGone("壞掉").Should().BeEmpty();
        ThumbnailPublishRules.ParseGone(null).Should().BeEmpty();

        var week = (long)ThumbnailPublishRules.GoneRetryAfter.TotalMilliseconds;
        ThumbnailPublishRules.IsGone(parsed, first, 1000 + week - 1).Should().BeTrue();
        ThumbnailPublishRules.IsGone(parsed, first, 1000 - week + 1).Should().BeTrue("時鐘稍微往回也算");
        ThumbnailPublishRules.IsGone(parsed, first, 1000 - week).Should().BeFalse("時鐘被調回去太多：不會一直不試");

        var many = Enumerable.Range(0, ThumbnailPublishRules.MaxGone + 3).ToDictionary(i => MediaTestFiles.NewId(), i => 10_000L + i);
        var oldest = many.OrderBy(e => e.Value).Take(3).Select(e => e.Key).ToList();
        ThumbnailPublishRules.PruneGone(many, 20_000);
        many.Should().HaveCount(ThumbnailPublishRules.MaxGone);
        many.Keys.Should().NotContain(oldest);
    }

    [Fact]
    public async Task E10_一頁_回應不對回null()
    {
        var reference = MediaTestFiles.NewId();
        relay.Responder = put => put.Path == "/api/v1/device/thumbs/missing"
            ? Json(HttpStatusCode.OK, $$"""{"refs":[{"ref":"{{reference}}","conversationId":"{{MediaTestFiles.ConversationId}}","itemId":"item-1"}],"next":null}""")
            : null;

        var page = await Publisher().FetchMissingAsync("1791600000000_" + reference, CancellationToken.None);

        page!.Refs.Should().ContainSingle().Which.Ref.Should().Be(reference);
        relay.Requests.Single().Query.Should().Be("?limit=200&before=1791600000000_" + reference);
        relay.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        (await Publisher().FetchMissingAsync(null, CancellationToken.None)).Should().BeNull();
    }
}
