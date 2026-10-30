using Myriagent.Agents;
using Myriagent.Usage;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// 三家解析器發的 token 增量（AgentTokenUsageEvent，側欄帳戶卡的計數）。數字取自 2026-10-22 的實測：
/// Claude 2.1（haiku、stream-json＋--include-partial-messages）每次 API 呼叫一則 message_start／message_delta，result 的 usage 是這一輪、
/// modelUsage 是這個程序的累計（含子代理、--resume 時含恢復的舊用量）；Codex app-server 每次呼叫一則 thread/tokenUsage/updated（total＝thread 累計），
/// thread/resume 一接上先送恢復的累計（帶上一輪的 turnId）
/// </summary>
public class TokenUsageParserTests
{
    private static AgentUsage U(long input, long output, long cached) => new(input, output, cached, null);

    private static AgentUsage Sum(IEnumerable<AgentEvent> events)
        => events.OfType<AgentTokenUsageEvent>().Aggregate(TokenTallyRules.Zero, (s, e) => TokenTallyRules.Add(s, e.Delta));

    private static string Line(object value) => JObject.FromObject(value).ToString(Newtonsoft.Json.Formatting.None);

    private static string Start(string id, long input, long creation, long read, long output)
        => Line(new { type = "stream_event", parent_tool_use_id = (string?)null, @event = new { type = "message_start", message = new { id, usage = new { input_tokens = input, cache_creation_input_tokens = creation, cache_read_input_tokens = read, output_tokens = output, service_tier = "standard" } } } });

    private static string Delta(long input, long creation, long read, long output)
        => Line(new
        {
            type = "stream_event", parent_tool_use_id = (string?)null,
            @event = new { type = "message_delta", delta = new { stop_reason = "end_turn" }, usage = new { input_tokens = input, cache_creation_input_tokens = creation, cache_read_input_tokens = read, output_tokens = output, output_tokens_details = new { thinking_tokens = 1 } } },
        });

    private static string Result((long In, long Creation, long Read, long Out) usage, (long In, long Creation, long Read, long Out) model)
        => Line(new
        {
            type = "result", subtype = "success", is_error = false, result = "done", total_cost_usd = 0.01,
            usage = new { input_tokens = usage.In, cache_creation_input_tokens = usage.Creation, cache_read_input_tokens = usage.Read, output_tokens = usage.Out },
            modelUsage = new Dictionary<string, object>
            {
                ["claude-haiku-5-5"] = new { inputTokens = model.In, outputTokens = model.Out, cacheReadInputTokens = model.Read, cacheCreationInputTokens = model.Creation, costUSD = 0.01 },
            },
        });

    [Fact]
    public void Claude_每次API呼叫即時記_加總等於這一輪的usage_第一則result只當基準()
    {
        var parser = new ClaudeStreamParser();
        var events = new List<AgentEvent>();
        void Feed(string line) => events.AddRange(parser.Parse(line).Events);

        Feed(Start("msg_1", 2, 30_631, 16_240, 6));
        events.OfType<AgentTokenUsageEvent>().Single().Delta.Should().Be(U(46_873, 6, 16_240), "message_start 一到就記輸入");
        Feed(Delta(2, 30_631, 16_240, 273));
        events.OfType<AgentTokenUsageEvent>().Last().Delta.Should().Be(U(0, 267, 0), "message_delta 只多了輸出");
        Feed("""{"type":"assistant","message":{"id":"msg_1","role":"assistant","content":[{"type":"text","text":"x"}],"usage":{"input_tokens":2,"cache_creation_input_tokens":30631,"cache_read_input_tokens":16240,"output_tokens":6}},"parent_tool_use_id":null}""");
        Feed(Start("msg_2", 2, 4_613, 46_871, 4));
        Feed(Delta(2, 4_613, 46_871, 607));
        // modelUsage 比 usage 多（--resume 恢復的舊用量或子代理）：程序的第一則 result 分不出來，不補
        Feed(Result((4, 35_244, 63_111, 880), (6, 50_000, 63_111, 900)));

        events.OfType<AgentTokenUsageEvent>().Should().HaveCount(4, "assistant 訊息不重複記、第一則 result 不補");
        Sum(events).Should().Be(U(98_359, 880, 63_111), "等於 result 的 usage（4＋35,244＋63,111、880）");
    }

