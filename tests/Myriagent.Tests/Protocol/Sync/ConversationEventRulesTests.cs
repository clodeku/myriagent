using System.Security.Cryptography;
using System.Text;
using Myriagent.Agents;
using Myriagent.Sync;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Protocol.Sync;

/// <summary>
/// 第 4 階段對話事件的種類、placement、識別碼、同步政策與共用驗證（WP4.1 步 3；docs/sync-protocol.md 14.2、設計 14.11 D30～D34、D37、D38）。
/// 裝置附加進 outbox 之前與伺服器收到時用同一份 Validate，所以正例（合格的一定收）與反例（不合格的一定擋）都要固定
/// </summary>
public sealed class ConversationEventRulesTests
{
    private const string ConvId = "0f3c9a52b7e94d0c8a61f2d4e5b6c7d8";

    // ───────────────────────────── 種類、類別與串流 ─────────────────────────────

    [Fact]
    public void 種類的類別_照SP14_2的表()
    {
        ConversationEventKinds.All.Should().Equal(
            "conv.created", "conv.meta", "conv.turn", "conv.attention", "conv.deleted", "conv.item", "conv.itemRemoved", "conv.queue", "conv.lease");
        foreach (var kind in new[] { ConversationEventKinds.Created, ConversationEventKinds.Meta, ConversationEventKinds.Turn, ConversationEventKinds.Attention, ConversationEventKinds.Deleted, ConversationEventKinds.Lease })
            ConversationEventKinds.ClassOf(kind).Should().Be(SyncClasses.Meta, kind);
        foreach (var kind in new[] { ConversationEventKinds.Item, ConversationEventKinds.ItemRemoved, ConversationEventKinds.Queue })
            ConversationEventKinds.ClassOf(kind).Should().Be(SyncClasses.Item, kind);
        ConversationEventKinds.ClassOf(SyncEventKinds.LinkUpsert).Should().BeNull();
        ConversationEventKinds.ClassOf("conv.forked").Should().BeNull("第 5 階段才有");
        ConversationEventKinds.ClassOf(null).Should().BeNull();
    }

    [Fact]
    public void Placement要對話id_串流是c加id_第3階段的Placement照舊不認得()
    {
        foreach (var kind in ConversationEventKinds.All)
        {
            ConversationEventKinds.Placement(kind, ConvId).Should().Be((ConversationEventKinds.ClassOf(kind)!, "c:" + ConvId), kind);
            ConversationEventKinds.Placement(kind, ConvId.ToUpperInvariant()).Should().BeNull("線上收到大寫一律拒絕（D38）");
            ConversationEventKinds.Placement(kind, null).Should().BeNull();
            SyncEventKinds.Placement(kind).Should().BeNull("第 3 階段的 Placement 不改（SyncWireFormatTests）");
            SyncEventKinds.IsDeviceAuthored(kind).Should().BeFalse("WP4.2 才讓伺服器收對話事件");
        }

        ConversationEventKinds.Placement(SyncEventKinds.LinkUpsert, ConvId).Should().BeNull();
    }

