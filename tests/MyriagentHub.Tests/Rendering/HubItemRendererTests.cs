using System.Collections.Concurrent;
using System.Text;
using Myriagent.Agents;
using Myriagent.Rendering;
using Myriagent.Sync;
using Myriagent.Tests.Rendering;
using MyriagentHub.Rendering;
using MyriagentHub.Sync;
using MyriagentHub.Tests.Sync;
using MyriagentHub.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyriagentHub.Tests.Rendering;

/// <summary>
/// 伺服器以共用渲染器產生瀏覽器的項目（WP4.2 計畫步 7；R17、R20、SEC-11、D15、D18、D20、D24、D33）。
/// 語料是 <see cref="HubItemCorpus"/>（黃金檔 hub-items.json）與桌面的 <see cref="ItemRendererCorpus"/>（只讀連結，F47）
/// </summary>
public sealed class HubItemRendererTests
{
    private const int Max = 256 * 1024;

    // ───────────────────────────── 黃金檔 ─────────────────────────────

    [Fact]
    public void 伺服器黃金檔逐位元組相同()
    {
        var corpus = HubItemCorpus.Build();
        var golden = new JObject
        {
            ["description"] = "伺服器渲染給瀏覽器的 Item（HubItemCorpus）。production：WP4.2 的網址（媒體參照一律沒有網址，D15 的轉換）；"
                              + "withMediaUrls：WP4.7 形狀的測試網址（有參照就有 /m/、/t/）",
            ["production"] = RenderAll(corpus, new HubMediaUrls()),
            ["withMediaUrls"] = RenderAll(corpus, new WithMediaUrls()),
        };

        HubGoldenFile.Verify("hub-items", golden);
    }

    // ───────────────────────────── 輸出形狀 ─────────────────────────────

    [Fact]
    public void 沒有任何path鍵_任何深度()
    {
        var corpus = HubItemCorpus.Build();
        var outputs = new List<JObject>();
        foreach (var urls in new HubMediaUrls[] { new(), new WithMediaUrls() })
            outputs.AddRange(corpus.Cases.Select(c => Render(c, urls, corpus)));
        outputs.AddRange(DesktopCorpusOnServer().Select(r => r.Server));

        foreach (var output in outputs)
            PathKeys(output).Should().BeEmpty((string?)output["id"]);
        outputs.Should().Contain(o => o["attachments"] != null).And.Contain(o => o["media"] != null);
    }

    [Fact]
    public void Finish拿掉任何深度與大小寫的path鍵_答案的題目id除外()
    {
        var rendered = new JObject
        {
            ["id"] = "i1",
            ["path"] = @"C:\a.txt",
            ["deep"] = new JObject { ["Path"] = @"C:\b.txt", ["list"] = new JArray(new JObject { ["PATH"] = @"C:\c.txt", ["keep"] = 1 }) },
            ["answers"] = new JObject { ["path"] = new JArray("docs") },
            ["seq"] = 7,
        };
        var item = Wire(new JObject { ["id"] = "i1", ["ord"] = 42L, ["ver"] = 1L, ["kind"] = "Question", ["status"] = "Done" });

        var finished = HubItemRules.Finish(rendered, item, new HubMediaUrls(), HubItemCorpus.Owner);

        PathKeys(finished).Should().BeEmpty();
        finished["deep"]!["list"]![0]!["keep"]!.Value<int>().Should().Be(1);
        finished["answers"]!["path"]!.Values<string>().Should().Equal("docs");
        finished["seq"]!.Value<long>().Should().Be(42);
    }

    [Fact]
    public void seq等於ord()
    {
        var corpus = HubItemCorpus.Build();
        foreach (var c in corpus.Cases)
            Render(c, new WithMediaUrls(), corpus)["seq"]!.Value<long>().Should().Be(c.Ord, c.Name);
        corpus.Cases.Select(c => c.Ord).Should().OnlyHaveUniqueItems().And.NotContain(Enumerable.Range(1, corpus.Cases.Count).Select(n => (long)n));
    }

    [Fact]
    public void risk轉小寫_不認得的值省略()
    {
        var corpus = HubItemCorpus.Build();
        string? Risk(string name) => (string?)Render(corpus.Get(name), new HubMediaUrls(), corpus)["risk"];

        Risk("approval-high").Should().Be("high");
        Risk("approval-normal").Should().Be("normal");
        Risk("question-id-path").Should().Be("normal");
        Risk("plan-approval").Should().Be("high");
        Render(corpus.Get("approval-unknown-risk"), new HubMediaUrls(), corpus).Should().NotContainKey("risk").And.ContainKey("requestDigest");
        Render(corpus.Get("approval-no-digest"), new HubMediaUrls(), corpus).Should().NotContainKey("risk").And.NotContainKey("requestDigest");
        corpus.Get("approval-unknown-risk").Payload["risk"]!.Value<string>().Should().Be("Extreme");
    }

