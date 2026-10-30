using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Media;
using Myriagent.Sync;
using Myriagent.Tests.Rendering;
using Myriagent.Tests.Sync.Fakes;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 複本的媒體網址（WP4.7 計畫步 12，D38）：<see cref="ReplicaMediaUrls"/> 本身，以及中樞（真的 HubHarness＋發布者）在有媒體來源時把複本的附件與代理媒體
/// 渲染成 <c>/m/</c>、<c>/t/</c>、只轉送登記過的鍵；沒有來源、關掉顯示、擁有者不是裝置 id 時照 WP4.3 沒有網址
/// </summary>
[Collection(AppPathsCollection.Name)]
public sealed class ReplicaMediaUrlsTests
{
    private static readonly string Owner = SyncIds.NewDeviceId();

    private const string IdA = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8";

    private static string Key(string? mediaRef = null) => MediaKeyRules.Format(Owner, mediaRef ?? MediaTestFiles.NewId());

    // ───────────────────────────── ReplicaMediaUrls ─────────────────────────────

    [Fact]
    public void 複本路徑才有網址_產生時登記()
    {
        var urls = new ReplicaMediaUrls();
        var image = Key();
        var video = Key();
        var document = Key();

        urls.MediaUrl(ReplicaMediaRules.PathFor(image, "a.png")).Should().Be("https://myriagent.local/m/" + image);
        urls.ThumbUrl(ReplicaMediaRules.PathFor(video, "b.mp4"), 320).Should().Be("https://myriagent.local/t/" + video + "?s=320");
        urls.ThumbUrl(ReplicaMediaRules.PathFor(video, "b.mp4"), 100).Should().EndWith("?s=160");
        urls.MediaUrl(ReplicaMediaRules.PathFor(document, "c.pdf")).Should().BeNull("不能直接顯示的不給原檔網址");

        urls.IsAllowed(image, MediaPurposes.Media).Should().BeTrue();
        urls.IsAllowed(image, MediaPurposes.Thumb).Should().BeTrue("原檔登記過的縮圖也可以");
        urls.IsAllowed(video, MediaPurposes.Thumb).Should().BeTrue();
        urls.IsAllowed(video, MediaPurposes.Media).Should().BeFalse("只登記了縮圖");
        urls.IsAllowed(document, MediaPurposes.Media).Should().BeFalse();
        urls.IsAllowed(image, "file").Should().BeFalse();

        urls.Clear();
        urls.IsAllowed(image, MediaPurposes.Media).Should().BeFalse();
        urls.IsAllowed(video, MediaPurposes.Thumb).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\owner\a.png")]
    [InlineData(@"\\server\share\a.png")]
    [InlineData("a.png")]
    [InlineData("")]
    public void 其他路徑沒有網址_Markdown一律是井號(string path)
    {
        var urls = new ReplicaMediaUrls();
        urls.MediaUrl(path).Should().BeNull();
        urls.ThumbUrl(path, 320).Should().BeNull();
        urls.MarkdownImageUrl(path).Should().Be("#");
        urls.MarkdownLinkUrl(path).Should().Be("#");
    }

    // ───────────────────────────── 中樞 ─────────────────────────────

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            H = new HubHarness();
            Engine = new RecordingSyncEngine { Conversations = Box };
            Publisher = H.OnUi(() => ConversationPublisher.TryCreate(H.Hub, Engine, Time))!;
            H.Ui.Invoke(() => H.Hub.AttachConversationSink(Publisher));
        }

        public HubHarness H { get; }

        public ManualTimeProvider Time { get; } = new();

        public ConversationChangeBox Box { get; } = new();

        public RecordingSyncEngine Engine { get; }

        public ConversationPublisher Publisher { get; }

        public void Show() => H.Ui.Invoke(() => H.Hub.ShowReplicas = true);

        public void Deliver(ReplicaDelta delta) => H.Ui.Invoke(() => Publisher.ApplyReplicaDelta(delta));

        public IReplicaMediaSource? For(string key, string purpose) => H.OnUi(() => H.Hub.ReplicaMediaFor(key, purpose));

        /// <summary>打開 <see cref="IdA"/> 並交給它這些項目，回傳畫面收到的項目（id → 渲染結果）</summary>
        public Dictionary<string, JObject> OpenWith(string owner, params AgentChatItem[] items)
        {
            Deliver(new ReplicaDelta([Meta(owner)], [], [], Full: true));
            var view = H.Main;
            H.Command(view, "open", new { conversationId = IdA });
            var mark = view.Mark();
            Deliver(new ReplicaDelta([], [], [new ReplicaItems(IdA, true, items, [])]));
            H.Command(view, "loadOlder", new { conversationId = IdA });
            return view.All().Skip(mark)
                .SelectMany(m => m.Value<string>("type") switch
                {
                    "item" => new[] { (JObject)m["item"]! },
                    "conversation" => ((JArray)m["items"]!).Cast<JObject>(),
                    _ => Enumerable.Empty<JObject>(),
                })
                .GroupBy(i => i.Value<string>("id")!)
                .ToDictionary(g => g.Key, g => g.Last());
        }

