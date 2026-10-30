using System.Text;
using Myriagent.Sync;
using MyriagentHub.Media;
using Newtonsoft.Json.Linq;
using K = Myriagent.Sync.ConversationEventKinds;

namespace MyriagentHub.Tests.Media;

/// <summary>媒體參照索引的純函式規則（WP4.7 計畫 D6、D8）</summary>
public sealed class MediaRefIndexRulesTests
{
    private const string Conv = "0123456789abcdef0123456789abcdef";

    private static readonly string Device = SyncIds.NewDeviceId();

    private static readonly string RefA = MediaSeed.NewRef();

    private static readonly string RefB = MediaSeed.NewRef();

    private static byte[]? NoBlob(ConversationBlobRef _) => throw new InvalidOperationException("內嵌的陣列不該讀 blob");

    [Theory]
    [InlineData(K.Item, null, nameof(MediaIndexAction.Upsert))]
    [InlineData(K.ItemRemoved, null, nameof(MediaIndexAction.RemoveItem))]
    [InlineData(K.Deleted, null, nameof(MediaIndexAction.PurgeConversation))]
    [InlineData(K.Created, "full", nameof(MediaIndexAction.Ignore))]
    [InlineData(K.Created, null, nameof(MediaIndexAction.Ignore))]
    [InlineData(K.Meta, "full", nameof(MediaIndexAction.Ignore))]
    [InlineData(K.Meta, "metadata", nameof(MediaIndexAction.PurgeConversation))]
    [InlineData(K.Meta, "paused", nameof(MediaIndexAction.PurgeConversation))]
    [InlineData(K.Meta, "localOnly", nameof(MediaIndexAction.PurgeConversation))]
    [InlineData(K.Meta, "未來的值", nameof(MediaIndexAction.PurgeConversation))]
    [InlineData(K.Created, "metadata", nameof(MediaIndexAction.PurgeConversation))]
    [InlineData(K.Turn, null, nameof(MediaIndexAction.Ignore))]
    [InlineData(K.Queue, null, nameof(MediaIndexAction.Ignore))]
    [InlineData(K.Attention, null, nameof(MediaIndexAction.Ignore))]
    [InlineData("conv.unknown", "metadata", nameof(MediaIndexAction.Ignore))]
    public void ForEvent_依種類與政策(string kind, string? policy, string expected) =>
        MediaRefIndexRules.ForEvent(kind, policy).Should().Be(Enum.Parse<MediaIndexAction>(expected));

    [Fact]
    public void FromItemPayload_附件與媒體的ref各一列_同一個只列一次()
    {
        var payload = Item("i1");
        payload["attachments"] = MediaSeed.Attachments([RefA, RefA]);
        payload["media"] = new JArray(new JObject { ["kind"] = "image", ["name"] = "a.png", ["ref"] = RefB }, new JObject { ["kind"] = "image", ["name"] = "b.png", ["ref"] = RefA });

        var rows = MediaRefIndexRules.FromItemPayload(Device, Conv, payload, 1234, NoBlob);

        rows.Should().Equal(new MediaRefRow(Device, RefA, Conv, "item:i1", 1234), new MediaRefRow(Device, RefB, Conv, "item:i1", 1234));
    }

    [Fact]
    public void FromItemPayload_沒有ref與不合格的ref略過()
    {
        var payload = Item("i1");
        payload["attachments"] = new JArray(
            new JObject { ["id"] = "a", ["name"] = "x.pdf", ["kind"] = "Document", ["size"] = 1 },
            new JObject { ["id"] = "b", ["name"] = "y.png", ["kind"] = "Image", ["size"] = 1, ["ref"] = "not-a-ref" },
            new JObject { ["id"] = "c", ["name"] = "z.png", ["kind"] = "Image", ["size"] = 1, ["ref"] = 12 },
            "字串不是物件");
        payload["media"] = new JObject { ["kind"] = "image" };

        MediaRefIndexRules.FromItemPayload(Device, Conv, payload, 1, NoBlob).Should().BeEmpty();
    }

    [Fact]
    public void FromItemPayload_作者對話或項目id不合格回空的()
    {
        var payload = Item("i1");
        payload["attachments"] = MediaSeed.Attachments([RefA]);

        MediaRefIndexRules.FromItemPayload("not-a-device", Conv, payload, 1, NoBlob).Should().BeEmpty();
        MediaRefIndexRules.FromItemPayload(Device, "ABCDEF", payload, 1, NoBlob).Should().BeEmpty();
        MediaRefIndexRules.FromItemPayload(Device, Conv, Item("有/斜線"), 1, NoBlob).Should().BeEmpty();
        MediaRefIndexRules.FromItemPayload(Device, Conv, new JObject { ["attachments"] = MediaSeed.Attachments([RefA]) }, 1, NoBlob).Should().BeEmpty();
    }

    [Fact]
    public void FromItemPayload_attachments是blob參照時讀blob取ref()
    {
        var content = Encoding.UTF8.GetBytes(MediaSeed.Attachments([RefA, RefB]).ToString(Newtonsoft.Json.Formatting.None));
        var sha = MediaBytes.Sha(content);
        var payload = Item("i1");
        payload["attachments"] = new JObject { ["blob"] = sha, ["length"] = content.LongLength };
        var asked = new List<ConversationBlobRef>();

        var rows = MediaRefIndexRules.FromItemPayload(Device, Conv, payload, 5, r =>
        {
            asked.Add(r);
            return content;
        });

        asked.Should().Equal(new ConversationBlobRef(sha, content.LongLength));
        rows.Select(r => r.Ref).Should().Equal(RefA, RefB);
    }

