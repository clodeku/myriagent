using Myriagent.Agents;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Usage;

/// <summary>
/// 上下文用量提示（ContextUsageRules 與三家解析器發的 AgentContextUsageEvent）。數字取自 2026-10-23 的實測（tools/probes/context_window_probe.py）：
/// Claude 2.1.309（haiku-5-5）第一次呼叫 input 2＋快取建立 22,061、輸出 4，modelUsage 的 contextWindow 1,000,000，get_context_usage 的
/// autoCompactThreshold 967,000；/compact 的 pre_tokens 22,113＝上一次呼叫的合計。Codex 0.163.0 的 last.totalTokens 14,233、modelContextWindow 258,400，
/// 壓縮後那一則 last 只有 totalTokens 7,586。Grok 1.0.67 的 response_completed input 36,675＋快取 1,664＋輸出 82，模型清單 totalContextTokens 256,000
/// </summary>
public class ContextUsageRulesTests
{
    private static AgentContextUsageEvent Used(long used) => new(used, null);

    private static AgentContextUsageEvent Window(long window, long? auto = null) => new(null, window, auto);

    // ───── 合併 ─────

    [Fact]
    public void Apply_有帶的欄位取代_沒帶的沿用()
    {
        var u = ContextUsageRules.Apply(null, Used(22_067));
        u.Should().Be(new ContextUsage(22_067, null, null));
        u = ContextUsageRules.Apply(u, Window(1_000_000));
        u.Should().Be(new ContextUsage(22_067, 1_000_000, null));
        u = ContextUsageRules.Apply(u, Window(1_000_000, 967_000));
        u.Should().Be(new ContextUsage(22_067, 1_000_000, 967_000));
        u = ContextUsageRules.Apply(u, Used(22_113));
        u.Should().Be(new ContextUsage(22_113, 1_000_000, 967_000), "只帶用量時視窗與門檻沿用");
        ContextUsageRules.Apply(null, new AgentContextUsageEvent(null, null)).Should().BeNull("什麼都沒有就是沒有");
    }

    [Fact]
    public void Apply_壓縮清掉用量_換了視窗而沒帶門檻時門檻作廢_門檻0是不自動壓縮()
    {
        var before = new ContextUsage(22_113, 1_000_000, 967_000);
        ContextUsageRules.Apply(before, new AgentContextUsageEvent(null, null, Compacted: true))
            .Should().Be(new ContextUsage(null, 1_000_000, 967_000), "壓縮後等下一次呼叫");
        ContextUsageRules.Apply(before, new AgentContextUsageEvent(7_586, null, Compacted: true))
            .Should().Be(new ContextUsage(7_586, 1_000_000, 967_000), "同一則就帶了新的用量就用它");

        ContextUsageRules.Apply(before, Window(200_000)).Should().Be(new ContextUsage(22_113, 200_000, null), "換成別的模型：舊門檻不適用");
        ContextUsageRules.Apply(before, Window(1_000_000)).Should().Be(before, "同一個視窗：門檻沿用");
        ContextUsageRules.Apply(before, Window(1_000_000, 0)).Should().Be(new ContextUsage(22_113, 1_000_000, null), "CLI 回報不自動壓縮");
        ContextUsageRules.Apply(before, Window(100_000, 167_000)).Should().Be(new ContextUsage(22_113, 100_000, null), "門檻比視窗大不合理，不用");
        ContextUsageRules.Apply(before, new AgentContextUsageEvent(-1, -5)).Should().Be(before, "負數當成沒帶");
    }

    // ───── 給網頁的檢視 ─────

    [Fact]
    public void ToView_百分比以視窗為分母_嚴重度以門檻為分母_不明時null()
    {
        var view = ContextUsageRules.ToView(new ContextUsage(22_113, 1_000_000, 967_000))!;
        view.Value<long>("used").Should().Be(22_113);
        view.Value<long>("window").Should().Be(1_000_000);
        view.Value<double>("percent").Should().Be(2.2);
        view.Value<long>("autoCompactAt").Should().Be(967_000);
        view.Value<string>("severity").Should().Be("ok");

        var codex = ContextUsageRules.ToView(new ContextUsage(14_233, 258_400, null))!;
        codex.Value<double>("percent").Should().Be(5.5);
        codex["autoCompactAt"]!.Type.Should().Be(JTokenType.Null);

        ContextUsageRules.ToView(new ContextUsage(300_000, 258_400, null))!.Value<double>("percent").Should().Be(100, "封頂");
        ContextUsageRules.ToView(null).Should().BeNull();
        ContextUsageRules.ToView(new ContextUsage(null, 1_000_000, null)).Should().BeNull("壓縮後、還沒有呼叫");
        ContextUsageRules.ToView(new ContextUsage(22_113, null, null)).Should().BeNull("視窗不明（第一則 result 之前）");
        ContextUsageRules.ToView(new ContextUsage(22_113, 0, null)).Should().BeNull();
    }

