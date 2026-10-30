using System.Text;
using Myriagent.Sync;
using MyriagentHub.Sync;
using Newtonsoft.Json.Linq;
using static MyriagentHub.Tests.Sync.ConversationSamples;

namespace MyriagentHub.Tests.Sync;

/// <summary>對話事件在寫入者之外的預先檢查（WP4.2 計畫步 2；D3、D8、D44、提案 C6、C10）</summary>
public sealed class ConversationPrecheckRulesTests
{
    private static ConversationPrecheck Check(SyncEvent e) => ConversationPrecheckRules.Check(e, SyncLimits.Default);

    [Fact]
    public void 串流與id不符_invalid()
    {
        var e = Event(ConversationEventKinds.Item, Json(Item()), stream: SyncStreams.Conversation(OtherConv));
        var result = Check(e);
        result.Rejected.Should().Be(SyncErrorCodes.Invalid);
        result.DlpClean.Should().BeFalse("還沒跑到 DLP");
        result.Json.Should().BeNull();
        result.Parsed.Should().BeNull();
    }

    [Fact]
    public void 裝置送的conv_lease_一律invalid_payload不存()
    {
        // 第 5 階段：conv.lease 只由伺服器寫（D2）；形狀合格的也一樣，連 DLP 都不跑
        var lease = ConversationWire.Serialize(new ConversationLeasePayload("d_8P2Q0000000000000000000000", 2, "d_7K3M0000000000000000000000", 1, LeaseReasons.Restore));
        ConversationEventRules.Validate(ConversationEventKinds.Lease, lease).Should().BeNull("陽性對照：形狀本身合格");

        var result = Check(Event(ConversationEventKinds.Lease, lease));
        result.Rejected.Should().Be(SyncErrorCodes.Invalid);
        result.DlpClean.Should().BeFalse("payload 不存進 rejected_events");
        result.Json.Should().BeNull();
    }

    [Fact]
    public void 大寫id_invalid()
    {
        var upper = Conv.ToUpperInvariant().Replace("0", "A", StringComparison.Ordinal);
        Check(Event(ConversationEventKinds.Item, Json(Item()), conversationId: upper)).Rejected.Should().Be(SyncErrorCodes.Invalid);
        Check(Event(ConversationEventKinds.Meta, Json(Meta()), conversationId: "0123456789ABCDEF0123456789abcdef")).Rejected.Should().Be(SyncErrorCodes.Invalid);
    }

    [Fact]
    public void 外框不合格_invalid()
    {
        Check(Event(ConversationEventKinds.Item, Json(Item()), epoch: 0)).Rejected.Should().Be(SyncErrorCodes.Invalid, "epoch ≥1");
        Check(Event(ConversationEventKinds.Item, Json(Item()), epoch: null)).Rejected.Should().Be(SyncErrorCodes.Invalid);
        Check(Event("link.upsert", Json(Item()))).Rejected.Should().Be(SyncErrorCodes.Invalid, "不是對話種類");
        Check(Event(ConversationEventKinds.Item, Json(Item()), conversationId: "short")).Rejected.Should().Be(SyncErrorCodes.Invalid);
    }

    [Fact]
    public void 雜湊不符_invalid()
    {
        var result = Check(Event(ConversationEventKinds.Item, Json(Item()), sha: PayloadRules.Sha256Hex("其他")));
        result.Rejected.Should().Be(SyncErrorCodes.Invalid);
        result.DlpClean.Should().BeFalse("還沒跑到 DLP 的拒絕不存 payload");
    }

    [Fact]
    public void 解碼失敗_invalid且DlpClean為假()
    {
        var e = Event(ConversationEventKinds.Item, Json(Item())) with { Enc = PayloadRules.EncGzip, Payload = "不是 base64" };
        var result = Check(e);
        result.Rejected.Should().Be(SyncErrorCodes.Invalid);
        result.DlpClean.Should().BeFalse();
        Check(Event(ConversationEventKinds.Item, Json(Item())) with { Enc = "brotli" }).Rejected.Should().Be(SyncErrorCodes.Invalid);
    }