    [Fact]
    public void FromItemPayload_blob讀不到_長度不符_不是UTF8_不是陣列時略過那欄()
    {
        var content = Encoding.UTF8.GetBytes(MediaSeed.Attachments([RefA]).ToString(Newtonsoft.Json.Formatting.None));

        // 讀不到：那欄略過，另一欄照收
        Refs(content, _ => null).Should().Equal(RefB);
        // 長度和參照的不符
        Refs(content, _ => content[..^1]).Should().Equal(RefB);
        // 不是嚴格 UTF-8（長度相符）
        var broken = (byte[])content.Clone();
        broken[2] = 0xFF;
        Refs(content, _ => broken).Should().Equal(RefB);
        // JSON 物件不是陣列
        var obj = Encoding.UTF8.GetBytes("{\"ref\":\"" + RefA + "\"}");
        Refs(obj, _ => obj).Should().Equal(RefB);
        // 對照：讀得到就兩欄都有
        Refs(content, _ => content).Should().Equal(RefA, RefB);
    }

    /// <summary>attachments 換成 <paramref name="declared"/> 的 blob 參照、media 內嵌 <see cref="RefB"/>，回傳索引出的參照</summary>
    private static IEnumerable<string> Refs(byte[] declared, Func<ConversationBlobRef, byte[]?> readBlob)
    {
        var payload = Item("i1");
        payload["attachments"] = new JObject { ["blob"] = MediaBytes.Sha(declared), ["length"] = declared.LongLength };
        payload["media"] = new JArray(new JObject { ["kind"] = "image", ["name"] = "a.png", ["ref"] = RefB });
        return MediaRefIndexRules.FromItemPayload(Device, Conv, payload, 1, readBlob).Select(r => r.Ref).ToList();
    }

    [Fact]
    public void BlobShas_只列blob參照的欄位_同一個只列一次()
    {
        var a = new string('a', 64);
        var b = new string('b', 64);
        var payload = Item("i1");
        payload["text"] = new JObject { ["blob"] = a, ["length"] = 10 };
        payload["output"] = new JObject { ["blob"] = b, ["length"] = 20 };
        payload["detail"] = new JObject { ["blob"] = a, ["length"] = 10 };
        payload["diff"] = new JArray();
        payload["title"] = "一般字串";

        MediaRefIndexRules.BlobShas(payload).Should().Equal(a, b);
        MediaRefIndexRules.BlobShas(Item("i2")).Should().BeEmpty();
    }

    [Fact]
    public void ItemBlobRefId_與對話id互轉()
    {
        var refId = MediaRefIndexRules.ItemBlobRefId(Conv, "item-1");
        refId.Should().Be(Conv + ":item-1");
        MediaRefIndexRules.ConversationOfItemBlobRef(refId).Should().Be(Conv);
        MediaRefIndexRules.ConversationOfItemBlobRef(Conv).Should().BeNull("沒有分隔字元與項目 id");
        MediaRefIndexRules.ConversationOfItemBlobRef(Conv + ":").Should().BeNull();
        MediaRefIndexRules.ConversationOfItemBlobRef(Conv.ToUpperInvariant() + ":x").Should().BeNull();
        MediaRefIndexRules.ConversationOfItemBlobRef("upload@123").Should().BeNull();
        MediaRefIndexRules.ConversationOfItemBlobRef(null).Should().BeNull();
        MediaRefIndexRules.ItemPurpose("abc").Should().Be("item:abc");
    }

    [Theory]
    [InlineData(null, 200)]
    [InlineData("", 200)]
    [InlineData("abc", 200)]
    [InlineData("-5", 200)]
    [InlineData("0", 1)]
    [InlineData("1", 1)]
    [InlineData("50", 50)]
    [InlineData("200", 200)]
    [InlineData("100000", 200)]
    public void MissingLimit_預設200_夾在1到200(string? value, int expected) => MediaRefIndexRules.MissingLimit(value).Should().Be(expected);

    [Fact]
    public void 游標往返_不合格的回false()
    {
        var cursor = new MissingThumbCursor(1_760_000_000_123, RefA);
        var text = MediaRefIndexRules.FormatCursor(cursor);
        text.Should().Be("1760000000123_" + RefA);
        MediaRefIndexRules.TryParseCursor(text, out var parsed).Should().BeTrue();
        parsed.Should().Be(cursor);

        MediaRefIndexRules.TryParseCursor(null, out var none).Should().BeTrue();
        none.Should().BeNull();
        MediaRefIndexRules.TryParseCursor(string.Empty, out none).Should().BeTrue();
        none.Should().BeNull();
        foreach (var bad in new[] { "_" + RefA, "abc_" + RefA, "-1_" + RefA, "123_", "123_notaref", "123", "12345678901234567890_" + RefA, " 1_" + RefA })
            MediaRefIndexRules.TryParseCursor(bad, out _).Should().BeFalse(bad);
    }

    private static JObject Item(string id) => new() { ["id"] = id, ["ord"] = 1, ["ver"] = 1, ["kind"] = "Assistant", ["status"] = "Done" };
}