    [Fact]
    public void Claude_之後的result_modelUsage比上一則多出又沒串流到的子代理用量補記()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse(Start("msg_1", 2, 30_631, 16_240, 6));
        parser.Parse(Delta(2, 30_631, 16_240, 273));
        parser.Parse(Start("msg_2", 2, 4_613, 46_871, 4));
        parser.Parse(Delta(2, 4_613, 46_871, 607));
        parser.Parse(Result((4, 35_244, 63_111, 880), (4, 35_244, 63_111, 880)));

        // 第二輪叫了子代理（實測：子代理沒有串流事件，只出現在 modelUsage）
        var events = new List<AgentEvent>();
        events.AddRange(parser.Parse(Start("msg_3", 2, 28_039, 18_859, 6)).Events);
        events.AddRange(parser.Parse(Delta(2, 28_039, 18_859, 712)).Events);
        events.AddRange(parser.Parse(Start("msg_4", 2, 5_288, 46_898, 8)).Events);
        events.AddRange(parser.Parse(Delta(2, 5_288, 46_898, 101)).Events);
        var result = parser.Parse(Result((4, 33_327, 65_757, 813), (4 + 6, 35_244 + 75_408, 63_111 + 65_757, 880 + 1_175)));
        events.AddRange(result.Events);

        result.Events.OfType<AgentTokenUsageEvent>().Single().Delta.Should().Be(U(42_083, 362, 0), "子代理：輸入 2＋42,081、輸出 362");
        Sum(events).Should().Be(U(141_171, 1_175, 65_757), "這一輪實際用掉的＝modelUsage 的差");
        result.TurnCompleted!.Usage.Should().Be(new AgentUsage(99_088, 813, 65_757, 0.01), "這一輪的 usage 照舊（標題列的輸入／輸出）");
    }

    [Fact]
    public void Claude_子代理的串流事件不記_delta只帶輸出也行()
    {
        var parser = new ClaudeStreamParser();
        parser.Parse("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"sub","usage":{"input_tokens":500,"output_tokens":1}}},"parent_tool_use_id":"toolu_1"}""")
            .Events.OfType<AgentTokenUsageEvent>().Should().BeEmpty("子代理的事件整則略過（由 modelUsage 的差補上）");

        parser.Parse("""{"type":"stream_event","event":{"type":"message_start","message":{"id":"m","usage":{"input_tokens":10,"output_tokens":1}}},"parent_tool_use_id":null}""");
        parser.Parse("""{"type":"stream_event","event":{"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":42}},"parent_tool_use_id":null}""")
            .Events.OfType<AgentTokenUsageEvent>().Single().Delta.Should().Be(U(0, 41, 0), "沒帶的輸入欄位沿用 message_start 的");
    }

    [Fact]
    public void ClaudeRules_用量欄位與modelUsage合計()
    {
        var fields = ClaudeStreamRules.MergeMessageUsage(null, JObject.Parse("""{"input_tokens":2,"cache_creation_input_tokens":30631,"cache_read_input_tokens":16240,"output_tokens":6}"""));
        ClaudeStreamRules.MessageUsageOf(fields).Should().Be(U(46_873, 6, 16_240));
        var merged = ClaudeStreamRules.MergeMessageUsage(fields, JObject.Parse("""{"output_tokens":273,"input_tokens":"x","cache_read_input_tokens":-1}"""));
        ClaudeStreamRules.MessageUsageOf(merged).Should().Be(U(46_873, 273, 16_240), "不是數字或負的當成沒帶");
        ClaudeStreamRules.MessageUsageOf(null).Should().BeNull();

        ClaudeStreamRules.SumModelUsage(JObject.Parse("""{"a":{"inputTokens":4,"outputTokens":880,"cacheReadInputTokens":63111,"cacheCreationInputTokens":35244},"b":{"inputTokens":1,"outputTokens":2}}"""))
            .Should().Be(U(98_360, 882, 63_111));
        ClaudeStreamRules.SumModelUsage(new JObject()).Should().BeNull();
        ClaudeStreamRules.SumModelUsage(null).Should().BeNull();
    }

    private static string CodexUsage(string thread, string turn, (long In, long Cached, long Out) total, (long In, long Cached, long Out) last)
        => Line(new
        {
            method = "thread/tokenUsage/updated",
            @params = new
            {
                threadId = thread, turnId = turn,
                tokenUsage = new
                {
                    total = new { inputTokens = total.In, cachedInputTokens = total.Cached, outputTokens = total.Out, reasoningOutputTokens = 0, totalTokens = total.In + total.Out },
                    last = new { inputTokens = last.In, cachedInputTokens = last.Cached, outputTokens = last.Out, reasoningOutputTokens = 0, totalTokens = last.In + last.Out },
                    modelContextWindow = 258_400,
                },
            },
        });

    private static string CodexTurnStarted(string turn) => Line(new { method = "turn/started", @params = new { threadId = "th", turn = new { id = turn, status = "inProgress" } } });

    private static string CodexTurnCompleted(string turn)
        => Line(new { method = "turn/completed", @params = new { threadId = "th", turn = new { id = turn, items = Array.Empty<object>(), status = "completed", error = (string?)null } } });

    [Fact]
    public void Codex_total的差是增量_一輪的用量是這一輪的合計不只最後一次呼叫()
    {
        var parser = new CodexEventParser();
        var events = new List<AgentEvent>();
        CodexParseResult Feed(string line)
        {
            var r = parser.Parse(line, "th");
            events.AddRange(r.Events);
            return r;
        }

        Feed(CodexTurnStarted("t1"));
        Feed(CodexUsage("th", "t1", (14_200, 0, 34), (14_200, 0, 34)));
        Feed(CodexUsage("th", "t1", (28_457, 14_080, 39), (14_257, 14_080, 5)));
        var t1 = Feed(CodexTurnCompleted("t1")).TurnCompleted!;

        events.OfType<AgentTokenUsageEvent>().Select(e => e.Delta).Should().Equal(U(14_200, 34, 0), U(14_257, 5, 14_080));
        t1.Usage.Should().Be(U(28_457, 39, 14_080), "以前只取 last（14,257＋5），少算了第一次呼叫");

        Feed(CodexTurnStarted("t2"));
        Feed(CodexUsage("th", "t2", (42_732, 14_080, 44), (14_275, 0, 5)));
        Feed(CodexTurnCompleted("t2")).TurnCompleted!.Usage.Should().Be(U(14_275, 5, 0));
        Sum(events).Should().Be(U(42_732, 44, 14_080), "全部增量＝thread 的 total");
    }

    [Fact]
    public void Codex_續接時恢復的累計只當基準_即使晚於turn_started到()
    {
        var parser = new CodexEventParser();
        var events = new List<AgentEvent>();
        void Feed(string line) => events.AddRange(parser.Parse(line, "th").Events);

        // 新程序 thread/resume：先送恢復的累計（上一輪 t2 的 turnId）
        Feed(CodexUsage("th", "t2", (42_732, 14_080, 44), (14_275, 0, 5)));
        events.OfType<AgentTokenUsageEvent>().Should().BeEmpty();

        Feed(CodexTurnStarted("t3"));
        Feed(CodexUsage("th", "t3", (57_035, 28_160, 78), (14_303, 14_080, 34)));
        Feed(CodexUsage("th", "t3", (71_395, 42_240, 83), (14_360, 14_080, 5)));
        Sum(events).Should().Be(U(28_663, 39, 28_160));

        // 另一個新程序：恢復的那則剛好在 turn/started 之後才到（turnId 是上一輪的）→ 一樣只當基準
        var late = new CodexEventParser();
        var lateEvents = new List<AgentEvent>();
        lateEvents.AddRange(late.Parse(CodexTurnStarted("t4"), "th").Events);
        lateEvents.AddRange(late.Parse(CodexUsage("th", "t3", (71_395, 42_240, 83), (14_360, 14_080, 5)), "th").Events);
        lateEvents.AddRange(late.Parse(CodexUsage("th", "t4", (85_800, 56_320, 90), (14_405, 14_080, 7)), "th").Events);
        Sum(lateEvents).Should().Be(U(14_405, 7, 14_080));
    }

    [Fact]
    public void Codex_子代理的thread也記_但不算進主對話這一輪的用量()
    {
        var parser = new CodexEventParser();
        var events = new List<AgentEvent>();
        CodexParseResult Feed(string line)
        {
            var r = parser.Parse(line, "th");
            events.AddRange(r.Events);
            return r;
        }

        Feed(CodexTurnStarted("t1"));
        Feed(CodexUsage("th", "t1", (14_200, 0, 34), (14_200, 0, 34)));
        Feed(CodexUsage("child", "c1", (9_000, 0, 120), (9_000, 0, 120)));
        Feed(CodexUsage("child", "c1", (18_500, 8_000, 150), (9_500, 8_000, 30)));
        Feed(CodexTurnCompleted("t1")).TurnCompleted!.Usage.Should().Be(U(14_200, 34, 0));
        Sum(events).Should().Be(U(32_700, 184, 8_000));
    }

    [Fact]
    public void Antigravity_步驟的usage即時記_同一步驟再回報只記多的部分()
    {
        var parser = new AntigravityStreamParser();
        parser.BeginTurn();
        var events = new List<AgentEvent>();
        void Feed(string line) => events.AddRange(parser.Parse(line).Events);

        Feed("""{"event":"step_update","step_update":{"conversation_id":"c","step_index":1,"state":"DONE","step_type":"agent_response","usage":{"input_tokens":11922,"output_tokens":123,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":12045}}}""");
        Feed("""{"event":"step_update","step_update":{"conversation_id":"c","step_index":1,"state":"DONE","step_type":"agent_response","usage":{"input_tokens":11922,"output_tokens":150,"thinking_tokens":5,"cache_read_tokens":0,"total_tokens":12077}}}""");
        Feed("""{"event":"step_update","step_update":{"conversation_id":"c","step_index":4,"state":"DONE","step_type":"agent_response","usage":{"input_tokens":12000,"output_tokens":110,"thinking_tokens":0,"cache_read_tokens":272,"total_tokens":12382}}}""");

        events.OfType<AgentTokenUsageEvent>().Select(e => e.Delta).Should().Equal(U(11_922, 123, 0), U(0, 32, 0), U(12_272, 110, 272));
    }

    // ───── 分模型（每週 5% 的分段紀錄要知道每一筆是哪個模型，2026-10-25） ─────

    private static string StartWithModel(string id, string model, long input, long creation, long read, long output)
        => Line(new { type = "stream_event", parent_tool_use_id = (string?)null, @event = new { type = "message_start", message = new { id, model, usage = new { input_tokens = input, cache_creation_input_tokens = creation, cache_read_input_tokens = read, output_tokens = output } } } });

    private static string ResultModels(params (string Model, long In, long Creation, long Read, long Out)[] models)
        => Line(new
        {
            type = "result", subtype = "success", is_error = false, result = "done",
            usage = new { input_tokens = 0, output_tokens = 0 },
            modelUsage = models.ToDictionary(m => m.Model, m => (object)new { inputTokens = m.In, outputTokens = m.Out, cacheReadInputTokens = m.Read, cacheCreationInputTokens = m.Creation }),
        });

    [Fact]
    public void Claude_主對話的增量帶訊息的模型與快取寫入()
    {
        var parser = new ClaudeStreamParser();
        var start = parser.Parse(StartWithModel("m1", "claude-opus-5-5", 2, 30_631, 16_240, 6)).Events.OfType<AgentTokenUsageEvent>().Single();
        start.Model.Should().Be("claude-opus-5-5");
        start.CacheWriteTokens.Should().Be(30_631, "cache_creation 另外帶（含在輸入裡）");
        start.Delta.Should().Be(U(46_873, 6, 16_240));
        var delta = parser.Parse(Delta(2, 30_631, 16_240, 273)).Events.OfType<AgentTokenUsageEvent>().Single();
        delta.Model.Should().Be("claude-opus-5-5", "message_delta 記在同一則訊息的模型");
        delta.CacheWriteTokens.Should().Be(0);
    }

    [Fact]
    public void Claude_子代理逐字稿的用量經解析器記帳_一輪結束只補都沒看到的部分_依模型()
    {
        var parser = new ClaudeStreamParser { FreshSession = true };
        parser.Parse(StartWithModel("m1", "claude-opus-5-5", 2, 6_000, 40_000, 5));
        parser.Parse(Delta(2, 6_000, 40_000, 300));
        // 子代理（sonnet）兩次呼叫，逐字稿讀到的（ClaudeSubagentUsageTail）
        var sub = parser.AddSubagentUsage("claude-sonnet-5-5", new long[] { 2, 16_542, 0, 138 })!;
        sub.Model.Should().Be("claude-sonnet-5-5");
        sub.Delta.Should().Be(U(16_544, 138, 0));
        sub.CacheWriteTokens.Should().Be(16_542);
        parser.AddSubagentUsage("claude-sonnet-5-5", new long[] { 2, 779, 16_542, 55 });
        parser.AddSubagentUsage("claude-sonnet-5-5", new long[] { 0, 0, 0, 0 }).Should().BeNull();

        // modelUsage 多了 haiku 的一次（CLI 自己的小模型呼叫）與 opus 的壓縮呼叫
        var result = parser.Parse(ResultModels(
            ("claude-opus-5-5", 2 + 1, 6_000, 40_000 + 9_000, 300 + 400),
            ("claude-sonnet-5-5", 4, 17_321, 16_542, 193),
            ("claude-haiku-5-5", 10, 0, 0, 20)));
        var extra = result.Events.OfType<AgentTokenUsageEvent>().ToList();
        extra.Select(e => e.Model).Should().BeEquivalentTo(new[] { "claude-haiku-5-5", "claude-opus-5-5" }, "新的 session 第一則 result 就能補；sonnet 已從逐字稿記滿");
        extra.Single(e => e.Model == "claude-opus-5-5").Delta.Should().Be(U(9_001, 400, 9_000));
        extra.Single(e => e.Model == "claude-haiku-5-5").Delta.Should().Be(U(10, 20, 0));
    }

    [Fact]
    public void Claude_記帳的模型名稱對不到modelUsage的鍵_補的總量照樣扣掉_不重複記()
    {
        var parser = new ClaudeStreamParser { FreshSession = true };
        // 訊息沒帶 model（舊版、或名稱寫法不同）
        parser.Parse(Start("m1", 2, 30_631, 16_240, 6));
        parser.Parse(Delta(2, 30_631, 16_240, 273));
        var result = parser.Parse(ResultModels(("claude-haiku-5-5[1m]", 2 + 3, 30_631, 16_240, 273 + 7)));
        result.Events.OfType<AgentTokenUsageEvent>().Single().Delta.Should().Be(U(3, 7, 0), "只補串流沒看到的 3 輸入、7 輸出");

        ClaudeStreamRules.MatchModelKey("claude-opus-5-5", new[] { "claude-opus-5-5[1m]" }).Should().Be("claude-opus-5-5[1m]");
        ClaudeStreamRules.MatchModelKey("claude-opus-5-5-20261001", new[] { "claude-opus-5-5" }).Should().Be("claude-opus-5-5");
        ClaudeStreamRules.MatchModelKey(null, new[] { "claude-opus-5-5" }).Should().BeNull();
        ClaudeStreamRules.MatchModelKey("claude-sonnet-5-5", new[] { "claude-opus-5-5" }).Should().BeNull();
    }

    [Fact]
    public void Claude_續接的程序第一則result不補_之後依模型補()
    {
        var parser = new ClaudeStreamParser();
        parser.FreshSession.Should().BeFalse();
        parser.Parse(StartWithModel("m1", "claude-opus-5-5", 2, 100, 200, 5));
        parser.Parse(ResultModels(("claude-opus-5-5", 999, 999, 999, 999))).Events.OfType<AgentTokenUsageEvent>().Should().BeEmpty("恢復的舊用量分不出來");
        parser.Parse(StartWithModel("m2", "claude-opus-5-5", 1, 0, 300, 2));
        parser.Parse(ResultModels(("claude-opus-5-5", 999 + 1 + 4, 999, 999 + 300, 999 + 2 + 6))).Events.OfType<AgentTokenUsageEvent>().Single()
            .Should().Match<AgentTokenUsageEvent>(e => e.Model == "claude-opus-5-5" && e.Delta == U(4, 6, 0));
    }

    [Fact]
    public void Codex_主對話與子代理各記自己的模型()
    {
        var parser = new CodexEventParser { MainModel = "gpt-6-astra" };
        var events = new List<AgentEvent>();
        void Feed(string line) => events.AddRange(parser.Parse(line, "th").Events);
        Feed(CodexTurnStarted("t1"));
        Feed(CodexUsage("th", "t1", (14_200, 0, 34), (14_200, 0, 34)));
        Feed(Line(new { method = "item/started", @params = new { threadId = "th", turnId = "t1", item = new { type = "subAgentActivity", id = "call_1", kind = "started", model = "gpt-6-mini", agentThreadId = "child" } } }));
        Feed(CodexUsage("child", "c1", (9_000, 0, 120), (9_000, 0, 120)));
        Feed(CodexUsage("other", "c2", (1_000, 0, 10), (1_000, 0, 10)));
        events.OfType<AgentTokenUsageEvent>().Select(e => e.Model).Should().Equal("gpt-6-astra", "gpt-6-mini", "gpt-6-astra");
    }

    [Fact]
    public void Grok_一輪結束依modelUsage逐模型發增量_沒有modelUsage記在目前的模型()
    {
        var result = JObject.Parse("""{"stopReason":"end_turn","_meta":{"usage":{"inputTokens":3000,"outputTokens":50,"cachedReadTokens":2000,"modelUsage":{"grok-4.7-build":{"inputTokens":2000,"outputTokens":40,"cachedReadTokens":1500},"grok-4.7-build-fast":{"inputTokens":1000,"outputTokens":10,"cachedReadTokens":500}}}}}""");
        GrokAcpRules.ParseTokenEvents(result, "x").Should().Equal(
            new AgentTokenUsageEvent(U(2_000, 40, 1_500), "grok-4.7-build"),
            new AgentTokenUsageEvent(U(1_000, 10, 500), "grok-4.7-build-fast"));
        var old = JObject.Parse("""{"stopReason":"end_turn","_meta":{"usage":{"inputTokens":3000,"outputTokens":50,"cachedReadTokens":2000}}}""");
        GrokAcpRules.ParseTokenEvents(old, "grok-4.6-build").Should().Equal(new AgentTokenUsageEvent(U(3_000, 50, 2_000), "grok-4.6-build"));
        GrokAcpRules.ParseTokenEvents(JObject.Parse("""{"stopReason":"cancelled"}"""), "x").Should().BeEmpty();
    }
}
