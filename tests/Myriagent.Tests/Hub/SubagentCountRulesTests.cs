using Myriagent.Hub;
using FluentAssertions;
using Xunit;

namespace Myriagent.Tests.Hub;

public sealed class SubagentCountRulesTests
{
    [Fact]
    public void 只認Claude的local_agent工作()
    {
        SubagentCountRules.IsSubagentTask("local_agent").Should().BeTrue();
        SubagentCountRules.IsSubagentTask("local_bash").Should().BeFalse("背景指令是背景工作，不是子代理");
        SubagentCountRules.IsSubagentTask("monitor").Should().BeFalse();
        SubagentCountRules.IsSubagentTask(null).Should().BeFalse();
    }

    [Fact]
    public void 工具卡與工作以tool_use_id對齊_同一個子代理只算一個()
    {
        SubagentCountRules.TaskKey("t1", "toolu_a").Should().Be("toolu_a");
        SubagentCountRules.TaskKey("t1", null).Should().Be("task:t1", "沒有工具卡的工作用 task id，不會和工具卡的 id 撞在一起");
        SubagentCountRules.Count(["toolu_a", "toolu_b"], ["toolu_a", "task:t9"]).Should().Be(3);
        SubagentCountRules.Count([], []).Should().Be(0);
    }
}