        public void Dispose() => H.Dispose();
    }

    private static ReplicaMeta Meta(string owner)
        => ReplicaRules.ToMeta(new ReplicaMetaRow(IdA, owner, 3,
            ConversationWire.Serialize(new ConversationMetaPayload("Codex", "acc-owner", "擁有者的對話", "FullAccess", "gpt-5", "high", true, @"C:\owner\repo",
                ConversationSyncPolicies.Full, 1791500000000, 1791500060000, false, false)), false, 7))!;

    /// <summary>照線上的路徑：擁有者的項目（一張附件圖、一個影片附件、一張代理產出的圖）→ conv.item → 複本的項目</summary>
    private static (AgentChatItem User, AgentChatItem Assistant, string ImageRef, string VideoRef, string MadeRef) Items()
    {
        var imageRef = MediaTestFiles.NewId();
        var videoRef = MediaTestFiles.NewId();
        var madeRef = MediaTestFiles.NewId();
        var user = new AgentChatItem
        {
            Id = "i1", Ord = 1, Kind = AgentItemKind.User, Status = AgentItemStatus.Done, Text = "附圖",
            Attachments =
            {
                new AgentAttachment { Id = "x1", FileName = "screen.png", StoredPath = @"C:\owner\att\screen.png", Kind = AgentAttachmentKind.Image, SizeBytes = 2048 },
                new AgentAttachment { Id = "x2", FileName = "clip.mp4", StoredPath = @"C:\owner\att\clip.mp4", Kind = AgentAttachmentKind.Video, SizeBytes = 4096 },
            },
            MediaRefs = [new AgentMediaRef { Ref = imageRef, AttachmentId = "x1" }, new AgentMediaRef { Ref = videoRef, AttachmentId = "x2" }],
        };
        var assistant = new AgentChatItem
        {
            Id = "i2", Ord = 2, Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Done, Text = "畫好了",
            MediaPaths = { @"C:\owner\out\chart.png" },
            MediaRefs = [new AgentMediaRef { Ref = madeRef, MediaPath = @"C:\owner\out\chart.png" }],
        };
        return (ViaWire(user), ViaWire(assistant), imageRef, videoRef, madeRef);
    }

    private static AgentChatItem ViaWire(AgentChatItem source)
    {
        ConversationWire.TryItem(source, 1, out var wire).Should().BeTrue();
        return ReplicaRules.ToItem(ConversationWire.Serialize(wire!))!;
    }

    [Fact]
    public void 有媒體來源時_附件與代理媒體有網址_只轉送登記過的鍵()
    {
        using var rig = new Rig();
        var source = new FakeReplicaMediaSource();
        rig.Box.ReplicaMedia = source;   // 引擎掛在盒子上、經發布者交給中樞（正式的路徑）
        rig.Show();
        var (user, assistant, imageRef, videoRef, madeRef) = Items();

        var rendered = rig.OpenWith(Owner, user, assistant);

        var image = MediaKeyRules.Format(Owner, imageRef);
        var video = MediaKeyRules.Format(Owner, videoRef);
        var made = MediaKeyRules.Format(Owner, madeRef);
        var attachments = ((JArray)rendered["i1"]["attachments"]!).Cast<JObject>().ToList();
        attachments[0].Value<string>("thumbUrl").Should().Be("https://myriagent.local/t/" + image + "?s=320");
        attachments[0].Value<string>("url").Should().Be("https://myriagent.local/m/" + image);
        attachments[0].Value<string>("path").Should().BeEmpty("複本路徑不給網頁（開啟、在檔案總管顯示只對這台的檔案有用）");
        attachments[0].Value<string>("name").Should().Be("screen.png");
        attachments[1].Value<string>("thumbUrl").Should().Be("https://myriagent.local/t/" + video + "?s=320", "影片附件的縮圖由擁有者電腦產生");
        attachments[1].Value<string>("url").Should().Be("https://myriagent.local/m/" + video);
        var media = (JObject)((JArray)rendered["i2"]["media"]!).Single();
        media.Value<string>("url").Should().Be("https://myriagent.local/m/" + made);
        media.Value<string>("name").Should().Be("chart.png");
        media["path"]!.Type.Should().Be(JTokenType.Null);
        rendered.Values.Select(i => i.ToString()).Should().NotContain(s => s.Contains("myriagent-replica:", StringComparison.Ordinal) || s.Contains(@"C:\owner", StringComparison.Ordinal));

        rig.For(image, MediaPurposes.Media).Should().BeSameAs(source);
        rig.For(image, MediaPurposes.Thumb).Should().BeSameAs(source);
        rig.For(made, MediaPurposes.Media).Should().BeSameAs(source);
        rig.For(Key(), MediaPurposes.Media).Should().BeNull("沒登記過的鍵（網頁自己拼的網址）不轉送");
        rig.For(image, "file").Should().BeNull();

        // 本機的白名單不受影響
        rig.H.OnUi(() => rig.H.Hub.IsMediaAllowed("myriagent-replica:" + image + "/screen.png")).Should().BeFalse();
        rig.H.OnUi(() => rig.H.Hub.IsThumbAllowed(@"C:\owner\att\screen.png")).Should().BeFalse();
    }

    [Fact]
    public void ShowReplicas關著仍是NullMediaUrlBuilder_關掉顯示時登記清掉()
    {
        using var rig = new Rig();
        var source = new FakeReplicaMediaSource();
        rig.H.Ui.Invoke(() => rig.H.Hub.ReplicaMediaSourceForTest = source);
        var (user, _, imageRef, _, _) = Items();
        var image = MediaKeyRules.Format(Owner, imageRef);

        // 關著：複本不建立，任何鍵都不轉送
        rig.Deliver(new ReplicaDelta([Meta(Owner)], [], [], Full: true));
        rig.H.Conversation(IdA).Should().BeNull();
        rig.For(image, MediaPurposes.Media).Should().BeNull();

        rig.Show();
        rig.OpenWith(Owner, user)["i1"]["attachments"]![0]!.Value<string>("url").Should().NotBeNull("陽性對照：打開顯示之後有網址");
        rig.For(image, MediaPurposes.Media).Should().BeSameAs(source);

        rig.H.Ui.Invoke(() => rig.H.Hub.ShowReplicas = false);
        rig.For(image, MediaPurposes.Media).Should().BeNull("關掉顯示：登記清掉、不轉送");
        rig.H.Ui.Invoke(() => rig.H.Hub.ShowReplicas = true);
        rig.For(image, MediaPurposes.Media).Should().BeNull("再打開也要重新渲染才登記");
        source.Calls.Should().BeEmpty("中樞只回來源，不自己呼叫");
    }

    [Fact]
    public void 沒有媒體來源時_照WP4點3沒有網址()
    {
        using var rig = new Rig();
        rig.Show();
        var (user, assistant, imageRef, _, _) = Items();

        var rendered = rig.OpenWith(Owner, user, assistant);

        var attachment = (JObject)rendered["i1"]["attachments"]![0]!;
        attachment.Value<string>("url").Should().BeNull();
        attachment.Value<string>("thumbUrl").Should().BeNull();
        attachment.Value<string>("path").Should().BeEmpty();
        rendered["i2"]["media"].Should().BeNull("沒有本機路徑的代理媒體不顯示");
        rig.For(MediaKeyRules.Format(Owner, imageRef), MediaPurposes.Media).Should().BeNull();
    }

    [Fact]
    public void 擁有者不是裝置id時沒有網址()
    {
        using var rig = new Rig();
        rig.H.Ui.Invoke(() => rig.H.Hub.ReplicaMediaSourceForTest = new FakeReplicaMediaSource());
        rig.Show();
        var (user, _, _, _, _) = Items();

        var rendered = rig.OpenWith("d_01J9Q0000000000000000000OW", user);

        rendered["i1"]["attachments"]![0]!.Value<string>("url").Should().BeNull();
        rendered["i1"]["attachments"]![0]!.Value<string>("thumbUrl").Should().BeNull();
    }

    [Fact]
    public void 發布者釋放之後沒有來源()
    {
        using var rig = new Rig();
        var source = new FakeReplicaMediaSource();
        rig.Box.ReplicaMedia = source;
        rig.Publisher.ReplicaMediaSource.Should().BeSameAs(source);

        rig.Box.ClearReplicaMedia(new FakeReplicaMediaSource());
        rig.Box.ReplicaMedia.Should().BeSameAs(source, "只清自己掛的");
        rig.H.Ui.Invoke(() => rig.Publisher.Dispose());
        rig.Publisher.ReplicaMediaSource.Should().BeNull();
        rig.Box.ClearReplicaMedia(source);
        rig.Box.ReplicaMedia.Should().BeNull();
    }
}
