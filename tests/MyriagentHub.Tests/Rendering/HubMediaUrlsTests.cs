using Myriagent.Sync;
using MyriagentHub.Rendering;
using MyriagentHub.Tests.Media;

namespace MyriagentHub.Tests.Rendering;

/// <summary>正式的媒體網址形狀（WP4.7 計畫 D2、步 5）：伺服器黃金檔用測試子類別，這裡鎖住正式的 <see cref="HubMediaUrls.ForRef"/></summary>
public sealed class HubMediaUrlsTests
{
    private const string Ref = "0123456789ABCDEFGHJKMNPQRS";

    private static readonly string Device = "d_" + "0123456789ABCDEFGHJKMNPQRS";

    private readonly HubMediaUrls urls = new();

    [Fact]
    public void ForRef_三種用途的整串網址()
    {
        SyncIds.IsDeviceId(Device).Should().BeTrue("測試的裝置 id 要合格");
        urls.ForRef(Device, Ref, HubMediaUrls.PurposeThumb, 320).Should().Be("/t/" + Device + "-" + Ref + "?s=320");
        urls.ForRef(Device, Ref, HubMediaUrls.PurposeMedia).Should().Be("/m/" + Device + "-" + Ref);
        urls.ForRef(Device, Ref, HubMediaUrls.PurposeFile).Should().Be("/f/" + Device + "-" + Ref);
    }

    [Theory]
    [InlineData(null, 320)]
    [InlineData(1, 160)]
    [InlineData(160, 160)]
    [InlineData(161, 320)]
    [InlineData(320, 320)]
    [InlineData(4000, 320)]
    public void ForRef_縮圖只有160與320(int? size, int expected) =>
        urls.ForRef(Device, Ref, HubMediaUrls.PurposeThumb, size).Should().EndWith("?s=" + expected);

    [Theory]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS", null)]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS", "")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS", "0123456789abcdefghjkmnpqrs")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS", "0123456789ABCDEFGHJKMNPQR")]
    [InlineData("d_0123456789ABCDEFGHJKMNPQRS", "../../etc/passwd")]
    [InlineData("not-a-device", "0123456789ABCDEFGHJKMNPQRS")]
    [InlineData("", "0123456789ABCDEFGHJKMNPQRS")]
    public void ForRef_不合格的參照或裝置回null(string device, string? mediaRef)
    {
        foreach (var purpose in new[] { HubMediaUrls.PurposeMedia, HubMediaUrls.PurposeThumb, HubMediaUrls.PurposeFile })
            urls.ForRef(device, mediaRef, purpose, 320).Should().BeNull(purpose);
    }

    [Fact]
    public void ForRef_不認得的用途回null()
    {
        urls.ForRef(Device, Ref, "download").Should().BeNull();
        urls.ForRef(Device, Ref, "Media").Should().BeNull("用途分大小寫");
    }

    [Fact]
    public void ItemSanitizer保留這三種網址()
    {
        var mediaRef = MediaSeed.NewRef();
        var media = urls.ForRef(Device, mediaRef, HubMediaUrls.PurposeMedia)!;
        var thumb = urls.ForRef(Device, mediaRef, HubMediaUrls.PurposeThumb, 320)!;
        var file = urls.ForRef(Device, mediaRef, HubMediaUrls.PurposeFile)!;

        ItemSanitizer.IsAllowedSrc(media).Should().BeTrue();
        ItemSanitizer.IsAllowedSrc(thumb).Should().BeTrue();
        ItemSanitizer.IsAllowedHref(media).Should().BeTrue();
        ItemSanitizer.IsAllowedHref(file).Should().BeTrue();
        var html = "<p><img src=\"" + thumb + "\" alt=\"a\"><a href=\"" + file + "\">f</a></p>";
        ItemSanitizer.Sanitize(html).Should().Contain("src=\"" + thumb.Replace("&", "&amp;") + "\"").And.Contain("href=\"" + file + "\"");
    }
}
