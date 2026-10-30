using System.Globalization;
using Myriagent.Media;
using Myriagent.Sync;
using MyriagentHub.Media;
using MyriagentHub.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Media;

/// <summary>遠端檔案指令輸出的改寫規則（WP4.7 計畫 D37）：裝置只送參照，伺服器換成本站網址；裝置的網址一律丟掉</summary>
public sealed class CommandOutputRewriteRulesTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";

    private const long Now = 1_791_580_000_000;

    private static readonly string Device = SyncIds.NewDeviceId();

    private static readonly ConversationOwnership Owner = new(Conv, Device, 1, ConversationSyncPolicies.Full);

    [Fact]
    public void image帶ref_換成本站網址_拿掉ref與ver_登記list參照()
    {
        var mediaRef = MediaSeed.NewRef();
        var message = Files(Entry("logo.png", "image", mediaRef, ver: 1712345678L));

        var (rewritten, rows) = Rewrite(message);

        var key = MediaKeyRules.Format(Device, mediaRef);
        var entry = (JObject)rewritten!["entries"]![0]!;
        entry.Value<string>("url").Should().Be("/m/" + key);
        entry.Value<string>("thumbUrl").Should().Be("/t/" + key + "?s=160&v=1712345678");
        entry.ContainsKey("ref").Should().BeFalse();
        entry.ContainsKey("ver").Should().BeFalse();
        entry.Value<string>("name").Should().Be("logo.png");
        rows.Should().Equal(new MediaRefRow(Device, mediaRef, Conv, MediaRefPurposes.List, Now));
        rewritten.Value<bool>("truncated").Should().BeFalse("其他欄位原樣");
        rewritten.Value<string>("path").Should().Be("C:\\repo\\proj");
        message["entries"]![0]!.Value<string>("ref").Should().Be(mediaRef, "改寫的是複本，不動呼叫端的物件");
    }

    [Fact]
    public void 影片也有網址_字串的ver也收_同一個ref只登記一次()
    {
        var mediaRef = MediaSeed.NewRef();
        var (rewritten, rows) = Rewrite(Files(Entry("clip.mp4", "video", mediaRef, ver: "42"), Entry("copy.mp4", "video", mediaRef)));

        var key = MediaKeyRules.Format(Device, mediaRef);
        rewritten!["entries"]![0]!.Value<string>("thumbUrl").Should().Be("/t/" + key + "?s=160&v=42");
        rewritten["entries"]![1]!.Value<string>("thumbUrl").Should().Be("/t/" + key + "?s=160", "沒有 ver 就不帶 v");
        rows.Should().HaveCount(1);
    }

    [Fact]
    public void 裝置送的本機網址被拿掉()
    {
        var local = "https://myriagent.local/media?path=C%3A%5Crepo%5Cx.png";
        var withRef = Entry("x.png", "image", MediaSeed.NewRef());
        withRef["url"] = local;
        withRef["thumbUrl"] = "https://myriagent.local/thumb?path=x&size=160";
        var withoutRef = Entry("y.png", "image", null);
        withoutRef["url"] = local;
        withoutRef["thumbUrl"] = local;
        var folder = Entry("src", "dir", null);
        folder["url"] = local;

        var (rewritten, _) = Rewrite(Files(withRef, withoutRef, folder));

        rewritten!.ToString(Formatting.None).Should().NotContain("myriagent.local");
        rewritten["entries"]![1]!["url"]!.Type.Should().Be(JTokenType.Null);
        rewritten["entries"]![1]!["thumbUrl"]!.Type.Should().Be(JTokenType.Null);
        rewritten["entries"]![2]!["url"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void 參照不合格_種類或檔名不是可顯示的媒體時url是null()
    {
        var entries = new[]
        {
            Entry("a.png", "image", "not-a-ref"),
            Entry("b.png", "image", MediaSeed.NewRef().ToLowerInvariant()),
            Entry("c.txt", "code", MediaSeed.NewRef()),
            Entry("d.mp3", "audio", MediaSeed.NewRef()),
            Entry("e.psd", "image", MediaSeed.NewRef()),
            Entry("f", "dir", MediaSeed.NewRef()),
        };
        var (rewritten, rows) = Rewrite(Files(entries));

        rows.Should().BeEmpty();
        foreach (var entry in rewritten!["entries"]!.Cast<JObject>())
        {
            entry["url"]!.Type.Should().Be(JTokenType.Null, entry.Value<string>("name"));
            entry["thumbUrl"]!.Type.Should().Be(JTokenType.Null, entry.Value<string>("name"));
            entry.ContainsKey("ref").Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("12a")]
    [InlineData("-5")]
    [InlineData("")]
    [InlineData("12345678901234567890")]
    [InlineData("1.5")]
    public void ver含非數字時不帶v(string ver)
    {
        var mediaRef = MediaSeed.NewRef();
        var (rewritten, _) = Rewrite(Files(Entry("a.png", "image", mediaRef, ver: ver)));
        rewritten!["entries"]![0]!.Value<string>("thumbUrl").Should().Be("/t/" + MediaKeyRules.Format(Device, mediaRef) + "?s=160");
    }

    [Fact]
    public void ver是負數或小數時不帶v()
    {
        foreach (var ver in new JToken[] { -5, 1.5, true, new JArray() })
        {
            var mediaRef = MediaSeed.NewRef();
            var entry = Entry("a.png", "image", mediaRef);
            entry["ver"] = ver;
            var (rewritten, _) = Rewrite(Files(entry));
            rewritten!["entries"]![0]!.Value<string>("thumbUrl").Should().EndWith("?s=160", ver.ToString());
        }
    }

    [Fact]
    public void fileRef_換成f網址_expiresAt被夾住()
    {
        var mediaRef = MediaSeed.NewRef();
        var cap = Now + (long)MediaLookupRules.FileLifetime.TotalMilliseconds;

        var (late, rows) = Rewrite(FileRef(mediaRef, expiresAt: cap + 60_000));
        late!.Value<string>("url").Should().Be("/f/" + MediaKeyRules.Format(Device, mediaRef));
        late.Value<long>("expiresAt").Should().Be(cap, "裝置給的比現在加 15 分鐘晚：夾住");
        late.ContainsKey("ref").Should().BeFalse();
        late.Value<string>("name").Should().Be("report.txt");
        rows.Should().Equal(new MediaRefRow(Device, mediaRef, Conv, MediaRefPurposes.File, Now));

        Rewrite(FileRef(mediaRef, expiresAt: cap - 1)).Message!.Value<long>("expiresAt").Should().Be(cap - 1, "較早的照裝置的");
        Rewrite(FileRef(mediaRef, expiresAt: null)).Message!.Value<long>("expiresAt").Should().Be(cap, "沒給就是現在加 15 分鐘");
        var text = FileRef(mediaRef, expiresAt: null);
        text["expiresAt"] = "明天";
        Rewrite(text).Message!.Value<long>("expiresAt").Should().Be(cap);
    }

    [Fact]
    public void fileRef有error時不寫列_url是null()
    {
        var withError = FileRef(MediaSeed.NewRef(), expiresAt: Now + 1000);
        withError["error"] = "這個檔案不能從網頁讀取";
        withError["url"] = "https://myriagent.local/media?path=x";

        var (rewritten, rows) = Rewrite(withError);

        rows.Should().BeEmpty();
        rewritten!["url"]!.Type.Should().Be(JTokenType.Null);
        rewritten.Value<string>("error").Should().Be("這個檔案不能從網頁讀取");
        rewritten.ToString(Formatting.None).Should().NotContain("myriagent.local");

        var (badRef, none) = Rewrite(FileRef("not-a-ref", expiresAt: null));
        none.Should().BeEmpty();
        badRef!["url"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void 別台裝置的對話回null()
    {
        var other = new ConversationOwnership(Conv, SyncIds.NewDeviceId(), 1, ConversationSyncPolicies.Full);
        foreach (var message in new[] { Files(Entry("a.png", "image", MediaSeed.NewRef())), FileRef(MediaSeed.NewRef(), null), Search() })
        {
            var type = message.Value<string>("type");
            CommandOutputRewriteRules.Rewrite(Device, "cmd", Conv, other, message, Now).Message.Should().BeNull(type);
            CommandOutputRewriteRules.Rewrite(Device, "cmd", Conv, owner: null, message, Now).Message.Should().BeNull(type + "：對話不在");
            CommandOutputRewriteRules.Rewrite(Device, "cmd", Conv, Owner with { ConversationId = "f" + Conv[1..] }, message, Now).Message.Should().BeNull(type + "：目錄回的是別的對話");
            CommandOutputRewriteRules.Rewrite("not-a-device", "cmd", Conv, Owner, message, Now).Message.Should().BeNull(type + "：裝置 id 不合格");
            CommandOutputRewriteRules.Rewrite(Device, "cmd", Conv, Owner, message, Now).Message.Should().NotBeNull(type + "：正向對照");
        }
    }

    [Fact]
    public void 沒有對話的指令輸出不檢查擁有者_也不登記參照()
    {
        var mediaRef = MediaSeed.NewRef();
        var (files, rows) = CommandOutputRewriteRules.Rewrite(Device, "listFiles", null, null, Files(Entry("a.png", "image", mediaRef), Entry("src", "dir", null)), Now);
        files.Should().NotBeNull();
        rows.Should().BeEmpty();
        files!["entries"]![0]!["url"]!.Type.Should().Be(JTokenType.Null);

        var (fileRef, none) = CommandOutputRewriteRules.Rewrite(Device, "fileFetch", null, null, FileRef(mediaRef, null), Now);
        none.Should().BeEmpty();
        fileRef!["url"]!.Type.Should().Be(JTokenType.Null);
    }

    [Fact]
    public void 三千筆長路徑改寫後超過1MB回null()
    {
        // 裝置送的本身放得下（≤1 MB−4 KB，CommandOutput 的上限），每筆換成兩個網址後超過 1 MB
        var entries = Enumerable.Range(0, 3000).Select(i =>
        {
            var name = i.ToString("D4", CultureInfo.InvariantCulture) + "_screenshot.png";
            var entry = Entry(name, "image", MediaSeed.NewRef(), ver: 1712345678L);
            entry["path"] = "C:\\repo\\" + new string('d', 90) + "\\" + name;
            return entry;
        }).ToArray();
        var message = Files(entries);
        CommandOutputRewriteRules.Utf8Length(message).Should().BeInRange(800_000, CommandOutputRewriteRules.MaxMessageBytes - 4096, "樣本要接近上限但裝置送得出來");

        var (rewritten, rows) = Rewrite(message);

        rewritten.Should().BeNull();
        rows.Should().BeEmpty("丟掉的不登記");
        var (small, _) = Rewrite(Files(entries.Take(100).ToArray()));
        small.Should().NotBeNull("正向對照：少一點就放得下");
    }

    [Fact]
    public void fileSearch_toast與不認得的type原樣回同一個物件()
    {
        foreach (var message in new[] { Search(), new JObject { ["type"] = "toast", ["text"] = "完成" }, new JObject { ["type"] = "futureType", ["x"] = 1 }, new JObject { ["x"] = 1 } })
        {
            var before = message.ToString(Formatting.None);
            var (rewritten, rows) = Rewrite(message);
            ReferenceEquals(rewritten, message).Should().BeTrue(before);
            rewritten!.ToString(Formatting.None).Should().Be(before);
            rows.Should().BeEmpty();
        }
    }

    [Fact]
    public void entries不是陣列或含非物件時照樣改寫其他部分()
    {
        var odd = Files();
        odd["entries"] = "不是陣列";
        Rewrite(odd).Message!.Value<string>("entries").Should().Be("不是陣列");

        var mixed = Files(Entry("a.png", "image", MediaSeed.NewRef()));
        ((JArray)mixed["entries"]!).Add("字串");
        var (rewritten, rows) = Rewrite(mixed);
        rows.Should().HaveCount(1);
        rewritten!["entries"]![1]!.Value<string>().Should().Be("字串");
    }

    private static (JObject? Message, IReadOnlyList<MediaRefRow> Rows) Rewrite(JObject message) =>
        CommandOutputRewriteRules.Rewrite(Device, "listFiles", Conv, Owner, message, Now);

    private static JObject Files(params JObject[] entries) => new()
    {
        ["type"] = "files",
        ["requestId"] = "r12",
        ["conversationId"] = Conv,
        ["path"] = "C:\\repo\\proj",
        ["parent"] = null,
        ["root"] = "C:\\repo\\proj",
        ["entries"] = new JArray(entries.Cast<object>().ToArray()),
        ["truncated"] = false,
        ["error"] = null,
    };

    private static JObject Entry(string name, string kind, string? mediaRef, JToken? ver = null)
    {
        var entry = new JObject
        {
            ["name"] = name,
            ["path"] = "C:\\repo\\proj\\" + name,
            ["kind"] = kind,
            ["size"] = kind == "dir" ? null : 48213,
            ["modified"] = 1_791_580_000_000L,
            ["hidden"] = false,
        };
        if (mediaRef is not null)
            entry["ref"] = mediaRef;
        if (ver is not null)
            entry["ver"] = ver;
        return entry;
    }

    private static JObject FileRef(string mediaRef, long? expiresAt)
    {
        var message = new JObject
        {
            ["type"] = "fileRef",
            ["requestId"] = "r14",
            ["conversationId"] = Conv,
            ["ref"] = mediaRef,
            ["name"] = "report.txt",
            ["size"] = 18233,
            ["error"] = null,
        };
        if (expiresAt is not null)
            message["expiresAt"] = expiresAt.Value;
        return message;
    }

    private static JObject Search() => new()
    {
        ["type"] = "fileSearch",
        ["requestId"] = "r13",
        ["conversationId"] = Conv,
        ["query"] = "app",
        ["results"] = new JArray(new JObject { ["relativePath"] = "src\\app.js", ["fullPath"] = "C:\\repo\\proj\\src\\app.js", ["kind"] = "file" }),
    };
}
