using Myriagent.Hub;
using Myriagent.Sync;

namespace Myriagent.Tests.Sync.Rules;

/// <summary>對話發布的彙總提示（WP4.3 計畫步 7，D14、D32）：每種原因一則、帶對話數，不列對話</summary>
public sealed class PublishNoticeRulesTests
{
    [Fact]
    public void 每種原因都有文字_帶對話數()
    {
        foreach (var kind in Enum.GetValues<PublishNoticeKind>())
        {
            var text = PublishNoticeRules.Text(kind, 37);
            text.Should().Contain("37", $"{kind} 的提示要說是幾個對話");
            text.Should().NotBeNullOrWhiteSpace();
        }

        Enum.GetValues<PublishNoticeKind>().Select(k => PublishNoticeRules.Text(k, 1)).Should().OnlyHaveUniqueItems("不同的原因說法不同");
    }

    [Fact]
    public void 重新配對前的舊身分_說明之後不再上傳()
        => PublishNoticeRules.Text(PublishNoticeKind.NotOwner, 2).Should().Contain("重新配對").And.Contain("不再上傳");

    [Fact]
    public void 對話數不是正數或不認得的種類_丟例外()
    {
        var zero = () => PublishNoticeRules.Text(PublishNoticeKind.Blocked, 0);
        zero.Should().Throw<ArgumentOutOfRangeException>();
        var unknown = () => PublishNoticeRules.Text((PublishNoticeKind)99, 1);
        unknown.Should().Throw<ArgumentOutOfRangeException>();
    }
}
