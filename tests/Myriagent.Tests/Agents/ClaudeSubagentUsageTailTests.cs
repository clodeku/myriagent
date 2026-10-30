using Myriagent.Agents;
using Myriagent.Tests.TestSupport;
using Newtonsoft.Json.Linq;

namespace Myriagent.Tests.Agents;

/// <summary>
/// Claude 子代理逐字稿的用量（2026-10-25 實測：轉送的子代理訊息 output 是開頭的 16，逐字稿那幾行是最終的 138）：
/// 建立時已有的內容不算、只讀新增的完整行、同一則訊息的多行以 id 去重取最大、帶模型
/// </summary>
public sealed class ClaudeSubagentUsageTailTests : IDisposable
{
    private readonly TempDirectory temp = new("subagent-tail");

    public void Dispose() => temp.Dispose();

    private static string Line(string id, string model, long input, long creation, long read, long output)
        => new JObject
        {
            ["type"] = "assistant",
            ["message"] = new JObject
            {
                ["id"] = id, ["model"] = model, ["role"] = "assistant",
                ["content"] = new JArray(new JObject { ["type"] = "tool_use", ["name"] = "Read" }),
                ["usage"] = new JObject { ["input_tokens"] = input, ["cache_creation_input_tokens"] = creation, ["cache_read_input_tokens"] = read, ["output_tokens"] = output },
                ["stop_reason"] = "tool_use",
            },
        }.ToString(Newtonsoft.Json.Formatting.None) + "\n";

    [Fact]
    public void 只讀新增的完整行_同一則訊息去重取最大_帶模型()
    {
        var transcript = temp.Combine("projects", "C--w", "sid.jsonl");
        var dir = ClaudeSubagentUsageTail.DirectoryFor(transcript);
        dir.Should().Be(temp.Combine("projects", "C--w", "sid", "subagents"));
        Directory.CreateDirectory(dir);
        var old = Path.Combine(dir, "agent-old.jsonl");
        File.WriteAllText(old, Line("msg_old", "claude-sonnet-5-5", 2, 9_000, 0, 50));

        var tail = new ClaudeSubagentUsageTail(dir);
        tail.Poll().Should().BeEmpty("建立時已有的內容（之前的程序）不算");

        var file = Path.Combine(dir, "agent-a1.jsonl");
        File.AppendAllText(file, Line("msg_1", "claude-sonnet-5-5", 2, 16_542, 0, 138));
        File.AppendAllText(file, Line("msg_1", "claude-sonnet-5-5", 2, 16_542, 0, 138));   // 同一則訊息的第二個內容區塊
        File.AppendAllText(file, "{\"type\":\"user\",\"message\":{\"content\":\"x\"}}\n");
        File.AppendAllText(file, "{\"type\":\"assistant\",\"message\":{\"id\":\"msg_2\",\"model\":\"claude-sonnet-5-5\",\"usage\":{\"input_tokens\":2");   // 寫到一半
        var first = tail.Poll();
        first.Should().ContainSingle();
        first[0].Model.Should().Be("claude-sonnet-5-5");
        first[0].Fields.Should().Equal(2, 16_542, 0, 138);

        File.AppendAllText(file, ",\"cache_creation_input_tokens\":779,\"cache_read_input_tokens\":16542,\"output_tokens\":55}}}\n");
        File.AppendAllText(old, Line("msg_old", "claude-sonnet-5-5", 2, 9_000, 0, 80));
        var second = tail.Poll();
        second.Select(u => u.Fields).Should().BeEquivalentTo(new[] { new long[] { 2, 779, 16_542, 55 }, new long[] { 2, 9_000, 0, 80 } },
            "寫完的半行接著讀；舊檔新增的行照樣讀（建立時的位置之後）");
        tail.Poll().Should().BeEmpty();
    }
}