    [Theory]
    [InlineData(100_000, 200_000, null, "ok")]
    [InlineData(139_999, 200_000, null, "ok")]
    [InlineData(140_000, 200_000, null, "warn")]
    [InlineData(170_000, 200_000, null, "high")]
    [InlineData(190_000, 200_000, null, "max")]
    [InlineData(140_000, 200_000, 167_000L, "warn")]   // 有門檻時分母是門檻：140,000／167,000＝83.8%
    [InlineData(142_000, 200_000, 167_000L, "high")]   // 85.0%
    [InlineData(159_000, 200_000, 167_000L, "max")]    // 95.2%
    [InlineData(700_000, 1_000_000, 967_000L, "warn")]
    [InlineData(950_000, 1_000_000, 967_000L, "max")]
    public void Severity_70_85_95(long used, long window, long? auto, string expected)
        => ContextUsageRules.Severity(used, window, auto).Should().Be(expected);

    // ───── Claude ─────

    [Fact]
    public void Claude_呼叫的上下文是四欄相加_輸入都是0時不明()
    {
        ContextUsageRules.ClaudeCallTokens(new long[] { 2, 22_061, 0, 4 }).Should().Be(22_067);
        ContextUsageRules.ClaudeCallTokens(new long[] { 0, 0, 0, 42 }).Should().BeNull("舊版 API 的 message_delta 只帶 output");
        ContextUsageRules.ClaudeCallTokens(null).Should().BeNull();
    }

    [Fact]
    public void Claude_視窗取目前模型那一列_認不得時只有一列才用()
    {
        var two = JObject.Parse("""{"claude-haiku-5-5":{"contextWindow":1000000},"claude-sonnet-5-5":{"contextWindow":200000}}""");
        ContextUsageRules.ClaudeWindow(two, "claude-sonnet-5-5").Should().Be(200_000);
        ContextUsageRules.ClaudeWindow(two, "CLAUDE-HAIKU-5-5").Should().Be(1_000_000, "大小寫不同也認得");
        ContextUsageRules.ClaudeWindow(two, null).Should().BeNull("兩列又不知道目前的模型：不猜");
        ContextUsageRules.ClaudeWindow(JObject.Parse("""{"claude-haiku-5-5":{"contextWindow":1000000}}"""), "fake").Should().Be(1_000_000);
        ContextUsageRules.ClaudeWindow(JObject.Parse("""{"m":{"inputTokens":1}}"""), "m").Should().BeNull("舊版沒有 contextWindow");
        ContextUsageRules.ClaudeWindow(null, "m").Should().BeNull();
    }

    [Fact]
    public void Claude_get_context_usage的回應_視窗取rawMaxTokens_不自動壓縮時門檻是0()
    {
        var real = JObject.Parse("""
            {"totalTokens":24219,"maxTokens":1000000,"rawMaxTokens":1000000,"percentage":2,"autocompactSource":"model-default","model":"claude-haiku-5-5",
             "autoCompactThreshold":967000,"isAutoCompactEnabled":true,"apiUsage":{"input_tokens":2,"output_tokens":4}}
            """);
        ContextUsageRules.ParseClaudeContextResponse(real).Should().Be(((long?)1_000_000, (long?)967_000));
        ContextUsageRules.ParseClaudeContextResponse(JObject.Parse("""{"maxTokens":200000,"autoCompactThreshold":167000,"isAutoCompactEnabled":false}"""))
            .Should().Be(((long?)200_000, (long?)0));
        ContextUsageRules.ParseClaudeContextResponse(new JObject()).Should().Be(((long?)null, (long?)null), "舊版回空的 response");
        ContextUsageRules.ParseClaudeContextResponse(null).Should().Be(((long?)null, (long?)null));
    }

    private static string Line(object value) => JObject.FromObject(value).ToString(Newtonsoft.Json.Formatting.None);