    [Fact]
    public void requestDigest原樣轉送_只在卡片上()
    {
        var corpus = HubItemCorpus.Build();
        foreach (var name in new[] { "approval-high", "approval-normal", "approval-unknown-risk", "question-answers", "question-id-path", "plan-approval" })
            ((string?)Render(corpus.Get(name), new HubMediaUrls(), corpus)["requestDigest"]).Should().Be(HubItemCorpus.Digest, name);

        // 工具卡帶了摘要與風險（線上照收），瀏覽器的 Item 只有卡片才有
        corpus.Get("tool-with-card-fields").Payload.Should().ContainKey("requestDigest").And.ContainKey("risk");
        Render(corpus.Get("tool-with-card-fields"), new HubMediaUrls(), corpus).Should().NotContainKey("requestDigest").And.NotContainKey("risk");
    }

    [Fact]
    public void createdAt是null_time用收到的時間()
    {
        var corpus = HubItemCorpus.Build();
        corpus.Get("no-created-at").Payload.Should().NotContainKey("createdAt");

        Render(corpus.Get("no-created-at"), new HubMediaUrls(), corpus)["time"]!.Value<long>().Should().Be(HubItemCorpus.ReceivedAt);
        Render(corpus.Get("created-at-overflow"), new HubMediaUrls(), corpus)["time"]!.Type.Should().Be(JTokenType.Null);
        var notice = corpus.Get("notice-cli");
        Render(notice, new HubMediaUrls(), corpus)["time"]!.Value<long>().Should().Be(notice.Payload["createdAt"]!.Value<long>());
    }

    // ───────────────────────────── 媒體（D15） ─────────────────────────────

    [Fact]
    public void 沒有網址的媒體_改成只有檔名的附件_media是空的()
    {
        var corpus = HubItemCorpus.Build();
        var item = corpus.Get("assistant-media");

        var production = Render(item, new HubMediaUrls(), corpus);
        production.Should().NotContainKey("media");
        production["attachments"]!.Should().BeEquivalentTo(new JArray(
            MediaTile("media-1", "圖表.png"), MediaTile("media-2", "示範.mp4"), MediaTile("media-3", "沒有參照.png")));

        var withUrls = Render(item, new WithMediaUrls(), corpus);
        withUrls["media"]!.Should().BeEquivalentTo(new JArray(
            new JObject { ["url"] = "/m/" + HubItemCorpus.Ref(11), ["kind"] = "image", ["name"] = "圖表.png" },
            new JObject { ["url"] = "/m/" + HubItemCorpus.Ref(12), ["kind"] = "video", ["name"] = "示範.mp4" }));
        withUrls["attachments"]!.Should().BeEquivalentTo(new JArray(MediaTile("media-3", "沒有參照.png")));
    }

    [Fact]
    public void 沒有網址的影片附件_kind改成Document()
    {
        var corpus = HubItemCorpus.Build();
        var item = corpus.Get("user-attachments");

        var production = Attachments(Render(item, new HubMediaUrls(), corpus));
        production["att-video"]["kind"]!.Value<string>().Should().Be("Document");
        production["att-video"]["thumbUrl"]!.Type.Should().Be(JTokenType.Null);
        production["att-video"]["videoInfo"]!.Type.Should().Be(JTokenType.Null);
        production["att-video"]["size"]!.Value<string>().Should().Be("1.2 MB");
        production["att-image"]["kind"]!.Value<string>().Should().Be("Image");
        production.Values.Should().OnlyContain(a => a["url"]!.Type == JTokenType.Null && a["thumbUrl"]!.Type == JTokenType.Null);

        var withUrls = Attachments(Render(item, new WithMediaUrls(), corpus));
        withUrls["att-video"]["kind"]!.Value<string>().Should().Be("Video");
        withUrls["att-video"]["url"]!.Value<string>().Should().Be("/m/" + HubItemCorpus.Ref(2));
        withUrls["att-video"]["thumbUrl"]!.Value<string>().Should().Be("/t/" + HubItemCorpus.Ref(2) + "?s=320");
        withUrls["att-video"]["videoInfo"]!.Value<string>().Should().Be("0:03・6 張畫面");
        withUrls["att-avi"]["kind"]!.Value<string>().Should().Be("Document", "avi 不能直接播放：桌面也沒有 url");
        withUrls["att-video-noref"]["kind"]!.Value<string>().Should().Be("Document");
        withUrls["att-image"]["url"]!.Value<string>().Should().Be("/m/" + HubItemCorpus.Ref(1));
        withUrls["att-image"]["thumbUrl"]!.Value<string>().Should().Be("/t/" + HubItemCorpus.Ref(1) + "?s=320");
        withUrls["att-doc"]["url"]!.Type.Should().Be(JTokenType.Null);
        withUrls["att-doc"]["thumbUrl"]!.Type.Should().Be(JTokenType.Null);
        withUrls["att-image-noref"]["url"]!.Type.Should().Be(JTokenType.Null);
        withUrls["att-image-noref"]["size"]!.Value<string>().Should().Be("1023 B");
    }

