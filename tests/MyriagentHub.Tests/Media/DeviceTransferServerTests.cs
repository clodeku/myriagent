using Myriagent.Media;
using Myriagent.Remote;
using Myriagent.Sync;
using MyriagentHub.Media;
using MyriagentHub.Realtime;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Media;

/// <summary>
/// 電腦狀態面板的檔案傳輸在伺服器（2026-11-09，sync-protocol 14.15）：deviceBrowse／deviceFetch 輸出的投影（沒有對話、核對 requestId）、
/// fileRef 改寫成 xfer 參照、媒體查詢的 xfer（不看對話、2 小時）、型錄與瀏覽器分類
/// </summary>
public sealed class DeviceTransferServerTests
{
    private const long Now = 1_791_580_000_000;

    private static readonly string Device = SyncIds.NewDeviceId();

    private static OutputContext Context(string cmd, string requestId = "b1") => new(cmd, null, null, requestId, null);

    [Fact]
    public void deviceBrowse的files_conversationId要是null_requestId要對上()
    {
        var message = new JObject
        {
            ["type"] = "files", ["requestId"] = "b1", ["conversationId"] = JValue.CreateNull(), ["path"] = @"D:\a", ["parent"] = @"D:\",
            ["root"] = JValue.CreateNull(), ["truncated"] = false, ["error"] = JValue.CreateNull(),
            ["entries"] = new JArray(new JObject { ["name"] = "x.txt", ["path"] = @"D:\a\x.txt", ["kind"] = "file", ["size"] = 3, ["modified"] = 1, ["hidden"] = false }),
        };

        var projected = CommandOutputRules.Project(message, Context(RemoteCommandCatalog.DeviceBrowse));
        projected.Should().NotBeNull();
        projected!["conversationId"]!.Type.Should().Be(JTokenType.Null);
        projected.Value<string>("path").Should().Be(@"D:\a");

        CommandOutputRules.Project(message, Context(RemoteCommandCatalog.DeviceBrowse, "other")).Should().BeNull("requestId 對不上");
        var withConversation = (JObject)message.DeepClone();
        withConversation["conversationId"] = "0123456789abcdef0123456789abcdef";
        CommandOutputRules.Project(withConversation, Context(RemoteCommandCatalog.DeviceBrowse)).Should().BeNull("檔案傳輸沒有對話");
        var fileRef = new JObject { ["type"] = "fileRef", ["requestId"] = "b1", ["conversationId"] = JValue.CreateNull(), ["error"] = "x" };
        CommandOutputRules.Project(fileRef, Context(RemoteCommandCatalog.DeviceBrowse)).Should().BeNull("deviceBrowse 只能回 files");
    }

    [Fact]
    public void deviceFetch的fileRef改寫成xfer參照_url是f_期限最多兩小時()
    {
        var mediaRef = MediaSeed.NewRef();
        var message = new JObject
        {
            ["type"] = "fileRef", ["requestId"] = "f1", ["conversationId"] = JValue.CreateNull(), ["ref"] = mediaRef,
            ["name"] = "大檔.bin", ["size"] = 5_000_000_000L, ["expiresAt"] = Now + (long)TimeSpan.FromDays(1).TotalMilliseconds, ["error"] = JValue.CreateNull(),
        };
        var projected = CommandOutputRules.Project(message, Context(RemoteCommandCatalog.DeviceFetch, "f1"));
        projected.Should().NotBeNull();

        var (rewritten, rows) = CommandOutputRewriteRules.Rewrite(Device, RemoteCommandCatalog.DeviceFetch, null, null, projected!, Now);

        rewritten.Should().NotBeNull();
        rewritten!.Value<string>("url").Should().Be("/f/" + MediaKeyRules.Format(Device, mediaRef));
        rewritten["ref"].Should().BeNull();
        rewritten.Value<long>("expiresAt").Should().Be(Now + (long)MediaLookupRules.XferLifetime.TotalMilliseconds);
        rows.Should().ContainSingle().Which.Should().Be(new MediaRefRow(Device, mediaRef, string.Empty, MediaRefPurposes.Transfer, Now));
    }

