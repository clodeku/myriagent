using Myriagent.Media;

namespace Myriagent.Tests.Protocol.Media;

/// <summary>媒體網址的鍵 {deviceId}-{ref}（WP4.7 計畫 D2）</summary>
public class MediaKeyRulesTests
{
    private const string Device = "d_0123456789ABCDEFGHJKMNPQRS";

    private const string Ref = "0123456789ABCDEFGHJKMNPQRS";

    [Fact]
    public void Format_組出裝置與參照()
    {
        MediaKeyRules.Format(Device, Ref).Should().Be("d_0123456789ABCDEFGHJKMNPQRS-0123456789ABCDEFGHJKMNPQRS");
        MediaKeyRules.Format(Device, Ref).Length.Should().Be(MediaKeyRules.Length);
    }

    [Theory]
    [InlineData("d_0123456789abcdefghjkmnpqrs", Ref)]
    [InlineData("x_0123456789ABCDEFGHJKMNPQRS", Ref)]
    [InlineData(Device, "0123456789ABCDEFGHJKMNPQR")]
    [InlineData(Device, "0123456789ABCDEFGHJKMNPQRU")]
    public void Format_不合格丟例外(string deviceId, string mediaRef)
    {
        var act = () => MediaKeyRules.Format(deviceId, mediaRef);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryParse_取回裝置與參照()
    {
        MediaKeyRules.TryParse(MediaKeyRules.Format(Device, Ref), out var deviceId, out var mediaRef).Should().BeTrue();

        deviceId.Should().Be(Device);
        mediaRef.Should().Be(Ref);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("d_0123456789abcdefghjkmnpqrs-0123456789abcdefghjkmnpqrs")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS-0123456789abcdefghjkmnpqrs")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQR--0123456789ABCDEFGHJKMNPQRS")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS-0123456789ABCDEFGHJKMNPQR-")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS-0123456789ABCDEFGHJKMNPQR")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS-0123456789ABCDEFGHJKMNPQRS?x")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQ-0123456789ABCDEFGHJKMNPQRSTV")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRSTV-0123456789ABCDEFGHJKMNPQ")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS_0123456789ABCDEFGHJKMNPQRS")]
    [InlineData("x_0123456789ABCDEFGHJKMNPQRS-0123456789ABCDEFGHJKMNPQRS")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS-0123456789ABCDEFGHJKMNPQRI")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS-0123456789ABCDEFGHJKMNPQRL")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS-0123456789ABCDEFGHJKMNPQRO")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRU-0123456789ABCDEFGHJKMNPQRS")]
    public void TryParse_拒絕不合格(string? key)
    {
        MediaKeyRules.TryParse(key, out var deviceId, out var mediaRef).Should().BeFalse();

        deviceId.Should().BeNull();
        mediaRef.Should().BeNull();
    }
}