    // ───────────────────────────── SEC-11 ─────────────────────────────

    [Fact]
    public void 裝置多送的html_detailHtml_url_thumbUrl不出現()
    {
        var corpus = HubItemCorpus.Build();
        var item = corpus.Get("device-extra-keys");
        ConversationWire.Serialize(item.Payload).Should().Contain(HubItemCorpus.DeviceMark, "語料真的帶了多送的欄位");
        ((string?)item.Payload["attachments"]![0]!["url"]).Should().Contain(HubItemCorpus.DeviceMark);
        ((string?)item.Payload["media"]![0]!["url"]).Should().Contain(HubItemCorpus.DeviceMark);

        foreach (var urls in new HubMediaUrls[] { new(), new WithMediaUrls() })
        {
            var json = Renderer(urls, corpus).Render(item.Source(), Max);
            json.Should().NotContain(HubItemCorpus.DeviceMark).And.NotContain("evil.example").And.NotContain("onerror");
            var output = Parse(json);
            output["html"]!.Value<string>().Should().Be(AgentMarkdownRenderer.PlainToHtml("裝置多送了欄位"));
            output.Should().NotContainKey("detailHtml").And.NotContainKey("url").And.NotContainKey("thumbUrl");
        }
    }

    [Fact]
    public void Header欄位型別或長度不合格的meta_Validate就擋()
    {
        // 連到步 3：伺服器只物化 Validate 過的 meta，Header（標題、模型、模式、資料夾）不會帶著不合格的型別或長度進來
        ConversationEventRules.Validate(ConversationEventKinds.Meta, ConversationSamples.Json(ConversationSamples.Meta())).Should().BeNull("陽性對照");
        foreach (var (name, change) in new (string, Action<JObject>)[]
                 {
                     ("標題太長", m => m["title"] = new string('長', ConversationEventRules.MaxTitleLength + 1)),
                     ("標題是數字", m => m["title"] = 42),
                     ("沒有標題", m => m.Remove("title")),
                     ("模型太長", m => m["model"] = new string('m', ConversationEventRules.MaxShortTextLength + 1)),
                     ("模型是物件", m => m["model"] = new JObject { ["html"] = "<b>x</b>" }),
                     ("強度是陣列", m => m["effort"] = new JArray("high")),
                     ("不認得的權限模式", m => m["permissionMode"] = "Root"),
                     ("沒有自訂資料夾卻有路徑", m => m["workingDirectory"] = @"C:\repo"),
                     ("資料夾太長", m => { m["customFolder"] = true; m["workingDirectory"] = new string('d', ConversationEventRules.MaxPathLength + 1); }),
                     ("running 是字串", m => m["running"] = "true"),
                 })
        {
            var meta = ConversationSamples.Meta();
            change(meta);
            ConversationEventRules.Validate(ConversationEventKinds.Meta, ConversationSamples.Json(meta)).Should().Be(SyncErrorCodes.Invalid, name);
        }
    }

    // ───────────────────────────── 與桌面一致 ─────────────────────────────

    [Fact]
    public void 答案陣列轉回物件_與桌面形狀相同()
    {
        var corpus = HubItemCorpus.Build();
        var output = Render(corpus.Get("question-answers"), new HubMediaUrls(), corpus);

        var desktop = new AgentChatItem
        {
            Kind = AgentItemKind.Question,
            Answers = new Dictionary<string, List<string>>
            {
                ["q1"] = ["開新分支"],
                ["token"] = [ConversationWire.HiddenAnswer],
                ["q3"] = ["逾時", "關閉視窗", "另外：斷線重試"],
            },
        };
        var expected = new ItemRenderer(new HubMediaUrls()).Render(desktop, null)["answers"]!;
        output["answers"]!.ToString(Formatting.None).Should().Be(expected.ToString(Formatting.None));
        output["answers"]!.Type.Should().Be(JTokenType.Object);
        output.ToString(Formatting.None).Should().NotContain("不該出現的明文", "秘密題目的答案在線上就是「（已隱藏）」");
    }

