using MyriagentHub.Auth;
using MyriagentHub.Tests.TestSupport;

namespace MyriagentHub.Tests.Auth;

/// <summary>裝置存取權杖（cha_）：15 分鐘、綁 deviceId；每台同時最多兩個有效（RefreshAccessToken 沒有次數限制，表不能無限長）</summary>
public sealed class AccessTokenStoreTests
{
    [Fact]
    public void 換新時保留上一個_更早的作廢_別台不受影響_到期失效()
    {
        var clock = new ManualTimeProvider();
        var store = new AccessTokenStore(clock);
        var other = store.Issue("d_B");
        var first = store.Issue("d_A");
        var second = store.Issue("d_A");
        store.Validate(first.AccessToken).Should().Be("d_A");

        var third = store.Issue("d_A");
        store.Validate(first.AccessToken).Should().BeNull("每台最多兩個有效的");
        store.Validate(second.AccessToken).Should().Be("d_A", "換新之前發出的請求還用上一個");
        store.Validate(third.AccessToken).Should().Be("d_A");
        store.Validate(other.AccessToken).Should().Be("d_B");

        for (var i = 0; i < 100; i++)
            store.Issue("d_A");
        store.Validate(other.AccessToken).Should().Be("d_B");

        clock.Advance(TimeSpan.FromMinutes(16));
        store.Validate(other.AccessToken).Should().BeNull();
        store.RevokeDevice("d_A");
    }
}
