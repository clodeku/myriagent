using Myriagent.Sync;

namespace Myriagent.Tests.Sync;

/// <summary>cha_ 的換新時機與格式、保存（WP4.7 計畫步 8，D24）</summary>
public sealed class AccessTokenRulesTests
{
    [Fact]
    public void NeedsRefresh_12分59秒還不換_13分就換()
    {
        // 寫死數字：AccessTokenMinutes、AccessTokenRefreshBeforeMinutes 改值時這個測試跟著紅
        AccessTokenRules.NeedsRefresh(TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(59)).Should().BeFalse();
        AccessTokenRules.NeedsRefresh(TimeSpan.FromMinutes(13)).Should().BeTrue();
        AccessTokenRules.NeedsRefresh(TimeSpan.FromHours(2)).Should().BeTrue("睡眠醒來之後也要換");
        AccessTokenRules.NeedsRefresh(TimeSpan.Zero).Should().BeFalse();
        AccessTokenRules.RefreshAfter.Should().Be(TimeSpan.FromMinutes(13));
    }

    [Theory]
    [InlineData("cha_test", true)]
    [InlineData("cha_0123456789ABCDEFGHJKMNPQRS", true)]
    [InlineData("cha_", false)]
    [InlineData("chd_device", false)]
    [InlineData("CHA_test", false)]
    [InlineData("cha_有中文", false)]
    [InlineData("cha_a b", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsUsable_只收cha_開頭的可印ASCII(string? token, bool usable) => AccessTokenRules.IsUsable(token).Should().Be(usable);

    [Fact]
    public void IsUsable_換行與太長的不收()
    {
        AccessTokenRules.IsUsable("cha_a\r\nX-Evil: 1").Should().BeFalse("不能讓伺服器回的值改到標頭");
        AccessTokenRules.IsUsable("cha_" + new string('a', AccessTokenRules.MaxTokenLength - 4)).Should().BeTrue();
        AccessTokenRules.IsUsable("cha_" + new string('a', AccessTokenRules.MaxTokenLength - 3)).Should().BeFalse();
    }

    [Fact]
    public void AccessTokenHolder_整組換掉_清掉之後是null()
    {
        var holder = new AccessTokenHolder();
        holder.Current.Should().BeNull();

        holder.Set("cha_one", 100);
        holder.Current.Should().Be(("cha_one", 100L));
        holder.Set("cha_two", 250);
        holder.Current.Should().Be(("cha_two", 250L));
        holder.Clear();
        holder.Current.Should().BeNull();
        FluentActions.Invoking(() => holder.Set("", 1)).Should().Throw<ArgumentException>();
    }
}
