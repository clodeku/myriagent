using Myriagent.Sync;
using MyriagentHub.Media;
using MyriagentHub.Sync;

namespace MyriagentHub.Tests.Media;

/// <summary>媒體請求的參照檢查（WP4.7 計畫 D7）：端點與用途、到期、對話可見</summary>
public sealed class MediaLookupRulesTests
{
    private const string Device = "d_0123456789ABCDEFGHJKMNPQRS";

    private const string Conv = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData((int)MediaEndpointKind.Media, "item:x", true)]
    [InlineData((int)MediaEndpointKind.Media, "list", true)]
    [InlineData((int)MediaEndpointKind.Media, "file", false)]
    [InlineData((int)MediaEndpointKind.Thumb, "item:abc-1", true)]
    [InlineData((int)MediaEndpointKind.Thumb, "list", true)]
    [InlineData((int)MediaEndpointKind.Thumb, "file", false)]
    [InlineData((int)MediaEndpointKind.File, "file", true)]
    [InlineData((int)MediaEndpointKind.File, "item:x", false)]
    [InlineData((int)MediaEndpointKind.File, "list", false)]
    [InlineData((int)MediaEndpointKind.ReplicaMedia, "item:x", true)]
    [InlineData((int)MediaEndpointKind.ReplicaMedia, "file", false)]
    [InlineData((int)MediaEndpointKind.ReplicaThumb, "list", true)]
    [InlineData((int)MediaEndpointKind.ReplicaThumb, "file", false)]
    [InlineData((int)MediaEndpointKind.Media, "item:", false)]
    [InlineData((int)MediaEndpointKind.Media, "Item:x", false)]
    [InlineData((int)MediaEndpointKind.Media, "LIST", false)]
    [InlineData((int)MediaEndpointKind.File, "FILE", false)]
    [InlineData((int)MediaEndpointKind.Media, "", false)]
    [InlineData((int)MediaEndpointKind.Media, null, false)]
    public void 端點與用途相符(int endpoint, string? purpose, bool allowed) =>
        MediaLookupRules.Allows((MediaEndpointKind)endpoint, purpose).Should().Be(allowed);

    [Fact]
    public void list在30天到期()
    {
        var created = 1_700_000_000_000L;
        var days30 = (long)TimeSpan.FromDays(30).TotalMilliseconds;

        MediaLookupRules.Expired("list", created, created + days30 - 1).Should().BeFalse("30 天減 1 毫秒");
        MediaLookupRules.Expired("list", created, created + days30).Should().BeTrue("滿 30 天");
        MediaLookupRules.Expired("list", created, created + days30 + 1).Should().BeTrue("30 天加 1 毫秒");
    }

    [Fact]
    public void file在15分鐘到期()
    {
        var created = 1_700_000_000_000L;
        var minutes15 = (long)TimeSpan.FromMinutes(15).TotalMilliseconds;

        MediaLookupRules.Expired("file", created, created + minutes15 - 1).Should().BeFalse();
        MediaLookupRules.Expired("file", created, created + minutes15).Should().BeTrue();
        MediaLookupRules.Expired("file", created, created + minutes15 + 1).Should().BeTrue();
    }

    [Fact]
    public void 項目的參照不會到期_不認得的用途一律到期()
    {
        MediaLookupRules.Expired("item:x", 0, long.MaxValue).Should().BeFalse();
        MediaLookupRules.Expired("other", 1000, 1000).Should().BeTrue();
        MediaLookupRules.Expired(null, 1000, 1000).Should().BeTrue();
    }

    [Fact]
    public void 對話可見_存在_擁有者相符_政策full()
    {
        MediaLookupRules.Visible(new ConversationOwnership(Conv, Device, 1, ConversationSyncPolicies.Full), Device).Should().BeTrue();
        MediaLookupRules.Visible(null, Device).Should().BeFalse("對話不存在或已刪除");
        MediaLookupRules.Visible(new ConversationOwnership(Conv, "d_ZZZZZZZZZZZZZZZZZZZZZZZZZZ", 1, ConversationSyncPolicies.Full), Device).Should().BeFalse("擁有者是別台");
        MediaLookupRules.Visible(new ConversationOwnership(Conv, Device, 1, ConversationSyncPolicies.Metadata), Device).Should().BeFalse("降級成 metadata");
        MediaLookupRules.Visible(new ConversationOwnership(Conv, Device, 1, ConversationSyncPolicies.Paused), Device).Should().BeFalse("DLP 暫停");
        MediaLookupRules.Visible(new ConversationOwnership(Conv, Device, 1, ConversationSyncPolicies.LocalOnly), Device).Should().BeFalse();
    }

    [Fact]
    public void Miss_查不到的原因依序是用途_到期_可見性()
    {
        var full = new ConversationOwnership(Conv, Device, 1, ConversationSyncPolicies.Full);
        MediaLookupRules.Miss(MediaEndpointKind.Media, "item:x", 0, 0, full, Device).Should().BeNull();
        MediaLookupRules.Miss(MediaEndpointKind.File, "item:x", 0, 0, full, Device).Should().Be(MediaLookupMisses.Purpose);
        MediaLookupRules.Miss(MediaEndpointKind.File, "file", 0, (long)TimeSpan.FromMinutes(15).TotalMilliseconds, full, Device).Should().Be(MediaLookupMisses.Expired);
        MediaLookupRules.Miss(MediaEndpointKind.Thumb, "item:x", 0, 0, null, Device).Should().Be(MediaLookupMisses.NoConversation);
        MediaLookupRules.Miss(MediaEndpointKind.Thumb, "item:x", 0, 0, full, "d_ZZZZZZZZZZZZZZZZZZZZZZZZZZ").Should().Be(MediaLookupMisses.OtherOwner);
        MediaLookupRules.Miss(MediaEndpointKind.Thumb, "item:x", 0, 0, full with { SyncPolicy = ConversationSyncPolicies.Metadata }, Device)
            .Should().Be(MediaLookupMisses.NotFull);
        MediaLookupRules.Miss(MediaEndpointKind.File, "item:x", 0, 0, null, Device).Should().Be(MediaLookupMisses.Purpose, "用途先判");
    }

    [Theory]
    [InlineData((int)MediaEndpointKind.Media, "item:x", 0L, true, ConversationSyncPolicies.Full)]
    [InlineData((int)MediaEndpointKind.File, "item:x", 0L, true, ConversationSyncPolicies.Full)]
    [InlineData((int)MediaEndpointKind.File, "file", 900_000L, true, ConversationSyncPolicies.Full)]
    [InlineData((int)MediaEndpointKind.Thumb, "list", 0L, false, ConversationSyncPolicies.Full)]
    [InlineData((int)MediaEndpointKind.Thumb, "list", 0L, true, ConversationSyncPolicies.Paused)]
    public void Miss_是null正好是三條規則都通過(int endpoint, string purpose, long now, bool exists, string policy)
    {
        var owner = exists ? new ConversationOwnership(Conv, Device, 1, policy) : null;
        var kind = (MediaEndpointKind)endpoint;
        var passes = MediaLookupRules.Allows(kind, purpose) && !MediaLookupRules.Expired(purpose, 0, now) && MediaLookupRules.Visible(owner, Device);
        (MediaLookupRules.Miss(kind, purpose, 0, now, owner, Device) is null).Should().Be(passes);
    }

    [Theory]
    [InlineData((int)MediaEndpointKind.Media, MediaPurposes.Media)]
    [InlineData((int)MediaEndpointKind.ReplicaMedia, MediaPurposes.Media)]
    [InlineData((int)MediaEndpointKind.Thumb, MediaPurposes.Thumb)]
    [InlineData((int)MediaEndpointKind.ReplicaThumb, MediaPurposes.Thumb)]
    [InlineData((int)MediaEndpointKind.File, MediaPurposes.File)]
    public void 向來源要的用途(int endpoint, string purpose) => MediaLookupRules.RequestPurpose((MediaEndpointKind)endpoint).Should().Be(purpose);
}
