using Myriagent.Media;

namespace Myriagent.Tests.Protocol.Media;

/// <summary>瀏覽器上傳的 id（WP4.7 計畫 D21；和遠端指令的 uploads 同一份規則）</summary>
public class UploadIdRulesTests
{
    [Theory]
    [InlineData("01JABCDEFGHJKMNPQRSTVWXYZ0")]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("a")]
    [InlineData("up_load-1")]
    public void IsValid_通過(string id)
        => UploadIdRules.IsValid(id).Should().BeTrue();

    [Fact]
    public void IsValid_64字通過_65字不通過()
    {
        UploadIdRules.IsValid(new string('a', UploadIdRules.MaxLength)).Should().BeTrue();
        UploadIdRules.IsValid(new string('a', UploadIdRules.MaxLength + 1)).Should().BeFalse();
        UploadIdRules.MaxLength.Should().Be(64);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("{3f2504e0-4f89-11d3-9a0c-0305e82c3301}")]
    [InlineData("a b")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("上傳")]
    public void IsValid_不通過(string? id)
        => UploadIdRules.IsValid(id).Should().BeFalse();
}
