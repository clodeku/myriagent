using Myriagent.Agents;
using Myriagent.Hub;
using Myriagent.Media;
using Myriagent.Sync;
using Myriagent.Tests.Sync.Fakes;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Hub;

/// <summary>
/// 複本顯示圖片與影片的純函式（WP4.7 計畫步 12，D38）：複本路徑、線上代理媒體的參照、渲染用的複本、E9 的請求路徑與 Range、渲染後拿掉複本路徑
/// </summary>
public sealed class ReplicaMediaRulesTests
{
    private static readonly string Owner = SyncIds.NewDeviceId();

    private static string Key(string? mediaRef = null) => MediaKeyRules.Format(Owner, mediaRef ?? MediaTestFiles.NewId());

    /// <summary>照線上的路徑：擁有者的項目 → conv.item 的 payload → 複本的項目</summary>
    private static AgentChatItem ViaWire(AgentChatItem source)
    {
        ConversationWire.TryItem(source, 1, out var wire).Should().BeTrue();
        return ReplicaRules.ToItem(ConversationWire.Serialize(wire!))!;
    }

    private static AgentChatItem Source(Action<AgentChatItem> edit)
    {
        var item = new AgentChatItem { Id = "i1", Ord = 1, Kind = AgentItemKind.Assistant, Status = AgentItemStatus.Done, Text = "內容" };
        edit(item);
        return item;
    }

    // ── 複本路徑 ──

    [Fact]
    public void 複本路徑來回_不是本機磁碟路徑()
    {
        var key = Key();
        var path = ReplicaMediaRules.PathFor(key, "圖 1.png");

        path.Should().Be("myriagent-replica:" + key + "/圖 1.png");
        ReplicaMediaRules.TryKey(path, out var back).Should().BeTrue();
        back.Should().Be(key);
        AgentAttachmentRules.IsLocalDrivePath(path).Should().BeFalse("openPath、revealPath、/media、/thumb 一律不認");
        AgentAttachmentRules.IsDisplayableMedia(path).Should().BeTrue("副檔名照原檔，判斷能不能直接顯示");
    }