    [Fact]
    public void 超過128KB_too_large且DlpClean為假()
    {
        var item = Item(text: null);
        item["text"] = new string('a', 30 * 1024);
        item["detail"] = new string('b', 30 * 1024);
        item["output"] = new string('c', 30 * 1024);
        item["title"] = new string('d', 30 * 1024);
        item["toolName"] = new string('e', 10 * 1024);
        var json = Json(item);
        Encoding.UTF8.GetByteCount(json).Should().BeGreaterThan(128 * 1024, "前提");
        var result = Check(Event(ConversationEventKinds.Item, json));
        result.Rejected.Should().Be(SyncErrorCodes.TooLarge);
        result.DlpClean.Should().BeFalse();

        var inline = Check(Event(ConversationEventKinds.Item, Json(Item(new string('x', 33 * 1024)))));
        inline.Rejected.Should().Be(SyncErrorCodes.TooLarge, "內嵌字串超過 32 KB（Validate 的 too_large）");
        inline.DlpClean.Should().BeFalse();
    }

    [Fact]
    public void 上限照傳入的SyncLimits()
    {
        var json = Json(Item(new string('x', 3000)));
        ConversationPrecheckRules.Check(Event(ConversationEventKinds.Item, json), SyncLimits.Default with { MaxInlinePayload = 1000 })
            .Rejected.Should().Be(SyncErrorCodes.TooLarge);
    }

    [Fact]
    public void Validate不合格但沒有權杖_invalid且DlpClean為真()
    {
        var item = Item();
        item.Remove("id");
        var result = Check(Event(ConversationEventKinds.Item, Json(item)));
        result.Rejected.Should().Be(SyncErrorCodes.Invalid);
        result.DlpClean.Should().BeTrue("解碼成功、原文乾淨：被拒事件的 payload 可以存");

        var notJson = Check(Event(ConversationEventKinds.Item, "不是 JSON"));
        notJson.Rejected.Should().Be(SyncErrorCodes.Invalid);
        notJson.DlpClean.Should().BeTrue();
    }

    [Fact]
    public void Validate不合格而且有權杖_invalid且DlpClean為假()
    {
        var item = Item("看 " + GitHub());
        item.Remove("id");
        var result = Check(Event(ConversationEventKinds.Item, Json(item)));
        result.Rejected.Should().Be(SyncErrorCodes.Invalid);
        result.DlpClean.Should().BeFalse();
        result.Json.Should().BeNull();

        Check(Event(ConversationEventKinds.Item, "不是 JSON " + GitHub())).DlpClean.Should().BeFalse("解析失敗時對整段文字跑");
        var duplicate = "{\"text\":\"" + EscapeFirst(GitHub()) + "\",\"text\":\"ok\"}";
        var dup = Check(Event(ConversationEventKinds.Item, duplicate));
        dup.Rejected.Should().Be(SyncErrorCodes.Invalid, "重複的鍵");
        dup.DlpClean.Should().BeFalse("重複的鍵裡跳脫寫的權杖也讀得到");
    }

    [Fact]
    public void 建構式語法_invalid且DlpClean為假()
    {
        // 合併前審查 DLP-CTOR-1：權杖寫成 new 名稱(…) 的建構式名稱，走遍檢查看不到；原本 Validate 合格的會原樣存進 events／items、經 Pull 發出去，
        // Validate 不合格的會存進 rejected_events
        var key = AwsAccessKey();
        var xCtor = RawItem("\"hi\"", ",\"x\":new " + key + "(1)");
        ConversationEventRules.Validate(ConversationEventKinds.Item, xCtor).Should().BeNull("前提：Validate 收不在字串欄位上的建構式");
        var accepted = Check(Event(ConversationEventKinds.Item, xCtor));
        accepted.Rejected.Should().Be(SyncErrorCodes.Invalid);
        accepted.DlpClean.Should().BeFalse("payload 不存");
        accepted.Json.Should().BeNull();
        accepted.Remasked.Should().BeFalse();

        var textCtor = RawItem("new " + key + "()");
        ConversationEventRules.Validate(ConversationEventKinds.Item, textCtor).Should().Be(SyncErrorCodes.Invalid, "前提：text 不是字串");
        var rejected = Check(Event(ConversationEventKinds.Item, textCtor));
        rejected.Rejected.Should().Be(SyncErrorCodes.Invalid);
        rejected.DlpClean.Should().BeFalse("Validate 不合格時也不能當成乾淨的原文");

        // 名稱不是權杖也一樣（SyncJson 不會寫出建構式、undefined）
        foreach (var json in new[] { RawItem("\"hi\"", ",\"x\":new Foo(1)"), RawItem("\"hi\"", ",\"x\":undefined"), RawItem("new Foo()") })
        {
            var result = Check(Event(ConversationEventKinds.Item, json));
            result.Rejected.Should().Be(SyncErrorCodes.Invalid, json);
            result.DlpClean.Should().BeFalse(json);
        }

        Check(Event(ConversationEventKinds.Item, RawItem("\"new Foo(1) " + "是字串\""))).Rejected.Should().BeNull("對照：字串裡的文字照常接受");
    }

