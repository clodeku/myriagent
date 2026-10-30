using System.Reflection;
using Myriagent.Agents;
using Myriagent.Core;
using Myriagent.Sync;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>
/// 發布狀態的摘要（WP4.3 計畫 D11）：線上投影的摘要不含 ver、其餘每個欄位都算；來源項目的摘要照存檔格式，只有不存檔的欄位不算
/// </summary>
public sealed class PublishDigestRulesTests
{
    private static readonly JsonSerializer Wire = JsonSerializer.Create(SyncJson.Settings);

    private static JObject WireOf(object wireItem) => JObject.FromObject(wireItem, Wire);

    private static WireItem SampleWire() => new(
        Id: "item1",
        Ord: 3,
        Ver: 5,
        Kind: "Question",
        Status: "Waiting",
        CreatedAt: 1_791_580_000_000,
        UpdatedAt: 1_791_580_001_000,
        Text: "文字",
        ToolName: "工具",
        ToolKind: "bash",
        Title: "標題",
        Detail: "細節",
        Output: "輸出",
        Diff: [new AgentDiffLine("add", "+ 一行")],
        FilePath: "a.txt",
        Attachments: [new WireAttachment("att1", "a.png", "Image", 12, 2, 1.5, "0123456789ABCDEFGHJKMNPQRS")],
        Questions: [new WireQuestion("q1", "選擇", "要哪一個？", true, false, true, [new WireQuestionOption("甲", "說明", "預覽")])],
        Answers: [new WireAnswer("q1", ["（已隱藏）"])],
        Decision: "answered",
        CanAllowForSession: false,
        AllowSessionLabel: "本對話都允許",
        AnswerAsMessage: true,
        Media: [new WireMedia("0123456789ABCDEFGHJKMNPQRS", "image", "m.png")],
        Plan: [new WirePlanStep("步驟", "inProgress")],
        NoticeKind: "AppUpdate",
        RequestDigest: "rd1:abc",
        Risk: "High");

    [Fact]
    public void ver不同_摘要相同()
    {
        var item = PublishTestItems.Full();
        ConversationWire.TryItem(item, 1, out var first).Should().BeTrue();
        ConversationWire.TryItem(item, 999, out var later).Should().BeTrue();

        first!["ver"]!.Value<long>().Should().Be(1, "確認 ver 真的在 payload 裡");
        PublishDigestRules.Wire(first).Should().Be(PublishDigestRules.Wire(later!));
        PublishDigestRules.Wire(WireOf(SampleWire())).Should().Be(PublishDigestRules.Wire(WireOf(SampleWire() with { Ver = 6 })));
    }

    [Fact]
    public void Wire不改輸入()
    {
        var wire = WireOf(SampleWire());
        var before = wire.ToString(Formatting.None);

        PublishDigestRules.Wire(wire);

        wire.ToString(Formatting.None).Should().Be(before);
    }

    [Fact]
    public void WireItem每個屬性改一個值_摘要都不同()
    {
        var constructor = typeof(WireItem).GetConstructors().Single(c => c.GetParameters().Length > 1);
        var parameters = constructor.GetParameters();
        parameters.Should().HaveCount(29, "WireItem 的欄位數釘住：新增欄位時這個測試要一起看");
        var sample = SampleWire();
        var baseline = PublishDigestRules.Wire(WireOf(sample));
        var unchanged = new List<string>();

        foreach (var parameter in parameters)
        {
            var args = parameters.Select(p => typeof(WireItem).GetProperty(p.Name!)!.GetValue(sample)).ToArray();
            args[parameter.Position] = ChangedWire(args[parameter.Position], parameter.ParameterType);
            var changed = (WireItem)constructor.Invoke(args);
            if (PublishDigestRules.Wire(WireOf(changed)) == baseline)
                unchanged.Add(parameter.Name!);
        }

        unchanged.Should().Equal(new[] { "Ver" }, "摘要只不看 ver");
    }