    [Fact]
    public void 複本路徑_檔名清理_沒有檔名用file()
    {
        var key = Key();
        ReplicaMediaRules.PathFor(key, @"C:\owner\dir\a.png").Should().EndWith("/a.png", "只留最後一段");
        ReplicaMediaRules.PathFor(key, "../../x.mp4").Should().EndWith("/x.mp4");
        ReplicaMediaRules.PathFor(key, null).Should().EndWith("/file");
        FluentActions.Invoking(() => ReplicaMediaRules.PathFor("d_x-y", "a.png")).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\owner\a.png")]
    [InlineData("myriagent-replica:")]
    [InlineData("myriagent-replica:d_ABC-DEF/a.png")]
    [InlineData("MYRIAGENT-REPLICA:{key}/a.png")]
    [InlineData("myriagent-replica:{key}")]
    [InlineData("myriagent-replica:{key}a.png")]
    [InlineData(@"\\server\myriagent-replica:{key}/a.png")]
    public void 不是複本路徑_TryKey為false(string? path)
    {
        var key = Key();
        ReplicaMediaRules.TryKey(path?.Replace("{key}", key, StringComparison.Ordinal), out var found).Should().BeFalse();
        found.Should().BeNull();
    }

    // ── 線上的代理媒體 ──

    [Fact]
    public void 代理媒體的參照放進MediaRefs_接在附件後面_不合格的略過()
    {
        var attachmentRef = MediaTestFiles.NewId();
        var imageRef = MediaTestFiles.NewId();
        var videoRef = MediaTestFiles.NewId();
        var item = ViaWire(Source(i =>
        {
            i.Kind = AgentItemKind.User;
            i.Attachments.Add(new AgentAttachment { Id = "a1", FileName = "screen.png", StoredPath = @"C:\o\att\screen.png", Kind = AgentAttachmentKind.Image, SizeBytes = 10 });
            i.MediaPaths.AddRange([@"C:\o\out\chart.png", @"C:\o\out\clip.mp4", @"C:\o\out\no-ref.png", @"C:\o\out\notes.txt"]);
            i.MediaRefs =
            [
                new AgentMediaRef { Ref = attachmentRef, AttachmentId = "a1" },
                new AgentMediaRef { Ref = imageRef, MediaPath = @"C:\o\out\chart.png" },
                new AgentMediaRef { Ref = videoRef, MediaPath = @"C:\o\out\clip.mp4" },
                new AgentMediaRef { Ref = MediaTestFiles.NewId(), MediaPath = @"C:\o\out\notes.txt" },
            ];
        }));

        item.MediaPaths.Should().BeEmpty("WireItemReader 不轉代理媒體：沒有本機路徑");
        item.MediaRefs!.Select(r => (r.Ref, r.AttachmentId, r.MediaPath)).Should().Equal(
            (attachmentRef, "a1", null),
            (imageRef, null, "chart.png"),
            (videoRef, null, "clip.mp4"));
    }

    [Fact]
    public void 沒有代理媒體_MediaRefs不動()
    {
        var item = ViaWire(Source(_ => { }));
        item.MediaRefs.Should().BeNull();
    }

    // ── 渲染用的複本 ──

    [Fact]
    public void WithMedia_附件與代理媒體換成複本路徑_原本的不動()
    {
        var attachmentRef = MediaTestFiles.NewId();
        var imageRef = MediaTestFiles.NewId();
        var item = ViaWire(Source(i =>
        {
            i.Kind = AgentItemKind.User;
            i.Attachments.Add(new AgentAttachment { Id = "a1", FileName = "screen.png", StoredPath = @"C:\o\att\screen.png", Kind = AgentAttachmentKind.Image, SizeBytes = 10 });
            i.Attachments.Add(new AgentAttachment { Id = "a2", FileName = "沒參照.png", StoredPath = @"C:\o\att\b.png", Kind = AgentAttachmentKind.Image, SizeBytes = 10 });
            i.MediaPaths.Add(@"C:\o\out\chart.png");
            i.MediaRefs =
            [
                new AgentMediaRef { Ref = attachmentRef, AttachmentId = "a1" },
                new AgentMediaRef { Ref = imageRef, MediaPath = @"C:\o\out\chart.png" },
            ];
        }));

        var shown = ReplicaMediaRules.WithMedia(item, Owner)!;

        shown.Should().NotBeSameAs(item);
        shown.Attachments.Select(a => a.StoredPath).Should().Equal(ReplicaMediaRules.PathFor(MediaKeyRules.Format(Owner, attachmentRef), "screen.png"), string.Empty);
        shown.MediaPaths.Should().Equal(ReplicaMediaRules.PathFor(MediaKeyRules.Format(Owner, imageRef), "chart.png"));
        item.Attachments.Select(a => a.StoredPath).Should().OnlyContain(p => p.Length == 0, "共用的項目不改");
        item.MediaPaths.Should().BeEmpty();
    }

    [Fact]
    public void WithMedia_擁有者不是裝置id或沒有參照回null()
    {
        var withRef = ViaWire(Source(i =>
        {
            i.MediaPaths.Add(@"C:\o\out\chart.png");
            i.MediaRefs = [new AgentMediaRef { Ref = MediaTestFiles.NewId(), MediaPath = @"C:\o\out\chart.png" }];
        }));

        ReplicaMediaRules.WithMedia(withRef, null).Should().BeNull();
        ReplicaMediaRules.WithMedia(withRef, "d_01J9Q0000000000000000000OW").Should().BeNull("O 不是 Crockford 字元");
        ReplicaMediaRules.WithMedia(withRef, "server").Should().BeNull();
        ReplicaMediaRules.WithMedia(ViaWire(Source(_ => { })), Owner).Should().BeNull();

        // 只有不在附件上的附件參照：沒有東西可換
        var orphan = new AgentChatItem { Id = "i2", Ord = 2, Kind = AgentItemKind.User, MediaRefs = [new AgentMediaRef { Ref = MediaTestFiles.NewId(), AttachmentId = "zz" }] };
        ReplicaMediaRules.WithMedia(orphan, Owner).Should().BeNull();
    }

    [Fact]
    public void HidePaths_拿掉複本路徑_其他照舊()
    {
        var replicaPath = ReplicaMediaRules.PathFor(Key(), "a.png");
        var rendered = new JObject
        {
            ["id"] = "i1",
            ["attachments"] = new JArray(new JObject { ["path"] = replicaPath, ["name"] = "a.png" }, new JObject { ["path"] = string.Empty }),
            ["media"] = new JArray(new JObject { ["path"] = replicaPath, ["name"] = "a.png", ["url"] = "u" }, new JObject { ["path"] = @"C:\local\b.png" }),
        };

        ReplicaMediaRules.HidePaths(rendered).Should().BeSameAs(rendered);

        rendered["attachments"]![0]!.Value<string>("path").Should().BeEmpty();
        rendered["attachments"]![0]!.Value<string>("name").Should().Be("a.png");
        rendered["attachments"]![1]!.Value<string>("path").Should().BeEmpty();
        rendered["media"]![0]!["path"]!.Type.Should().Be(JTokenType.Null);
        rendered["media"]![0]!.Value<string>("url").Should().Be("u");
        rendered["media"]![1]!.Value<string>("path").Should().Be(@"C:\local\b.png", "不是複本路徑的不動（複本不會有，防呆）");
        ReplicaMediaRules.HidePaths(new JObject { ["id"] = "x" }).Should().BeEquivalentTo(new JObject { ["id"] = "x" });
    }

    // ── E9 ──

    [Fact]
    public void RequestPath_原檔沒有大小_縮圖只有160與320()
    {
        var key = Key();
        ReplicaMediaRules.RequestPath(key, MediaPurposes.Media, null).Should().Be("/api/v1/device/media/" + key + "?purpose=media");
        ReplicaMediaRules.RequestPath(key, MediaPurposes.Thumb, 160).Should().Be("/api/v1/device/media/" + key + "?purpose=thumb&s=160");
        ReplicaMediaRules.RequestPath(key, MediaPurposes.Thumb, 320).Should().Be("/api/v1/device/media/" + key + "?purpose=thumb&s=320");
        ReplicaMediaRules.RequestPath(key, MediaPurposes.Media, 160).Should().BeNull();
        ReplicaMediaRules.RequestPath(key, MediaPurposes.Thumb, null).Should().BeNull();
        ReplicaMediaRules.RequestPath(key, MediaPurposes.Thumb, 240).Should().BeNull();
        ReplicaMediaRules.RequestPath(key, "file", null).Should().BeNull();
        ReplicaMediaRules.RequestPath(key + "x", MediaPurposes.Media, null).Should().BeNull();
        ReplicaMediaRules.RequestPath(null, MediaPurposes.Media, null).Should().BeNull();
    }

    [Theory]
    [InlineData(null, "bytes=0-8388607", true)]
    [InlineData("", "bytes=0-8388607", true)]
    [InlineData("items=0-1", "bytes=0-8388607", true)]
    [InlineData("bytes=0-1,4-5", "bytes=0-8388607", true)]
    [InlineData("bytes=100-199", "bytes=100-199", false)]
    [InlineData("bytes=100-", "bytes=100-8388707", false)]
    [InlineData("bytes=0-99999999", "bytes=0-8388607", false)]
    [InlineData("bytes=10485760-20971519", "bytes=10485760-18874367", false)]
    [InlineData("bytes=-500", "bytes=-500", false)]
    public void RequestRange_一次最多8MB(string? browser, string expected, bool whole)
        => ReplicaMediaRules.RequestRange(browser).Should().Be((expected, whole));

    [Theory]
    [InlineData(0, 160)]
    [InlineData(160, 160)]
    [InlineData(161, 320)]
    [InlineData(320, 320)]
    [InlineData(1024, 320)]
    public void ThumbSize只有兩種(int size, int expected) => ReplicaMediaRules.ThumbSize(size).Should().Be(expected);
}