    [Fact]
    public void 合格且有權杖_遮蔽後接受_Json是遮蔽版_DlpClean為假()
    {
        var token = GitHub();
        var e = Event(ConversationEventKinds.Item, Json(Item("看 " + token, ord: 4, ver: 2)));
        var result = Check(e);
        result.Rejected.Should().BeNull();
        result.Remasked.Should().BeTrue();
        result.DlpClean.Should().BeFalse("原文不是 DLP 乾淨的");
        result.Json.Should().NotContain(token).And.Contain(CredentialLeakRules.Replacement);
        result.Parsed!["text"]!.Value<string>().Should().Be("看 " + CredentialLeakRules.Replacement);
        result.Parsed["ord"]!.Value<long>().Should().Be(4);
        result.Parsed["ver"]!.Value<long>().Should().Be(2);
        result.StoreEnc.Should().Be(PayloadRules.EncJson);
        PayloadRules.TryDecode(result.StoreEnc, result.StorePayload, 128 * 1024, out var stored).Should().BeTrue();
        stored.Should().Be(result.Json, "存的是遮蔽版");
        result.StorePayload.Should().NotContain(token);
    }

    [Fact]
    public void 遮蔽版8KB以上_以gzip存()
    {
        var result = Check(Event(ConversationEventKinds.Item, Json(Item(new string('字', 4000) + " " + GitHub()))));
        result.Remasked.Should().BeTrue();
        result.StoreEnc.Should().Be(PayloadRules.EncGzip);
        PayloadRules.TryDecode(result.StoreEnc, result.StorePayload, 128 * 1024, out var stored).Should().BeTrue();
        stored.Should().Be(result.Json);
    }

    [Fact]
    public void 原文以跳脫寫的權杖_也遮蔽後接受()
    {
        var raw = "{\"id\":\"item1\",\"ord\":1,\"ver\":1,\"kind\":\"Assistant\",\"status\":\"Done\",\"text\":\"" + EscapeFirst(GitHub()) + "\"}";
        CredentialLeakRules.HasLeak(raw).Should().BeFalse("前提：對原文掃描比對不到");
        var result = Check(Event(ConversationEventKinds.Item, raw));
        result.Rejected.Should().BeNull();
        result.Remasked.Should().BeTrue("走遍的是解析後的值");
        result.Parsed!["text"]!.Value<string>().Should().Be(CredentialLeakRules.Replacement);
    }

    [Fact]
    public void 合格且乾淨_Rejected為null且Parsed不是null_不遮蔽()
    {
        var json = Json(Item("乾淨的文字"));
        var result = Check(Event(ConversationEventKinds.Item, json));
        result.Rejected.Should().BeNull();
        result.DlpClean.Should().BeTrue();
        result.Remasked.Should().BeFalse();
        result.Json.Should().Be(json, "原文照樣（存裝置送的原始位元組）");
        result.Parsed.Should().NotBeNull();
        result.Parsed!["text"]!.Value<string>().Should().Be("乾淨的文字");
        result.StorePayload.Should().BeNull();
        result.StoreEnc.Should().BeNull();
    }

    [Fact]
    public void gzip的乾淨事件_Json是解壓後的原文()
    {
        var json = Json(Item(new string('a', 9000)));
        var e = Event(ConversationEventKinds.Item, json);
        e.Enc.Should().Be(PayloadRules.EncGzip, "前提");
        var result = Check(e);
        result.Rejected.Should().BeNull();
        result.Json.Should().Be(json);
        result.StorePayload.Should().BeNull();
    }

