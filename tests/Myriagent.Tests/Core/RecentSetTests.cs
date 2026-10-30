using Myriagent.Core;

namespace Myriagent.Tests.Core;

/// <summary>有上限的集合（媒體端點的白名單）：超過上限移除最久沒用到的，加入與查詢都算用到</summary>
public class RecentSetTests
{
    [Fact]
    public void 超過上限時移除最久沒用到的_查詢也算用到()
    {
        var set = new RecentSet(3, StringComparer.OrdinalIgnoreCase);
        set.Add(@"C:\a.png");
        set.Add(@"C:\b.png");
        set.Add(@"C:\c.png");
        set.Contains(@"c:\A.PNG").Should().BeTrue("不分大小寫；查詢也讓它變成最近用到的");

        set.Add(@"C:\d.png");

        set.Count.Should().Be(3);
        set.Contains(@"C:\b.png").Should().BeFalse("最久沒用到的先移除");
        set.Contains(@"C:\a.png").Should().BeTrue();
        set.Contains(@"C:\c.png").Should().BeTrue();
        set.Contains(@"C:\d.png").Should().BeTrue();
    }

    [Fact]
    public void 重複加入不增加筆數_移除後查不到()
    {
        var set = new RecentSet(10);
        set.Add("x");
        set.Add("x");
        set.Count.Should().Be(1);

        set.Remove("x").Should().BeTrue();
        set.Remove("x").Should().BeFalse();
        set.Contains("x").Should().BeFalse();
        set.Count.Should().Be(0);
    }

    [Fact]
    public void 上限必須大於0()
        => FluentActions.Invoking(() => new RecentSet(0)).Should().Throw<ArgumentOutOfRangeException>();
}