    [Fact]
    public void 與桌面ItemRenderer一致()
    {
        var compared = 0;
        var notOnWire = new List<string>();
        var desktopFailed = new List<string>();
        foreach (var (name, desktop, server, wire) in DesktopCorpusOnServer(notOnWire))
        {
            if (desktop["kind"]!.Value<string>() == nameof(AgentItemKind.Error) && desktop["html"]!.Value<string>()!.Contains("這一筆內容無法顯示", StringComparison.Ordinal))
            {
                // 桌面渲染時丟例外（存檔裡的清單有 null）：線上的投影已略過 null，伺服器照常渲染，不比對
                desktopFailed.Add(name);
                server["kind"]!.Value<string>().Should().NotBe(nameof(AgentItemKind.Error), name);
                continue;
            }

            var expected = Normalize(desktop);
            if (wire["createdAt"] is null)
                expected["time"] = HubItemCorpus.ReceivedAt;
            Normalize(server).ToString(Formatting.None).Should().Be(expected.ToString(Formatting.None), name);
            compared++;
        }

        notOnWire.Should().Equal(new[] { "undefined-enums" }, "種類或狀態是沒定義的數值：TryItem 不上線");
        // media-null-element 在桌面的網址（LocalMediaUrls）會丟例外，伺服器的網址不看路徑，照常比對
        desktopFailed.Should().BeEquivalentTo(new[] { "attachments-null-element", "diff-null-text" });
        compared.Should().Be(ItemRendererCorpus.All().Sum(c => c.Items.Count) - 3).And.BeGreaterThan(80);
    }

    // ───────────────────────────── 執行緒 ─────────────────────────────