    [Fact]
    public void Source只忽略Seq與不存檔的RequestKey()
    {
        var properties = PublishTestItems.Properties(typeof(AgentChatItem));
        properties.Should().HaveCount(35, "AgentChatItem 的屬性數釘住");
        var baseline = PublishDigestRules.Source(PublishTestItems.Full());
        var unchanged = new List<string>();

        foreach (var property in properties)
        {
            var item = PublishTestItems.Full();
            property.SetValue(item, PublishTestItems.Changed(property.GetValue(item), property.PropertyType));
            if (PublishDigestRules.Source(item) == baseline)
                unchanged.Add(property.Name);
        }

        unchanged.Should().Equal(new[] { nameof(AgentChatItem.RequestKey), nameof(AgentChatItem.Seq) },
            "只有不存進 items.json 的欄位不算（記憶體裡的項目與從檔案讀回的同一筆摘要相同，計畫附錄 B 步 2）");
        properties.Where(p => p.IsDefined(typeof(JsonIgnoreAttribute))).Select(p => p.Name)
            .Should().Equal(nameof(AgentChatItem.RequestKey), nameof(AgentChatItem.Seq));
    }

    [Fact]
    public void Source_從items_json讀回的同一筆摘要相同()
    {
        var item = PublishTestItems.Full();
        var json = JsonConvert.SerializeObject(item, JsonFile.Settings);
        var loaded = JsonConvert.DeserializeObject<AgentChatItem>(json, JsonFile.Settings)!;

        loaded.Seq.Should().Be(0, "Seq 不存檔");
        loaded.RequestKey.Should().BeNull("RequestKey 不存檔");
        PublishDigestRules.Source(loaded).Should().Be(PublishDigestRules.Source(item));
    }

    [Fact]
    public void Payload_種類或內容不同就不同()
    {
        var meta = PublishDigestRules.Payload(ConversationEventKinds.Meta, "{\"title\":\"a\"}");

        PublishDigestRules.Payload(ConversationEventKinds.Meta, "{\"title\":\"a\"}").Should().Be(meta, "同樣的輸入同樣的摘要");
        PublishDigestRules.Payload(ConversationEventKinds.Created, "{\"title\":\"a\"}").Should().NotBe(meta);
        PublishDigestRules.Payload(ConversationEventKinds.Meta, "{\"title\":\"b\"}").Should().NotBe(meta);
        PublishDigestRules.Payload(ConversationEventKinds.Item, "{}").Should().NotBe(PublishDigestRules.Payload(ConversationEventKinds.ItemRemoved, "{}"));
        PublishDigestRules.Payload(ConversationEventKinds.Item, "Removed\n{}").Should().NotBe(PublishDigestRules.Payload(ConversationEventKinds.ItemRemoved, "{}"),
            "種類與內容的切點不會混淆");
    }

    [Fact]
    public void Payload_不是對話種類丟例外()
    {
        FluentActions.Invoking(() => PublishDigestRules.Payload(SyncEventKinds.LinkUpsert, "{}")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PublishDigestRules.Payload("conv.meta\n", "{}")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => PublishDigestRules.Payload(ConversationEventKinds.Meta, null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void 摘要都是64個小寫十六進位()
    {
        new[]
        {
            PublishDigestRules.Wire(WireOf(SampleWire())),
            PublishDigestRules.Source(PublishTestItems.Full()),
            PublishDigestRules.Payload(ConversationEventKinds.Queue, "{}"),
        }.Should().AllSatisfy(digest => PayloadRules.IsSha256Hex(digest).Should().BeTrue(digest));
    }

    private static object? ChangedWire(object? value, Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            var element = type.GetGenericArguments()[0];
            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            foreach (var item in (System.Collections.IEnumerable)value!)
                list.Add(item);
            list.Add(WireSample(element));
            return list;
        }

        return PublishTestItems.Changed(value, type);
    }

    private static object WireSample(Type element)
    {
        if (element == typeof(AgentDiffLine))
            return new AgentDiffLine("del", "- 刪掉");
        if (element == typeof(WireAttachment))
            return new WireAttachment("att2", "b.pdf", "Document", 1, null, null, null);
        if (element == typeof(WireQuestion))
            return new WireQuestion("q2", null, "第二題", false, true, false, []);
        if (element == typeof(WireAnswer))
            return new WireAnswer("q2", ["乙"]);
        if (element == typeof(WireMedia))
            return new WireMedia(null, "video", "v.mp4");
        if (element == typeof(WirePlanStep))
            return new WirePlanStep("下一步", "pending");
        throw new NotSupportedException($"新的線上清單元素型別 {element}：在 WireSample 補上");
    }
}
