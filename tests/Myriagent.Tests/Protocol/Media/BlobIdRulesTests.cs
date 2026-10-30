using Myriagent.Media;

namespace Myriagent.Tests.Protocol.Media;

/// <summary>blob 的 id 與存放的相對路徑（設計 3.5:668）</summary>
public class BlobIdRulesTests
{
    private const string Sha = "ab0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcd";

    [Fact]
    public void IsBlobId_只收64個小寫十六進位()
    {
        BlobIdRules.IsBlobId(Sha).Should().BeTrue();
        BlobIdRules.IsBlobId(Sha.ToUpperInvariant()).Should().BeFalse();
        BlobIdRules.IsBlobId(Sha[..63]).Should().BeFalse();
        BlobIdRules.IsBlobId(Sha + "0").Should().BeFalse();
        BlobIdRules.IsBlobId(null).Should().BeFalse();
        BlobIdRules.IsBlobId(@"..\" + Sha[3..]).Should().BeFalse();
    }

    [Fact]
    public void RelativePath_前2碼的資料夾()
        => BlobIdRules.RelativePath(Sha).Should().Be(Path.Combine("ab", Sha));

    [Fact]
    public void RelativePath_不是blob_id丟例外()
    {
        var act = () => BlobIdRules.RelativePath(@"..\..\x");

        act.Should().Throw<ArgumentException>();
    }
}