    private static string ClaudeStart(string id, string model, long input, long creation, long read, long output, string? parent = null)
        => Line(new { type = "stream_event", parent_tool_use_id = parent, @event = new { type = "message_start", message = new { id, model, usage = new { input_tokens = input, cache_creation_input_tokens = creation, cache_read_input_tokens = read, output_tokens = output } } } });

    private static string ClaudeDelta(long input, long creation, long read, long output)
        => Line(new { type = "stream_event", parent_tool_use_id = (string?)null, @event = new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { input_tokens = input, cache_creation_input_tokens = creation, cache_read_input_tokens = read, output_tokens = output } } });

    private const string ClaudeResult = """
        {"type":"result","subtype":"success","is_error":false,"result":"OK","usage":{"input_tokens":2,"cache_creation_input_tokens":22061,"cache_read_input_tokens":0,"output_tokens":4},
         "modelUsage":{"claude-haiku-5-5":{"inputTokens":2,"outputTokens":4,"cacheReadInputTokens":0,"cacheCreationInputTokens":22061,"contextWindow":1000000,"maxOutputTokens":128000},
                       "claude-sonnet-5-5":{"inputTokens":9,"outputTokens":9,"cacheReadInputTokens":0,"cacheCreationInputTokens":0,"contextWindow":200000,"maxOutputTokens":64000}}}
        """;

    [Fact]
    public void Claude解析器_每次呼叫發用量_result發目前模型的視窗_子代理不發_壓縮清掉()
    {
        var parser = new ClaudeStreamParser();
        var events = new List<AgentEvent>();
        void Feed(string line) => events.AddRange(parser.Parse(line).Events);

        Feed(ClaudeStart("sub", "claude-sonnet-5-5", 500, 0, 0, 1, parent: "toolu_1"));
        events.OfType<AgentContextUsageEvent>().Should().BeEmpty("子代理的上下文不是主對話的");

        Feed(ClaudeStart("msg_1", "claude-haiku-5-5", 2, 22_061, 0, 1));
        Feed(ClaudeDelta(2, 22_061, 0, 4));
        Feed(ClaudeDelta(2, 22_061, 0, 4));
        Feed(ClaudeResult.Replace("\n", string.Empty));
        events.OfType<AgentContextUsageEvent>().Should().Equal(
            new AgentContextUsageEvent(22_064, null),
            new AgentContextUsageEvent(22_067, null),
            new AgentContextUsageEvent(null, 1_000_000));

        events.Clear();
        Feed("""{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"manual","pre_tokens":22067,"post_tokens":3134}}""");
        events.OfType<AgentContextUsageEvent>().Single().Should().Be(new AgentContextUsageEvent(null, null, Compacted: true));
        events.OfType<AgentNoticeEvent>().Single().Message.Should().Be("已壓縮較早的對話內容", "/compact 不是自動壓縮");
        Feed("""{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"auto","pre_tokens":900000}}""");
        events.OfType<AgentNoticeEvent>().Last().Message.Should().Be("對話已很長，Claude 自動壓縮了較早的內容");

        events.Clear();
        Feed(ClaudeStart("msg_2", "claude-haiku-5-5", 2, 5_041, 18_167, 4));
        events.OfType<AgentContextUsageEvent>().Single().Should().Be(new AgentContextUsageEvent(23_214, null), "壓縮後的第一次呼叫重新發（就算數字碰巧一樣）");
    }

    [Fact]
    public void Claude_get_context_usage的請求()
    {
        var request = JObject.Parse(ClaudeStreamRules.BuildContextUsageRequest("ctx-7"));
        request.Value<string>("type").Should().Be("control_request");
        request.Value<string>("request_id").Should().Be("ctx-7");
        request["request"]!.Value<string>("subtype").Should().Be("get_context_usage");
        request["request"]!.Value<string>("detail").Should().Be("summary");
    }

    // ───── Codex ─────

    private static string CodexUsage(string thread, string turn, long lastTotal, long lastIn, long lastOut, long? window = 258_400)
        => Line(new
        {
            method = "thread/tokenUsage/updated",
            @params = new
            {
                threadId = thread, turnId = turn,
                tokenUsage = new
                {
                    total = new { totalTokens = 28_487, inputTokens = 28_477, cachedInputTokens = 14_080, outputTokens = 10, reasoningOutputTokens = 0 },
                    last = new { totalTokens = lastTotal, inputTokens = lastIn, cachedInputTokens = 0, outputTokens = lastOut, reasoningOutputTokens = 0 },
                    modelContextWindow = window,
                },
            },
        });

    [Fact]
    public void Codex_last的totalTokens與modelContextWindow_壓縮後那一則只有totalTokens_子代理不發()
    {
        ContextUsageRules.ParseCodexTokenUsage(JObject.Parse("""{"last":{"totalTokens":14233,"inputTokens":14228,"outputTokens":5},"modelContextWindow":258400}"""))
            .Should().Be(((long?)14_233, (long?)258_400));
        ContextUsageRules.ParseCodexTokenUsage(JObject.Parse("""{"last":{"totalTokens":7586,"inputTokens":0,"outputTokens":0},"modelContextWindow":258400}"""))
            .Should().Be(((long?)7_586, (long?)258_400), "壓縮後的大小");
        ContextUsageRules.ParseCodexTokenUsage(JObject.Parse("""{"total":{"totalTokens":1}}""")).Should().Be(((long?)null, (long?)null));
        ContextUsageRules.ParseCodexTokenUsage(null).Should().Be(((long?)null, (long?)null));

        var parser = new CodexEventParser();
        var events = new List<AgentEvent>();
        void Feed(string line) => events.AddRange(parser.Parse(line, "th").Events);
        Feed(Line(new { method = "turn/started", @params = new { threadId = "th", turn = new { id = "t1", status = "inProgress" } } }));
        Feed(CodexUsage("th", "t1", 14_233, 14_228, 5));
        Feed(CodexUsage("sub-thread", "t1", 99_999, 99_000, 999));
        Feed(CodexUsage("th", "t1", 7_586, 0, 0));
        events.OfType<AgentContextUsageEvent>().Should().Equal(
            new AgentContextUsageEvent(14_233, 258_400),
            new AgentContextUsageEvent(7_586, 258_400));
    }

    // ───── Grok ─────

    [Fact]
    public void Grok_每次呼叫與prompt回應的用量_模型清單的視窗()
    {
        ContextUsageRules.GrokCallTokens(JObject.Parse("""{"input_tokens":36675,"output_tokens":82,"cache_read_input_tokens":1664,"cache_creation_input_tokens":0,"reasoning_tokens":81}"""))
            .Should().Be(38_421);
        ContextUsageRules.GrokCallTokens(JObject.Parse("""{"output_tokens":82}""")).Should().BeNull();
        ContextUsageRules.GrokCallTokens(null).Should().BeNull();

        ContextUsageRules.GrokPromptTokens(JObject.Parse("""
            {"stopReason":"end_turn","_meta":{"totalTokens":38430,"modelId":"grok-4.7","inputTokens":38339,"outputTokens":82,"cachedReadTokens":1664,
             "usage":{"inputTokens":38339,"outputTokens":82,"totalTokens":38421}}}
            """)).Should().Be(38_421, "頂層的 inputTokens 已含快取讀取");
        ContextUsageRules.GrokPromptTokens(JObject.Parse("""{"stopReason":"end_turn","_meta":{"usage":{"inputTokens":1}}}""")).Should().BeNull("舊版只有整輪合計");
        ContextUsageRules.GrokPromptTokens(null).Should().BeNull();

        var (current, windows) = ContextUsageRules.ParseGrokWindows(JObject.Parse("""
            {"currentModelId":"grok-4.7","availableModels":[
              {"modelId":"grok-4.7","name":"Grok 4.7","_meta":{"totalContextTokens":256000,"contextWindows":[256000,500000]}},
              {"modelId":"grok-4.5","name":"Grok 4.5","_meta":{}},
              {"name":"沒有 id","_meta":{"totalContextTokens":1}}]}
            """));
        current.Should().Be("grok-4.7");
        windows.Should().BeEquivalentTo(new Dictionary<string, long> { ["grok-4.7"] = 256_000 });
        ContextUsageRules.ParseGrokWindowChoices(JObject.Parse("""
            {"currentModelId":"grok-4.7","availableModels":[
              {"modelId":"grok-4.7","name":"Grok 4.7","_meta":{"totalContextTokens":256000,"contextWindows":[256000,500000,256000]}},
              {"modelId":"grok-4.5","name":"Grok 4.5","_meta":{}}]}
            """)).Should().BeEquivalentTo(new Dictionary<string, long[]> { ["grok-4.7"] = [256_000, 500_000] });
        ContextUsageRules.ParseGrokWindows(null).Windows.Should().BeEmpty();
    }
}