    [Fact]
    public void 並行渲染_結果與單執行緒逐位元組相同()
    {
        var corpus = HubItemCorpus.Build();
        var sources = corpus.Cases.Select(c => c.Source()).Concat(DesktopSources()).ToList();
        var renderer = Renderer(new WithMediaUrls(), corpus);
        var expected = sources.Select(s => renderer.Render(s, Max)).ToList();
        var mismatches = 0;
        var errors = new ConcurrentQueue<Exception>();
        using var start = new Barrier(8);

        var threads = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();
                for (var round = 0; round < 50; round++)
                {
                    for (var i = 0; i < sources.Count; i++)
                    {
                        if (renderer.Render(sources[i], Max) != expected[i])
                            Interlocked.Increment(ref mismatches);
                    }
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue(ex);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        errors.Should().BeEmpty();
        mismatches.Should().Be(0);
        sources.Should().HaveCountGreaterThan(100);
    }

    // ───────────────────────────── 大小與佔位（D20、D33） ─────────────────────────────

    [Fact]
    public void blob讀不到_佔位不當機()
    {
        var corpus = HubItemCorpus.Build();
        foreach (var (item, blobs) in new (HubItemCase, IBlobStore)[]
                 {
                     (corpus.Get("blob-missing"), new FakeBlobStore(corpus.Blobs)),
                     (corpus.Get("blob-text"), new EmptyBlobStore()),
                     (corpus.Get("blob-diff"), new EmptyBlobStore()),
                 })
        {
            var output = Parse(new HubItemRenderer(new HubMediaUrls(), blobs, NullLogger<HubItemRenderer>.Instance).Render(item.Source(), Max));

            output["id"]!.Value<string>().Should().Be(item.Id);
            output["kind"]!.Value<string>().Should().Be(item.Name == "blob-diff" ? "Notice" : "Assistant", "工具卡的佔位改成 Notice");
            output["status"]!.Value<string>().Should().Be("Done");
            output["html"]!.Value<string>().Should().Be(AgentMarkdownRenderer.PlainToHtml(HubItemRules.TooLargeMessage));
            output["seq"]!.Value<long>().Should().Be(item.Ord);
            output["time"]!.Value<long>().Should().Be(item.Payload["createdAt"]!.Value<long>());
        }
    }

    [Fact]
    public void blob換回的內容照常渲染()
    {
        var corpus = HubItemCorpus.Build();
        var blobs = new FakeBlobStore(corpus.Blobs);
        var renderer = new HubItemRenderer(new HubMediaUrls(), blobs, NullLogger<HubItemRenderer>.Instance);

        Parse(renderer.Render(corpus.Get("blob-text").Source(), Max))["html"]!.Value<string>().Should().Contain("<strong>Markdown</strong>").And.Contain("<code>ok</code>");
        var diff = Parse(renderer.Render(corpus.Get("blob-diff").Source(), Max));
        diff["diff"]!.ToString(Formatting.None).Should().Be("""[{"t":"del","s":"舊的一行"},{"t":"add","s":"新的一行"}]""");
        diff["diffStats"]!.ToString(Formatting.None).Should().Be("""{"add":1,"del":1}""");
        blobs.Reads.Should().Be(2);
    }

    [Fact]
    public void blob內容有權杖_佔位()
    {
        var token = ConversationSamples.GitHub();
        foreach (var (field, text) in new[]
                 {
                     ("text", "看起來正常的回覆 " + token),
                     ("output", ConversationSamples.PemBegin() + "\n" + ConversationSamples.PemBody()),
                     // 陣列欄位的 blob 是 JSON：以 \u 跳脫寫的權杖在原文裡比對不到，要解析後再掃
                     ("diff", "[{\"t\":\"add\",\"s\":\"" + ConversationSamples.EscapeFirst(token) + "\"}]"),
                 })
        {
            var payload = ConversationSamples.Item(text: null);
            payload["kind"] = field == "text" ? "Assistant" : "Tool";
            var content = Encoding.UTF8.GetBytes(text);
            var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
            payload[field] = new JObject { ["blob"] = sha, ["length"] = content.Length };
            var store = new FakeBlobStore(new Dictionary<string, byte[]> { [sha] = content });

            var json = new HubItemRenderer(new HubMediaUrls(), store, NullLogger<HubItemRenderer>.Instance).Render(Source(payload), Max);

            json.Should().NotContain(token[4..]).And.NotContain("MIIEow", field);
            Parse(json)["html"]!.Value<string>().Should().Be(AgentMarkdownRenderer.PlainToHtml(HubItemRules.UnavailableMessage), field);
        }
    }

    [Fact]
    public void TryReadText_copyItem的原文_只收Assistant_blob換回_讀不到或命中DLP不給()
    {
        // WP4.2 D30：原文不渲染；blob 換回的內容一樣跑第二道 DLP（同渲染）
        HubItemRenderer.TryReadText(new EmptyBlobStore(), "json", ConversationSamples.Item("**原文** 不渲染").ToString(Formatting.None), out var text)
            .Should().Be(ItemTextOutcome.Ok);
        text.Should().Be("**原文** 不渲染");

        var content = Encoding.UTF8.GetBytes("blob 裡的原文");
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        var withBlob = ConversationSamples.Item(text: null);
        withBlob["text"] = new JObject { ["blob"] = sha, ["length"] = content.Length };
        var body = withBlob.ToString(Formatting.None);
        HubItemRenderer.TryReadText(new FakeBlobStore(new Dictionary<string, byte[]> { [sha] = content }), "json", body, out text).Should().Be(ItemTextOutcome.Ok);
        text.Should().Be("blob 裡的原文");
        HubItemRenderer.TryReadText(new EmptyBlobStore(), "json", body, out text).Should().Be(ItemTextOutcome.Unreadable, "blob 讀不到");
        text.Should().BeEmpty();

        var token = ConversationSamples.GitHub();
        var leak = Encoding.UTF8.GetBytes("看起來正常的回覆 " + token);
        var leakSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(leak));
        withBlob["text"] = new JObject { ["blob"] = leakSha, ["length"] = leak.Length };
        HubItemRenderer.TryReadText(new FakeBlobStore(new Dictionary<string, byte[]> { [leakSha] = leak }), "json", withBlob.ToString(Formatting.None), out text)
            .Should().Be(ItemTextOutcome.Unreadable, "blob 不在上行的 DLP 範圍內");
        text.Should().BeEmpty();

        var tool = ConversationSamples.Item("工具的輸出");
        tool["kind"] = "Tool";
        HubItemRenderer.TryReadText(new EmptyBlobStore(), "json", tool.ToString(Formatting.None), out text).Should().Be(ItemTextOutcome.NotAssistant);
        text.Should().BeEmpty();
        HubItemRenderer.TryReadText(new EmptyBlobStore(), "json", "{壞掉", out _).Should().Be(ItemTextOutcome.Unreadable, "payload 解不開");
    }