    [Theory]
    [InlineData(ConvId, true)]
    [InlineData("0F3C9A52B7E94D0C8A61F2D4E5B6C7D8", false)]
    [InlineData("0f3c9a52b7e94d0c8a61f2d4e5b6c7d", false)]
    [InlineData("0f3c9a52b7e94d0c8a61f2d4e5b6c7d8a", false)]
    [InlineData("0f3c9a52b7e94d0c8a61f2d4e5b6c7dg", false)]
    [InlineData("0f3c9a52-b7e9-4d0c-8a61-f2d4e5b6", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 線上的對話id只收32個小寫十六進位(string? id, bool expected)
        => ConversationIds.IsWireId(id).Should().Be(expected);

    [Fact]
    public void 本機資料夾名大小寫都收_送出前轉小寫()
    {
        ConversationIds.ToWire(ConvId).Should().Be(ConvId);
        ConversationIds.ToWire("0F3C9A52B7E94D0C8A61F2D4E5B6C7D8").Should().Be(ConvId);
        ConversationIds.ToWire("0f3c9a52b7e94d0c8a61f2d4e5b6c7dZ").Should().BeNull();
        ConversationIds.ToWire("..\\0f3c9a52b7e94d0c8a61f2d4e5b6c7").Should().BeNull();
        ConversationIds.ToWire(null).Should().BeNull();
    }

    [Theory]
    [InlineData("0f3c9a52b7e94d0c8a61f2d4e5b6c7d8", true)]
    [InlineData("text-01-user-plain", true)]
    [InlineData("A_b-9", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("..", false)]
    [InlineData("項目", false)]
    [InlineData(null, false)]
    public void 項目id_英數字連字號底線(string? id, bool expected)
        => ConversationIds.IsItemId(id).Should().Be(expected);

    [Fact]
    public void 項目id的長度上限是64()
    {
        ConversationIds.IsItemId(new string('a', 64)).Should().BeTrue();
        ConversationIds.IsItemId(new string('a', 65)).Should().BeFalse();
    }

    [Fact]
    public void Epoch至少是1_沒有租約時是1()
    {
        ConversationIds.IsEpoch(1).Should().BeTrue();
        ConversationIds.IsEpoch(7).Should().BeTrue();
        ConversationIds.IsEpoch(0).Should().BeFalse();
        ConversationIds.IsEpoch(-1).Should().BeFalse();
        ConversationIds.IsEpoch(null).Should().BeFalse();
        ConversationWire.EpochOf(new AgentConversation()).Should().Be(1);
        ConversationWire.EpochOf(new AgentConversation { LeaseEpoch = 3 }).Should().Be(3);
    }

    // ───────────────────────────── 同步政策 ─────────────────────────────

    [Theory]
    [InlineData(null, "full")]
    [InlineData("full", "full")]
    [InlineData("metadata", "metadata")]
    [InlineData("localOnly", "localOnly")]
    [InlineData("paused", "paused")]
    [InlineData("Full", "localOnly")]
    [InlineData("LocalOnly", "localOnly")]
    [InlineData("public", "localOnly")]
    [InlineData("", "localOnly")]
    public void 同步政策_null是full_不認得的是最嚴格的localOnly(string? stored, string expected)
    {
        ConversationSyncPolicies.Interpret(stored).Should().Be(expected);
        ConversationSyncPolicies.Effective(stored, providerSyncable: true).Should().Be(expected);
        ConversationSyncPolicies.Effective(stored, providerSyncable: false).Should().Be("localOnly", "不進同步的代理種類一律 localOnly（D39）");
    }

    [Fact]
    public void 同步政策的發布範圍_full全部_metadata與paused只有meta類_localOnly什麼都不發()
    {
        ConversationSyncPolicies.All.Should().Equal("full", "metadata", "localOnly", "paused");
        foreach (var kind in ConversationEventKinds.All)
        {
            var meta = ConversationEventKinds.ClassOf(kind) == SyncClasses.Meta;
            ConversationSyncPolicies.Publishes("full", kind).Should().BeTrue(kind);
            ConversationSyncPolicies.Publishes("metadata", kind).Should().Be(meta, kind);
            ConversationSyncPolicies.Publishes("paused", kind).Should().Be(meta, kind);
            ConversationSyncPolicies.Publishes("localOnly", kind).Should().BeFalse("連 conv.created 都沒有");
            ConversationSyncPolicies.Publishes("Full", kind).Should().BeFalse("沒經 Interpret 的值不發");
            ConversationSyncPolicies.Publishes(null, kind).Should().BeFalse();
        }

        ConversationSyncPolicies.Publishes("full", SyncEventKinds.LinkUpsert).Should().BeFalse("不是對話種類");
    }

    // ───────────────────────────── 事件外框 ─────────────────────────────

    [Fact]
    public void 事件外框_種類id與epoch與串流要一致()
    {
        ConversationEventRules.CheckEvent(ConversationEventKinds.Item, "c:" + ConvId, ConvId, 1).Should().BeNull();
        ConversationEventRules.CheckEvent(ConversationEventKinds.Created, "c:" + ConvId, ConvId, 4).Should().BeNull();
        ConversationEventRules.CheckEvent(ConversationEventKinds.Item, "catalog", ConvId, 1).Should().Be("invalid");
        ConversationEventRules.CheckEvent(ConversationEventKinds.Item, "c:" + ConvId.ToUpperInvariant(), ConvId.ToUpperInvariant(), 1).Should().Be("invalid");
        ConversationEventRules.CheckEvent(ConversationEventKinds.Item, "c:" + ConvId, ConvId, 0).Should().Be("invalid");
        ConversationEventRules.CheckEvent(ConversationEventKinds.Item, "c:" + ConvId, ConvId, null).Should().Be("invalid");
        ConversationEventRules.CheckEvent(ConversationEventKinds.Item, "c:" + ConvId, null, 1).Should().Be("invalid");
        ConversationEventRules.CheckEvent(SyncEventKinds.LinkUpsert, "c:" + ConvId, ConvId, 1).Should().Be("invalid");
        ConversationEventRules.CheckEvent(ConversationEventKinds.Item, "c:0f3c9a52b7e94d0c8a61f2d4e5b6c7d9", ConvId, 1).Should().Be("invalid", "串流與對話 id 不符");
    }

    // ───────────────────────────── 共用的解析與大小 ─────────────────────────────

    [Fact]
    public void 上限與伺服器宣告的一致()
    {
        ConversationEventRules.MaxPayloadBytes.Should().Be(SyncLimits.Default.MaxInlinePayload, "sync-protocol 5.4 的解碼上限");
        ConversationEventRules.MaxInlineStringBytes.Should().Be(32 * 1024);
    }

    [Theory]
    [InlineData("""{"id":"a","id":"b"}""")]
    [InlineData("""{"id":"a"} // 註解""")]
    [InlineData("""/* 註解 */ {"id":"a"}""")]
    [InlineData("""{"id":/* 註解 */"a"}""")]
    [InlineData("""{"id":"a"} {"id":"b"}""")]
    [InlineData("""{"id":"a"} x""")]
    [InlineData("""["id"]""")]
    [InlineData("\"a\"")]
    [InlineData("")]
    [InlineData("{")]
    public void 只收一種解讀_重複鍵註解與多餘內容一律拒絕(string json)
        => ConversationEventRules.Validate(ConversationEventKinds.ItemRemoved, json).Should().Be("invalid");

    [Fact]
    public void 深度超過32拒絕()
    {
        var ok = Nest(31);
        var deep = Nest(33);

        ConversationEventRules.Validate(ConversationEventKinds.Deleted, ok).Should().BeNull("未知欄位照舊忽略");
        ConversationEventRules.Validate(ConversationEventKinds.Deleted, deep).Should().Be("invalid");

        static string Nest(int depth) => string.Concat(Enumerable.Repeat("{\"a\":", depth - 1)) + "{}" + new string('}', depth - 1);
    }

    [Fact]
    public void 整筆超過128KB或內嵌字串超過32KB是too_large()
    {
        var inline = new string('x', ConversationEventRules.MaxInlineStringBytes);
        ConversationEventRules.Validate(ConversationEventKinds.Deleted, new JObject { ["note"] = inline }.ToString()).Should().BeNull("剛好 32 KB 可以");
        ConversationEventRules.Validate(ConversationEventKinds.Deleted, new JObject { ["note"] = inline + "x" }.ToString()).Should().Be("too_large");
        ConversationEventRules.Validate(ConversationEventKinds.Deleted, new JObject { ["note"] = new string('字', 11_000) }.ToString())
            .Should().Be("too_large", "以 UTF-8 位元組算：一萬一千個中文字是 33,000 位元組");

        var many = new JObject();
        for (int i = 0; i < 5; i++)
            many["n" + i] = new string('x', 30 * 1024);
        ConversationEventRules.Validate(ConversationEventKinds.Deleted, many.ToString(Newtonsoft.Json.Formatting.None)).Should().Be("too_large");
        ConversationEventRules.Validate(ConversationEventKinds.Deleted, null).Should().Be("invalid");
        ConversationEventRules.Validate("link.upsert", "{}").Should().Be("invalid", "不是對話事件");
    }

    [Fact]
    public void 原因只說哪裡不對_不含payload的內容()
    {
        ConversationEventRules.Validate(ConversationEventKinds.ItemRemoved, """{"id":"秘密 sk-不該出現"}""", out var detail).Should().Be("invalid");
        detail.Should().Be("id 不合格");
    }

    // ───────────────────────────── 每個種類的正反例 ─────────────────────────────

    [Fact]
    public void Meta_正例()
    {
        foreach (var kind in new[] { ConversationEventKinds.Created, ConversationEventKinds.Meta })
        {
            Check(kind, Meta()).Should().BeNull(kind);
            Check(kind, Meta(o => o.Remove("localAccountId"))).Should().BeNull("帳戶不明（meta.json 重建）");
            Check(kind, Meta(o => o.Remove("permissionMode"))).Should().BeNull("存檔裡是沒定義的數值");
            Check(kind, Meta(o => { o["model"] = "opus"; o["effort"] = "high"; })).Should().BeNull();
            Check(kind, Meta(o => { o["customFolder"] = true; o["workingDirectory"] = @"C:\repo\proj"; })).Should().BeNull();
            Check(kind, Meta(o => o["customFolder"] = true)).Should().BeNull("路徑太長時只說是自訂資料夾");
            Check(kind, Meta(o => o["syncPolicy"] = "metadata")).Should().BeNull();
            Check(kind, Meta(o => o["syncPolicy"] = "paused")).Should().BeNull();
            Check(kind, Meta(o => o["future"] = new JObject { ["x"] = 1 })).Should().BeNull("未知欄位忽略");
            Check(kind, Meta(o => o["title"] = new string('題', ConversationEventRules.MaxTitleLength))).Should().BeNull();
            foreach (var provider in new[] { "Claude", "Codex", "Grok", "Antigravity" })
                Check(kind, Meta(o => o["provider"] = provider)).Should().BeNull($"{provider}（prov1 起四種都同步）");
        }
    }

    [Theory]
    [InlineData("provider", "\"Gemini\"")]
    [InlineData("provider", "\"grok\"")]
    [InlineData("provider", "\"claude\"")]
    [InlineData("provider", null)]
    [InlineData("localAccountId", "\"a/b\"")]
    [InlineData("localAccountId", "\"\"")]
    [InlineData("title", null)]
    [InlineData("title", "1")]
    [InlineData("permissionMode", "\"ask\"")]
    [InlineData("permissionMode", "\"5\"")]
    [InlineData("permissionMode", "4")]
    [InlineData("model", "\"\"")]
    [InlineData("customFolder", null)]
    [InlineData("customFolder", "\"false\"")]
    [InlineData("workingDirectory", "\"C:\\\\repo\"")]
    [InlineData("syncPolicy", "\"localOnly\"")]
    [InlineData("syncPolicy", "\"Full\"")]
    [InlineData("syncPolicy", null)]
    [InlineData("createdAt", "-1")]
    [InlineData("createdAt", "1.5")]
    [InlineData("updatedAt", "\"1791580000000\"")]
    [InlineData("running", null)]
    [InlineData("waiting", "0")]
    public void Meta_反例(string field, string? json)
    {
        var payload = Meta(o =>
        {
            if (json == null)
                o.Remove(field);
            else
                o[field] = JToken.Parse(json);
        });

        Check(ConversationEventKinds.Created, payload).Should().Be("invalid", $"{field}={json ?? "（沒有）"}");
        Check(ConversationEventKinds.Meta, payload).Should().Be("invalid", $"{field}={json ?? "（沒有）"}");
    }

    [Fact]
    public void Meta_models_正例()
    {
        var models = new JArray
        {
            new JObject { ["value"] = "opus", ["label"] = "Opus", ["description"] = "複雜工作", ["efforts"] = new JArray("low", "high") },
            new JObject { ["value"] = "haiku", ["label"] = "Haiku" },
            new JObject { ["value"] = "x", ["label"] = "x", ["description"] = string.Empty, ["efforts"] = new JArray(), ["future"] = 1 },
        };
        Check(ConversationEventKinds.Meta, Meta(o => o["models"] = models)).Should().BeNull("每項裡不認得的欄位照舊忽略");
        Check(ConversationEventKinds.Created, Meta(o => o["models"] = new JArray())).Should().BeNull();
        Check(ConversationEventKinds.Meta, Meta(o => o["models"] = JValue.CreateNull())).Should().BeNull();
        var full = new JArray(Enumerable.Range(0, ConversationEventRules.MaxModelOptions).Select(i => new JObject
        {
            ["value"] = new string('v', ConversationEventRules.MaxShortTextLength - 3) + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture),
            ["label"] = new string('名', ConversationEventRules.MaxShortTextLength),
            ["description"] = new string('說', ConversationEventRules.MaxModelDescriptionLength),
            ["efforts"] = new JArray(Enumerable.Range(0, ConversationEventRules.MaxModelEfforts).Select(e => "e" + e)),
        }));
        Check(ConversationEventKinds.Meta, Meta(o => o["models"] = full)).Should().BeNull("每個上限剛好");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("\"opus\"")]
    [InlineData("[\"opus\"]")]
    [InlineData("[{\"label\":\"Opus\"}]")]
    [InlineData("[{\"value\":\"\",\"label\":\"Opus\"}]")]
    [InlineData("[{\"value\":1,\"label\":\"Opus\"}]")]
    [InlineData("[{\"value\":\"opus\"}]")]
    [InlineData("[{\"value\":\"opus\",\"label\":\"\"}]")]
    [InlineData("[{\"value\":\"opus\",\"label\":\"Opus\",\"description\":1}]")]
    [InlineData("[{\"value\":\"opus\",\"label\":\"Opus\",\"efforts\":\"high\"}]")]
    [InlineData("[{\"value\":\"opus\",\"label\":\"Opus\",\"efforts\":[\"\"]}]")]
    [InlineData("[{\"value\":\"opus\",\"label\":\"Opus\",\"efforts\":[1]}]")]
    public void Meta_models_反例(string json)
    {
        Check(ConversationEventKinds.Meta, Meta(o => o["models"] = JToken.Parse(json))).Should().Be("invalid", json);
        Check(ConversationEventKinds.Created, Meta(o => o["models"] = JToken.Parse(json))).Should().Be("invalid", json);
    }

    [Fact]
    public void Meta_models_超過上限拒絕()
    {
        static JObject Option(Action<JObject>? edit = null)
        {
            var o = new JObject { ["value"] = "opus", ["label"] = "Opus" };
            edit?.Invoke(o);
            return o;
        }

        Check(ConversationEventKinds.Meta, Meta(o => o["models"] = new JArray(Enumerable.Range(0, ConversationEventRules.MaxModelOptions + 1).Select(_ => Option()))))
            .Should().Be("invalid");
        Check(ConversationEventKinds.Meta, Meta(o => o["models"] = new JArray(Option(m => m["value"] = new string('v', ConversationEventRules.MaxShortTextLength + 1)))))
            .Should().Be("invalid");
        Check(ConversationEventKinds.Meta, Meta(o => o["models"] = new JArray(Option(m => m["label"] = new string('名', ConversationEventRules.MaxShortTextLength + 1)))))
            .Should().Be("invalid");
        Check(ConversationEventKinds.Meta, Meta(o => o["models"] = new JArray(Option(m => m["description"] = new string('說', ConversationEventRules.MaxModelDescriptionLength + 1)))))
            .Should().Be("invalid");
        Check(ConversationEventKinds.Meta, Meta(o => o["models"] = new JArray(Option(m => m["efforts"] = new JArray(Enumerable.Range(0, ConversationEventRules.MaxModelEfforts + 1).Select(e => "e" + e))))))
            .Should().Be("invalid");
    }

    [Fact]
    public void Meta_標題超過上限與模型過長拒絕()
    {
        Check(ConversationEventKinds.Meta, Meta(o => o["title"] = new string('題', ConversationEventRules.MaxTitleLength + 1))).Should().Be("invalid");
        Check(ConversationEventKinds.Meta, Meta(o => o["model"] = new string('m', ConversationEventRules.MaxShortTextLength + 1))).Should().Be("invalid");
        Check(ConversationEventKinds.Meta, Meta(o => { o["customFolder"] = true; o["workingDirectory"] = new string('p', ConversationEventRules.MaxPathLength + 1); }))
            .Should().Be("invalid");
    }

    [Fact]
    public void Turn_正反例()
    {
        Check(ConversationEventKinds.Turn, """{"phase":"started"}""").Should().BeNull();
        Check(ConversationEventKinds.Turn, """{"phase":"completed","outcome":"completed"}""").Should().BeNull();
        Check(ConversationEventKinds.Turn, """{"phase":"completed","outcome":"interrupted","usage":{"inputTokens":12345,"outputTokens":2100,"cachedInputTokens":0}}""").Should().BeNull();
        Check(ConversationEventKinds.Turn, """{"phase":"completed","outcome":"failed","usage":{"inputTokens":1,"outputTokens":2,"cachedInputTokens":3,"costUsd":0.0123}}""").Should().BeNull();

        Check(ConversationEventKinds.Turn, """{"phase":"started","outcome":"completed"}""").Should().Be("invalid", "started 不帶 outcome");
        Check(ConversationEventKinds.Turn, """{"phase":"started","usage":{"inputTokens":1,"outputTokens":2,"cachedInputTokens":3}}""").Should().Be("invalid");
        Check(ConversationEventKinds.Turn, """{"phase":"completed"}""").Should().Be("invalid", "completed 要 outcome");
        Check(ConversationEventKinds.Turn, """{"phase":"completed","outcome":"Completed"}""").Should().Be("invalid");
        Check(ConversationEventKinds.Turn, """{"phase":"completed","outcome":"completed","usage":{"inputTokens":-1,"outputTokens":2,"cachedInputTokens":3}}""").Should().Be("invalid");
        Check(ConversationEventKinds.Turn, """{"phase":"completed","outcome":"completed","usage":{"inputTokens":1,"outputTokens":2}}""").Should().Be("invalid");
        Check(ConversationEventKinds.Turn, """{"phase":"completed","outcome":"completed","usage":{"inputTokens":1,"outputTokens":2,"cachedInputTokens":3,"costUsd":-0.1}}""").Should().Be("invalid");
        Check(ConversationEventKinds.Turn, """{"phase":"resumed"}""").Should().Be("invalid");
        Check(ConversationEventKinds.Turn, "{}").Should().Be("invalid");
    }

    [Fact]
    public void Attention的speech_選填字串_空字串可以_超過上限或不是字串不合格()
    {
        Check(ConversationEventKinds.Attention, """{"kind":"completed","speech":"「整理報表」已完成：月報已經匯出成 Excel"}""").Should().BeNull();
        Check(ConversationEventKinds.Attention, """{"kind":"completed","speech":""}""").Should().BeNull("空字串＝不念");
        Check(ConversationEventKinds.Attention, """{"kind":"completed","speech":null}""").Should().BeNull();
        Check(ConversationEventKinds.Attention, SyncJson.Serialize(new ConversationAttentionPayload("completed", null, new string('字', ConversationEventRules.MaxSpeechLength))))
            .Should().BeNull();
        Check(ConversationEventKinds.Attention, SyncJson.Serialize(new ConversationAttentionPayload("completed", null, new string('字', ConversationEventRules.MaxSpeechLength + 1))))
            .Should().Be("invalid");
        Check(ConversationEventKinds.Attention, """{"kind":"completed","speech":5}""").Should().Be("invalid");
        SyncJson.Serialize(new ConversationAttentionPayload("completed")).Should().Be("""{"kind":"completed"}""", "沒帶 speech 時和以前逐位元組相同");
    }

    [Fact]
    public void AttentionWithoutSpeech_拿掉speech_沒有就回null()
    {
        ConversationEventRules.AttentionWithoutSpeech("""{"kind":"completed","itemId":"0f3c9a52b7e94d0c8a61f2d4e5b6c7d8","speech":"「甲」已完成：說明"}""")
            .Should().Be("""{"kind":"completed","itemId":"0f3c9a52b7e94d0c8a61f2d4e5b6c7d8"}""");
        ConversationEventRules.AttentionWithoutSpeech("""{"kind":"completed"}""").Should().BeNull();
        ConversationEventRules.AttentionWithoutSpeech("{}").Should().BeNull();
        ConversationEventRules.AttentionWithoutSpeech("not json").Should().BeNull();
    }

    [Fact]
    public void Attention_Deleted_ItemRemoved_正反例()
    {
        foreach (var kind in new[] { "question", "approval", "plan", "completed", "failed" })
            Check(ConversationEventKinds.Attention, $$"""{"kind":"{{kind}}"}""").Should().BeNull(kind);
        Check(ConversationEventKinds.Attention, """{"kind":"approval","itemId":"0f3c9a52b7e94d0c8a61f2d4e5b6c7d8"}""").Should().BeNull();
        Check(ConversationEventKinds.Attention, """{"kind":"alert"}""").Should().Be("invalid");
        Check(ConversationEventKinds.Attention, """{"kind":"approval","itemId":"a/b"}""").Should().Be("invalid");

        Check(ConversationEventKinds.Deleted, "{}").Should().BeNull();
        Check(ConversationEventKinds.Deleted, SyncJson.Serialize(new ConversationDeletedPayload())).Should().BeNull();
        Check(ConversationEventKinds.Deleted, "[]").Should().Be("invalid");

        Check(ConversationEventKinds.ItemRemoved, """{"id":"0f3c9a52b7e94d0c8a61f2d4e5b6c7d8"}""").Should().BeNull();
        Check(ConversationEventKinds.ItemRemoved, "{}").Should().Be("invalid");
        Check(ConversationEventKinds.ItemRemoved, """{"id":"..\\x"}""").Should().Be("invalid");
    }

    [Fact]
    public void Queue_正反例()
    {
        Check(ConversationEventKinds.Queue, """{"entries":[]}""").Should().BeNull();
        Check(ConversationEventKinds.Queue, """{"entries":[{"id":"q1","displayText":"接著幫我跑測試"}],"more":2}""").Should().BeNull();

        Check(ConversationEventKinds.Queue, "{}").Should().Be("invalid");
        Check(ConversationEventKinds.Queue, """{"entries":[{"id":"q1"}]}""").Should().Be("invalid");
        Check(ConversationEventKinds.Queue, """{"entries":[{"id":"q 1","displayText":"x"}]}""").Should().Be("invalid");
        Check(ConversationEventKinds.Queue, """{"entries":[],"more":-1}""").Should().Be("invalid");
        Check(ConversationEventKinds.Queue, new JObject { ["entries"] = new JArray(new JObject { ["id"] = "q1", ["displayText"] = new string('x', ConversationEventRules.MaxQueueTextLength + 1) }) }.ToString())
            .Should().Be("invalid");
        var tooMany = new JArray(Enumerable.Range(0, ConversationEventRules.MaxQueueEntries + 1).Select(i => new JObject { ["id"] = "q" + i, ["displayText"] = "x" }));
        Check(ConversationEventKinds.Queue, new JObject { ["entries"] = tooMany }.ToString()).Should().Be("invalid");
    }

    [Fact]
    public void Item_最小的正例與選用欄位()
    {
        Check(ConversationEventKinds.Item, Item()).Should().BeNull();
        Check(ConversationEventKinds.Item, Item(o =>
        {
            o["toolKind"] = "edit";
            o["decision"] = "allow";
            o["requestDigest"] = "rd1:" + new string('a', 64);
            o["risk"] = "High";
            o["noticeKind"] = "SomethingNew";
            o["canAllowForSession"] = false;
            o["answerAsMessage"] = true;
            o["diff"] = JArray.Parse("""[{"t":"hunk","s":"@@ -1 +1 @@"},{"t":"del","s":"a"},{"t":"add","s":"b"},{"t":"ctx","s":""}]""");
            o["attachments"] = JArray.Parse("""[{"id":"x1","name":"a.png","kind":"Image","size":10,"ref":"01J9Q0000000000000000000AB"},{"id":"x2","name":"b.mp4","kind":"Video","size":0,"frames":6,"duration":35.5}]""");
            o["media"] = JArray.Parse("""[{"ref":"01J9Q0000000000000000000AB","kind":"image","name":"chart.png"},{"kind":"video","name":"a.mp4"}]""");
            o["questions"] = JArray.Parse("""[{"id":"要怎麼彙總？","header":"彙總","text":"要怎麼彙總？","multiSelect":false,"allowOther":true,"isSecret":false,"options":[{"label":"依日期","description":"…"}]}]""");
            o["answers"] = JArray.Parse("""[{"id":"要怎麼彙總？","values":["依日期"]}]""");
            o["plan"] = JArray.Parse("""[{"text":"讀取報表","status":"completed"}]""");
            o["filePath"] = @"C:\repo\proj\a.cs";
        })).Should().BeNull("risk、noticeKind 是不認得的名稱也照收（新版的值），讀的一方當成沒有值");
    }

    [Theory]
    [InlineData("id", null)]
    [InlineData("id", "\"a/b\"")]
    [InlineData("ord", "0")]
    [InlineData("ord", null)]
    [InlineData("ord", "\"1\"")]
    [InlineData("ver", "0")]
    [InlineData("kind", "\"user\"")]
    [InlineData("kind", "\"Unknown\"")]
    [InlineData("kind", "0")]
    [InlineData("status", "\"done\"")]
    [InlineData("status", null)]
    [InlineData("createdAt", "-5")]
    [InlineData("text", "1")]
    [InlineData("text", "[]")]
    [InlineData("toolKind", "\"\"")]
    [InlineData("risk", "\"high risk\"")]
    [InlineData("risk", "1")]
    [InlineData("canAllowForSession", "\"true\"")]
    [InlineData("diff", "[{\"t\":\"change\",\"s\":\"a\"}]")]
    [InlineData("diff", "{\"t\":\"add\",\"s\":\"a\"}")]
    [InlineData("attachments", "[{\"id\":\"x1\",\"name\":\"a.png\",\"kind\":\"image\",\"size\":10}]")]
    [InlineData("attachments", "[{\"id\":\"x1\",\"name\":\"a.png\",\"kind\":\"Image\",\"size\":-1}]")]
    [InlineData("attachments", "[{\"id\":\"x1\",\"name\":\"a.png\",\"kind\":\"Image\",\"size\":1,\"ref\":\"C:\\\\a.png\"}]")]
    [InlineData("media", "[{\"kind\":\"audio\",\"name\":\"a.mp3\"}]")]
    [InlineData("questions", "[{\"id\":\"q\",\"text\":\"q\",\"multiSelect\":false,\"allowOther\":true,\"isSecret\":false}]")]
    [InlineData("answers", "[{\"id\":\"q\",\"values\":[1]}]")]
    [InlineData("answers", "{\"q\":[\"A\"]}")]
    [InlineData("plan", "[{\"text\":\"a\"}]")]
    public void Item_反例(string field, string? json)
    {
        var payload = Item(o =>
        {
            if (json == null)
                o.Remove(field);
            else
                o[field] = JToken.Parse(json);
        });

        Check(ConversationEventKinds.Item, payload).Should().Be("invalid", $"{field}={json ?? "（沒有）"}");
    }

    [Theory]
    [InlineData("processInstance")]
    [InlineData("requestKey")]
    [InlineData("seq")]
    [InlineData("storedPath")]
    [InlineData("StoredPath")]
    [InlineData("modelImagePaths")]
    [InlineData("mediaPaths")]
    [InlineData("mediaRefs")]
    [InlineData("path")]
    public void Item_只在記憶體的欄位與本機路徑_任何一層都拒絕(string key)
    {
        Check(ConversationEventKinds.Item, Item(o => o[key] = "x")).Should().Be("invalid", "頂層");
        Check(ConversationEventKinds.Item, Item(o => o["attachments"] = new JArray(new JObject
        {
            ["id"] = "x1", ["name"] = "a.png", ["kind"] = "Image", ["size"] = 1, [key] = @"C:\Users\Lin\a.png",
        }))).Should().Be("invalid", "附件裡");
        Check(ConversationEventKinds.Item, Item(o => o["future"] = new JObject { ["deep"] = new JObject { [key] = 1 } })).Should().Be("invalid", "未知欄位的深處也查");
    }

    [Fact]
    public void Item_blob參照的格式()
    {
        var sha = new string('a', 64);
        Check(ConversationEventKinds.Item, Item(o => o["text"] = new JObject { ["blob"] = sha, ["length"] = 40_000 })).Should().BeNull();
        Check(ConversationEventKinds.Item, Item(o => o["diff"] = new JObject { ["blob"] = sha, ["length"] = 1 })).Should().BeNull("陣列欄位也能整欄換成 blob");

        Check(ConversationEventKinds.Item, Item(o => o["text"] = new JObject { ["blob"] = sha.ToUpperInvariant(), ["length"] = 40_000 })).Should().Be("invalid");
        Check(ConversationEventKinds.Item, Item(o => o["text"] = new JObject { ["blob"] = sha, ["length"] = 0 })).Should().Be("invalid");
        Check(ConversationEventKinds.Item, Item(o => o["text"] = new JObject { ["blob"] = sha, ["length"] = ConversationEventRules.MaxBlobLength + 1 })).Should().Be("invalid");
        Check(ConversationEventKinds.Item, Item(o => o["text"] = new JObject { ["blob"] = sha, ["length"] = 1, ["more"] = 1 })).Should().Be("invalid");
        Check(ConversationEventKinds.Item, Item(o => o["text"] = new JObject { ["blob"] = sha })).Should().Be("invalid");
        Check(ConversationEventKinds.Item, Item(o => o["decision"] = new JObject { ["blob"] = sha, ["length"] = 1 })).Should().Be("invalid", "短欄位不能換成 blob");
        Check(ConversationEventKinds.Item, Item(o => o["kind"] = new JObject { ["blob"] = sha, ["length"] = 1 })).Should().Be("invalid");
    }

    // ───────────────────────────── 放進上限（TryFit） ─────────────────────────────

    [Fact]
    public void Fit_超過32KB的字串換成blob_內容與雜湊對得上_讀回一樣()
    {
        var output = string.Concat(Enumerable.Range(0, 3000).Select(i => $"第 {i} 行輸出\n"));
        var item = Wire(new AgentChatItem { Id = "t1", Ord = 1, Kind = AgentItemKind.Tool, ToolKind = "bash", Title = "npm test", Output = output });
        Encoding.UTF8.GetByteCount(output).Should().BeGreaterThan(ConversationEventRules.MaxInlineStringBytes);
        var before = (JObject)item.DeepClone();

        ConversationEventRules.TryFit(item, out var blobs).Should().BeTrue();

        blobs.Should().ContainSingle();
        var blob = blobs[0];
        Encoding.UTF8.GetString(blob.Content).Should().Be(output);
        blob.Sha256.Should().Be(Convert.ToHexStringLower(SHA256.HashData(blob.Content)));
        JToken.DeepEquals(item["output"], new JObject { ["blob"] = blob.Sha256, ["length"] = blob.Content.Length }).Should().BeTrue();
        item["title"]!.Value<string>().Should().Be("npm test", "小欄位照樣內嵌");
        Check(ConversationEventKinds.Item, item.ToString()).Should().BeNull();

        ConversationWire.TryReadItem(item, sha => sha == blob.Sha256 ? blob.Content : null, out var read).Should().BeTrue();
        ConversationWire.TryReadItem(before, _ => null, out var original).Should().BeTrue();
        read.Should().BeEquivalentTo(original);
        read!.Output.Should().Be(output);
    }

    [Fact]
    public void Fit_字串都不大但整筆超過128KB時_由大到小整欄換掉()
    {
        // 差異每行都很短，整份卻超過上限：整個陣列換成一個 blob（逐行換只會換出幾百個 blob 而且不一定放得下）
        var diff = Enumerable.Range(0, 3000).Select(i => new AgentDiffLine("add", $"var line{i} = \"{new string('x', 40)}\";")).ToList();
        var detail = new string('d', 20_000);
        var item = Wire(new AgentChatItem { Id = "e1", Ord = 9, Kind = AgentItemKind.Tool, ToolKind = "edit", Detail = detail, Diff = diff, FilePath = @"C:\repo\a.cs" });
        Encoding.UTF8.GetByteCount(item.ToString(Newtonsoft.Json.Formatting.None)).Should().BeGreaterThan(ConversationEventRules.MaxPayloadBytes);

        ConversationEventRules.TryFit(item, out var blobs).Should().BeTrue();

        ConversationEventRules.TryReadBlobRef(item["diff"], out _).Should().BeTrue("最大的一欄先換");
        item["detail"]!.Type.Should().Be(JTokenType.String, "換掉差異之後就放得下了");
        blobs.Should().ContainSingle();
        JArray.Parse(Encoding.UTF8.GetString(blobs[0].Content)).Should().HaveCount(3000);
        Encoding.UTF8.GetByteCount(item.ToString(Newtonsoft.Json.Formatting.None)).Should().BeLessThanOrEqualTo(ConversationEventRules.MaxPayloadBytes);
        Check(ConversationEventKinds.Item, item.ToString()).Should().BeNull();

        var contents = blobs.ToDictionary(b => b.Sha256, b => b.Content);
        ConversationWire.TryReadItem(item, sha => contents.GetValueOrDefault(sha), out var read).Should().BeTrue();
        read!.Diff.Should().BeEquivalentTo(diff);
        read.Detail.Should().Be(detail);
    }

    [Fact]
    public void Fit_問題裡有超過32KB的字串時_整個問題陣列換成blob()
    {
        var huge = new string('問', 12_000);
        var item = Wire(new AgentChatItem
        {
            Id = "q1", Ord = 2, Kind = AgentItemKind.Question, Status = AgentItemStatus.Waiting,
            Questions = { new AgentQuestion { Id = huge, Text = huge, Options = { new AgentQuestionOption { Label = "A" } } } },
            Answers = { [huge] = ["A"] },
        });

        ConversationEventRules.TryFit(item, out var blobs).Should().BeTrue();

        ConversationEventRules.TryReadBlobRef(item["questions"], out _).Should().BeTrue();
        ConversationEventRules.TryReadBlobRef(item["answers"], out _).Should().BeTrue("答案的 id 是題目文字，也超過 32 KB");
        blobs.Should().HaveCount(2);
        Check(ConversationEventKinds.Item, item.ToString()).Should().BeNull();
    }

    [Fact]
    public void Fit_同樣的內容只上傳一次_結果固定()
    {
        var text = new string('a', 40_000);
        var first = Wire(new AgentChatItem { Id = "a1", Ord = 1, Kind = AgentItemKind.Assistant, Text = text, Detail = text });
        var second = (JObject)first.DeepClone();

        ConversationEventRules.TryFit(first, out var blobs).Should().BeTrue();
        ConversationEventRules.TryFit(second, out _).Should().BeTrue();

        blobs.Should().ContainSingle("text 與 detail 的內容相同");
        first.ToString().Should().Be(second.ToString());
        ConversationEventRules.TryFit(first, out var again).Should().BeTrue("已經放得下就不再換");
        again.Should().BeEmpty();
    }

    [Fact]
    public void Fit_能換的欄位都換了還是太大時回false()
    {
        var item = Wire(new AgentChatItem { Id = "a1", Ord = 1, Kind = AgentItemKind.Assistant, Text = "x" });
        item["future"] = new string('f', 30_000);

        ConversationEventRules.TryFit(item, out _, maxPayloadBytes: 20_000).Should().BeFalse("future 不是能換成 blob 的欄位");
    }

    [Fact]
    public void 讀回時blob拿不到或內容不符回false()
    {
        var text = new string('a', 40_000);
        var item = Wire(new AgentChatItem { Id = "a1", Ord = 1, Kind = AgentItemKind.Assistant, Text = text });
        ConversationEventRules.TryFit(item, out var blobs).Should().BeTrue();
        var blob = blobs.Single();

        ConversationWire.TryReadItem(item, _ => null, out _).Should().BeFalse("拿不到");
        ConversationWire.TryReadItem(item, _ => Encoding.UTF8.GetBytes(new string('b', 40_000)), out _).Should().BeFalse("雜湊不符");
        ConversationWire.TryReadItem(item, _ => blob.Content[..^1], out _).Should().BeFalse("長度不符");
        ConversationWire.TryReadItem(item, _ => blob.Content, out var read).Should().BeTrue();
        read!.Text.Should().Be(text);
    }

    private static string? Check(string kind, string payload) => ConversationEventRules.Validate(kind, payload);

    private static string? Check(string kind, JObject payload) => ConversationEventRules.Validate(kind, payload.ToString());

    private static JObject Wire(AgentChatItem item)
    {
        ConversationWire.TryItem(item, ver: 1, out var wire).Should().BeTrue();
        return wire!;
    }

    private static JObject Meta(Action<JObject>? change = null)
    {
        var o = JObject.Parse("""
            {"provider":"Claude","localAccountId":"a1","title":"修正登入流程","permissionMode":"Ask","customFolder":false,
             "syncPolicy":"full","createdAt":1791580000000,"updatedAt":1791580001000,"running":false,"waiting":false}
            """);
        change?.Invoke(o);
        return o;
    }

    private static JObject Item(Action<JObject>? change = null)
    {
        var o = JObject.Parse("""{"id":"0f3c9a52b7e94d0c8a61f2d4e5b6c7d8","ord":1,"ver":1,"kind":"User","status":"Done","createdAt":1791580000000,"text":"你好"}""");
        change?.Invoke(o);
        return o;
    }
}