    [Fact]
    public void 遮蔽後仍命中_invalid_DlpClean為假()
    {
        ConversationPrecheckRules.RemaskForTest = (_, payload) => ConversationWire.Serialize(payload);
        try
        {
            var result = Check(Event(ConversationEventKinds.Item, Json(Item("看 " + GitHub()))));
            result.Rejected.Should().Be(SyncErrorCodes.Invalid);
            result.DlpClean.Should().BeFalse();
            result.Remasked.Should().BeFalse("被擋的不算再遮蔽後接受");
            result.Json.Should().BeNull();
            result.StorePayload.Should().BeNull();
        }
        finally
        {
            ConversationPrecheckRules.RemaskForTest = null;
        }

        Check(Event(ConversationEventKinds.Item, Json(Item("看 " + GitHub())))).Rejected.Should().BeNull("注入點清掉之後照常遮蔽");
    }

    [Fact]
    public void 遮蔽後不合格_invalid()
    {
        ConversationPrecheckRules.RemaskForTest = (_, _) => "{}";
        try
        {
            var result = Check(Event(ConversationEventKinds.Item, Json(Item("看 " + GitHub()))));
            result.Rejected.Should().Be(SyncErrorCodes.Invalid, "遮蔽後 Validate 不過");
            result.DlpClean.Should().BeFalse();
        }
        finally
        {
            ConversationPrecheckRules.RemaskForTest = null;
        }
    }

    [Fact]
    public void 再遮蔽丟出毒事件的例外_invalid_其他例外照舊往外丟()
    {
        var e = Event(ConversationEventKinds.Item, Json(Item("看 " + GitHub())));
        try
        {
            ConversationPrecheckRules.RemaskForTest = (_, _) => throw new ArgumentException("壞資料");
            var result = Check(e);
            result.Rejected.Should().Be(SyncErrorCodes.Invalid, "只拒收這一筆，連線不中斷（D37）");
            result.DlpClean.Should().BeFalse();

            ConversationPrecheckRules.RemaskForTest = (_, _) => throw new InvalidOperationException("程式狀態");
            var unexpected = () => Check(e);
            unexpected.Should().Throw<InvalidOperationException>("不是這一筆的內容造成的：照舊往外丟");
        }
        finally
        {
            ConversationPrecheckRules.RemaskForTest = null;
        }
    }

    [Fact]
    public void 遮蔽後超過上限_invalid()
    {
        // 很多個沒有長度下限的短權杖（sk-proj-，8 位元組）：換成「［疑似權杖已遮蔽］」（27 位元組）後內嵌字串超過 32 KB
        var text = string.Concat(Enumerable.Repeat(ScopedOpenAiPrefix() + " ", 3600));
        Encoding.UTF8.GetByteCount(text).Should().BeLessThan(32 * 1024, "前提：原文合格");
        var json = Json(Item(text));
        ConversationEventRules.Validate(ConversationEventKinds.Item, json).Should().BeNull("前提：原文合格");
        var result = Check(Event(ConversationEventKinds.Item, json));
        result.Rejected.Should().Be(SyncErrorCodes.Invalid);
        result.DlpClean.Should().BeFalse();
        result.Remasked.Should().BeFalse();
    }

    [Theory]
    [InlineData(ConversationEventKinds.Created)]
    [InlineData(ConversationEventKinds.Meta)]
    [InlineData(ConversationEventKinds.Turn)]
    [InlineData(ConversationEventKinds.Attention)]
    [InlineData(ConversationEventKinds.Item)]
    [InlineData(ConversationEventKinds.ItemRemoved)]
    [InlineData(ConversationEventKinds.Queue)]
    public void 空物件payload_除了deleted每種對話種類都回invalid(string kind)
    {
        var result = Check(Event(kind, "{}"));
        result.Rejected.Should().Be(SyncErrorCodes.Invalid, "沒有 conv1 的連線上桌面把 Pending 的列換成 {}（提案 C10）");
        result.DlpClean.Should().BeTrue("{} 是乾淨的：舊伺服器與這裡都只存 {}");
    }

    [Fact]
    public void 空物件的deleted_通過()
    {
        var result = Check(Event(ConversationEventKinds.Deleted, "{}"));
        result.Rejected.Should().BeNull();
        result.Json.Should().Be("{}");
        result.Remasked.Should().BeFalse();
    }