    [Fact]
    public void 超過上限_Shrink之後在上限內且留尾段()
    {
        const int limit = 8 * 1024;

        // 先完整渲染：4,000 個雙引號只有 4,000 字，轉成 &quot; 之後超過上限，再只留尾段
        var quotes = Source(Payload("User", new string('"', 4_000) + "\n尾端記號"));
        var quoted = Renderer(new HubMediaUrls()).Render(quotes, limit);
        Encoding.UTF8.GetByteCount(quoted).Should().BeLessThanOrEqualTo(limit);
        var quotedHtml = Parse(quoted)["html"]!.Value<string>();
        quotedHtml.Should().StartWith("…（前 ").And.Contain("尾端記號").And.Contain(CredentialLeakRules.Replacement, "切點所在的第一行換掉");

        // 三欄加起來已經超過上限（blob 換回的長文）：直接只留尾段
        var lines = string.Concat(Enumerable.Range(0, 30_000).Select(i => $"第 {i} 行\n")) + "最後一行";
        var blob = Encoding.UTF8.GetBytes(lines);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(blob));
        var longPayload = Payload("Assistant", null);
        longPayload["text"] = new JObject { ["blob"] = sha, ["length"] = blob.Length };
        var long_ = new HubItemRenderer(new HubMediaUrls(), new FakeBlobStore(new Dictionary<string, byte[]> { [sha] = blob }), NullLogger<HubItemRenderer>.Instance)
            .Render(Source(longPayload), limit);
        Encoding.UTF8.GetByteCount(long_).Should().BeLessThanOrEqualTo(limit);
        var html = Parse(long_)["html"]!.Value<string>();
        html.Should().Contain("最後一行").And.Contain("第 29999 行").And.NotContain("第 0 行").And.Contain("字省略）");