    [Fact]
    public void deviceFetch的fileRef有error時不登記()
    {
        var message = new JObject { ["type"] = "fileRef", ["requestId"] = "f1", ["conversationId"] = JValue.CreateNull(), ["error"] = "不給" };
        var projected = CommandOutputRules.Project(message, Context(RemoteCommandCatalog.DeviceFetch, "f1"))!;

        var (rewritten, rows) = CommandOutputRewriteRules.Rewrite(Device, RemoteCommandCatalog.DeviceFetch, null, null, projected, Now);

        rows.Should().BeEmpty();
        rewritten!["url"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void deviceTree的fileTree_投影相對路徑與大小_格式不對整則丟掉()
    {
        var message = new JObject
        {
            ["type"] = "fileTree", ["requestId"] = "t1", ["conversationId"] = JValue.CreateNull(), ["root"] = @"D:\相簿", ["name"] = "相簿",
            ["files"] = new JArray(new JObject { ["path"] = "a.txt", ["size"] = 5, ["extra"] = "x" }, new JObject { ["path"] = @"2024\b.jpg", ["size"] = 2 }),
            ["dirs"] = new JArray("2024"), ["truncated"] = false, ["error"] = JValue.CreateNull(),
        };

        var projected = CommandOutputRules.Project(message, Context(RemoteCommandCatalog.DeviceTree, "t1"));

        projected.Should().NotBeNull();
        projected!["files"]!.Should().HaveCount(2);
        projected["files"]![0]!["extra"].Should().BeNull("只留 path、size");
        projected.Value<string>("name").Should().Be("相簿");
        CommandOutputRules.Project(message, Context(RemoteCommandCatalog.DeviceTree, "other")).Should().BeNull("requestId 對不上");
        var badSize = (JObject)message.DeepClone();
        badSize["files"]![0]!["size"] = -1;
        CommandOutputRules.Project(badSize, Context(RemoteCommandCatalog.DeviceTree, "t1")).Should().BeNull();
        CommandOutputRules.Project(message, Context(RemoteCommandCatalog.DeviceBrowse, "t1")).Should().BeNull("deviceBrowse 只能回 files");
    }

    [Fact]
    public void deviceFetchMany的fileRefs_每個參照改寫成f網址並登記xfer_有error的不登記()
    {
        var r1 = MediaSeed.NewRef();
        var r2 = MediaSeed.NewRef();
        var message = new JObject
        {
            ["type"] = "fileRefs", ["requestId"] = "m1", ["conversationId"] = JValue.CreateNull(), ["root"] = @"D:\相簿",
            ["expiresAt"] = Now + (long)TimeSpan.FromDays(1).TotalMilliseconds,
            ["refs"] = new JArray(
                new JObject { ["path"] = "a.txt", ["name"] = "a.txt", ["size"] = 5, ["ref"] = r1 },
                new JObject { ["path"] = "id_rsa", ["error"] = "不給" },
                new JObject { ["path"] = @"2024\b.jpg", ["name"] = "b.jpg", ["size"] = 2, ["ref"] = r2 }),
        };
        var projected = CommandOutputRules.Project(message, Context(RemoteCommandCatalog.DeviceFetchMany, "m1"));
        projected.Should().NotBeNull();

        var (rewritten, rows) = CommandOutputRewriteRules.Rewrite(Device, RemoteCommandCatalog.DeviceFetchMany, null, null, projected!, Now);

        rewritten.Should().NotBeNull();
        var refs = rewritten!["refs"]!.OfType<JObject>().ToList();
        refs[0].Value<string>("url").Should().Be("/f/" + MediaKeyRules.Format(Device, r1));
        refs[0]["ref"].Should().BeNull("參照不給瀏覽器");
        refs[1]["url"]!.Type.Should().Be(JTokenType.Null);
        refs[2].Value<string>("url").Should().Be("/f/" + MediaKeyRules.Format(Device, r2));
        rewritten.Value<long>("expiresAt").Should().Be(Now + (long)MediaLookupRules.XferLifetime.TotalMilliseconds);
        rows.Should().Equal(
            new MediaRefRow(Device, r1, string.Empty, MediaRefPurposes.Transfer, Now),
            new MediaRefRow(Device, r2, string.Empty, MediaRefPurposes.Transfer, Now));

        var tooMany = (JObject)message.DeepClone();
        tooMany["refs"] = new JArray(Enumerable.Range(0, CommandOutputFileRules.MaxRefs + 1).Select(i => new JObject { ["path"] = "f" + i, ["error"] = "x" }));
        CommandOutputRules.Project(tooMany, Context(RemoteCommandCatalog.DeviceFetchMany, "m1")).Should().BeNull();
    }

    [Fact]
    public void 媒體查詢的xfer_只給f與E11_不看對話_兩小時後到期()
    {
        MediaLookupRules.Allows(MediaEndpointKind.File, MediaRefPurposes.Transfer).Should().BeTrue();
        MediaLookupRules.Allows(MediaEndpointKind.ReplicaFile, MediaRefPurposes.Transfer).Should().BeTrue();
        MediaLookupRules.Allows(MediaEndpointKind.Media, MediaRefPurposes.Transfer).Should().BeFalse();
        MediaLookupRules.Allows(MediaEndpointKind.Thumb, MediaRefPurposes.Transfer).Should().BeFalse();
        MediaLookupRules.Miss(MediaEndpointKind.File, MediaRefPurposes.Transfer, Now, Now + 1000, null, Device).Should().BeNull("沒有對話也看得到");
        MediaLookupRules.Miss(MediaEndpointKind.File, MediaRefPurposes.Transfer, Now, Now + (long)MediaLookupRules.XferLifetime.TotalMilliseconds, null, Device)
            .Should().Be(MediaLookupMisses.Expired);
        MediaLookupRules.Miss(MediaEndpointKind.File, MediaRefPurposes.File, Now, Now + 1000, null, Device).Should().Be(MediaLookupMisses.NoConversation, "fileFetch 照舊要對話");
        MediaLookupRules.RequestPurpose(MediaEndpointKind.File, MediaRefPurposes.Transfer).Should().Be(MediaPurposes.Transfer);
        MediaLookupRules.RequestPurpose(MediaEndpointKind.ReplicaFile, MediaRefPurposes.Transfer).Should().Be(MediaPurposes.Transfer);
        MediaLookupRules.RequestPurpose(MediaEndpointKind.File, MediaRefPurposes.File).Should().Be(MediaPurposes.File);
    }

    [Fact]
    public void 三個指令是型錄的Device指令_層級2_瀏覽器轉送_桌面的四個只在本機()
    {
        foreach (var name in RemoteCommandCatalog.DeviceTransferCommands)
        {
            var spec = RemoteCommandCatalog.Find(name);
            spec.Should().NotBeNull(name);
            spec!.Route.Should().Be(RemoteRoute.Device);
            spec.BaseTier.Should().Be(2);
            BrowserCommandRules.Classify(name).Should().Be(BrowserCommandClass.NotAvailable, "第 4 階段的處理者轉給電腦");
        }

        foreach (var name in new[] { "peerXferSave", "peerXferCancel", "peerXferPause", "peerXferResume", "peerXferReveal", "xferPickFolder" })
        {
            RemoteCommandCatalog.IsLocalOnly(name).Should().BeTrue(name);
            BrowserCommandRules.Classify(name).Should().Be(BrowserCommandClass.DesktopOnly, name);
        }
    }
}