    [Fact]
    public void 各種類的合格事件_通過()
    {
        Check(Event(ConversationEventKinds.Created, Meta())).Rejected.Should().BeNull();
        Check(Event(ConversationEventKinds.Meta, Meta(policy: ConversationSyncPolicies.Paused))).Rejected.Should().BeNull();
        Check(Event(ConversationEventKinds.Turn, new JObject { ["phase"] = "started" })).Rejected.Should().BeNull();
        Check(Event(ConversationEventKinds.Attention, new JObject { ["kind"] = "plan" })).Rejected.Should().BeNull();
        Check(Event(ConversationEventKinds.ItemRemoved, new JObject { ["id"] = "item1" })).Rejected.Should().BeNull();
        Check(Event(ConversationEventKinds.Queue, Queue("一則"))).Rejected.Should().BeNull();
        Check(Event(ConversationEventKinds.Item, Item())).Rejected.Should().BeNull();
    }

    [Fact]
    public void meta與queue命中_遮蔽後接受()
    {
        var meta = Check(Event(ConversationEventKinds.Meta, Meta("標題 " + GitHub())));
        meta.Rejected.Should().BeNull();
        meta.Remasked.Should().BeTrue();
        meta.Parsed!["title"]!.Value<string>().Should().Be("標題 " + CredentialLeakRules.Replacement);

        var queue = Check(Event(ConversationEventKinds.Queue, Queue("貼上 " + GitHub())));
        queue.Remasked.Should().BeTrue();
        queue.Parsed!["entries"]![0]!["displayText"]!.Value<string>().Should().Be("貼上 " + CredentialLeakRules.Replacement);
    }

    [Fact]
    public void 從不回dlp()
    {
        var leaky = Item("看 " + GitHub());
        var invalidLeaky = (JObject)leaky.DeepClone();
        invalidLeaky.Remove("id");
        var events = new List<SyncEvent>
        {
            Event(ConversationEventKinds.Item, leaky),
            Event(ConversationEventKinds.Item, invalidLeaky),
            Event(ConversationEventKinds.Meta, Meta("標題 " + GitHub())),
            Event(ConversationEventKinds.Item, "不是 JSON " + GitHub()),
            Event(ConversationEventKinds.Item, Json(Item(string.Concat(Enumerable.Repeat(ScopedOpenAiPrefix() + " ", 3600))))),
            Event(ConversationEventKinds.Item, Json(leaky), stream: "c:x"),
            Event(ConversationEventKinds.Item, Json(leaky), sha: PayloadRules.Sha256Hex("x")),
        };
        foreach (var kind in ConversationEventKinds.All)
            events.Add(Event(kind, "{}"));

        ConversationPrecheckRules.RemaskForTest = (_, payload) => ConversationWire.Serialize(payload);
        try
        {
            events.Select(Check).Should().OnlyContain(r => r.Rejected != ConversationRejectCodes.Dlp, "遮蔽後仍命中也是 invalid");
        }
        finally
        {
            ConversationPrecheckRules.RemaskForTest = null;
        }

        var results = events.Select(Check).ToList();
        results.Select(r => r.Rejected).Should().NotContain(ConversationRejectCodes.Dlp);
        results.Should().Contain(r => r.Remasked, "樣本裡有遮蔽後接受的");
        results.Should().Contain(r => r.Rejected == SyncErrorCodes.Invalid && !r.DlpClean, "樣本裡有命中而被擋的");
    }

    [Fact]
    public async Task 注入點只影響設定它的流程()
    {
        var leaky = Event(ConversationEventKinds.Item, Json(Item("看 " + GitHub())));
        using var set = new ManualResetEventSlim();
        using var checkedOther = new ManualResetEventSlim();
        ConversationPrecheck? other = null;
        var otherTask = Task.Run(() =>
        {
            set.Wait();
            other = Check(leaky);
            checkedOther.Set();
        });
        ConversationPrecheckRules.RemaskForTest = (_, payload) => ConversationWire.Serialize(payload);
        try
        {
            set.Set();
            checkedOther.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();
            Check(leaky).Rejected.Should().Be(SyncErrorCodes.Invalid, "設定它的流程用注入的函式");
        }
        finally
        {
            ConversationPrecheckRules.RemaskForTest = null;
        }

        await otherTask;
        other!.Rejected.Should().BeNull("其他流程（並行的測試）照常遮蔽");
    }
}