        // 工具卡的輸出與說明也只留尾段
        var tool = Payload("Tool", null);
        tool["output"] = new string('o', 20_000) + "\n輸出尾端";
        tool["detail"] = new string('d', 20_000) + "\n說明尾端";
        var toolJson = Parse(Renderer(new HubMediaUrls()).Render(Source(tool), limit));
        toolJson["output"]!.Value<string>().Should().StartWith("…（前 ").And.EndWith("輸出尾端");
        toolJson["detail"]!.Value<string>().Should().StartWith("…（前 ").And.EndWith("說明尾端");
    }

    [Fact]
    public void Shrink之後仍超過_佔位()
    {
        // 問卷不會被縮短：題目很多時只留尾段也放不下
        var payload = Payload("Question", null);
        payload["status"] = "Waiting";
        payload["requestDigest"] = HubItemCorpus.Digest;
        payload["questions"] = new JArray(Enumerable.Range(0, 200).Select(i => new JObject
        {
            ["id"] = "q" + i,
            ["text"] = new string('題', 100),
            ["multiSelect"] = false,
            ["allowOther"] = true,
            ["isSecret"] = false,
            ["options"] = new JArray(),
        }));

        var json = Renderer(new HubMediaUrls()).Render(Source(payload, ord: 77), 4 * 1024);

        var output = Parse(json);
        output["kind"]!.Value<string>().Should().Be("Notice");
        output["status"]!.Value<string>().Should().Be("Waiting");
        output["html"]!.Value<string>().Should().Be(AgentMarkdownRenderer.PlainToHtml(HubItemRules.TooLargeMessage));
        output["seq"]!.Value<long>().Should().Be(77);
        output.Should().NotContainKey("requestDigest").And.NotContainKey("questions");
    }

    [Fact]
    public void payload解不開_佔位_記錄不含內容()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new LoggerFactory([logs]);
        var renderer = new HubItemRenderer(new HubMediaUrls(), new EmptyBlobStore(), factory.CreateLogger<HubItemRenderer>());
        var secret = ConversationSamples.GitHub();

        foreach (var (enc, body) in new[] { ("json", "{\"text\":\"" + secret + "\""), ("json", "[1,2]"), ("gzip", "不是base64" + secret), ("zstd", "{}") })
        {
            var output = Parse(renderer.Render(new HubItemSource(HubItemCorpus.Conversation, HubItemCorpus.Owner, null, "broken", 9, body, enc, HubItemCorpus.ReceivedAt), Max));
            output["id"]!.Value<string>().Should().Be("broken");
            output["kind"]!.Value<string>().Should().Be("Notice");
            output["status"]!.Value<string>().Should().Be("Done");
            output["time"]!.Value<long>().Should().Be(HubItemCorpus.ReceivedAt);
            output["seq"]!.Value<long>().Should().Be(9);
            output["html"]!.Value<string>().Should().Be(AgentMarkdownRenderer.PlainToHtml(HubItemRules.UnavailableMessage));
        }

        logs.Entries.Should().ContainSingle("同一筆只記一次");
        logs.AllText().Should().Contain("broken").And.Contain(HubItemCorpus.Conversation).And.NotContain(secret[4..]);
    }

    [Fact]
    public void 渲染失敗的記錄_最多1000個鍵()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new LoggerFactory([logs]);
        var renderer = new HubItemRenderer(new HubMediaUrls(), new EmptyBlobStore(), factory.CreateLogger<HubItemRenderer>());

        for (var i = 0; i < HubItemRenderer.MaxLoggedFailures + 200; i++)
            renderer.Render(new HubItemSource(HubItemCorpus.Conversation, HubItemCorpus.Owner, null, "item" + i, i + 1, "x", "json", HubItemCorpus.ReceivedAt), Max);

        logs.Entries.Should().HaveCount(HubItemRenderer.MaxLoggedFailures);
    }

    [Fact]
    public void Markdown轉換失敗_每筆項目各記一次()
    {
        // 每次渲染都 new 一個 ItemRenderer（D24）：共用的話它自己的「失敗過的項目」會讓別的項目、別次的記錄被吞掉或記到別筆上
        var logs = new CapturingLoggerProvider();
        using var factory = new LoggerFactory([logs]);
        var renderer = new HubItemRenderer(new HubMediaUrls(), new EmptyBlobStore(), factory.CreateLogger<HubItemRenderer>());
        foreach (var id in new[] { "deep-a", "deep-b", "deep-a", "deep-b" })
        {
            var payload = Payload("Assistant", new string('>', 130) + " 引用");
            payload["id"] = id;
            renderer.Render(Source(payload), Max);
        }

        logs.Entries.Select(e => e.Text).Should().SatisfyRespectively(
            first => first.Should().Contain("deep-a").And.Contain("markdown"),
            second => second.Should().Contain("deep-b").And.Contain("markdown"));
    }

    [Fact]
    public void Markdown太深_改用純文字_照樣渲染()
    {
        var payload = Payload("Assistant", new string('>', 130) + " 引用");
        var output = Parse(Renderer(new HubMediaUrls()).Render(Source(payload), Max));

        output["kind"]!.Value<string>().Should().Be("Assistant");
        output["html"]!.Value<string>().Should().Be(AgentMarkdownRenderer.PlainToHtml(new string('>', 130) + " 引用"));
    }

    [Fact]
    public void 本機檔案連結_淨化後是帶完整路徑的頁內錨點_網頁據此送fileFetch()
    {
        const string pptx = @"C:\repo\slides\output\年度簡報 #1+20%.pptx";
        var payload = Payload("Assistant", $"- [下載完整 PowerPoint](<{pptx}>)\n- [UNC](<\\\\evil\\share\\a.pptx>)");

        var html = Parse(Renderer(new HubMediaUrls()).Render(Source(payload), Max))["html"]!.Value<string>()!;

        var hrefs = System.Text.RegularExpressions.Regex.Matches(html, "href=\"([^\"]*)\"").Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).ToList();
        hrefs.Should().HaveCount(2);
        hrefs[0].Should().StartWith(HubMediaUrls.LocalFilePrefix);
        Uri.UnescapeDataString(hrefs[0][HubMediaUrls.LocalFilePrefix.Length..]).Should().Be(pptx);
        hrefs[1].Should().Be("#", "UNC 不是本機檔案，網頁也不會送 fileFetch");
        html.Should().Contain("下載完整 PowerPoint");
    }

    // ───────────────────────────── 輔助 ─────────────────────────────

    private static HubItemRenderer Renderer(HubMediaUrls urls, HubCorpus? corpus = null)
        => new(urls, new FakeBlobStore(corpus?.Blobs ?? new Dictionary<string, byte[]>()), NullLogger<HubItemRenderer>.Instance);

    private static JObject Render(HubItemCase item, HubMediaUrls urls, HubCorpus corpus) => Parse(Renderer(urls, corpus).Render(item.Source(), Max));

    private static JArray RenderAll(HubCorpus corpus, HubMediaUrls urls)
        => new(corpus.Cases.Select(c => new JObject { ["name"] = c.Name, ["item"] = Render(c, urls, corpus) }));

    /// <summary>不把字串轉成日期的解析（JObject.Parse 預設會）</summary>
    internal static JObject Parse(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json)) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static WireItem Wire(JObject payload)
    {
        ConversationWire.TryReadItem(payload, _ => null, out var item).Should().BeTrue();
        return item!;
    }

    /// <summary>一筆最小的 conv.item（id、ord、ver、kind、status、createdAt）</summary>
    private static JObject Payload(string kind, string? text)
    {
        var payload = ConversationSamples.Item(text: text, ord: 5);
        payload["kind"] = kind;
        payload["createdAt"] = HubItemCorpus.ReceivedAt - 60_000;
        return payload;
    }

    private static HubItemSource Source(JObject payload, long ord = 5)
    {
        var (enc, body) = PayloadRules.Encode(ConversationWire.Serialize(payload));
        return new HubItemSource(HubItemCorpus.Conversation, HubItemCorpus.Owner, HubItemCorpus.WorkingDirectory, (string)payload["id"]!, ord, body, enc, HubItemCorpus.ReceivedAt);
    }

    private static JObject MediaTile(string id, string name) => new()
    {
        ["id"] = id, ["name"] = name, ["kind"] = "Document", ["size"] = null, ["thumbUrl"] = null, ["url"] = null, ["videoInfo"] = null,
    };

    private static Dictionary<string, JObject> Attachments(JObject output)
        => output["attachments"]!.Children<JObject>().ToDictionary(a => (string)a["id"]!);

    /// <summary>名稱是 path 的屬性（不分大小寫）；頂層 answers 物件的鍵是題目 id，不算</summary>
    private static List<string> PathKeys(JObject root)
        => root.Descendants().OfType<JProperty>()
            .Where(p => string.Equals(p.Name, "path", StringComparison.OrdinalIgnoreCase)
                        && !(p.Parent?.Parent is JProperty { Name: "answers" } answers && answers.Parent == root))
            .Select(p => p.Path)
            .ToList();

    private static readonly HashSet<string> RemovedKeys = new(StringComparer.Ordinal) { "path", "seq", "url", "thumbUrl", "requestDigest", "risk", "attachments", "media" };

    /// <summary>
    /// 桌面有、伺服器刻意沒有的欄位，要明列在這裡並說明原因：<c>source</c>（使用者訊息從 claude.ai 或 Claude App 送來）不上線
    /// （WP4.3 計畫 D34：送上線要改同步契約），伺服器的 Item 沒有它
    /// </summary>
    private static readonly HashSet<string> KnownDesktopOnlyKeys = new(StringComparer.Ordinal) { "source" };

    /// <summary>比對前的正規化：拿掉伺服器刻意不同的欄位；空字串當成 null（線上省略空字串，讀回時是 null）</summary>
    private static JObject Normalize(JObject output)
    {
        var copy = (JObject)output.DeepClone();
        foreach (var property in copy.Descendants().OfType<JProperty>().Where(p => RemovedKeys.Contains(p.Name) || KnownDesktopOnlyKeys.Contains(p.Name)).ToList())
            property.Remove();
        foreach (var value in copy.Descendants().OfType<JValue>().Where(v => v.Type == JTokenType.String && (string?)v == string.Empty).ToList())
            value.Replace(JValue.CreateNull());
        return copy;
    }

    /// <summary>
    /// 桌面語料的每一筆：桌面的方式（共用渲染器＋伺服器的網址，html 淨化）與伺服器的方式（TryItem → 序列化 → 存 → TryReadItem → 渲染）。
    /// Ord 填桌面的 Seq；TryItem 不上線的名稱收進 <paramref name="notOnWire"/>
    /// </summary>
    private static IEnumerable<(string Name, JObject Desktop, JObject Server, JObject Wire)> DesktopCorpusOnServer(List<string>? notOnWire = null)
    {
        var renderer = Renderer(new HubMediaUrls());
        foreach (var corpus in ItemRendererCorpus.All())
        {
            foreach (var item in corpus.Items)
            {
                var name = item.Id[(item.Id.IndexOf('-', item.Id.IndexOf('-') + 1) + 1)..];
                item.Ord = item.Seq;
                var desktop = new ItemRenderer(new HubMediaUrls()).Render(item, corpus.WorkingDirectory);
                foreach (var key in new[] { "html", "detailHtml" })
                {
                    if (desktop[key] is JValue { Type: JTokenType.String, Value: string html })
                        desktop[key] = ItemSanitizer.Sanitize(html);
                }

                if (!ConversationWire.TryItem(item, 1, out var wire))
                {
                    notOnWire?.Add(name);
                    continue;
                }

                var (enc, body) = PayloadRules.Encode(ConversationWire.Serialize(wire));
                var source = new HubItemSource(HubItemCorpus.Conversation, HubItemCorpus.Owner, corpus.WorkingDirectory, item.Id, item.Ord.Value, body, enc, HubItemCorpus.ReceivedAt);
                yield return (name, desktop, Parse(renderer.Render(source, Max)), wire);
            }
        }
    }

    private static IEnumerable<HubItemSource> DesktopSources()
    {
        foreach (var corpus in ItemRendererCorpus.All())
        {
            foreach (var item in corpus.Items)
            {
                item.Ord = item.Seq;
                if (!ConversationWire.TryItem(item, 1, out var wire))
                    continue;
                var (enc, body) = PayloadRules.Encode(ConversationWire.Serialize(wire));
                yield return new HubItemSource(HubItemCorpus.Conversation, HubItemCorpus.Owner, corpus.WorkingDirectory, item.Id, item.Ord.Value, body, enc, HubItemCorpus.ReceivedAt);
            }
        }
    }
}
